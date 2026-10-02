using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgenticShop.Ordering.Contracts;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using AgenticShop.Shared.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// What every Ordering API test needs: a clean database and clean fakes, plus the handful of shapes
/// that turning an HTTP response into an assertion would otherwise repeat.
/// </summary>
/// <remarks>
/// Derived classes carry <c>[Collection("Ordering API")]</c> themselves; xunit does not inherit it.
/// The collection runs sequentially, which is what makes resetting shared fixture state per test safe.
/// </remarks>
public abstract class OrderApiTestBase(OrderingApiFixture fixture) : IAsyncLifetime
{
    protected OrderingApiFixture Fixture { get; } = fixture;

    // Everything below goes through Fixture rather than capturing the primary-constructor parameter
    // again: a parameter used both to initialise state and to close over it is a compile error, and
    // reading through the property keeps one owner for the fixture.
    protected HttpClient Client => Fixture.Client;

    protected FakeCatalogClient Catalog => Fixture.Catalog;

    protected FakeStockClient Stock => Fixture.Stock;

    public Task InitializeAsync() => Fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Adds a product to the fake catalog and returns its id, so a test reads as a catalog.</summary>
    protected Guid AddProduct(
        string name = "Widget",
        decimal price = 10.00m,
        string currency = "USD",
        bool isActive = true)
    {
        var productId = Guid.NewGuid();

        Catalog.WithProduct(productId, name, price, currency, isActive);

        return productId;
    }

    /// <summary>
    /// Named <c>OrderWith</c> rather than <c>Order</c>: inside these classes the simple name would be
    /// the inherited method, which then shadows the <see cref="Order"/> entity and makes
    /// <c>Order.MaxLines</c> a method group.
    /// </summary>
    protected static CreateOrderRequest OrderWith(params (Guid ProductId, int Quantity)[] lines)
        => new([.. lines.Select(line => new CreateOrderLineRequest(line.ProductId, line.Quantity))]);

    protected async Task<HttpResponseMessage> PlaceAsync(
        CreateOrderRequest request,
        string? correlationId = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(request)
        };

        AddCorrelationId(message, correlationId);

        return await Client.SendAsync(message);
    }

    /// <summary>For the binding failures that have no valid DTO to send: malformed JSON and friends.</summary>
    protected async Task<HttpResponseMessage> PlaceRawAsync(string json, string? correlationId = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        AddCorrelationId(message, correlationId);

        return await Client.SendAsync(message);
    }

    private static void AddCorrelationId(HttpRequestMessage message, string? correlationId)
    {
        if (correlationId is not null)
        {
            message.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);
        }
    }

    protected async Task<OrderResponse> PlaceSuccessfullyAsync(params (Guid ProductId, int Quantity)[] lines)
    {
        var response = await PlaceAsync(OrderWith(lines));

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the placement should have succeeded: {Body}",
            await response.Content.ReadAsStringAsync());

        return await BodyAsync(response);
    }

    protected async Task<OrderResponse> BodyAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<OrderResponse>())!;

    protected async Task<ProblemDetails> ProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        return (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
    }

    /// <summary>
    /// The order id a failed placement echoes back. Without it a client cannot reach the row that was
    /// written, and cannot tell "nothing was created" from "something was created and is broken".
    /// </summary>
    protected static Guid OrderIdFrom(ProblemDetails problem)
    {
        problem.Extensions.Should().ContainKey("orderId");

        return Guid.Parse(problem.Extensions["orderId"]!.ToString()!);
    }

    protected async Task<Order?> ReadOrderAsync(Guid id)
    {
        using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.Orders
            .AsNoTracking()
            .Include(order => order.Lines)
            .FirstOrDefaultAsync(order => order.Id == id);
    }

    /// <summary>Every order row, which is how "nothing was persisted" is asserted rather than assumed.</summary>
    protected async Task<List<Order>> ReadAllOrdersAsync()
    {
        using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        return await db.Orders
            .AsNoTracking()
            .Include(order => order.Lines)
            .ToListAsync();
    }
}
