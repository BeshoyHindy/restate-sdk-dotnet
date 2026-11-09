using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Cancelling an invocation cancels the request-response calls it is still waiting on, so a
///     child that holds a virtual object's lock does not keep it after its caller is gone.
/// </summary>
public class CancelPropagationTests : IDisposable
{
    private const string ChildId = "inv_child";

    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly InvocationStateMachine _sm;
    private ProtocolReader? _outboundReader;

    public CancelPropagationTests()
    {
        _sm = new InvocationStateMachine(new ProtocolReader(_inbound.Reader), new ProtocolWriter(_outbound.Writer),
            negotiatedVersion: ServiceProtocolVersion.V7);
    }

    public void Dispose()
    {
        _inbound.Writer.Complete();
        _inbound.Reader.Complete();
        _outboundReader?.Dispose();
        _outbound.Writer.Complete();
        _outbound.Reader.Complete();
        _sm.Dispose();
    }

    private async Task WriteInboundAsync(MessageType type, IMessage message)
    {
        var payload = message.ToByteArray();
        var header = new byte[MessageHeader.Size];
        MessageHeader.Create(type, MessageFlags.None, (uint)payload.Length).Write(header);
        await _inbound.Writer.WriteAsync(header);
        await _inbound.Writer.WriteAsync(payload);
    }

    private Task DeliverInvocationIdAsync(uint index, string invocationId)
    {
        return WriteInboundAsync(MessageType.CallInvocationIdCompletion,
            new Gen.CallInvocationIdCompletionNotificationMessage { CompletionId = index, InvocationId = invocationId });
    }

    private Task DeliverCancelAsync()
    {
        return WriteInboundAsync(MessageType.SignalNotification,
            new Gen.SignalNotificationMessage { Idx = InvocationStateMachine.CancelSignalIndex, Void = new Gen.Void() });
    }

    /// <summary>Reads the next frame the state machine wrote.</summary>
    private async Task<(MessageType Type, byte[] Payload)> ReadFrameAsync()
    {
        _outboundReader ??= new ProtocolReader(_outbound.Reader);
        using var message = await _outboundReader.ReadMessageAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
                            ?? throw new InvalidOperationException("stream ended");
        return (message.Header.Type, message.Payload.ToArray());
    }

    private async Task<Gen.CallCommandMessage> ReadCallAsync()
    {
        var frame = await ReadFrameAsync();
        Assert.Equal(MessageType.CallCommand, frame.Type);
        return Gen.CallCommandMessage.Parser.ParseFrom(frame.Payload);
    }

    /// <summary>Fails the handler as a cancelled invocation does and returns every frame written from then on.</summary>
    private async Task<List<(MessageType Type, byte[] Payload)>> FailAndDrainAsync()
    {
        await _sm.FailTerminalAsync(409, "The invocation was cancelled.", CancellationToken.None);
        _outbound.Writer.Complete();

        var frames = new List<(MessageType, byte[])>();
        _outboundReader ??= new ProtocolReader(_outbound.Reader);
        while (await _outboundReader.ReadMessageAsync() is { } message)
            using (message)
                frames.Add((message.Header.Type, message.Payload.ToArray()));

        return frames;
    }

    private static void AssertCancelled(string target, (MessageType Type, byte[] Payload) frame)
    {
        Assert.Equal(MessageType.SendSignalCommand, frame.Type);
        Assert.Equal(ProtobufCodec.CreateCancelInvocationCommand(target).ToByteArray(), frame.Payload);
    }

    private ValueTask<string> CallChild()
    {
        return _sm.CallAsync<string>("Child", "key", "block", (object?)null, default, CancellationToken.None);
    }

    [Fact]
    public async Task Cancel_WhileBlockedOnACall_CancelsTheChildBeforeTheOutput()
    {
        _sm.Initialize("inv-1", [0xAB], "", 0, 0);
        _ = _sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var call = CallChild().AsTask();
        await DeliverInvocationIdAsync((await ReadCallAsync()).InvocationIdNotificationIdx, ChildId);
        await DeliverCancelAsync();

        var cancelled = await Assert.ThrowsAsync<TerminalException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(409, cancelled.Code);

        var frames = await FailAndDrainAsync();
        Assert.Equal([MessageType.SendSignalCommand, MessageType.OutputCommand, MessageType.End],
            frames.Select(f => f.Type));
        AssertCancelled(ChildId, frames[0]);
    }

