using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Mediator;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate.Events;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public record OrderPayloadItem(int ItemId, int Quantity);

public record OrderPayload(string ShippingAddress, List<OrderPayloadItem> Items, decimal FinalPrice);

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;
    private readonly IMediator _mediator;
    private readonly HttpClient _httpClient;
    private readonly ServiceBusSender _orderQueueSender;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer,
        IMediator mediator,
        HttpClient httpClient,
        ServiceBusSender orderQueueSender)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
        _mediator = mediator;
        _httpClient = httpClient;
        _orderQueueSender = orderQueueSender;
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
            return new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);
        await _mediator.Publish(new OrderCreatedEvent(order));

        var payload = new {
            order = new OrderPayload(
            ShippingAddress: FormatAddress(order.ShipToAddress),
            Items: order.OrderItems
                .Select(oi => new OrderPayloadItem(oi.ItemOrdered.CatalogItemId, oi.Units))
                .ToList(),
            FinalPrice: order.Total())
        };

        var json = JsonSerializer.Serialize(payload);

        // Service Bus: ServiceBusMessage kell, nem string
        var message = new ServiceBusMessage(json)
        {
            ContentType = "application/json"
        };
        await _orderQueueSender.SendMessageAsync(message);

        // Azure Function hívás
        var functionUrl = Environment.GetEnvironmentVariable("AZURE_FUNCTION_URL");
        var functionKey = Environment.GetEnvironmentVariable("AZURE_FUNCTION_API_KEY");

        Guard.Against.NullOrWhiteSpace(functionUrl, nameof(functionUrl));

        using var request = new HttpRequestMessage(HttpMethod.Post, functionUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(functionKey))
        {
            request.Headers.Add("x-functions-key", functionKey);
        }

        using var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Function call failed: {(int)response.StatusCode} {response.StatusCode}. {body}");
        }
    }

    private static string FormatAddress(Address address) =>
        $"{address.Street}, {address.City}, {address.State} {address.ZipCode}, {address.Country}";
}