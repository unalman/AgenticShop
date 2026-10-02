namespace AgenticShop.Ordering.Domain;

/// <summary>
/// Thrown when the lines of one order do not share a currency, so their prices cannot be added.
/// Carries the offending codes as a typed property rather than leaving them only in
/// <see cref="Exception.Message"/>, which is for logs.
/// </summary>
/// <remarks>
/// Raised during catalog resolution, before any reservation is made, so it needs no compensation.
/// Classified as a 400 with a **null** detail, exactly like every other domain 400 in the
/// repository: invariant three says no exception message reaches the client. <see cref="Currencies"/>
/// therefore exists for the log line rather than for the response — it is what makes "the request
/// was invalid" diagnosable, and it keeps the typed-property convention that a reworded
/// <see cref="Exception.Message"/> cannot silently break.
/// </remarks>
public sealed class MixedCurrencyException(IReadOnlyList<string> currencies)
    : Exception($"An order must use a single currency; received {string.Join(", ", currencies)}.")
{
    public IReadOnlyList<string> Currencies { get; } = currencies;
}
