using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.StateMachine;

public class InvocationStateMachineTests : IDisposable
{
    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly ProtocolReader _reader;
    private readonly ProtocolWriter _writer;

    public InvocationStateMachineTests()
    {
        _reader = new ProtocolReader(_inbound.Reader);
        _writer = new ProtocolWriter(_outbound.Writer);
    }

    public void Dispose()
    {
        _inbound.Writer.Complete();
        _inbound.Reader.Complete();
        _outbound.Writer.Complete();
        _outbound.Reader.Complete();
    }

    private InvocationStateMachine CreateSm()
    {
        return new InvocationStateMachine(_reader, _writer);
    }

    // ------- Initialization -------

    [Fact]
    public void InitialState_IsWaitingStart()
    {
        using var sm = CreateSm();
        Assert.Equal(InvocationState.WaitingStart, sm.State);
    }

    [Fact]
    public void Initialize_WithInputOnly_TransitionsToProcessing()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-123", "my-key", 42, 1);   // 1 = the Input entry only

        Assert.Equal(InvocationState.Processing, sm.State);
        Assert.Equal("inv-123", sm.InvocationId);
        Assert.Equal("my-key", sm.Key);
        Assert.Equal(42UL, sm.RandomSeed);
    }

    [Fact]
    public void Initialize_WithReplayBatch_TransitionsToReplaying()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-123", "my-key", 42, 3);   // Input + 2 entries → provisional Replaying

        Assert.Equal(InvocationState.Replaying, sm.State);
    }

    [Fact]
    public void Initialize_KnownEntriesZero_Throws()
    {
        using var sm = CreateSm();
        Assert.Throws<ProtocolException>(() =>
            sm.Initialize("inv-123", "my-key", 42, 0));   // KNOWN_ENTRIES_IS_ZERO parity
    }

    [Fact]
    public void Initialize_ThrowsWhenCalledTwice()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        Assert.Throws<InvalidOperationException>(() =>
            sm.Initialize("inv-2", "", 0, 1));
    }

    [Fact]
    public void JsonOptions_IsExposed()
    {
        using var sm = CreateSm();
        Assert.NotNull(sm.JsonOptions);
    }

    // ------- State guards -------

    [Fact]
    public async Task Send_ThrowsInWaitingStartState()
    {
        using var sm = CreateSm();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sm.SendAsync("Svc", null, "Handler", (object?)null, null, null, CancellationToken.None).AsTask());
    }

    [Fact]
    public void SetState_ThrowsInWaitingStartState()
    {
        using var sm = CreateSm();
        Assert.Throws<InvalidOperationException>(() =>
            sm.SetState("key", 42));
    }

    [Fact]
    public async Task RunAsync_ThrowsInClosedState()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);
        await sm.CompleteAsync(null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sm.RunAsync("test", () => Task.FromResult(1), CancellationToken.None));
    }

    // ------- Side effects -------
    // A blocking RunAsync parks on the RunCompletionNotification (the ack barrier); the closure runs
    // synchronously, the proposal is flushed, then we deliver the notification so the await resolves.

    [Fact]
    public async Task RunAsync_InProcessingMode_ExecutesActionAndResolvesFromNotification()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        // The blocking Run parks on the RunCompletionNotification (the ack barrier). Start the SM's
        // pump so the notification we write to the inbound pipe routes into the completion table.
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var executed = false;
        var runTask = sm.RunAsync("test", async () =>
        {
            executed = true;
            await Task.CompletedTask;
            return 42;
        }, CancellationToken.None);

        // Run command id starts at 1 (FirstCompletionId). Deliver the ack so the await resolves.
        await WriteRunCompletionAsync(1, JsonSerializer.SerializeToUtf8Bytes(42));
        var result = await runTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executed);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_Void_InProcessingMode_ExecutesActionAndResolvesFromNotification()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var executed = false;
        var runTask = sm.RunAsync("test", async () =>
        {
            executed = true;
            await Task.CompletedTask;
        }, CancellationToken.None);

        await WriteRunCompletionAsync(1, ReadOnlyMemory<byte>.Empty);
        await runTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await pump.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executed);
    }

    // Writes one RunCompletionNotification to the inbound pipe; the SM's pump routes it.
    private async Task WriteRunCompletionAsync(uint completionId, ReadOnlyMemory<byte> value)
    {
        var notification = new Gen.RunCompletionNotificationMessage { CompletionId = completionId };
        // RunCompletionNotification has no Void result; an unset result oneof reads as empty success.
        if (!value.IsEmpty) notification.Value = new Gen.Value { Content = ByteString.CopyFrom(value.Span) };

        var writer = new ProtocolWriter(_inbound.Writer);
        writer.WriteMessage(MessageType.RunCompletion, notification.ToByteArray());
        await writer.FlushAsync(CancellationToken.None);
        _inbound.Writer.Complete();   // EOF after the single notification
    }

    // ------- Calls -------

    [Fact]
    public async Task Send_InProcessingMode_ReturnsHandleAfterFlush()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        // SendAsync flushes then returns a lazy handle immediately (fire-and-forget); resolving the
        // id is a separate suspension point, so the send itself completes quickly.
        var handle = await sm.SendAsync("Greeter", null, "Greet", (object?)"hello", null, null, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(handle);
    }

    // ------- State -------

    [Fact]
    public void SetState_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 1);

        sm.SetState("count", 42);
    }

    [Fact]
    public void ClearState_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 1);

        sm.SetState("count", 42);
        sm.ClearState("count");
    }

    [Fact]
    public void ClearAllState_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 1);

        sm.SetState("a", 1);
        sm.SetState("b", 2);
        sm.ClearAllState();
    }

    [Fact]
    public async Task GetStateAsync_WithEagerState_ReturnsWithoutWire()
    {
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>?>
        {
            ["count"] = JsonSerializer.SerializeToUtf8Bytes(42)
        };

        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 1, eagerState, partialState: true);

        var value = await sm.GetStateAsync<int>("count", CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task GetStateAsync_WithEmptyEagerValue_ReturnsDefault()
    {
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>?>
        {
            ["empty"] = ReadOnlyMemory<byte>.Empty
        };

        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 1, eagerState, partialState: true);

        var value = await sm.GetStateAsync<int>("empty", CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, value);
    }

    // ------- Awakeable -------

    [Fact]
    public void Awakeable_InProcessingMode_ReturnsIdAndSignalId()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB, 0xCD], "", 0, 1);

        var (id, signalId) = sm.Awakeable();

        // Format: "sign_1" + Base64UrlSafe(rawId + BigEndian32(signalId)); first signal id = 17 (B4).
        Assert.StartsWith("sign_1", id);
        Assert.Equal(17u, signalId);
    }

    [Fact]
    public void Awakeable_SignalIdsStartAt17AndIncrement()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB, 0xCD], "", 0, 1);

        var (_, first) = sm.Awakeable();
        var (_, second) = sm.Awakeable();
        Assert.Equal(17u, first);
        Assert.Equal(18u, second);
    }

    [Fact]
    public void ResolveAwakeable_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        sm.ResolveAwakeable("some-id", new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void RejectAwakeable_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        sm.RejectAwakeable("some-id", "timeout");
    }

    // ------- Promises -------

    [Fact]
    public void ResolvePromise_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 1);

        sm.ResolvePromise("approval", new { Approved = true });
    }

    [Fact]
    public void RejectPromise_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 1);

        sm.RejectPromise("approval", "denied");
    }

    // ------- Output / Error -------

    [Fact]
    public async Task CompleteAsync_TransitionsToClosed()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        await sm.CompleteAsync(new byte[] { 1, 2 }, CancellationToken.None);

        Assert.Equal(InvocationState.Closed, sm.State);
    }

    [Fact]
    public async Task FailAsync_TransitionsToClosed()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        await sm.FailAsync(500, "something broke", CancellationToken.None);

        Assert.Equal(InvocationState.Closed, sm.State);
    }

    [Fact]
    public async Task FailAsync_WithNextRetryDelay_EmitsRetryableErrorCarryingDelay()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        await sm.FailAsync(503, "retry me", CancellationToken.None, TimeSpan.FromMilliseconds(2500));

        var error = await ReadFirstErrorAsync();
        Assert.Equal(503u, error.Code);
        Assert.Equal("retry me", error.Message);
        Assert.True(error.HasNextRetryDelay);
        Assert.Equal(2500ul, error.NextRetryDelay);
    }

    [Fact]
    public async Task FailAsync_WithoutDelay_EmitsErrorWithoutDelayOverride()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        await sm.FailAsync(500, "boom", CancellationToken.None);

        var error = await ReadFirstErrorAsync();
        Assert.False(error.HasNextRetryDelay);
    }

    private async Task<Gen.ErrorMessage> ReadFirstErrorAsync()
    {
        var reader = new ProtocolReader(_outbound.Reader);
        var message = await reader.ReadMessageAsync(CancellationToken.None)
                      ?? throw new InvalidOperationException("No message emitted");
        Assert.Equal(MessageType.Error, message.Header.Type);
        var parsed = Gen.ErrorMessage.Parser.ParseFrom(message.Payload);
        message.Dispose();
        return parsed;
    }

    // ------- Disposal -------

    [Fact]
    public void Dispose_TransitionsToClosed()
    {
        var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        sm.Dispose();
        Assert.Equal(InvocationState.Closed, sm.State);
    }
}
