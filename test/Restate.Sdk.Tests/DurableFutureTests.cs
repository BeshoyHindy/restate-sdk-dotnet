using System.Buffers;
using System.Text.Json;
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
        var future = new DurableFuture<string>(tcs, JsonSerializerOptions.Default, "inv-123");

        Assert.Equal("inv-123", future.InvocationId);

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
    public async Task LazyCallFuture_GetResultTwice_ReturnsSameValue()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var source = PendingInit();
        var future = new LazyCallFuture<string>(source.ValueTask, JsonSerializerOptions.Default);

        source.SetResult(tcs);
        var buffer = new ArrayBufferWriter<byte>();
        JsonSerializer.Serialize(new Utf8JsonWriter(buffer), "hello");
        tcs.SetResult(CompletionResult.Success(buffer.WrittenMemory));

        Assert.Equal("hello", await future.GetResult());
        Assert.Equal("hello", await future.GetResult());
    }

    [Fact]
    public async Task LazyCallFuture_Failed_ThrowsSameExceptionOnEveryAwait()
    {
        var source = PendingInit();
        var future = new LazyCallFuture<string>(source.ValueTask, JsonSerializerOptions.Default);
        var failure = new TerminalException("call setup failed");

        source.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask()));
    }

    [Fact]
    public async Task LazyCallFuture_FailedCompletion_ThrowsOnEveryAwait()
    {
        var tcs = new TaskCompletionSource<CompletionResult>();
        var source = PendingInit();
        var future = new LazyCallFuture<string>(source.ValueTask, JsonSerializerOptions.Default);

        source.SetResult(tcs);
        tcs.SetResult(CompletionResult.Failure(500, "downstream failed"));

        var first = await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());
        var second = await Assert.ThrowsAsync<TerminalException>(() => future.GetResult().AsTask());

        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.Message, second.Message);
    }
}
