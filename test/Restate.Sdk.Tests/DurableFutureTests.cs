using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Journal;

namespace Restate.Sdk.Tests;

public class DurableFutureTests
{
    [Fact]
    public async Task Completed_ReturnsValue()
    {
        var future = DurableFuture<int>.Completed(42);

        var result = await future.GetResult();

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Completed_InvocationIdIsNull()
    {
        var future = DurableFuture<string>.Completed("hello");

        Assert.Null(future.InvocationId);
        Assert.Equal("hello", await future.GetResult());
    }

    [Fact]
    public async Task TcsBacked_ResolvesWhenCompleted()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new DurableFuture<string>(tcs, JsonSerializerOptions.Default);

        // A future built without a slot started no invocation, so it reports no id.
        Assert.Null(future.InvocationId);

        // Serialize "hello" to simulate a completion
        var buffer = new ArrayBufferWriter<byte>();
        JsonSerializer.Serialize(
            new Utf8JsonWriter(buffer), "hello");
        tcs.SetResult(CompletionResult.Success(buffer.WrittenMemory));

        var result = await future.GetResult();
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task TcsBacked_ThrowsOnFailure()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new DurableFuture<int>(tcs, JsonSerializerOptions.Default);

        tcs.SetResult(CompletionResult.Failure(500, "something went wrong"));

        await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());
    }

    [Fact]
    public async Task NonGeneric_GetResult_ReturnsObjectValue()
    {
        var future = DurableFuture<int>.Completed(99);

        IDurableFuture nonGeneric = future;
        var result = await nonGeneric.GetResult();

        Assert.Equal(99, result);
    }

    [Fact]
    public async Task VoidFuture_ResolvesSuccessfully()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new VoidDurableFuture(tcs);

        tcs.SetResult(CompletionResult.Success(ReadOnlyMemory<byte>.Empty));

        var result = await future.GetResult();
        Assert.True(result);
    }

    [Fact]
    public async Task VoidFuture_Completed_IsImmediate()
    {
        var future = VoidDurableFuture.Completed();

        var result = await future.GetResult();
        Assert.True(result);
    }

    [Fact]
    public async Task VoidFuture_ThrowsOnFailure()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new VoidDurableFuture(tcs);

        tcs.SetResult(CompletionResult.Failure(500, "timer failed"));

        await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());
    }

    [Fact]
    public void VoidFuture_InvocationId_IsNull()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new VoidDurableFuture(tcs);

        Assert.Null(future.InvocationId);
    }

    // ------- Lazy futures: awaiting more than once -------
    //
    // The lazy futures hold the state machine's ValueTask until the handler (or a combinator)
    // asks for the result. A ValueTask may only be consumed once, so each of these must preserve
    // it at construction; SingleUseValueTaskSource models a source that enforces that contract.

    private static SingleUseValueTaskSource<TaskCompletionSource<CompletionResult>> PendingInit()
    {
        return new SingleUseValueTaskSource<TaskCompletionSource<CompletionResult>>();
    }

    [Fact]
    public async Task LazyRunFuture_GetResultTwice_ReturnsSameValue()
    {
        var source = new SingleUseValueTaskSource<(TaskCompletionSource<CompletionResult> Tcs, int Result)>();
        var future = new LazyRunFuture<int>(source.ValueTask);

        source.SetResult((new TaskCompletionSource<CompletionResult>(), 42));

        Assert.Equal(42, await future.GetResult());
        Assert.Equal(42, await future.GetResult());
    }

    [Fact]
    public async Task LazyRunFuture_Failed_ThrowsSameExceptionOnEveryAwait()
    {
        var source = new SingleUseValueTaskSource<(TaskCompletionSource<CompletionResult> Tcs, int Result)>();
        var future = new LazyRunFuture<int>(source.ValueTask);
        var failure = new TerminalException("run failed");

        source.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask()));
    }

    [Fact]
    public async Task LazyTimerFuture_GetResultTwice_ResolvesBothTimes()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var source = PendingInit();
        var future = new LazyTimerFuture(source.ValueTask);

        source.SetResult(tcs);
        tcs.SetResult(CompletionResult.Success(ReadOnlyMemory<byte>.Empty));

        Assert.Null(await future.GetResult());
        Assert.Null(await future.GetResult());
    }

    [Fact]
    public async Task LazyTimerFuture_Failed_ThrowsSameExceptionOnEveryAwait()
    {
        var source = PendingInit();
        var future = new LazyTimerFuture(source.ValueTask);
        var failure = new TerminalException("timer setup failed");

        source.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask()));
    }

    [Fact]
    public async Task LazyNotificationFuture_GetResultTwice_ReturnsSameValue()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var source = PendingInit();
        var future = new LazyNotificationFuture<string>(source.ValueTask, JsonSerializerOptions.Default);

        source.SetResult(tcs);
        var buffer = new ArrayBufferWriter<byte>();
        JsonSerializer.Serialize(new Utf8JsonWriter(buffer), "hello");
        tcs.SetResult(CompletionResult.Success(buffer.WrittenMemory));

        Assert.Equal("hello", await future.GetResult());
        Assert.Equal("hello", await future.GetResult());
    }

    [Fact]
    public async Task LazyNotificationFuture_Failed_ThrowsSameExceptionOnEveryAwait()
    {
        var source = PendingInit();
        var future = new LazyNotificationFuture<string>(source.ValueTask, JsonSerializerOptions.Default);
        var failure = new TerminalException("call setup failed");

        source.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask()));
    }

    [Fact]
    public async Task LazyNotificationFuture_FailedCompletion_ThrowsOnEveryAwait()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var source = PendingInit();
        var future = new LazyNotificationFuture<string>(source.ValueTask, JsonSerializerOptions.Default);

        source.SetResult(tcs);
        tcs.SetResult(CompletionResult.Failure(500, "downstream failed"));

        var first = await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());
        var second = await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());

        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public async Task LazyTimerFuture_DeliveredFailure_ThrowsSameExceptionOnEveryAwait()
    {
        // Delivered as a failed result rather than a faulted setup: the TerminalException it
        // stands for is built on every read, so only a kept outcome makes the two awaits agree.
        var tcs = new TaskCompletionSource<CompletionResult>();
        var source = PendingInit();
        var future = new LazyTimerFuture(source.ValueTask);

        source.SetResult(tcs);
        tcs.SetResult(CompletionResult.Failure(500, "sleep rejected"));

        var first = await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());
        var second = await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());

        Assert.Same(first, second);
    }

    // ------- Concurrent first awaits -------
    //
    // A future decodes its delivery on the first await and keeps the outcome. Two first awaits
    // can land on two threads, so the memo has to be once-only under a race rather than a
    // read-then-write: otherwise both threads decode and the awaits hand back two objects.

    [Fact]
    public void DurableFuture_AwaitedConcurrently_DecodesOnceAndSharesTheValue()
    {
        const int awaiters = 8;
        const int rounds = 200;

        RaiseThreadPoolFloor(awaiters * 2);

        var decoder = new CountingPayloadConverter();
        var options = new JsonSerializerOptions { Converters = { decoder } };

        for (var round = 0; round < rounds; round++)
        {
            var tcs = new TaskCompletionSource<CompletionResult>();
            var future = new DurableFuture<Payload>(tcs, options);

            // Delivered up front, so the decode runs inline on each awaiter's own thread.
            tcs.SetResult(Serialized(new Payload(round), options));

            using var start = new Barrier(awaiters);
            var observed = new Payload[awaiters];
            Parallel.For(0, awaiters, new ParallelOptions { MaxDegreeOfParallelism = awaiters }, i =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(10));
                observed[i] = future.GetResult().AsTask().GetAwaiter().GetResult();
            });

            Assert.All(observed, payload => Assert.Same(observed[0], payload));
        }

        Assert.Equal(rounds, decoder.Reads);
    }

    [Fact]
    public void RawCompletion_FailureReadConcurrently_HandsBackOneInstance()
    {
        const int readers = 8;
        const int rounds = 200;

        RaiseThreadPoolFloor(readers * 2);

        for (var round = 0; round < rounds; round++)
        {
            var tcs = new TaskCompletionSource<CompletionResult>();
            tcs.SetResult(CompletionResult.Failure(500, "boom"));
            var completion = RawCompletion.OfDelivery(tcs.Task);

            // A combinator woken by the delivery and an await it resumed read the failure on two
            // pool threads at once; both must get the one instance the delivery stands for.
            using var start = new Barrier(readers);
            var observed = new Exception?[readers];
            Parallel.For(0, readers, new ParallelOptions { MaxDegreeOfParallelism = readers }, i =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(10));
                observed[i] = completion.Failure;
            });

            Assert.All(observed, failure => Assert.Same(observed[0], failure));
        }
    }

    /// <summary>
    ///     A floor under the pool, so every awaiter reaches its barrier instead of waiting for the
    ///     pool to inject the thread that would release it.
    /// </summary>
    private static void RaiseThreadPoolFloor(int workers)
    {
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerThreads, workers), completionPortThreads);
    }

    private static CompletionResult Serialized<T>(T value, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        JsonSerializer.Serialize(new Utf8JsonWriter(buffer), value, options);
        return CompletionResult.Success(buffer.WrittenMemory);
    }

    /// <summary>A decoded result object, to assert that every await hands back the same instance.</summary>
    private sealed record Payload(int Value);

    /// <summary>Counts how often a payload is decoded, from any thread.</summary>
    private sealed class CountingPayloadConverter : JsonConverter<Payload>
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public override Payload Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            Interlocked.Increment(ref _reads);
            return new Payload(reader.GetInt32());
        }

        public override void Write(Utf8JsonWriter writer, Payload value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}