    [Fact]
    public async Task Suspend_WaitsOnTheCancelSignal()
    {
        _sm.Initialize("inv-1", [0xAB], "", 0, 0);

        _ = CallChild().AsTask();
        var call = await ReadCallAsync();
        await _sm.SuspendAsync(CancellationToken.None);

        var frame = await ReadFrameAsync();
        Assert.Equal(MessageType.Suspension, frame.Type);
        var awaitingOn = Gen.SuspensionMessage.Parser.ParseFrom(frame.Payload).AwaitingOn;
        Assert.Contains(call.ResultCompletionId, awaitingOn.WaitingCompletions);
        Assert.Equal(new uint[] { InvocationStateMachine.CancelSignalIndex }, awaitingOn.WaitingSignals);
    }

    [Fact]
    public async Task Cancel_LeavesCompletedCallsAndOneWaySendsAlone()
    {
        _sm.Initialize("inv-1", [0xAB], "", 0, 0);
        _ = _sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var finished = CallChild().AsTask();
        var finishedCall = await ReadCallAsync();
        await DeliverInvocationIdAsync(finishedCall.InvocationIdNotificationIdx, "inv_finished");
        await WriteInboundAsync(MessageType.CallCompletion, new Gen.CallCompletionNotificationMessage
        {
            CompletionId = finishedCall.ResultCompletionId,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("done")) }
        });
        Assert.Equal("done", await finished.WaitAsync(TimeSpan.FromSeconds(5)));

        var send = _sm.SendAsync("Child", "key", "fire", null, default, CancellationToken.None).AsTask();
        var sendFrame = await ReadFrameAsync();
        Assert.Equal(MessageType.OneWayCallCommand, sendFrame.Type);
        var sendCommand = Gen.OneWayCallCommandMessage.Parser.ParseFrom(sendFrame.Payload);
        await WriteInboundAsync(MessageType.CallInvocationIdCompletion,
            new Gen.CallInvocationIdCompletionNotificationMessage
            {
                CompletionId = sendCommand.InvocationIdNotificationIdx,
                InvocationId = "inv_sent"
            });
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        var pending = CallChild().AsTask();
        await DeliverInvocationIdAsync((await ReadCallAsync()).InvocationIdNotificationIdx, ChildId);
        await DeliverCancelAsync();
        await Assert.ThrowsAsync<TerminalException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        var frames = await FailAndDrainAsync();
        Assert.Equal([MessageType.SendSignalCommand, MessageType.OutputCommand, MessageType.End],
            frames.Select(f => f.Type));
        AssertCancelled(ChildId, frames[0]);
    }

    /// <summary>
    ///     Stages a resumed journal: the input, a call to the child, the child's invocation id and
    ///     the CANCEL signal, followed by <paramref name="moreCommands" />.
    /// </summary>
    private async Task StartResumedAfterCancelAsync(params (MessageType Type, IMessage Message)[] moreCommands)
    {
        await WriteInboundAsync(MessageType.Start, new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8("inv-cancelled"),
            DebugId = "inv-cancelled",
            KnownEntries = (uint)(4 + moreCommands.Length),
            Key = "",
            RandomSeed = 0
        });
        await WriteInboundAsync(MessageType.InputCommand, new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("World")) }
        });
        await WriteInboundAsync(MessageType.CallCommand,
            ProtobufCodec.CreateCallCommand("Child", "block", "key", "null"u8, completionId: 2,
                invocationIdNotificationIdx: 1));
        await DeliverInvocationIdAsync(1, ChildId);
        await DeliverCancelAsync();
        foreach (var (type, message) in moreCommands)
            await WriteInboundAsync(type, message);

        await _sm.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ResumedAfterCancel_CancelsTheReplayedCall()
    {
        await StartResumedAfterCancelAsync();

        var cancelled = await Assert.ThrowsAsync<TerminalException>(async () => await CallChild());
        Assert.Equal(409, cancelled.Code);

        var frames = await FailAndDrainAsync();
        Assert.Equal([MessageType.SendSignalCommand, MessageType.OutputCommand, MessageType.End],
            frames.Select(f => f.Type));
        AssertCancelled(ChildId, frames[0]);
    }

    [Fact]
    public async Task ResumedAfterTheChildWasCancelled_ReplaysTheCancelWithoutSendingItAgain()
    {
        await StartResumedAfterCancelAsync(
            (MessageType.SendSignalCommand, ProtobufCodec.CreateCancelInvocationCommand(ChildId)));

        await Assert.ThrowsAsync<TerminalException>(async () => await CallChild());
        Assert.Equal(InvocationState.Processing, _sm.State);

        // The next command the handler writes is live: the journaled cancel was not taken for it.
        _sm.SetState("compensated", true);

        var frames = await FailAndDrainAsync();
        Assert.Equal([MessageType.SetStateCommand, MessageType.OutputCommand, MessageType.End],
            frames.Select(f => f.Type));
    }
}
