using System.Buffers;
using System.Text.Json;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Journal;
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

    private static void Complete<T>(TaskCompletionSource<CompletionResult> tcs, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        JsonSerializer.Serialize(
            new Utf8JsonWriter(buffer), value);
        tcs.SetResult(CompletionResult.Success(buffer.WrittenMemory));
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

    // ── SettledResult ──

    [Fact]
    public void SettledResult_Failure_NullError_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SettledResult<int>.Failure(null!));
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