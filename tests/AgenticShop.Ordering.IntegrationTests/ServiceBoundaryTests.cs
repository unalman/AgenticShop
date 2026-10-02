using System.Reflection;
using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// Pins Ordering's position in the topology: it is the orchestrator, and the only service allowed to
/// make an outbound call.
/// </summary>
/// <remarks>
/// The mirror image of Stock's <c>LeafServiceBoundaryTests</c>, which asserts that no
/// <see cref="IHttpClientFactory"/> is registered at all. Here the opposite is true, so the
/// interesting boundary is a different one: orchestration must happen over HTTP against a contract
/// Ordering declares itself, never through a compile-time reference or a second service's tables.
/// <c>Directory.Build.targets</c> rejects the reference at build time; these assertions cover the
/// runtime equivalent, so a dependency introduced through DI or reflection is caught too.
/// </remarks>
[Collection("Ordering API")]
public sealed class ServiceBoundaryTests(OrderingApiFixture fixture)
{
    /// <summary>
    /// Shared infrastructure is not a service. It is the one <c>AgenticShop.*</c> assembly a service
    /// may reference, and <c>Directory.Build.targets</c> exempts exactly this name and no other.
    /// </summary>
    private const string SharedInfrastructureAssembly = "AgenticShop.Shared";

    [Fact]
    public void TheAssemblyDoesNotReferenceAnyOtherAgenticShopService()
    {
        var referenced = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .Where(name => name is not null && name.StartsWith("AgenticShop.", StringComparison.Ordinal))
            .ToList();

        // An exact set match, not merely "contains no service": asserting the shared library *is*
        // referenced proves the exemption is about that one assembly rather than a blanket hole, so
        // a second reference would fail here even though it is not a service name.
        referenced.Should().BeEquivalentTo(
            new[] { SharedInfrastructureAssembly },
            "a service may reference only the shared infrastructure library, never another service; " +
            "the build guard in Directory.Build.targets enforces this at compile time and this " +
            "catches the runtime equivalent");
    }

    [Fact]
    public void OrderingDoesNotKnowTheOtherServicesTypes()
    {
        // Stronger than the assembly check: nothing named after another service may even be loaded,
        // so a contract cannot be borrowed rather than redeclared.
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Where(name => name is not null)
            .Where(name => name!.Contains("Catalog", StringComparison.Ordinal)
                           || name!.Contains("Stock", StringComparison.Ordinal))
            .ToList();

        loaded.Should().BeEmpty(
            "Ordering declares its own CatalogProduct and StockReservationSnapshot; there is no shared " +
            "contract assembly and must not be one");
    }

    [Fact]
    public void BothClientSeamsAreRegistered()
    {
        using var scope = fixture.CreateScope();

        scope.ServiceProvider.GetService<ICatalogClient>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IStockClient>().Should().NotBeNull();
    }

    [Fact]
    public void TheSeamIsTheInterfaceNotTheConcreteClient()
    {
        // What makes the clients fakeable, and what a broker-based implementation would swap into in
        // Phase 2. If the registration were keyed on CatalogClient and StockClient, replacing them
        // would need a different mechanism and the tests would be reaching into the HTTP stack.
        using var scope = fixture.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICatalogClient>().Should().BeSameAs(fixture.Catalog);
        scope.ServiceProvider.GetRequiredService<IStockClient>().Should().BeSameAs(fixture.Stock);
    }

    [Fact]
    public void AnHttpClientFactoryIsRegistered()
    {
        // The deliberate inverse of Stock's leaf-service assertion. Ordering is where outbound calls
        // live, and IHttpClientFactory is what gives them a pooled handler and a configured timeout.
        using var scope = fixture.CreateScope();

        scope.ServiceProvider.GetService<IHttpClientFactory>().Should().NotBeNull();
    }

    [Fact]
    public void OrderingMapsOnlyItsOwnTables()
    {
        // One DbContext, and therefore one migration history and one database. A second context would
        // mean either a shared database or a cross-service join, and both are what the boundary
        // forbids.
        var contexts = typeof(Program).Assembly
            .GetTypes()
            .Where(type => typeof(DbContext).IsAssignableFrom(type) && !type.IsAbstract)
            .ToList();

        contexts.Should().BeEquivalentTo([typeof(OrderingDbContext)]);
    }

    [Fact]
    public void TheOrderingAssemblyIsTheOneUnderTest()
    {
        // Guards the guard: if the assertions above ever resolved Program from the wrong assembly
        // they would pass without testing anything.
        typeof(Program).Assembly.GetName().Name.Should().Be("AgenticShop.Ordering");
        typeof(Program).Namespace.Should().BeNull("top-level statements live in the global namespace");
        Assembly.GetExecutingAssembly().GetName().Name.Should().Be("AgenticShop.Ordering.IntegrationTests");
    }
}
