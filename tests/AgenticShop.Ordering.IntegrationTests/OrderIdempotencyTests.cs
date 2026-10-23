using System.Net;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.IntegrationTests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Ordering.IntegrationTests;

/// <summary>
/// <c>POST /orders</c> under a repeated <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// <para>
/// The property that matters is not "the second request gets the same status" — it is <b>the second
/// request places nothing</b>. So the assertions go to the database rather than stopping at the
/// response: one order row, one key row, and one queued <c>OrderPlaced</c> no matter how many times
/// the key is presented. The last of those is the one that changed with the flip, and it is now the
/// sharpest of the three — a duplicate event is a duplicate reservation, and neither the order count
/// nor the status code would reveal it.
/// </para>
/// <para>
/// The claim's position in the placement sequence gets its own test, because it is the part that is
/// easy to get wrong. Claiming before resolve would burn the key on a basket the caller can still
/// fix; claiming after the write would leave a window in which two requests both persist an order.
/// <see cref="ABasketFailureDoesNotConsumeTheKey"/> is what pins it.
/// </para>
/// <para>
/// <b>What is no longer tested here, and why.</b> Two tests used to assert that a repeated key
/// replayed a recorded 409 (stock refused) or 502 (downstream fault). Neither status can be recorded
/// any more: Stock is not called during a placement, so it cannot refuse inside one, and the only
/// downstream that is called — Catalog — fails before the key is claimed at all. A refusal now arrives
/// asynchronously and settles the order as <c>Failed</c>, which
/// <see cref="StockOutcomeConsumerTests"/> covers against a real broker. The replay rule changed with
/// them: the status is derived from where the order actually is rather than replayed verbatim, so a
/// key whose order has settled replays a 200.
/// </para>
/// </remarks>
[Collection("Ordering API")]
public sealed class OrderIdempotencyTests(OrderingApiFixture fixture) : OrderApiTestBase(fixture)
{
    [Fact]
    public async Task ARepeatedKeyReplaysTheOrderAndPlacesNothingAgain()
    {
        var productId = AddProduct();
        var key = NewIdempotencyKey();

        var first = await PlaceAsync(OrderWith((productId, 2)), idempotencyKey: key);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var firstBody = await BodyAsync(first);

        var second = await PlaceAsync(OrderWith((productId, 2)), idempotencyKey: key);

        second.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            "a repeated key replays the recorded outcome, it does not report a conflict");

        var secondBody = await BodyAsync(second);
        secondBody.Id.Should().Be(firstBody.Id, "the same key means the same order");
        secondBody.OrderNumber.Should().Be(firstBody.OrderNumber);

        second.Headers.Location!.ToString().Should().Be($"/api/v1/orders/{firstBody.Id}");

        (await ReadAllOrdersAsync()).Should().HaveCount(1, "the second attempt placed nothing");
        (await ReadAllIdempotencyKeysAsync()).Should().HaveCount(1);

        // The assertion that matters most now. One order and one key are consistent with a second
        // event having been queued against the same order, and that would be a second reservation —
        // a real double-hold wearing the clothes of a correct replay.
        (await ReadAllOutboxMessagesAsync()).Should()
            .HaveCount(1, "one placement queues one OrderPlaced, however many times the key is presented");

