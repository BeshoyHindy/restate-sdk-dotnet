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
}