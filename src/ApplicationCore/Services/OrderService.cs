using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Mediator;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate.Events;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;
    private readonly IMediator _mediator;
    private readonly HttpClient _httpClient;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer, 
        IMediator mediator,
        HttpClient httpClient
    )
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
        _mediator = mediator;
        _httpClient = httpClient;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

        Guard.Against.Null(basket, nameof(basket));
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);
        OrderCreatedEvent orderCreatedEvent = new OrderCreatedEvent(order);
        await _mediator.Publish(orderCreatedEvent);
   

        var functionUrl = Environment.GetEnvironmentVariable("AZURE_FUNCTION_URL");
        var subscriptionKey = Environment.GetEnvironmentVariable("AZURE_FUNCTION_API_KEY");

        var payload = new OrderPayload(
            ShippingAddress: FormatAddress(order.ShipToAddress),
            Items: order.OrderItems
                .Select(oi => new OrderPayloadItem(
                        ItemId: oi.ItemOrdered.CatalogItemId,
                        Quantity: oi.Units
                    )
                )
                .ToList(),
            FinalPrice: order.Total());

        var json = JsonSerializer.Serialize(payload);

        //if (!string.IsNullOrEmpty(functionUrl))
        //{
            var request = new HttpRequestMessage(HttpMethod.Post, functionUrl);
            //request.Content = JsonContent.Create(orderPayload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            //request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
            request.Headers.Add("x-functions-key", subscriptionKey);

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Error occured: {response.StatusCode}");
            }
        //}
    }

    

    private static string FormatAddress(Address address) =>
        string.Join(", ", new[] { address.Street, address.City, address.State, address.ZipCode, address.Country }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    private sealed record OrderPayload(
        [property: JsonPropertyName("shipping_address")] string ShippingAddress,
        [property: JsonPropertyName("items")] List<OrderPayloadItem> Items,
        [property: JsonPropertyName("final_price")] decimal FinalPrice);

    private sealed record OrderPayloadItem(
        [property: JsonPropertyName("item_id")] int ItemId,
        [property: JsonPropertyName("quantity")] int Quantity
    );
}
