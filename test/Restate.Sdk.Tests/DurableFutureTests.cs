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

    [Fact]
    public async Task VoidFuture_NonGeneric_GetResult_ReturnsNull()
    {
        // The explicit IDurableFuture.GetResult() on VoidDurableFuture (DurableFuture.cs:102-106)
        // awaits the typed bool overload but discards it and yields null — the void shape carries no
        // value across the non-generic combinator boundary. The typed overload is already covered;
        // this pins the non-generic erasure arm that combinators (ctx.All/ctx.Race over a Sleep
        // future) drive when they box every member future as IDurableFuture.
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new VoidDurableFuture(tcs);
        tcs.SetResult(CompletionResult.Success(ReadOnlyMemory<byte>.Empty));

        IDurableFuture nonGeneric = future;
        var result = await nonGeneric.GetResult();

        Assert.Null(result);
    }

    [Fact]
    public async Task VoidFuture_NonGeneric_GetResult_PropagatesFailure()
    {
        // The non-generic arm still flows through ThrowIfFailure (via the typed overload), so a
        // failed completion surfaces as TerminalException even though the success value is erased.
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new VoidDurableFuture(tcs);
        tcs.SetResult(CompletionResult.Failure(500, "timer failed"));

        IDurableFuture nonGeneric = future;
        await Assert.ThrowsAsync<TerminalException>(() => nonGeneric.GetResult().AsTask());
    }

    // ---- DurableFuture<T>.Task combinator accessor (DurableFuture.cs:43, 0/4 branches) ----------

    [Fact]
    public void DurableFuture_TaskProperty_PreCompleted_IsNull()
    {
        // The combinator-facing Task getter has a 4-way branch: the _isPreCompleted ternary plus the
        // _resolved ??= null-coalescing assignment. A Completed() future is pre-completed, so the
        // getter takes the null arm — proving ctx.All/ctx.Race over an already-resolved replay future
        // never spins up a phantom task.
        var future = DurableFuture<int>.Completed(7);

        Assert.Null(future.Task);
    }

    [Fact]
    public async Task DurableFuture_TaskProperty_Pending_MaterializesAndCaches()
    {
        // A thunk-backed (non-pre-completed) future: the first Task access materializes the resolve
        // task via _resolved ??=, the second returns the SAME cached instance — both arms of the
        // null-coalescing assignment plus the non-pre-completed ternary arm.
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new DurableFuture<int>(tcs, JsonSerializerOptions.Default);

        var first = future.Task;
        var second = future.Task;
        Assert.NotNull(first);
        Assert.Same(first, second);   // cached — the thunk runs exactly once

        var buffer = new ArrayBufferWriter<byte>();
        JsonSerializer.Serialize(new Utf8JsonWriter(buffer), 11);
        tcs.SetResult(CompletionResult.Success(buffer.WrittenMemory));
        Assert.Equal(11, await future.GetResult());
    }

    [Fact]
    public void VoidFuture_TaskProperty_MaterializesAndCaches()
    {
        // VoidDurableFuture.Task (DurableFuture.cs:91) is the void-shape combinator accessor; the same
        // _resolved ??= caching contract — first access materializes, second returns the cached task.
        var tcs = new TaskCompletionSource<CompletionResult>();
        var future = new VoidDurableFuture(tcs);

        var first = future.Task;
        var second = future.Task;
        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    // ---- LazyCallFuture<T> / LazyRunFuture<T> accessor arms -------------------------------------

    [Fact]
    public async Task LazyCallFuture_InvocationIdNull_NonGenericGetResult_DeserializesValue()
    {
        // LazyCallFuture<T> backs ctx.Call. Its InvocationId is always null (DurableFuture.cs:183) and
        // its explicit IDurableFuture.GetResult() (lines 193-196) is the boxed combinator entry the
        // typed combinators never reach directly. Drive both: read InvocationId, then await through the
        // non-generic interface so the explicit-impl arm deserializes the call's response value.
        var buffer = new ArrayBufferWriter<byte>();
        JsonSerializer.Serialize(new Utf8JsonWriter(buffer), "pong");
        var captured = buffer.WrittenMemory;
        var future = new LazyCallFuture<string>(
            () => new ValueTask<CompletionResult>(System.Threading.Tasks.Task.FromResult(
                CompletionResult.Success(captured))),
            JsonSerializerOptions.Default);

        Assert.Null(future.InvocationId);

        IDurableFuture nonGeneric = future;
        var result = await nonGeneric.GetResult();
        Assert.Equal("pong", result);
    }

    [Fact]
    public async Task LazyRunFuture_InvocationIdNull_AndGetResultDeserializes()
    {
        // LazyRunFuture<T> backs ctx.RunAsync<T>. Its InvocationId getter (DurableFuture.cs:128) is the
        // run-future analogue — always null — and no production caller reads it. Pin it, then prove the
        // typed GetResult still resolves the locally computed value via the ack notification.
        var buffer = new ArrayBufferWriter<byte>();
        JsonSerializer.Serialize(new Utf8JsonWriter(buffer), 123);
        var captured = buffer.WrittenMemory;
        var future = new LazyRunFuture<int>(
            () => new ValueTask<CompletionResult>(System.Threading.Tasks.Task.FromResult(
                CompletionResult.Success(captured))),
            JsonSerializerOptions.Default);

        Assert.Null(future.InvocationId);
        Assert.Equal(123, await future.GetResult());
    }
}