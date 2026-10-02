namespace AgenticShop.Ordering.Domain;

/// <summary>
/// Persisted as a string with a CHECK constraint, so order state is readable directly in psql.
/// That matters once Phase 2 adds a saga and reconciliation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Pending"/> is the aggregate's construction state and is <b>never persisted in
/// Phase 0</b>. The row is written exactly once, after reservation, confirmation and
/// reconciliation have all finished, and it is written already terminal — so no reader can ever
/// observe a half-placed order. It stays in the vocabulary, and in the CHECK, because Phase 2's
/// saga writes the order <i>before</i> Stock replies, at which point a persisted
/// <see cref="Pending"/> becomes the normal case.
/// </para>
/// <para>
/// <see cref="PartiallyConfirmed"/> exists because Stock's <c>Confirmed</c> is terminal and
/// confirm decrements on-hand: a confirmed reservation cannot be released, so an order whose
/// confirms fail part-way through cannot be rolled back, only recorded. Calling it
/// <see cref="Failed"/> would invite a re-order that ships the confirmed lines a second time.
/// See <c>../../../docs/ROADMAP.md</c> → "Confirm-phase failure".
/// </para>
/// </remarks>
public enum OrderStatus
{
    /// <summary>Construction state. Placement is still in flight; never persisted in Phase 0.</summary>
    Pending = 0,

    /// <summary>Terminal. Every line was reserved and confirmed: the goods have left stock.</summary>
    Confirmed = 1,

    /// <summary>
    /// Terminal. Nothing was confirmed, and every hold that could be released was released.
    /// A stranded hold may remain if a release call itself failed — lossy, but nothing shipped.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// Terminal, and requires reconciliation. At least one line was confirmed and at least one
    /// was not, or the reconciliation read failed and the outcome is unknown. Stock moved for
    /// part of this order and cannot be asked to move it back.
    /// </summary>
    PartiallyConfirmed = 3
}
