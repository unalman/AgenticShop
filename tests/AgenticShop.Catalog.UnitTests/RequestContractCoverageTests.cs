using AgenticShop.Catalog.Contracts;
using AgenticShop.Shared.Contracts;
using FluentAssertions;

namespace AgenticShop.Catalog.UnitTests;

/// <summary>
/// Closes the one gap the <see cref="IRequestContract"/> marker leaves open: forgetting to
/// implement it means a DTO is silently never validated, which is how the original
/// over-length-SKU 500 came about. Reflecting over the contracts assembly turns that
/// omission into a build-time test failure instead of a production incident.
/// </summary>
/// <remarks>
/// The assembly is anchored on a Catalog contract type, **not** on <see cref="IRequestContract"/>.
/// The marker now lives in <c>AgenticShop.Shared</c>, so deriving the assembly from it would scan
/// the shared project — which contains no DTOs — and this guard would pass without covering
/// anything. <see cref="TheCoverageCheckIsNotPassingVacuously"/> is what proves the anchor is right.
/// </remarks>
public class RequestContractCoverageTests
{
    private static readonly Type[] ContractTypes = typeof(CreateProductRequest).Assembly
        .GetTypes()
        .Where(t => t.Namespace == typeof(CreateProductRequest).Namespace)
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
            .Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void IRequestContractIsImplementedByTheKnownRequestDtos()
    {
        typeof(IRequestContract).IsAssignableFrom(typeof(CreateProductRequest)).Should().BeTrue();
        typeof(IRequestContract).IsAssignableFrom(typeof(UpdateProductRequest)).Should().BeTrue();
    }
}
