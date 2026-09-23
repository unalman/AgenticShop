using System.Net;
using System.Net.Http.Json;
using AgenticShop.Catalog.Contracts;
using AgenticShop.Catalog.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Catalog.IntegrationTests;

/// <summary>
/// Proves the xmin concurrency token is actually enforced by PostgreSQL. A unit test
/// cannot do this: whether EF emits <c>WHERE xmin = @original</c> and whether the server
/// honours it are only observable against a real database.
/// </summary>
/// <remarks>
/// The mapping from <see cref="DbUpdateConcurrencyException"/> to HTTP 409 is covered in
/// CatalogExceptionHandlerTests. The two halves are tested separately because the conflict
/// cannot be provoked deterministically over HTTP — each request loads the row fresh, so it
/// always carries the current xmin. Only two genuinely overlapping requests can collide.
/// </remarks>
[Collection("Catalog API")]
public sealed class ConcurrencyTests(CatalogApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AStaleWriter_IsRejectedByTheXminToken()
    {
        var id = await SeedProductAsync();

        // Two independent units of work reading the same row, exactly as two overlapping
        // HTTP requests would.
        using var firstScope = fixture.CreateScope();
        using var secondScope = fixture.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var firstProduct = await first.Products.SingleAsync(p => p.Id == id);
        var secondProduct = await second.Products.SingleAsync(p => p.Id == id);

        firstProduct.Update("First writer wins", null, 10m);
        await first.SaveChangesAsync();

        secondProduct.Update("Second writer loses", null, 20m);

        Func<Task> staleWrite = () => second.SaveChangesAsync();

        await staleWrite.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the row's xmin changed when the first writer committed");
    }

    [Fact]
    public async Task TheFirstWriterSurvivesAConflict()
    {
        var id = await SeedProductAsync();

        using var firstScope = fixture.CreateScope();
        using var secondScope = fixture.CreateScope();
        using var verifyScope = fixture.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var firstProduct = await first.Products.SingleAsync(p => p.Id == id);
        var secondProduct = await second.Products.SingleAsync(p => p.Id == id);

        firstProduct.Update("First writer wins", null, 10m);
        await first.SaveChangesAsync();

        secondProduct.Update("Second writer loses", null, 20m);

        try
        {
            await second.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Expected — the assertion below is what matters.
        }

        var persisted = await verifyScope.ServiceProvider
            .GetRequiredService<CatalogDbContext>()
            .Products
            .SingleAsync(p => p.Id == id);

        persisted.Name.Should().Be("First writer wins");
        persisted.Price.Should().Be(10m);
    }

    [Fact]
    public async Task SequentialWritesBothSucceed()
    {
        // Guards against the token being wired up so tightly that ordinary serial updates
        // start failing.
        var id = await SeedProductAsync();

        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var product = await db.Products.SingleAsync(p => p.Id == id);

        product.Update("Revision one", null, 10m);
        await db.SaveChangesAsync();

        product.Update("Revision two", null, 20m);
        var act = () => db.SaveChangesAsync();

        await act.Should().NotThrowAsync();

        (await db.Products.SingleAsync(p => p.Id == id)).Name.Should().Be("Revision two");
    }

    private async Task<Guid> SeedProductAsync()
    {
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/v1/products",
            new CreateProductRequest("SKU-CONCURRENCY", "Espresso Machine", null, 199.99m, null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<ProductResponse>();

        return created!.Id;
    }
}
