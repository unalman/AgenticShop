namespace AgenticShop.Catalog.Contracts;

/// <summary>
/// Marker for request DTOs that <see cref="Validation.DataAnnotationValidationFilter"/>
/// should validate.
/// </summary>
/// <remarks>
/// Detection is explicit rather than inferred from a namespace, so a DTO placed in an
/// unexpected folder is not silently skipped. The gap that remains — forgetting to
/// implement this interface — is closed by a unit test that reflects over every
/// <c>*Request</c> type in this namespace and asserts it implements the marker.
/// </remarks>
public interface IRequestContract
{
}
