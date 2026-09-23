using AgenticShop.Catalog.Contracts;
using FluentAssertions;

namespace AgenticShop.Catalog.UnitTests;

/// <summary>
/// Closes the one gap the <see cref="IRequestContract"/> marker leaves open: forgetting to
/// implement it means a DTO is silently never validated, which is how the original
/// over-length-SKU 500 came about. Reflecting over the contracts assembly turns that
/// omission into a build-time test failure instead of a production incident.
/// </summary>
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
