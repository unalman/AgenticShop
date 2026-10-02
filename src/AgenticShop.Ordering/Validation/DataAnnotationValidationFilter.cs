using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AgenticShop.Ordering.Contracts;

namespace AgenticShop.Ordering.Validation;

/// <summary>
/// Runs DataAnnotations validation over request DTOs and returns RFC 9457
/// <c>ValidationProblemDetails</c> on failure.
/// </summary>
/// <remarks>
/// <para>
/// Minimal APIs do not validate body DTOs on their own — automatic model validation is an
/// MVC/<c>[ApiController]</c> behaviour. Without this filter the attributes declared in
/// <c>Contracts/</c> are decorative: an out-of-range value sails through to PostgreSQL, which
/// rejects the insert, and the caller gets a 500 instead of a 400.
/// </para>
/// <para>
/// <see cref="Validator.TryValidateObject"/> does not cascade into complex properties or
/// collections, so recursion here is what makes a nested DTO actually validate. Member paths are
/// reported as <c>Lines[0].Quantity</c> so the error is actionable rather than pointing at the
/// whole collection. Until Ordering existed that recursion was proven only by synthetic contracts;
/// <c>CreateOrderRequest.Lines</c> is the first real nested DTO to go through it.
/// </para>
/// <para>
/// Candidates are identified by the <see cref="IRequestContract"/> marker rather than by
/// namespace, so injected services are skipped and a DTO cannot be missed by living in the
/// wrong folder.
/// </para>
/// <para>
/// This is the third deliberate copy of Catalog's filter, not a shared type. Extraction into a
/// shared library was raised before this copy was written and deferred to Phase 1 by decision —
/// see <c>docs/DECISIONS.md</c>. Its behaviour is specified once, by Catalog's
/// <c>DataAnnotationValidationFilterTests</c>; Ordering asserts only that the filter is wired to
/// its endpoint groups and that its own nested contract validates, because re-testing identical
/// code proves nothing and creates a second place to update.
/// </para>
/// </remarks>
public sealed class DataAnnotationValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        Dictionary<string, List<string>>? errors = null;

        foreach (var argument in context.Arguments)
        {
            if (argument is not IRequestContract contract)
            {
                continue;
            }

            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Visit(contract, prefix: string.Empty, found, new HashSet<object>(ReferenceEqualityComparer.Instance));

            if (found.Count == 0)
            {
                continue;
            }

            errors ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var (member, messages) in found)
            {
                if (!errors.TryGetValue(member, out var existing))
                {
                    existing = [];
                    errors[member] = existing;
                }

                existing.AddRange(messages);
            }
        }

        return errors is null
            ? await next(context)
            : Results.ValidationProblem(errors.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal));
    }

    private static void Visit(
        object instance,
        string prefix,
        Dictionary<string, List<string>> errors,
        HashSet<object> visited)
    {
        // Guards against a reference cycle recursing forever.
        if (!visited.Add(instance))
        {
            return;
        }

        // A collection is traversed by element, not by property. Reflecting over a List<T>
        // yields Count and Capacity rather than its contents, so without this branch a
        // collection nested inside another collection would never be validated at all.
        if (instance is IEnumerable elements)
        {
            var index = 0;

            foreach (var element in elements)
            {
                if (element is not null && !IsLeaf(element.GetType()))
                {
                    Visit(element, $"{prefix}[{index}]", errors, visited);
                }

                index++;
            }

            return;
        }

        var results = new List<ValidationResult>();

        Validator.TryValidateObject(
            instance,
            new ValidationContext(instance),
            results,
            validateAllProperties: true);

        foreach (var result in results)
        {
            var message = result.ErrorMessage ?? "The value is invalid.";

            // A class-level attribute reports no members, so attach it to the object's
            // own path instead of dropping it silently.
            var reported = result.MemberNames.ToList();
            var members = reported.Count > 0
                ? reported
                : [prefix.Length == 0 ? instance.GetType().Name : prefix];

            foreach (var member in members)
            {
                Add(errors, Join(prefix, member), message);
            }
        }

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var value = property.GetValue(instance);

            if (value is null || IsLeaf(value.GetType()))
            {
                continue;
            }

            Visit(value, Join(prefix, property.Name), errors, visited);
        }
    }

    private static string Join(string prefix, string member)
        => prefix.Length == 0 ? member : $"{prefix}.{member}";

    private static void Add(Dictionary<string, List<string>> errors, string member, string message)
    {
        if (!errors.TryGetValue(member, out var messages))
        {
            messages = [];
            errors[member] = messages;
        }

        messages.Add(message);
    }

    /// <summary>
    /// Types that cannot contain further validated members. Checked before the
    /// <see cref="IEnumerable"/> branch because <c>string</c> is enumerable.
    /// </summary>
    private static bool IsLeaf(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        return target.IsPrimitive
            || target.IsEnum
            || target == typeof(string)
            || target == typeof(decimal)
            || target == typeof(DateTime)
            || target == typeof(DateTimeOffset)
            || target == typeof(DateOnly)
            || target == typeof(TimeOnly)
            || target == typeof(TimeSpan)
            || target == typeof(Guid)
            || target == typeof(Uri);
    }
}