        // Both answers are 202 because this fixture has no Stock on the other end of the broker, so
        // the order is still Pending when the replay arrives. That is exactly the case the derived
        // status is written for; StockOutcomeConsumerTests covers the settled half, where the same key
        // replays a 200.
        secondBody.Status.Should().Be(nameof(OrderStatus.Pending));
    }

    // Two tests lived here: ARepeatedKeyAfterAStockRefusalReplaysThe409WithTheSameOrderId and
    // ARepeatedKeyAfterADownstreamFailureReplaysThe502RatherThanA409. Both are gone rather than
    // weakened, because the states they replayed can no longer be recorded.
    //
    // The first made Stock refuse a line and asserted the 409 was replayed verbatim. Stock is not
    // called during a placement any more, so no refusal can happen inside one; a refusal is now an
    // asynchronous outcome that settles the order as Failed, covered by StockOutcomeConsumerTests.
    //
    // The second made the confirm phase fault and asserted the 502 was replayed rather than a 409, and
    // it existed to justify storing the status code instead of deriving it from Order.Status — because
    // a Failed order could have come from either. That ambiguity is gone with the confirm phase: after
    // the claim is taken the only outcomes are "accepted" and, later, whatever the consumer decides.
    // The stored code is now a constant 202, which is why the replay derives its status from the
    // order's current state instead.

    [Fact]
    public async Task ABasketFailureDoesNotConsumeTheKey()
    {
        var usd = AddProduct(name: "Widget", currency: "USD");
        var eur = AddProduct(name: "Gadget", currency: "EUR");
        var key = NewIdempotencyKey();

        // Mixed currency is caught during resolve, which runs before the claim.
        var rejected = await PlaceAsync(OrderWith((usd, 1), (eur, 1)), idempotencyKey: key);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ReadAllIdempotencyKeysAsync()).Should()
            .BeEmpty("a request that never reached a side effect must not burn the caller's key");

        // The same key, with the basket fixed, places normally.
        var retried = await PlaceAsync(OrderWith((usd, 1)), idempotencyKey: key);

        retried.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            "the key is still unused, so the corrected request is a first attempt rather than a replay");

        (await ReadAllOrdersAsync()).Should().HaveCount(1);
        (await ReadAllIdempotencyKeysAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task AnUnavailableProductDoesNotConsumeTheKeyEither()
    {
        var inactive = AddProduct(isActive: false);
        var active = AddProduct(name: "Widget");
        var key = NewIdempotencyKey();

        var rejected = await PlaceAsync(OrderWith((inactive, 1)), idempotencyKey: key);
        rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAllIdempotencyKeysAsync()).Should().BeEmpty();

        var retried = await PlaceAsync(OrderWith((active, 1)), idempotencyKey: key);

        retried.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await ReadAllOrdersAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task AKeyClaimedByAnAttemptThatNeverCompletedIsA409()
    {
        var productId = AddProduct();
        const string strandedKey = "stranded-claim";

        // The state a crashed process leaves behind: claimed, never completed. Written directly
        // because no request path can produce it and still return.
        using (var scope = Fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            db.IdempotencyKeys.Add(OrderIdempotencyKey.Create(strandedKey, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var response = await PlaceAsync(OrderWith((productId, 1)), idempotencyKey: strandedKey);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await ProblemAsync(response);
        problem.Title.Should().Be("Conflict.");
        problem.Extensions.Should().NotContainKey("orderId", "no order exists for a claim that never completed");

        // Fail closed: the request was refused, so nothing was placed and no stock was held.
        (await ReadAllOrdersAsync()).Should().BeEmpty();
        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TheKeyIsNotEchoedIntoThe409Body()
    {
        var productId = AddProduct();
        var key = NewIdempotencyKey();

        using (var scope = Fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            db.IdempotencyKeys.Add(OrderIdempotencyKey.Create(key, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var response = await PlaceAsync(OrderWith((productId, 1)), idempotencyKey: key);

        // The key is untrusted input. It belongs in the log line, where the correlation id ties it to
        // this response, and not in a body that may be pasted into a ticket.
        (await response.Content.ReadAsStringAsync()).Should().NotContain(key);
    }

    [Fact]
    public async Task AMissingIdempotencyKeyIsA400()
    {
        var productId = AddProduct();

        var response = await PlaceWithoutIdempotencyKeyAsync(OrderWith((productId, 1)));

        // A required header that is absent is a binding failure, so the shared BadHttpRequestException
        // arm answers it — the same path that already handled malformed JSON.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ProblemAsync(response);
        problem.Extensions.Should().NotContainKey("orderId");

        (await ReadAllOrdersAsync()).Should().BeEmpty();
        (await ReadAllIdempotencyKeysAsync()).Should().BeEmpty();
        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(65, "over the 64-character bound")]
    [InlineData(200, "far over the bound")]
    public async Task AnOverLongIdempotencyKeyIsA400(int length, string because)
    {
        var productId = AddProduct();

        var response = await PlaceAsync(
            OrderWith((productId, 1)),
            idempotencyKey: new string('a', length));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        (await ReadAllOrdersAsync()).Should().BeEmpty();
        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("semi;colon")]
    [InlineData("quote\"mark")]
    [InlineData("non-ascii-é")]
    public async Task AnIdempotencyKeyWithUnsafeCharactersIsA400(string key)
    {
        var productId = AddProduct();

        var response = await PlaceAsync(OrderWith((productId, 1)), idempotencyKey: key);

        // Rejected rather than sanitised: a key is a caller-chosen identifier, so silently rewriting
        // it would make two different keys collide. The bound exists because the value reaches a
        // primary key column and the log.
        //
        // A newline is not in this list because it cannot get here: HttpClient throws
        // FormatException when asked to build a header containing one, so the transport rejects it
        // before there is a request. OrderIdempotencyKeyTests covers that character directly.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAllOrdersAsync()).Should().BeEmpty();
        (await ReadAllOutboxMessagesAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("d94f8a1e6c2b4f07a5e3c1b9d7f5a3e1")]
    [InlineData("basket-4711")]
    [InlineData("order_2026.10.03_001")]
    public async Task TheAcceptedKeyShapesAllPlace(string key)
    {
        var productId = AddProduct();

        var response = await PlaceAsync(OrderWith((productId, 1)), idempotencyKey: key);

        response.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            "a GUID, a prefixed token and a dotted date form are all ordinary caller choices");
    }

    [Fact]
    public async Task ConcurrentRequestsWithTheSameKeyPlaceExactlyOneOrder()
    {
        var productId = AddProduct(price: 10.00m);
        var key = NewIdempotencyKey();

        const int attempts = 12;

        var responses = await Task.WhenAll(
            Enumerable.Range(0, attempts)
                .Select(_ => PlaceAsync(OrderWith((productId, 1)), idempotencyKey: key)));

        var statuses = responses.Select(response => response.StatusCode).ToList();

        statuses.Should().NotContain(
            status => (int)status >= 500,
            "a lost race for the key is a 409 or a replay, never a server fault");

        (await ReadAllOrdersAsync()).Should().HaveCount(1, "exactly one request won the claim");
        (await ReadAllIdempotencyKeysAsync()).Should().HaveCount(1);

        // The invariant that makes the key worth having, and the sharpest assertion in this test:
        // one line, so one queued event, however many requests presented the key. Without the claim
        // the losers would each have written an order *and* an OrderPlaced, and twelve events for one
        // basket is twelve reservations — a far worse outcome than a duplicate row, because nothing
        // downstream can tell the copies apart.
        //
        // An exact count in a parallel test, which the house rule normally forbids — safe here because
        // the count does not depend on interleaving. Exactly one request can insert the key row, and
        // only that request ever reaches the write. The overselling tests cannot say the same, because
        // there the number of winners genuinely depends on who reaches xmin first.
        (await ReadAllOutboxMessagesAsync()).Should().HaveCount(1);

        statuses.Should().OnlyContain(
            status => status == HttpStatusCode.Accepted || status == HttpStatusCode.Conflict,
            "every attempt either won the claim, replayed it, or was refused while it was still open");

        statuses.Should().Contain(HttpStatusCode.Accepted, "the claim winner placed a real order");

        // Most of the 202s are replays, not placements — which is the point, and the reason the
        // counts above are 1 rather than 12. What makes them all legitimate is that they name the same
        // order: a replay that returned a different id would be a second placement wearing a 202.
        var accepted = responses.Where(response => response.StatusCode == HttpStatusCode.Accepted).ToList();
        var acceptedIds = new HashSet<Guid>();

        foreach (var response in accepted)
        {
            var body = await BodyAsync(response);

            acceptedIds.Add(body.Id);

            response.Headers.Location!.ToString().Should()
                .Be($"/api/v1/orders/{body.Id}", "a 202 carries the Location of the order to poll");
        }

        acceptedIds.Should().HaveCount(1, "every 202, first attempt or replay, names the same order");

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
