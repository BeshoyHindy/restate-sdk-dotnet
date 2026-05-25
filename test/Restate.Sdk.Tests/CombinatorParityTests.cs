using System.Buffers;
using System.Text.Json;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests;

/// <summary>
///     Exercises the four shared-core future combinators (All / Any / Race / AllSettled) against the
///     <em>real</em> base <see cref="Context" /> virtuals. <see cref="MockObjectContext" /> inherits
///     the combinators unchanged from <see cref="Context" /> (only the service-level
///     <see cref="MockContext" /> overrides them), so it is the production code path under test.
/// </summary>
public class CombinatorParityTests
{
    private static MockObjectContext NewContext() => new();

    private static DurableFuture<T> Succeeded<T>(T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, value);
        }

        var tcs = new TaskCompletionSource<CompletionResult>();
        tcs.SetResult(CompletionResult.Success(buffer.WrittenMemory));
        return new DurableFuture<T>(tcs, JsonSerializerOptions.Default);
    }

    private static DurableFuture<T> Failed<T>(string message = "boom")
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        tcs.SetResult(CompletionResult.Failure(500, message));
        return new DurableFuture<T>(tcs, JsonSerializerOptions.Default);
    }

    private static (DurableFuture<T> Future, TaskCompletionSource<CompletionResult> Tcs) Pending<T>()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        return (new DurableFuture<T>(tcs, JsonSerializerOptions.Default), tcs);
    }

    // ── All (AllSucceededOrFirstFailed) ──

    [Fact]
    public async Task All_AllSucceed_ReturnsResultsInDeclarationOrder()
    {
        var results = await NewContext().All(Succeeded(1), Succeeded(2), Succeeded(3));
        Assert.Equal([1, 2, 3], results);
    }

    [Fact]
    public async Task All_FirstFailure_ShortCircuits_WithoutWaitingPendingPeer()
    {
        // f2 never completes; All must still throw as soon as f1 fails (short-circuit),
        // not block on Task.WhenAll-style wait-for-everyone.
        var failing = Failed<int>("first-to-fail");
        var (neverCompletes, _) = Pending<int>();

        var ex = await Assert.ThrowsAsync<TerminalException>(
            async () => await NewContext().All(failing, neverCompletes));
        Assert.Contains("first-to-fail", ex.Message);
    }

    // ── Any (FirstSucceededOrAllFailed) ──

    [Fact]
    public async Task Any_ReturnsFirstSuccess_IgnoringEarlierFailure()
    {
        var result = await NewContext().Any(Failed<string>(), Succeeded("winner"));
        Assert.Equal("winner", result);
    }

    [Fact]
    public async Task Any_AllFail_ThrowsAggregateOfEveryFailure()
    {
        var ex = await Assert.ThrowsAsync<AggregateException>(
            async () => await NewContext().Any(Failed<int>("a"), Failed<int>("b")));
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.All(ex.InnerExceptions, e => Assert.IsType<TerminalException>(e));
    }

    [Fact]
    public async Task Any_EmptyArray_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await NewContext().Any(Array.Empty<IDurableFuture<int>>()));
    }

    // ── Race (FirstCompleted) ──

    [Fact]
    public async Task Race_FirstToSettle_Wins_EvenWhenItFails()
    {
        var (slow, _) = Pending<int>();
        // Failed future settles immediately and should win the race, surfacing its failure.
        await Assert.ThrowsAsync<TerminalException>(
            async () => await NewContext().Race(Failed<int>("lost-fast"), slow));
    }

    // ── AllSettled (AllCompleted) ──

    [Fact]
    public async Task AllSettled_ReportsEachOutcome_WithoutThrowing()
    {
        var settled = await NewContext().AllSettled(Succeeded(7), Failed<int>("nope"), Succeeded(9));

        Assert.Equal(3, settled.Length);

        Assert.True(settled[0].IsSuccess);
        Assert.Equal(7, settled[0].Value);
        Assert.Null(settled[0].Error);

        Assert.False(settled[1].IsSuccess);
        Assert.IsType<TerminalException>(settled[1].Error);

        Assert.True(settled[2].IsSuccess);
        Assert.Equal(9, settled[2].Value);
    }

    // ── MockContext (service-level) deterministic overrides ──

    [Fact]
    public async Task MockContext_Any_ReturnsFirstSuccessInOrder()
    {
        var ctx = new MockContext();
        var result = await ctx.Any(Failed<string>(), Succeeded("ok"));
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task MockContext_AllSettled_ReportsEachOutcome()
    {
        var ctx = new MockContext();
        var settled = await ctx.AllSettled(Succeeded(1), Failed<int>());

        Assert.True(settled[0].IsSuccess);
        Assert.False(settled[1].IsSuccess);
    }
}
