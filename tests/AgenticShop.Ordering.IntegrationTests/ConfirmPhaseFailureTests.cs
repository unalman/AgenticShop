using System.Net;
using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using FluentAssertions;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// The confirm-phase policy, which exists because Stock's <c>Confirmed</c> is terminal and confirm
/// decrements on-hand: a confirmed reservation cannot be released, so an order whose confirms fail
/// part-way through cannot be rolled back, only recorded.
/// </summary>
/// <remarks>
/// <para>
/// None of this can be provoked deterministically against a live Stock. A mid-loop timeout needs a
/// proxy in front of the host, and "the confirm applied but the response was lost" cannot be produced
/// at all from outside. That is what the typed-client seam is for — <c>docs/ROADMAP.md</c> names it
/// "the seam the integration tests fake" — and the fake keeps real reservation state, so the
/// reconciliation read reports what the confirms actually did rather than what a test scripted.
/// </para>
/// <para>
/// The database is still real in every one of these tests, so each assertion about the persisted
/// status is a fact about PostgreSQL and not about an in-memory object.
/// </para>
/// </remarks>
[Collection("Ordering API")]
public sealed class ConfirmPhaseFailureTests(OrderingApiFixture fixture) : OrderApiTestBase(fixture)
{
    [Fact]
    public async Task AConfirmRefusedOnTheSecondLine_LeavesAPartiallyConfirmedOrder()
    {
        var (first, second, third) = ThreeProducts();

        Stock.ConfirmFailsWith = FakeStockClient.Failure.Refused;
        Stock.ConfirmFailsFromCall = 1;

        var response = await PlaceAsync(OrderWith((first, 1), (second, 1), (third, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var problem = await ProblemAsync(response);

        problem.Title.Should().Be("Downstream failure.");
        problem.Detail.Should().Be("The order was partially confirmed and requires reconciliation.");

        var persisted = await ReadOrderAsync(OrderIdFrom(problem));

        // Neither of the three statuses that already existed would have been true: Failed invites a
        // re-order that ships the first line twice, Confirmed denies that two lines never settled,
        // and Pending would never advance because Phase 0 has no worker.
        persisted!.Status.Should().Be(OrderStatus.PartiallyConfirmed);
        persisted.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task TheConfirmedReservationIsNeverReleased()
    {
        var (first, second, third) = ThreeProducts();

        Stock.ConfirmFailsWith = FakeStockClient.Failure.Refused;
        Stock.ConfirmFailsFromCall = 1;

        await PlaceAsync(OrderWith((first, 1), (second, 1), (third, 1)));

        var confirmed = Stock.ReservationIdsInStatus(StockReservationSnapshot.Statuses.Confirmed)
            .Should().ContainSingle().Subject;

        Stock.IdsPassedTo(FakeStockClient.ReleaseOperation)
            .Should().NotContain(confirmed,
                "Stock refuses to release a Confirmed reservation, and even if it did not, release " +
                "would return the hold without returning stock that has already left");
    }

    [Fact]
    public async Task TheStillPendingReservationsAreReleased()
    {
        var (first, second, third) = ThreeProducts();

        Stock.ConfirmFailsWith = FakeStockClient.Failure.Refused;
        Stock.ConfirmFailsFromCall = 1;

        await PlaceAsync(OrderWith((first, 1), (second, 1), (third, 1)));

        // The third line was never confirmed, so it is still holding stock for an order that will
        // never complete. Releasing it is the only part of this that can be undone.
        Stock.CountCalls(FakeStockClient.ConfirmOperation).Should().Be(2, "settling stops at the first failure");
        Stock.CountCalls(FakeStockClient.ReleaseOperation).Should().Be(2);
        Stock.ReservationIdsInStatus(StockReservationSnapshot.Statuses.Released)
            .Should().HaveCount(2);
        Stock.ReservationIdsInStatus(StockReservationSnapshot.Statuses.Pending)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task AConfirmThatAppliedButLostItsResponse_IsReconciledToConfirmed()
    {
        // Decision D3 says a 409 on confirm may mean "already confirmed" and must then be treated as
        // success. Ordering cannot tell that from an xmin conflict by status code, and must not parse
        // Stock's detail text, so it reads the authoritative state back instead.
        var first = AddProduct(name: "Anchor");
        var second = AddProduct(name: "Rope");

        Stock.ConfirmFailsWith = FakeStockClient.Failure.AppliedThenLost;
        Stock.ConfirmFailsFromCall = 1;

        var response = await PlaceAsync(OrderWith((first, 1), (second, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "the order really did complete; only the response to one call was lost");

        var body = await BodyAsync(response);

        body.Status.Should().Be(nameof(OrderStatus.Confirmed));
        Stock.CountCalls(FakeStockClient.ListOperation).Should().Be(1);
        Stock.CountCalls(FakeStockClient.ReleaseOperation).Should().Be(0);
        (await ReadOrderAsync(body.Id))!.Status.Should().Be(OrderStatus.Confirmed);
    }

    [Fact]
    public async Task WhenTheReconciliationReadFails_TheOrderIsPartiallyConfirmedNotFailed()
    {
        var first = AddProduct(name: "Anchor");
        var second = AddProduct(name: "Rope");

        Stock.ConfirmFailsWith = FakeStockClient.Failure.Fault;
        Stock.ConfirmFailsFromCall = 1;
        Stock.ListFails = true;

        var response = await PlaceAsync(OrderWith((first, 1), (second, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var persisted = await ReadOrderAsync(OrderIdFrom(await ProblemAsync(response)));

        // One line did ship. "We could not read the state back" must never be written down as
        // "nothing happened", because a client that believes that will re-order.
        persisted!.Status.Should().Be(OrderStatus.PartiallyConfirmed);
    }

    [Fact]
    public async Task WhenNothingWasConfirmed_TheOrderIsFailedAndEverythingIsReleased()
    {
        var first = AddProduct(name: "Anchor");
        var second = AddProduct(name: "Rope");

        Stock.ConfirmFailsWith = FakeStockClient.Failure.Fault;
        Stock.ConfirmFailsFromCall = 0;

        var response = await PlaceAsync(OrderWith((first, 1), (second, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var persisted = await ReadOrderAsync(OrderIdFrom(await ProblemAsync(response)));

        // Collapses onto the same outcome as a reserve-phase failure, which is the point: the clean
        // cases do not need the fourth status.
        persisted!.Status.Should().Be(OrderStatus.Failed);
        Stock.Reservations.Should().OnlyContain(r => r.Status == StockReservationSnapshot.Statuses.Released);
    }

    [Fact]
    public async Task AReleaseThatFailsDuringCompensation_DoesNotChangeTheClassification()
    {
        var (first, second, third) = ThreeProducts();

        Stock.ConfirmFailsWith = FakeStockClient.Failure.Refused;
        Stock.ConfirmFailsFromCall = 1;
        Stock.ReleasesFail = true;

        var response = await PlaceAsync(OrderWith((first, 1), (second, 1), (third, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var persisted = await ReadOrderAsync(OrderIdFrom(await ProblemAsync(response)));

        // The status describes what shipped, and a stranded hold ships nothing. The hold stays until
        // Phase 3's expiry worker; it is logged at Error here, and it is the accepted residual.
        persisted!.Status.Should().Be(OrderStatus.PartiallyConfirmed);
        Stock.CountCalls(FakeStockClient.ReleaseOperation).Should().Be(2, "every release was still attempted");
    }

    [Fact]
    public async Task APartiallyConfirmedOrderIsStillQueryableWithItsLines()
    {
        var (first, second, third) = ThreeProducts();

        Stock.ConfirmFailsWith = FakeStockClient.Failure.Refused;
        Stock.ConfirmFailsFromCall = 1;

        var problem = await ProblemAsync(await PlaceAsync(OrderWith((first, 1), (second, 1), (third, 1))));
        var orderId = OrderIdFrom(problem);

        var response = await Client.GetAsync($"/api/v1/orders/{orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await BodyAsync(response);

        body.Id.Should().Be(orderId);
        body.Status.Should().Be(nameof(OrderStatus.PartiallyConfirmed));
        body.Lines.Should().HaveCount(3);
    }

    [Fact]
    public async Task TheOrderIdInThe502BodyIsTheOrderThatWasWritten()
    {
        // A 502 with no identifying body leaves the client unable to tell "nothing was created" from
        // "something was created and is broken" — and POST /orders is not idempotent, so that
        // ambiguity is what turns one failure into a duplicate shipment.
        var (first, second, third) = ThreeProducts();

        Stock.ConfirmFailsWith = FakeStockClient.Failure.Refused;
        Stock.ConfirmFailsFromCall = 1;

        var problem = await ProblemAsync(await PlaceAsync(OrderWith((first, 1), (second, 1), (third, 1))));
        var orderId = OrderIdFrom(problem);

        (await ReadAllOrdersAsync()).Should().ContainSingle().Which.Id.Should().Be(orderId);
    }

    [Fact]
    public async Task NoOrderIsWrittenAsPendingWhateverHappens()
    {
        var (first, second, third) = ThreeProducts();

        Stock.ConfirmFailsWith = FakeStockClient.Failure.Fault;
        Stock.ConfirmFailsFromCall = 1;
        Stock.ListFails = true;

        await PlaceAsync(OrderWith((first, 1), (second, 1), (third, 1)));

        var orders = await ReadAllOrdersAsync();

        orders.Should().ContainSingle();
        orders.Should().OnlyContain(order => order.Status != OrderStatus.Pending);
    }

    private (Guid First, Guid Second, Guid Third) ThreeProducts() => (
        AddProduct(name: "Anchor", price: 4.00m),
        AddProduct(name: "Rope", price: 7.25m),
        AddProduct(name: "Chain", price: 15.00m));
}
