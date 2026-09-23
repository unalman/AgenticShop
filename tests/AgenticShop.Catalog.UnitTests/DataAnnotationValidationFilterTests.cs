using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AgenticShop.Catalog.Contracts;
using AgenticShop.Catalog.Validation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticShop.Catalog.UnitTests;

/// <summary>
/// The Catalog API has no nested request DTO yet, so these use synthetic contracts.
/// They exist because the filter is the reference implementation for Stock and Ordering,
/// and Ordering's create-order request will carry a collection of lines.
/// <see cref="Validator.TryValidateObject"/> does not cascade on its own — if that
/// regresses, nested DTOs stop validating silently and the failure surfaces as a 500.
/// </summary>
public class DataAnnotationValidationFilterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ValidContract_InvokesNext()
    {
        var (_, nextCalled) = await InvokeAsync(new ValidRequest("Espresso"));

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvalidTopLevelProperty_IsReportedAgainstThatMember()
    {
        var (result, nextCalled) = await InvokeAsync(new ValidRequest(null!));

        nextCalled.Should().BeFalse();
        (await ErrorsOf(result)).Should().ContainKey("Name");
    }

    [Fact]
    public async Task InvalidItemInANestedCollection_IsReportedWithItsIndexPath()
    {
        var request = new NestedOrderRequest(
            "buyer@example.com",
            [new OrderLineRequest(2, "AAA"), new OrderLineRequest(0, "BBB")]);

        var (result, _) = await InvokeAsync(request);

        // The second line is the invalid one; the path must say so rather than
        // blaming the collection as a whole.
        (await ErrorsOf(result)).Should().ContainKey("Lines[1].Quantity");
    }

    [Fact]
    public async Task SeveralInvalidItems_AreReportedSeparately()
    {
        var request = new NestedOrderRequest(
            "buyer@example.com",
            [new OrderLineRequest(0, "AAA"), new OrderLineRequest(-1, "")]);

        var errors = await ErrorsOf((await InvokeAsync(request)).Result);

        errors.Should().ContainKey("Lines[0].Quantity");
        errors.Should().ContainKey("Lines[1].Quantity");
        errors.Should().ContainKey("Lines[1].Code");
    }

    [Fact]
    public async Task DeeplyNestedObject_IsReportedWithADottedPath()
    {
        var request = new DeepRoot(new DeepMiddle(new DeepChild(99)));

        var errors = await ErrorsOf((await InvokeAsync(request)).Result);

        errors.Should().ContainKey("Middle.Child.Value");
    }

    [Fact]
    public async Task ACollectionNestedInsideACollectionItem_BuildsACombinedPath()
    {
        // Two collection levels with a named object between them. Reflecting over a List<T>
        // yields Count and Capacity rather than its contents, so a filter that recursed by
        // property alone would silently validate nothing below the first level.
        var request = new GroupedRequest(
        [
            new OrderLineGroup([new OrderLineRequest(1, "AAA")]),
            new OrderLineGroup([new OrderLineRequest(0, "BBB")])
        ]);

        var errors = await ErrorsOf((await InvokeAsync(request)).Result);

        errors.Should().ContainKey("Groups[1].Cells[0].Quantity");
    }

    [Fact]
    public async Task AJaggedCollection_BuildsAnIndexedPathAtEveryLevel()
    {
        var request = new JaggedRequest(
        [
            [new OrderLineRequest(1, "AAA")],
            [new OrderLineRequest(0, "BBB")]
        ]);

        var errors = await ErrorsOf((await InvokeAsync(request)).Result);

        errors.Should().ContainKey("Rows[1][0].Quantity");
    }

    [Fact]
    public async Task TopLevelAndNestedErrors_AreReportedTogether()
    {
        var request = new NestedOrderRequest("", [new OrderLineRequest(0, "AAA")]);

        var errors = await ErrorsOf((await InvokeAsync(request)).Result);

        errors.Should().ContainKey("CustomerEmail");
        errors.Should().ContainKey("Lines[0].Quantity");
    }

    [Fact]
    public async Task EmptyNestedCollection_IsReportedWithoutAnIndexPath()
    {
        var request = new NestedOrderRequest("buyer@example.com", []);

        var errors = await ErrorsOf((await InvokeAsync(request)).Result);

        errors.Should().ContainKey("Lines");
    }

    [Fact]
    public async Task NullNestedObject_IsSkippedAndNextIsInvoked()
    {
        var (_, nextCalled) = await InvokeAsync(new NullableNestedRequest(null));

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ClassLevelAttributeWithNoMemberNames_IsNotDropped()
    {
        var errors = await ErrorsOf((await InvokeAsync(new ClassLevelRequest("anything"))).Result);

        // ValidationResult reports no member names for a class-level attribute. It is
        // attached to the object's own path rather than silently discarded.
        errors.Should().ContainKey(nameof(ClassLevelRequest));
        errors[nameof(ClassLevelRequest)].Should().Contain("Object-level rule failed.");
    }

    [Fact]
    public async Task MultipleFailuresOnOneMember_AreAllReported()
    {
        // "abc" violates both the pattern and the minimum length. Deliberately avoids
        // [Required]: when a required value is missing the framework reports only that,
        // which would make this test pass without proving aggregation works.
        var errors = await ErrorsOf((await InvokeAsync(new MultiRuleRequest("abc"))).Result);

        errors["Value"].Should().Contain("Must be upper case.");
        errors["Value"].Should().Contain("Must be at least 5 characters.");
    }

    [Fact]
    public async Task ArgumentWithoutTheMarker_IsNotValidated()
    {
        // Documents the detection rule: the marker interface, not the namespace or the type
        // name. RequestContractCoverageTests is what stops a real DTO forgetting the marker.
        var (_, nextCalled) = await InvokeAsync(new UnmarkedRequest(null!));

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task NonContractArguments_AreSkipped()
    {
        var (_, nextCalled) = await InvokeAsync(
            42,
            Guid.NewGuid(),
            CancellationToken.None,
            "a plain string",
            new ValidRequest("Espresso"));

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task CycleInTheObjectGraph_DoesNotRecurseForever()
    {
        var parent = new CyclicRequest("ok") { Self = null };
        parent.Self = parent;

        var (_, nextCalled) = await InvokeAsync(parent);

        nextCalled.Should().BeTrue();
    }

    private static async Task<(object? Result, bool NextCalled)> InvokeAsync(params object?[] arguments)
    {
        var nextCalled = false;

        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };

        var result = await new DataAnnotationValidationFilter()
            .InvokeAsync(new TestFilterContext(arguments), next);

        return (result, nextCalled);
    }

    /// <summary>
    /// Executes the result and reads the wire body, so the tests assert the JSON a client
    /// actually receives rather than an internal result type.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string[]>> ErrorsOf(object? result)
    {
        result.Should().BeAssignableTo<IResult>();

        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            // ProblemHttpResult resolves ILoggerFactory and JsonOptions from the request
            // services, so a bare DefaultHttpContext is not enough.
            RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider()
        };
        context.Response.Body = body;

        await ((IResult)result!).ExecuteAsync(context);

        context.Response.ContentType.Should().Be("application/problem+json");

        body.Position = 0;
        var payload = await JsonSerializer.DeserializeAsync<ValidationProblemPayload>(body, JsonOptions);

        payload!.Status.Should().Be(400);

        return payload.Errors;
    }

    private sealed class TestFilterContext(IList<object?> arguments) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = new DefaultHttpContext();

        public override IList<object?> Arguments { get; } = arguments;

        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    private sealed record ValidationProblemPayload
    {
        public int? Status { get; init; }

        public string? Title { get; init; }

        public Dictionary<string, string[]> Errors { get; init; } = [];
    }

    internal sealed record ValidRequest(
        [property: Required]
        [property: StringLength(50, MinimumLength = 1)]
        string Name) : IRequestContract;

    internal sealed record OrderLineRequest(
        [property: Range(1, 1000)] int Quantity,
        [property: Required]
        [property: StringLength(10, MinimumLength = 1)]
        string Code);

    internal sealed record NestedOrderRequest(
        [property: Required] string CustomerEmail,
        [property: Required]
        [property: MinLength(1)]
        List<OrderLineRequest> Lines) : IRequestContract;

    internal sealed record DeepChild([property: Range(1, 10)] int Value);

    internal sealed record DeepMiddle(DeepChild Child);

    internal sealed record DeepRoot(DeepMiddle Middle) : IRequestContract;

    internal sealed record OrderLineGroup(
        [property: Required]
        [property: MinLength(1)]
        List<OrderLineRequest> Cells);

    internal sealed record GroupedRequest(List<OrderLineGroup> Groups) : IRequestContract;

    internal sealed record JaggedRequest(List<List<OrderLineRequest>> Rows) : IRequestContract;

    internal sealed record NullableNestedRequest(OrderLineRequest? Line) : IRequestContract;

    internal sealed record MultiRuleRequest(
        [property: RegularExpression("^[A-Z]+$", ErrorMessage = "Must be upper case.")]
        [property: StringLength(20, MinimumLength = 5, ErrorMessage = "Must be at least 5 characters.")]
        string Value) : IRequestContract;

    /// <summary>Deliberately omits <see cref="IRequestContract"/>.</summary>
    internal sealed record UnmarkedRequest([property: Required] string Name);

    internal sealed class CyclicRequest(string name) : IRequestContract
    {
        [Required]
        public string Name { get; } = name;

        public CyclicRequest? Self { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class AlwaysInvalidAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value) => false;
    }

    [AlwaysInvalid(ErrorMessage = "Object-level rule failed.")]
    internal sealed record ClassLevelRequest(string Name) : IRequestContract;
}
