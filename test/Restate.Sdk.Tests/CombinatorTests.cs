using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests;

public class CombinatorTests
{
    private static DurableFuture<T> CompletedFuture<T>(T value)
    {
        return DurableFuture<T>.Completed(value);
    }

    private static (DurableFuture<T> Future, TaskCompletionSource<CompletionResult> Tcs) PendingFuture<T>()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        return (new DurableFuture<T>(tcs, JsonSerializerOptions.Default), tcs);
    }

    private static CompletionResult Serialized<T>(T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        JsonSerializer.Serialize(
            new Utf8JsonWriter(buffer), value);
        return CompletionResult.Success(buffer.WrittenMemory);
    }

    private static void Complete<T>(TaskCompletionSource<CompletionResult> tcs, T value)
    {
        tcs.SetResult(Serialized(value));
    }

    // ── All ──

    [Fact]
    public async Task All_EmptyArray_ReturnsEmpty()
    {
        var ctx = new TestCombinatorContext();
        var results = await ctx.All(Array.Empty<IDurableFuture<int>>());
        Assert.Empty(results);
    }

    [Fact]
    public async Task All_AllCompleted_ReturnsInOrder()
    {
        var ctx = new TestCombinatorContext();
        var f1 = CompletedFuture(1);
        var f2 = CompletedFuture(2);
        var f3 = CompletedFuture(3);

        var results = await ctx.All(f1, f2, f3);

        Assert.Equal([1, 2, 3], results);
    }

    [Fact]
    public async Task All_PendingFutures_WaitsForAll()
    {
        var ctx = new TestCombinatorContext();
        var (f1, tcs1) = PendingFuture<int>();
        var (f2, tcs2) = PendingFuture<int>();

        var task = ctx.All(f1, f2).AsTask();

        Assert.False(task.IsCompleted);

        Complete(tcs1, 10);
        Assert.False(task.IsCompleted);

        Complete(tcs2, 20);
        var results = await task;

        Assert.Equal([10, 20], results);
    }

    [Fact]
    public async Task All_OneFailure_ThrowsFirst()
    {
        var ctx = new TestCombinatorContext();
        var f1 = CompletedFuture(1);
        var (f2, tcs2) = PendingFuture<int>();

        tcs2.SetResult(CompletionResult.Failure(500, "boom"));

        await Assert.ThrowsAsync<TerminalException>(() => ctx.All(f1, f2).AsTask());
    }

    [Fact]
    public async Task All_FirstInputFailed_ThrowsWhileTheSecondIsPending()
    {
        var ctx = new BareContext();
        var (failed, failedTcs) = PendingFuture<int>();
        var (neverCompletes, _) = PendingFuture<int>();

        failedTcs.SetResult(CompletionResult.Failure(500, "boom"));

        var failure = await Assert.ThrowsAsync<TerminalException>(
            () => ctx.All(failed, neverCompletes).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("boom", failure.Message);
    }

    [Fact]
    public async Task All_SecondInputFailed_ThrowsWhileTheFirstIsPending()
    {
        var ctx = new BareContext();
        var (neverCompletes, _) = PendingFuture<int>();
        var (failed, failedTcs) = PendingFuture<int>();

        failedTcs.SetResult(CompletionResult.Failure(500, "boom"));

        var failure = await Assert.ThrowsAsync<TerminalException>(
            () => ctx.All(neverCompletes, failed).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("boom", failure.Message);
    }

    // ── Race ──

    [Fact]
    public async Task Race_FirstCompleted_Wins()
    {
        var ctx = new TestCombinatorContext();
        var (f1, tcs1) = PendingFuture<string>();
        var (f2, tcs2) = PendingFuture<string>();

        var task = ctx.Race(f1, f2).AsTask();

        Complete(tcs2, "second");
        var result = await task;

        Assert.Equal("second", result);
    }

    [Fact]
    public async Task Race_PreCompleted_ReturnsImmediately()
    {
        var ctx = new TestCombinatorContext();
        var f1 = CompletedFuture("fast");
        var (f2, _) = PendingFuture<string>();

        var result = await ctx.Race(f1, f2);

        Assert.Equal("fast", result);
    }

    [Fact]
    public async Task Race_SettledFutures_PicksTheEarlierArrival()
    {
        // Replay settles every journaled completion before the handler runs, so the race is
        // decided by the order the runtime delivered the results, not by argument order.
        var manager = new CompletionManager<int>();
        manager.TryComplete(2, Serialized("second"));
        manager.TryComplete(1, Serialized("first"));

        var ctx = new BareContext();
        var f1 = new DurableFuture<string>(manager.GetOrRegister(1), JsonSerializerOptions.Default);
        var f2 = new DurableFuture<string>(manager.GetOrRegister(2), JsonSerializerOptions.Default);

        Assert.Equal("second", await ctx.Race(f1, f2));
    }

    [Fact]
    public async Task WaitAll_SettledAndPendingFutures_YieldsSettledInArrivalOrder()
    {
        var manager = new CompletionManager<int>();
        manager.TryComplete(2, Serialized(20));
        manager.TryComplete(3, Serialized(30));

        var ctx = new BareContext();
        var settledSecond = new DurableFuture<int>(manager.GetOrRegister(2), JsonSerializerOptions.Default);
        var stillWaiting = new DurableFuture<int>(manager.GetOrRegister(1), JsonSerializerOptions.Default);
        var settledThird = new DurableFuture<int>(manager.GetOrRegister(3), JsonSerializerOptions.Default);

        var yielded = new List<IDurableFuture>();
        var enumeration = Task.Run(async () =>
        {
            await foreach (var (future, _) in ctx.WaitAll(settledThird, stillWaiting, settledSecond))
                yielded.Add(future);
        });

        manager.TryComplete(1, Serialized(10));
        await enumeration;

        // The two journaled futures come in delivery order; the one that was still waiting is
        // yielded when its result arrives.
        Assert.Equal([settledSecond, settledThird, stillWaiting], yielded);
    }

    // ── WaitAll ──

    [Fact]
    public async Task WaitAll_YieldsInCompletionOrder()
    {
        var ctx = new TestCombinatorContext();

        var tcs1 = new TaskCompletionSource<CompletionResult>();
        var tcs2 = new TaskCompletionSource<CompletionResult>();
        var tcs3 = new TaskCompletionSource<CompletionResult>();

        var f1 = new DurableFuture<int>(tcs1, JsonSerializerOptions.Default);
        var f2 = new DurableFuture<int>(tcs2, JsonSerializerOptions.Default);
        var f3 = new DurableFuture<int>(tcs3, JsonSerializerOptions.Default);

        // Complete in reverse order: 3, 1, 2
        Complete(tcs3, 30);
        Complete(tcs1, 10);

        var completionOrder = new List<IDurableFuture>();
        var errors = new List<Exception?>();

        // Start enumeration in background
        var enumTask = Task.Run(async () =>
        {
            await foreach (var (future, error) in ctx.WaitAll(f1, f2, f3))
            {
                completionOrder.Add(future);
                errors.Add(error);
            }
        });

        // Give it a moment to process the already-completed ones
        await Task.Delay(50);

        // Complete the last one
        Complete(tcs2, 20);

        await enumTask;

        Assert.Equal(3, completionOrder.Count);
        Assert.All(errors, e => Assert.Null(e));
    }

    [Fact]
    public async Task WaitAll_FaultedFuture_YieldsError()
    {
        var ctx = new TestCombinatorContext();

        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new DurableFuture<int>(tcs, JsonSerializerOptions.Default);

        tcs.SetResult(CompletionResult.Failure(500, "oops"));

        var results = new List<(IDurableFuture, Exception?)>();
        await foreach (var item in ctx.WaitAll(future)) results.Add(item);

        Assert.Single(results);
        Assert.NotNull(results[0].Item2);
        Assert.IsType<TerminalException>(results[0].Item2);
    }

    // ── Any ──

    [Fact]
    public async Task Any_FirstSuccessful_Wins()
    {
        var ctx = new BareContext();
        var (f1, _) = PendingFuture<string>();
        var f2 = CompletedFuture("winner");

        var result = await ctx.Any(f1, f2);

        Assert.Equal("winner", result);
    }

    [Fact]
    public async Task Any_IgnoresFailure_ReturnsLaterSuccess()
    {
        var ctx = new BareContext();
        var (f1, tcs1) = PendingFuture<int>();
        var (f2, tcs2) = PendingFuture<int>();

        tcs1.SetResult(CompletionResult.Failure(500, "boom"));
        var task = ctx.Any(f1, f2).AsTask();

        Assert.False(task.IsCompleted);

        Complete(tcs2, 42);
        var result = await task;

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Any_AllFailed_ThrowsAggregateException()
    {
        var ctx = new BareContext();
        var (f1, tcs1) = PendingFuture<int>();
        var (f2, tcs2) = PendingFuture<int>();

        tcs1.SetResult(CompletionResult.Failure(500, "first"));
        tcs2.SetResult(CompletionResult.Failure(500, "second"));

        var ex = await Assert.ThrowsAsync<AggregateException>(() => ctx.Any(f1, f2).AsTask());

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.All(ex.InnerExceptions, inner => Assert.IsType<TerminalException>(inner));
    }

    [Fact]
    public async Task Any_AllFailed_InnerExceptionsInInputOrder()
    {
        var ctx = new BareContext();
        var (f1, tcs1) = PendingFuture<int>();
        var (f2, tcs2) = PendingFuture<int>();

        var task = ctx.Any(f1, f2).AsTask();

        // Fail in reverse input order: the aggregate must still list failures in input order,
        // matching JavaScript's AggregateError.errors ordering.
        tcs2.SetResult(CompletionResult.Failure(500, "second"));
        tcs1.SetResult(CompletionResult.Failure(500, "first"));

        var ex = await Assert.ThrowsAsync<AggregateException>(() => task);

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Equal("first", ex.InnerExceptions[0].Message);
        Assert.Equal("second", ex.InnerExceptions[1].Message);
    }

    [Fact]
    public async Task Any_EmptyInput_ThrowsAggregateException()
    {
        var ctx = new BareContext();

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => ctx.Any(Array.Empty<IDurableFuture<int>>()).AsTask());

        Assert.Empty(ex.InnerExceptions);
    }

    [Fact]
    public async Task Any_CanceledFuture_CountsAsFailure()
    {
        // Invocation abort cancels pending completions (CompletionManager.CancelAll),
        // so canceled futures must aggregate like failures, in input order.
        var ctx = new BareContext();
        var (f1, tcs1) = PendingFuture<int>();
        var (f2, tcs2) = PendingFuture<int>();

        var task = ctx.Any(f1, f2).AsTask();

        tcs1.SetResult(CompletionResult.Failure(500, "boom"));
        tcs2.SetCanceled();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => task);

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.IsType<TerminalException>(ex.InnerExceptions[0]);
        Assert.IsType<TaskCanceledException>(ex.InnerExceptions[1]);
    }

    // ── AllSettled ──

    [Fact]
    public async Task AllSettled_AllSuccessful_ReturnsValuesInOrder()
    {
        var ctx = new BareContext();
        var f1 = CompletedFuture(1);
        var f2 = CompletedFuture(2);
        var f3 = CompletedFuture(3);

        var results = await ctx.AllSettled(f1, f2, f3);

        Assert.Equal(3, results.Length);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal([1, 2, 3], results.Select(r => r.Value));
    }

    [Fact]
    public async Task AllSettled_MixedOutcomes_ReportsEachResult()
    {
        var ctx = new BareContext();
        var f1 = CompletedFuture(10);
        var (f2, tcs2) = PendingFuture<int>();

        tcs2.SetResult(CompletionResult.Failure(500, "boom"));

        var results = await ctx.AllSettled(f1, f2);

        Assert.True(results[0].IsSuccess);
        Assert.Equal(10, results[0].Value);
        Assert.Null(results[0].Error);

        Assert.False(results[1].IsSuccess);
        Assert.Equal(default, results[1].Value);
        Assert.IsType<TerminalException>(results[1].Error);
    }

    [Fact]
    public async Task AllSettled_EmptyInput_ReturnsEmpty()
    {
        var ctx = new BareContext();

        var results = await ctx.AllSettled(Array.Empty<IDurableFuture<int>>());

        Assert.Empty(results);
    }

    [Fact]
    public async Task AllSettled_PendingFutures_WaitsForAll()
    {
        var ctx = new BareContext();
        var (f1, tcs1) = PendingFuture<int>();
        var (f2, tcs2) = PendingFuture<int>();

        var task = ctx.AllSettled(f1, f2).AsTask();

        Assert.False(task.IsCompleted);

        Complete(tcs1, 1);
        Assert.False(task.IsCompleted);

        tcs2.SetResult(CompletionResult.Failure(500, "late failure"));
        var results = await task;

        Assert.True(results[0].IsSuccess);
        Assert.False(results[1].IsSuccess);
    }

    [Fact]
    public async Task AllSettled_CanceledFuture_ReportsFailureWithoutThrowing()
    {
        // Invocation abort cancels pending completions (CompletionManager.CancelAll);
        // AllSettled must settle the canceled future as a failure instead of throwing.
        var ctx = new BareContext();
        var f1 = CompletedFuture(1);
        var (f2, tcs2) = PendingFuture<int>();

        tcs2.SetCanceled();

        var results = await ctx.AllSettled(f1, f2);

        Assert.True(results[0].IsSuccess);
        Assert.False(results[1].IsSuccess);
        Assert.IsType<TaskCanceledException>(results[1].Error);
    }

    // ── Suspension propagation ──
    //
    // When the input stream closes, pending completions are poisoned with SuspensionException.
    // Combinators must propagate it (so the invocation suspends) instead of settling poisoned
    // futures as failures: the journaled operations WILL complete on a later attempt.

    [Fact]
    public async Task Any_PoisonedFuture_PropagatesSuspension()
    {
        var ctx = new BareContext();
        var (f1, tcs1) = PendingFuture<int>();
        var (f2, tcs2) = PendingFuture<int>();

        var task = ctx.Any(f1, f2).AsTask();

        tcs1.SetResult(CompletionResult.Failure(500, "boom"));
        tcs2.SetException(new SuspensionException());

        await Assert.ThrowsAsync<SuspensionException>(() => task);
    }

    [Fact]
    public async Task Any_PoisonedFuture_StillPrefersSuccess()
    {
        var ctx = new BareContext();
        var (f1, tcs1) = PendingFuture<int>();
        var (f2, tcs2) = PendingFuture<int>();

        tcs1.SetException(new SuspensionException());
        Complete(tcs2, 42);

        var result = await ctx.Any(f1, f2);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task All_PoisonedFuture_PropagatesSuspension()
    {
        var ctx = new BareContext();
        var f1 = CompletedFuture(1);
        var (f2, tcs2) = PendingFuture<int>();

        tcs2.SetException(new SuspensionException());

        await Assert.ThrowsAsync<SuspensionException>(
            () => ctx.All(f1, f2).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task All_PoisonedFuture_DoesNotMaskADeliveredFailure()
    {
        var ctx = new BareContext();
        var (poisoned, poisonedTcs) = PendingFuture<int>();
        var (failed, failedTcs) = PendingFuture<int>();

        poisonedTcs.SetException(new SuspensionException());
        failedTcs.SetResult(CompletionResult.Failure(500, "boom"));

        var failure = await Assert.ThrowsAsync<TerminalException>(
            () => ctx.All(poisoned, failed).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal("boom", failure.Message);
    }

    [Fact]
    public async Task AllSettled_PoisonedFuture_PropagatesSuspension()
    {
        var ctx = new BareContext();
        var f1 = CompletedFuture(1);
        var (f2, tcs2) = PendingFuture<int>();

        tcs2.SetException(new SuspensionException());

        await Assert.ThrowsAsync<SuspensionException>(() => ctx.AllSettled(f1, f2).AsTask());
    }

    [Fact]
    public async Task WaitAll_PoisonedFuture_PropagatesSuspension()
    {
        var ctx = new BareContext();
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new DurableFuture<int>(tcs, JsonSerializerOptions.Default);

        tcs.SetException(new SuspensionException());

        await Assert.ThrowsAsync<SuspensionException>(async () =>
        {
            await foreach (var _ in ctx.WaitAll(future))
            {
            }
        });
    }

    // ── SettledResult ──

    [Fact]
    public void SettledResult_Failure_NullError_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SettledResult<int>.Failure(null!));
    }

    [Fact]
    public void SettledResult_FailureBranch_ErrorFlowsAsNonNull()
    {
        var result = SettledResult<int>.Failure(new InvalidOperationException("boom"));

        // [MemberNotNullWhen(false, nameof(Error))]: no null-forgiving operator needed here.
        if (!result.IsSuccess)
            Assert.Equal("boom", result.Error.Message);
        else
            Assert.Fail("Expected a failed settlement");
    }

    // ── MockContext Any / AllSettled ──

    [Fact]
    public async Task MockContext_Any_ReturnsFirstSuccessfulInOrder()
    {
        var ctx = new MockContext();
        var failing = ctx.RunAsync<string>("fail",
            () => Task.FromException<string>(new InvalidOperationException("boom")));
        var succeeding = ctx.RunAsync("ok", () => Task.FromResult("value"));

        var result = await ctx.Any(failing, succeeding);

        Assert.Equal("value", result);
    }

    [Fact]
    public async Task MockContext_Any_AllFailed_ThrowsAggregateException()
    {
        var ctx = new MockContext();
        var f1 = ctx.RunAsync<int>("a", () => Task.FromException<int>(new InvalidOperationException("first")));
        var f2 = ctx.RunAsync<int>("b", () => Task.FromException<int>(new InvalidOperationException("second")));

        var ex = await Assert.ThrowsAsync<AggregateException>(() => ctx.Any(f1, f2).AsTask());

        Assert.Equal(2, ex.InnerExceptions.Count);
    }

    [Fact]
    public async Task MockContext_AllSettled_CapturesFailures()
    {
        var ctx = new MockContext();
        var f1 = ctx.RunAsync("ok", () => Task.FromResult(7));
        var f2 = ctx.RunAsync<int>("fail", () => Task.FromException<int>(new InvalidOperationException("boom")));

        var results = await ctx.AllSettled(f1, f2);

        Assert.True(results[0].IsSuccess);
        Assert.Equal(7, results[0].Value);
        Assert.False(results[1].IsSuccess);
        Assert.IsType<InvalidOperationException>(results[1].Error);
    }

    // ── Lazy futures shared between combinators ──
    //
    // A lazy future holds the state machine's ValueTask, and every combinator it is handed to
    // calls GetResult() again. SingleUseValueTaskSource enforces the ValueTask contract (one
    // consumption only), so these fail unless the future preserved its ValueTask.

    [Fact]
    public async Task LazyRunFuture_InTwoCombinators_ResolvesInBoth()
    {
        var ctx = new BareContext();
        var source = new SingleUseValueTaskSource<(TaskCompletionSource<CompletionResult> Tcs, Payload Result)>();
        var future = new LazyRunFuture<Payload>(source.ValueTask);

        var all = ctx.All(future);
        var race = ctx.Race(future);

        var produced = new Payload(7);
        source.SetResult((new TaskCompletionSource<CompletionResult>(), produced));

        // Both combinators hand back the one object the side effect produced.
        Assert.Same(produced, Assert.Single(await all));
        Assert.Same(produced, await race);
    }

    [Fact]
    public async Task LazyNotificationFuture_InTwoCombinators_ResolvesInBoth()
    {
        var ctx = new BareContext();
        var tcs = new TaskCompletionSource<CompletionResult>();
        var source = new SingleUseValueTaskSource<TaskCompletionSource<CompletionResult>>();
        var decoder = new CountingInt32Converter();
        var future = new LazyNotificationFuture<int>(source.ValueTask, WithConverter(decoder));

        var all = ctx.All<int>(future);
        var settled = ctx.AllSettled<int>(future);

        source.SetResult(tcs);
        Complete(tcs, 11);

        var allResults = await all;
        Assert.Equal([11], allResults);
        var settledResult = Assert.Single(await settled);
        Assert.True(settledResult.IsSuccess);
        Assert.Equal(11, settledResult.Value);

        // The payload is decoded on the first combinator's await and kept for the second.
        Assert.Equal(1, decoder.Reads);
    }

    [Fact]
    public async Task LazyTimerFuture_InTwoCombinators_ResolvesInBoth()
    {
        var ctx = new BareContext();
        var tcs = new TaskCompletionSource<CompletionResult>();
        var source = new SingleUseValueTaskSource<TaskCompletionSource<CompletionResult>>();
        var future = new LazyTimerFuture(source.ValueTask);

        source.SetResult(tcs);
        tcs.SetResult(CompletionResult.Success(ReadOnlyMemory<byte>.Empty));

        var first = new List<(IDurableFuture Future, Exception? Error)>();
        await foreach (var item in ctx.WaitAll(future)) first.Add(item);

        var second = new List<(IDurableFuture Future, Exception? Error)>();
        await foreach (var item in ctx.WaitAll(future)) second.Add(item);

        Assert.Null(Assert.Single(first).Error);
        Assert.Null(Assert.Single(second).Error);
    }

    // ── Memoized decoded results ──
    //
    // A future decodes the runtime's delivery on the first await and keeps the outcome, so a
    // handler that awaits it again, or a combinator it is handed to afterwards, observes the
    // same value and the same failure.

    [Fact]
    public async Task DurableFuture_AwaitedTwice_DecodesOnce()
    {
        var decoder = new CountingInt32Converter();
        var manager = new CompletionManager<int>();
        manager.TryComplete(1, Serialized(7));

        var future = new DurableFuture<int>(manager.GetOrRegister(1), WithConverter(decoder));

        Assert.Equal(7, await future.GetResult());
        Assert.Equal(7, await future.GetResult());
        Assert.Equal(1, decoder.Reads);
    }

    [Fact]
    public async Task DurableFuture_AwaitedTwice_ReturnsTheSameValue()
    {
        var manager = new CompletionManager<int>();
        manager.TryComplete(1, Serialized(new Payload(7)));

        var future = new DurableFuture<Payload>(manager.GetOrRegister(1), JsonSerializerOptions.Default);

        var first = await future.GetResult();
        var second = await future.GetResult();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task DurableFuture_PayloadCannotBeDeserialized_RethrowsTheSameException()
    {
        var manager = new CompletionManager<int>();
        manager.TryComplete(1, Serialized("not-a-number"));

        var future = new DurableFuture<int>(manager.GetOrRegister(1), JsonSerializerOptions.Default);

        var first = await Assert.ThrowsAsync<JsonException>(() => future.GetResult().AsTask());
        var second = await Assert.ThrowsAsync<JsonException>(() => future.GetResult().AsTask());

        Assert.Same(first, second);
    }

    [Fact]
    public async Task VoidDurableFuture_DeliveredFailure_RethrowsTheSameException()
    {
        // Delivered as a failed result rather than a faulted wait: the TerminalException it
        // stands for is built on every read, so only a kept outcome makes the two awaits agree.
        var tcs = new TaskCompletionSource<CompletionResult>();
        tcs.SetResult(CompletionResult.Failure(500, "boom"));

        var future = new VoidDurableFuture(tcs);

        var first = await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());
        var second = await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());

        Assert.Same(first, second);
    }

    [Fact]
    public async Task AllSettled_FailedInput_ReportsTheExceptionTheAwaitThrows()
    {
        var ctx = new BareContext();
        var (failed, tcs) = PendingFuture<int>();

        tcs.SetResult(CompletionResult.Failure(500, "boom"));

        var settled = Assert.Single(await ctx.AllSettled(failed));
        var caught = await Assert.ThrowsAsync<TerminalException>(() => failed.GetResult().AsTask());

        // One failed delivery stands for one exception object: what the combinator reported is
        // what awaiting the input throws, so a handler can compare the two.
        Assert.Same(caught, settled.Error);
    }

    // ── Lazy, arrival-ordered combinators ──
    //
    // The combinators park on the runtime's deliveries, not on decoded values: the winner is the
    // branch the runtime delivered first — live and on replay alike — and a branch a combinator
    // does not hand back is never deserialized.

    [Fact]
    public async Task Race_LiveAndReplayedDeliveries_PickTheEarlierDelivery()
    {
        Assert.Equal("second", await RaceDeliveries(deliveredBeforeTheCall: false));
        Assert.Equal("second", await RaceDeliveries(deliveredBeforeTheCall: true));
    }

    [Fact]
    public async Task Any_LiveAndReplayedDeliveries_PickTheEarlierDelivery()
    {
        Assert.Equal("second", await AnyDeliveries(deliveredBeforeTheCall: false));
        Assert.Equal("second", await AnyDeliveries(deliveredBeforeTheCall: true));
    }

    [Fact]
    public async Task WaitAll_LiveAndReplayedDeliveries_YieldInDeliveryOrder()
    {
        var live = await WaitAllDeliveries(deliveredBeforeTheCall: false);
        var replayed = await WaitAllDeliveries(deliveredBeforeTheCall: true);

        Assert.Equal(["second", "first"], live);
        Assert.Equal(["second", "first"], replayed);
    }

    [Fact]
    public async Task Race_BackToBackLiveDeliveries_AlwaysPicksTheEarlierDelivery()
    {
        await AssertEarlierLiveDeliveryWins((ctx, first, second) => ctx.Race(first, second).AsTask());
    }

    [Fact]
    public async Task Any_BackToBackLiveDeliveries_AlwaysPicksTheEarlierDelivery()
    {
        await AssertEarlierLiveDeliveryWins((ctx, first, second) => ctx.Any(first, second).AsTask());
    }

    [Fact]
    public async Task Race_DeserializesOnlyTheBranchItHandsBack()
    {
        var decoder = new CountingInt32Converter();
        var (ctx, winner, loser) = TwoDeliveries(decoder);

        Assert.Equal(1, await ctx.Race(winner, loser));
        Assert.Equal(1, decoder.Reads);
    }

    [Fact]
    public async Task Any_DeserializesOnlyTheBranchItHandsBack()
    {
        var decoder = new CountingInt32Converter();
        var (ctx, winner, loser) = TwoDeliveries(decoder);

        Assert.Equal(1, await ctx.Any(winner, loser));
        Assert.Equal(1, decoder.Reads);
    }

    [Fact]
    public async Task All_DeserializesEachInputOnce()
    {
        var decoder = new CountingInt32Converter();
        var (ctx, first, second) = TwoDeliveries(decoder);

        var results = await ctx.All(first, second);

        Assert.Equal([1, 2], results);
        Assert.Equal(2, decoder.Reads);
    }

    [Fact]
    public async Task AllSettled_DeserializesEachInputOnce()
    {
        var decoder = new CountingInt32Converter();
        var (ctx, first, second) = TwoDeliveries(decoder);

        var results = await ctx.AllSettled(first, second);

        Assert.Equal([1, 2], results.Select(r => r.Value));
        Assert.Equal(2, decoder.Reads);
    }

    [Fact]
    public async Task WaitAll_DeserializesEachInputOnce()
    {
        var decoder = new CountingInt32Converter();
        var (ctx, first, second) = TwoDeliveries(decoder);

        var yielded = new List<IDurableFuture>();
        await foreach (var (future, _) in ctx.WaitAll(first, second))
            yielded.Add(future);

        Assert.Equal([first, second], yielded);
        Assert.Equal(2, decoder.Reads);
    }

    [Fact]
    public async Task Race_LosingBranchCannotBeDeserialized_LeavesNoUnobservedFault()
    {
        var unobserved = new List<Exception>();

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved)
                unobserved.AddRange(args.Exception.InnerExceptions);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            Assert.Equal("winner", await RaceAgainstAPoisonedLoser());

            // The loser's decode, if one ever started, is slow on purpose: give it time to fault
            // before collecting, so an abandoned faulted task would be reported here.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            lock (unobserved)
                Assert.DoesNotContain(unobserved, ex => ex.Message == PoisonStringConverter.Marker);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task Any_PayloadCannotBeDeserialized_FailsTheAttempt()
    {
        // A payload the serializer cannot read is a bug, not a result the runtime delivered:
        // it must fail the attempt so the invocation is retried, not settle as a failed branch.
        var manager = new CompletionManager<int>();
        manager.TryComplete(1, Serialized("not-a-number"));

        var ctx = new BareContext();
        var future = new DurableFuture<int>(manager.GetOrRegister(1), JsonSerializerOptions.Default);

        await Assert.ThrowsAsync<JsonException>(() => ctx.Any(future).AsTask());
    }

    [Fact]
    public async Task AllSettled_PayloadCannotBeDeserialized_FailsTheAttempt()
    {
        var manager = new CompletionManager<int>();
        manager.TryComplete(1, Serialized("not-a-number"));

        var ctx = new BareContext();
        var future = new DurableFuture<int>(manager.GetOrRegister(1), JsonSerializerOptions.Default);

        await Assert.ThrowsAsync<JsonException>(() => ctx.AllSettled(future).AsTask());
    }

    [Fact]
    public async Task Any_DeliveredFailure_CountsAsABranchFailure()
    {
        // A terminal failure the runtime delivered is the branch's own result: Any skips it and
        // takes the next branch instead of failing the attempt.
        var manager = new CompletionManager<int>();
        manager.TryFail(1, 500, "boom");
        manager.TryComplete(2, Serialized(42));

        var ctx = new BareContext();
        var failed = new DurableFuture<int>(manager.GetOrRegister(1), JsonSerializerOptions.Default);
        var succeeded = new DurableFuture<int>(manager.GetOrRegister(2), JsonSerializerOptions.Default);

        Assert.Equal(42, await ctx.Any(failed, succeeded));
    }

    [Fact]
    public async Task AllSettled_DeliveredFailure_SettlesAsABranchFailure()
    {
        var manager = new CompletionManager<int>();
        manager.TryFail(1, 500, "boom");
        manager.TryComplete(2, Serialized(42));

        var ctx = new BareContext();
        var failed = new DurableFuture<int>(manager.GetOrRegister(1), JsonSerializerOptions.Default);
        var succeeded = new DurableFuture<int>(manager.GetOrRegister(2), JsonSerializerOptions.Default);

        var results = await ctx.AllSettled(failed, succeeded);

        Assert.IsType<TerminalException>(results[0].Error);
        Assert.Equal(42, results[1].Value);
    }

    /// <summary>
    ///     Races two futures over completions delivered in reverse argument order: the second
    ///     argument is delivered first and its payload stalls while being decoded, so a
    ///     combinator that raced decoded values would hand back the first argument instead.
    ///     With <paramref name="deliveredBeforeTheCall" /> the deliveries are replayed before the
    ///     combinator is called; without it they arrive while it is already waiting.
    /// </summary>
    private static async Task<string> RaceDeliveries(bool deliveredBeforeTheCall)
    {
        var (ctx, manager, first, second) = StallingDeliveries();
        if (deliveredBeforeTheCall)
            Deliver(manager);

        var race = ctx.Race(first, second).AsTask();
        if (!deliveredBeforeTheCall)
        {
            Assert.False(race.IsCompleted);
            Deliver(manager);
        }

        return await race;
    }

    private static async Task<string> AnyDeliveries(bool deliveredBeforeTheCall)
    {
        var (ctx, manager, first, second) = StallingDeliveries();
        if (deliveredBeforeTheCall)
            Deliver(manager);

        var any = ctx.Any(first, second).AsTask();
        if (!deliveredBeforeTheCall)
        {
            Assert.False(any.IsCompleted);
            Deliver(manager);
        }

        return await any;
    }

    private static async Task<List<string>> WaitAllDeliveries(bool deliveredBeforeTheCall)
    {
        var (ctx, manager, first, second) = StallingDeliveries();
        if (deliveredBeforeTheCall)
            Deliver(manager);

        // Enumerated by hand rather than with await foreach: the first MoveNextAsync runs the
        // enumerator up to its first await, so the live case is parked on the two futures before
        // anything is delivered.
        await using var enumerator = ctx.WaitAll(first, second).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync();
        if (!deliveredBeforeTheCall)
        {
            Assert.False(moveNext.IsCompleted);
            Deliver(manager);
        }

        var yielded = new List<string>();
        while (await moveNext)
        {
            yielded.Add(ReferenceEquals(enumerator.Current.future, first) ? "first" : "second");
            moveNext = enumerator.MoveNextAsync();
        }

        return yielded;
    }

    /// <summary>
    ///     Delivers two completions back to back, second argument first, to a combinator that is
    ///     already waiting, and asserts it hands back the earlier delivery. The two deliveries wake
    ///     the combinator on the thread pool in whichever order it schedules them, so a single
    ///     attempt proves nothing; the delivery is repeated until every scheduling has had its
    ///     chance to resume the later delivery's wake-up first.
    /// </summary>
    private static async Task AssertEarlierLiveDeliveryWins(
        Func<Context, IDurableFuture<string>, IDurableFuture<string>, Task<string>> combinator)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var manager = new CompletionManager<int>();
            var ctx = new BareContext();
            var first = new DurableFuture<string>(manager.GetOrRegister(1), JsonSerializerOptions.Default);
            var second = new DurableFuture<string>(manager.GetOrRegister(2), JsonSerializerOptions.Default);

            var pending = combinator(ctx, first, second);
            Assert.False(pending.IsCompleted);
            Deliver(manager);

            Assert.Equal("second", await pending);
        }
    }

    private static void Deliver(CompletionManager<int> manager)
    {
        manager.TryComplete(2, Serialized("second"));
        manager.TryComplete(1, Serialized("first"));
    }

    private static (Context Ctx, CompletionManager<int> Manager, DurableFuture<string> First,
        DurableFuture<string> Second) StallingDeliveries()
    {
        var manager = new CompletionManager<int>();
        var options = WithConverter(new StallingStringConverter("second"));
        return (new BareContext(), manager,
            new DurableFuture<string>(manager.GetOrRegister(1), options),
            new DurableFuture<string>(manager.GetOrRegister(2), options));
    }

    private static (Context Ctx, DurableFuture<int> First, DurableFuture<int> Second) TwoDeliveries(
        JsonConverter decoder)
    {
        var manager = new CompletionManager<int>();
        manager.TryComplete(1, Serialized(1));
        manager.TryComplete(2, Serialized(2));

        var options = WithConverter(decoder);
        return (new BareContext(),
            new DurableFuture<int>(manager.GetOrRegister(1), options),
            new DurableFuture<int>(manager.GetOrRegister(2), options));
    }

    // Separate frame so the losing branch is unreachable — and collectable — by the time the
    // caller forces a garbage collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> RaceAgainstAPoisonedLoser()
    {
        var manager = new CompletionManager<int>();
        var options = WithConverter(new PoisonStringConverter());
        var ctx = new BareContext();
        var winner = new DurableFuture<string>(manager.GetOrRegister(1), options);
        var loser = new DurableFuture<string>(manager.GetOrRegister(2), options);

        manager.TryComplete(1, Serialized("winner"));
        manager.TryComplete(2, Serialized(PoisonStringConverter.Poison));

        return await ctx.Race(winner, loser);
    }

    private static JsonSerializerOptions WithConverter(JsonConverter converter)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(converter);
        return options;
    }

    /// <summary>
    ///     Decodes strings, stalling on one payload so that the thread pool finishes the other
    ///     branch's decode first.
    /// </summary>
    private sealed class StallingStringConverter(string stalls) : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString()!;
            if (value == stalls)
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
            return value;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    /// <summary>Decodes strings, failing on one payload the way a broken serializer would.</summary>
    private sealed class PoisonStringConverter : JsonConverter<string>
    {
        public const string Poison = "poison";
        public const string Marker = "poisoned payload";

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString()!;
            if (value != Poison)
                return value;

            // Slow enough that a combinator racing decoded values hands back the other branch
            // and leaves this fault behind.
            Thread.Sleep(TimeSpan.FromMilliseconds(100));
            throw new JsonException(Marker);
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    /// <summary>A result object, to assert that every await hands back the same instance.</summary>
    private sealed record Payload(int Value);

    /// <summary>Counts how often a branch's payload is decoded.</summary>
    private sealed class CountingInt32Converter : JsonConverter<int>
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Interlocked.Increment(ref _reads);
            return reader.GetInt32();
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>
    ///     Minimal concrete <see cref="Context" /> that stubs all abstract members,
    ///     so tests exercise the real base-class combinator implementations.
    /// </summary>
    private sealed class BareContext : Context
    {
        public override DurableRandom Random => null!;
        public override DurableConsole Console => null!;
        public override IReadOnlyDictionary<string, string> Headers => null!;
        public override string InvocationId => "bare-invocation";
        public override CancellationToken Aborted => CancellationToken.None;

        public override ValueTask<T> Run<T>(string name, Func<Task<T>> action)
        {
            throw new NotSupportedException();
        }

        public override ValueTask Run(string name, Func<Task> action)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<T> Run<T>(string name, Func<T> action)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action)
        {
            throw new NotSupportedException();
        }

        public override ValueTask Run(string name, Func<IRunContext, Task> action)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<T> Run<T>(string name, Func<Task<T>> action, RetryPolicy retryPolicy)
        {
            throw new NotSupportedException();
        }

        public override ValueTask Run(string name, Func<Task> action, RetryPolicy retryPolicy)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<T> Run<T>(string name, Func<T> action, RetryPolicy retryPolicy)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<TResponse> Call<TResponse>(string service, string key, string handler,
            object? request = null)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request,
            CallOptions options)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<TResponse> Call<TResponse>(string service, string key, string handler,
            object? request, CallOptions options)
        {
            throw new NotSupportedException();
        }

        public override ValueTask CancelInvocation(string invocationId)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
            TimeSpan? delay = null, string? idempotencyKey = null)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<InvocationHandle> Send(string service, string key, string handler,
            object? request = null, TimeSpan? delay = null, string? idempotencyKey = null)
        {
            throw new NotSupportedException();
        }

        public override TClient ServiceClient<TClient>()
        {
            throw new NotSupportedException();
        }

        public override TClient ObjectClient<TClient>(string key)
        {
            throw new NotSupportedException();
        }

        public override TClient WorkflowClient<TClient>(string key)
        {
            throw new NotSupportedException();
        }

        public override TClient ServiceSendClient<TClient>(SendOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public override TClient ObjectSendClient<TClient>(string key, SendOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public override TClient WorkflowSendClient<TClient>(string key, SendOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public override ValueTask Sleep(TimeSpan duration)
        {
            throw new NotSupportedException();
        }

        public override Awakeable<T> Awakeable<T>(ISerde<T>? serde = null)
        {
            throw new NotSupportedException();
        }

        public override void ResolveAwakeable<T>(string id, T payload, ISerde<T>? serde = null)
        {
            throw new NotSupportedException();
        }

        public override void RejectAwakeable(string id, string reason)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<DateTimeOffset> Now()
        {
            throw new NotSupportedException();
        }

        public override ValueTask<T> Attach<T>(string invocationId)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<T?> GetOutput<T>(string invocationId) where T : default
        {
            throw new NotSupportedException();
        }

        public override ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler,
            TRequest request, string? key = null)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<InvocationHandle> Send<TRequest>(string service, string handler,
            TRequest request, string? key = null, SendOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public override IDurableFuture<T> RunAsync<T>(string name, Func<Task<T>> action)
        {
            throw new NotSupportedException();
        }

        public override IDurableFuture Timer(TimeSpan duration)
        {
            throw new NotSupportedException();
        }

        public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler,
            object? request = null)
        {
            throw new NotSupportedException();
        }

        public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
            object? request = null)
        {
            throw new NotSupportedException();
        }

        public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler,
            object? request, CallOptions options)
        {
            throw new NotSupportedException();
        }

        public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
            object? request, CallOptions options)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler,
            TRequest request, string? key, CallOptions options)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<InvocationHandle> Send(string service, string handler, object? request,
            SendOptions options)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request,
            SendOptions options)
        {
            throw new NotSupportedException();
        }

        public override IDurableFuture<T> Signal<T>(string name)
        {
            throw new NotSupportedException();
        }

        public override SignalHandle<T> Signal<T>()
        {
            throw new NotSupportedException();
        }

        public override ValueTask ResolveSignal<T>(string invocationId, string name, T value)
        {
            throw new NotSupportedException();
        }

        public override ValueTask ResolveSignal<T>(string invocationId, int signalIndex, T value)
        {
            throw new NotSupportedException();
        }

        public override ValueTask RejectSignal(string invocationId, string name, string reason)
        {
            throw new NotSupportedException();
        }

        public override ValueTask RejectSignal(string invocationId, int signalIndex, string reason)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    ///     Lightweight context that exposes just the combinator methods for testing.
    ///     Mirrors Context's combinator logic.
    /// </summary>
    private sealed class TestCombinatorContext
    {
        public async ValueTask<T[]> All<T>(params IDurableFuture<T>[] futures)
        {
            var results = new T[futures.Length];
            var tasks = new Task[futures.Length];
            for (var i = 0; i < futures.Length; i++)
                tasks[i] = futures[i].GetResult().AsTask();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            for (var i = 0; i < futures.Length; i++)
                results[i] = await futures[i].GetResult().ConfigureAwait(false);

            return results;
        }

        public async ValueTask<T> Race<T>(params IDurableFuture<T>[] futures)
        {
            var tasks = new Task<T>[futures.Length];
            for (var i = 0; i < futures.Length; i++)
                tasks[i] = futures[i].GetResult().AsTask();

            var winner = await Task.WhenAny(tasks).ConfigureAwait(false);
            return await winner.ConfigureAwait(false);
        }

        public async IAsyncEnumerable<(IDurableFuture future, Exception? error)> WaitAll(
            params IDurableFuture[] futures)
        {
            var remaining = new List<(IDurableFuture future, Task task)>(futures.Length);
            for (var i = 0; i < futures.Length; i++)
                remaining.Add((futures[i], futures[i].GetResult().AsTask()));

            while (remaining.Count > 0)
            {
                var tasks = remaining.Select(r => r.task).ToArray();
                var completedTask = await Task.WhenAny(tasks).ConfigureAwait(false);

                for (var i = remaining.Count - 1; i >= 0; i--)
                    if (remaining[i].task == completedTask)
                    {
                        var entry = remaining[i];
                        remaining.RemoveAt(i);
                        var error = completedTask.IsFaulted
                            ? completedTask.Exception?.InnerException
                            : null;
                        yield return (entry.future, error);
                        break;
                    }
            }
        }
    }
}