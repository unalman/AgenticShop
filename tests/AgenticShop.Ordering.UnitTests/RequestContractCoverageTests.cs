using AgenticShop.Ordering.Contracts;
using FluentAssertions;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// Closes the one gap the <see cref="IRequestContract"/> marker leaves open: forgetting to implement
/// it means a DTO is silently never validated, which is how Catalog's original over-length-SKU 500
/// came about. Reflecting over the contracts assembly turns that omission into a build-time failure.
/// </summary>
/// <remarks>
/// A copy of Stock's guard rather than a shared test, and worth more here than there: Ordering owns
/// the first genuinely nested request DTO, so an unmarked type would skip validation for a whole
/// collection of lines rather than one field.
/// </remarks>
public class RequestContractCoverageTests
{
    private static readonly Type[] ContractTypes = typeof(IRequestContract).Assembly
        .GetTypes()
        .Where(t => t.Namespace == typeof(IRequestContract).Namespace)
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
        // Guards the guard. If the *Request naming convention ever changes, the test above would
        // match nothing and pass without proving anything.
        ContractTypes
            .Count(t => t.IsClass && t.Name.EndsWith("Request", StringComparison.Ordinal))
            .Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void IRequestContractIsImplementedByTheKnownRequestDtos()
    {
        typeof(IRequestContract).IsAssignableFrom(typeof(CreateOrderRequest)).Should().BeTrue();

        // The nested one matters most: it is reached by the filter's recursion rather than by the
        // marker, but marking it keeps this guard honest if it is ever bound directly.
        typeof(IRequestContract).IsAssignableFrom(typeof(CreateOrderLineRequest)).Should().BeTrue();
    }

    [Fact]
    public void ResponseDtosAreNotRequiredToCarryTheMarker()
    {
        // The marker means "validate me on the way in". Asserting it on responses too would be
        // noise, so this documents the boundary of the rule rather than extending it.
        typeof(IRequestContract).IsAssignableFrom(typeof(OrderResponse)).Should().BeFalse();
        typeof(IRequestContract).IsAssignableFrom(typeof(OrderLineResponse)).Should().BeFalse();
    }
}
