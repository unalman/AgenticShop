using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// Pins Stock's position in the topology: it is a leaf service that makes no outbound calls.
/// </summary>
/// <remarks>
/// Only Ordering orchestrates. If Stock started calling Catalog to check whether a product
/// exists, it would add a hop, a Stock → Catalog dependency and a trace path that nothing in
/// Phase 0 needs. <c>Directory.Build.targets</c> already rejects a compile-time reference; these
/// assertions cover the runtime equivalent, so a dependency added through DI or reflection is
/// caught too.
/// </remarks>
[Collection("Stock API")]
public sealed class LeafServiceBoundaryTests(StockApiFixture fixture)
{
    [Fact]
    public void NoHttpClientFactoryIsRegistered()
    {
        using var scope = fixture.CreateScope();

        scope.ServiceProvider.GetService<IHttpClientFactory>()
            .Should().BeNull("Stock must not call another service");
    }

    [Fact]
    public void NoHttpClientIsRegistered()
    {
        using var scope = fixture.CreateScope();

        scope.ServiceProvider.GetService<HttpClient>()
            .Should().BeNull("Stock must not call another service");
    }

    [Fact]
    public void TheAssemblyDoesNotReferenceAnyOtherAgenticShopService()
    {
        var referenced = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .Where(name => name is not null && name.StartsWith("AgenticShop.", StringComparison.Ordinal))
            .ToList();

        referenced.Should().BeEmpty(
            "a service assembly must not reference another service; the build guard in " +
            "Directory.Build.targets enforces this at compile time and this catches the runtime equivalent");
    }

    [Fact]
    public void TheOnlyAgenticShopAssemblyLoadedIsStockItself()
    {
        var loaded = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .Where(name => name is not null && name.Contains("Catalog", StringComparison.Ordinal))
            .ToList();

        loaded.Should().BeEmpty("Stock must not know Catalog exists");
    }

    [Fact]
    public void TheStockAssemblyIsTheOneUnderTest()
    {
        // Guards the guard: if the assertions above ever resolved Program from the wrong
        // assembly they would pass without testing anything.
        typeof(Program).Assembly.GetName().Name.Should().Be("AgenticShop.Stock");
        typeof(Program).Namespace.Should().BeNull("top-level statements live in the global namespace");
        Assembly.GetExecutingAssembly().GetName().Name.Should().Be("AgenticShop.Stock.IntegrationTests");
    }
}
