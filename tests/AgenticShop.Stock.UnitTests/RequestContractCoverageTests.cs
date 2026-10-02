using AgenticShop.Shared.Contracts;
using AgenticShop.Stock.Contracts;
using FluentAssertions;

namespace AgenticShop.Stock.UnitTests;

/// <summary>
/// Closes the one gap the <see cref="IRequestContract"/> marker leaves open: forgetting to
/// implement it means a DTO is silently never validated, which is how Catalog's original
/// over-length-SKU 500 came about. Reflecting over the contracts assembly turns that omission
/// into a build-time failure.
/// </summary>
/// <remarks>
/// The assembly is anchored on a Stock contract type, **not** on <see cref="IRequestContract"/> —
/// the marker now lives in <c>AgenticShop.Shared</c>, which contains no DTOs, so deriving the
/// assembly from it would scan nothing and this guard would pass vacuously.
/// <see cref="TheCoverageCheckIsNotPassingVacuously"/> is what proves the anchor is right.
/// </remarks>
public class RequestContractCoverageTests
{
    private static readonly Type[] ContractTypes = typeof(SetStockRequest).Assembly
        .GetTypes()
        .Where(t => t.Namespace == typeof(SetStockRequest).Namespace)
        .ToArray();

    [Fact]
    public void EveryRequestDto_ImplementsIRequestContract()
    {
        var offenders = ContractTypes
            .Where(t => t.IsClass && t.Name.EndsWith("Request", StringComparison.Ordinal))
            .Where(t => !typeof(IRequestContract).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "DataAnnotationValidationFilter only validates types marked IRequestContract, " +
            "so an unmarked request DTO would reach the database unvalidated");
    }

    [Fact]
    public void TheCoverageCheckIsNotPassingVacuously()
    {
        // Guards the guard. If the *Request naming convention ever changes, the test above
        // would match nothing and pass without proving anything.
        ContractTypes
            .Count(t => t.IsClass && t.Name.EndsWith("Request", StringComparison.Ordinal))
            .Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void IRequestContractIsImplementedByTheKnownRequestDtos()
    {
        typeof(IRequestContract).IsAssignableFrom(typeof(SetStockRequest)).Should().BeTrue();
        typeof(IRequestContract).IsAssignableFrom(typeof(UpdateStockRequest)).Should().BeTrue();
        typeof(IRequestContract).IsAssignableFrom(typeof(ReserveStockRequest)).Should().BeTrue();
    }

    [Fact]
    public void ResponseDtosAreNotRequiredToCarryTheMarker()
    {
        // The marker means "validate me on the way in". Asserting it on responses too would be
        // noise, so this documents the boundary of the rule rather than extending it.
        typeof(IRequestContract).IsAssignableFrom(typeof(StockResponse)).Should().BeFalse();
        typeof(IRequestContract).IsAssignableFrom(typeof(ReservationResponse)).Should().BeFalse();
    }
}
