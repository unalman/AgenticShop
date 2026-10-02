namespace AgenticShop.Shared.Contracts;

/// <summary>
/// Marker for request DTOs that <see cref="Validation.DataAnnotationValidationFilter"/> should
/// validate.
/// </summary>
/// <remarks>
/// Detection is explicit rather than inferred from a namespace, so a DTO placed in an unexpected
/// folder is not silently skipped. The gap that remains — forgetting to implement this interface —
/// is closed by a per-service unit test that reflects over every <c>*Request</c> type in that
/// service's <c>Contracts/</c> and asserts it implements the marker. That test is deliberately not
/// shared: each service has its own assembly and its own drift to catch.
/// </remarks>
public interface IRequestContract
{
}
