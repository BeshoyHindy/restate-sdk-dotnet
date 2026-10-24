using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests;

/// <summary>
///     <see cref="IDurableFuture{T}.InvocationId" /> as a handler sees it: the id of the call a
///     future started, read from the invocation-id notification the runtime delivers for it —
///     without the SDK ever registering a wait on that notification.
/// </summary>
public class CallFutureInvocationIdTests : IDisposable
{
    /// <summary>The completion ids a resumed journal records for its single replayed call.</summary>
    private const uint ReplayedInvocationIdIndex = 1;

    private const uint ReplayedResultCompletionId = 2;

    private readonly Pipe _inbound = new();
    private Context _ctx = null!;
    private Task? _incoming;
    private Pipe _outbound = new();
    private ProtocolReader? _outboundReader;
    private InvocationStateMachine _sm = null!;

    public void Dispose()
    {
        _inbound.Writer.Complete();
        _inbound.Reader.Complete();
        _outboundReader?.Dispose();
        _outbound.Writer.Complete();
        _outbound.Reader.Complete();
        _sm?.Dispose();
    }

    /// <summary>
    ///     Starts a first attempt — nothing replayed — with the reader loop running concurrently,
    ///     exactly as <c>InvocationHandler</c> drives it.
    /// </summary>
    /// <param name="outboundOptions">
    ///     Options for the outbound pipe. A pipe that pauses its writer keeps the call command's
    ///     flush in flight, which is what makes the context hand back a lazily created future.
    /// </param>
    private Context StartLive(PipeOptions? outboundOptions = null)
    {
        if (outboundOptions is not null)
            _outbound = new Pipe(outboundOptions);

        var sm = CreateContext();
        sm.Initialize("inv-caller", [0xAB], "", 0, 0);
        _incoming = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        return _ctx;
    }

    /// <summary>
    ///     Resumes an invocation whose journal holds one call: the <c>CallCommand</c> plus the
    ///     invocation-id notification the runtime delivered for it on the first attempt.
    /// </summary>
    private async Task<Context> StartReplayedCallAsync(string calledInvocationId)
    {
        var sm = CreateContext();

        await WriteInboundAsync(MessageType.Start, new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8("inv-caller"),
            DebugId = "inv-caller",
            KnownEntries = 3,
            RandomSeed = 0
        }.ToByteArray());
        await WriteInboundAsync(MessageType.InputCommand, new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("World")) }
        }.ToByteArray());
        await WriteInboundAsync(MessageType.CallCommand,
            ProtobufCodec.CreateCallCommand("Downstream", "Get", null, ReadOnlySpan<byte>.Empty,
                ReplayedResultCompletionId, ReplayedInvocationIdIndex).ToByteArray());
        await WriteInboundAsync(MessageType.CallInvocationIdCompletion,
            new Gen.CallInvocationIdCompletionNotificationMessage
            {
                CompletionId = ReplayedInvocationIdIndex,
                InvocationId = calledInvocationId
            }.ToByteArray());

        await sm.StartAsync(CancellationToken.None);
        Assert.Equal(InvocationState.Replaying, sm.State);
        return _ctx;
    }

    private InvocationStateMachine CreateContext()
    {
        _sm = new InvocationStateMachine(
            new ProtocolReader(_inbound.Reader), new ProtocolWriter(_outbound.Writer),
            negotiatedVersion: ServiceProtocolVersion.V7);
        _ctx = new DefaultContext(_sm, NullLogger.Instance, CancellationToken.None);
        return _sm;
    }

    /// <summary>
    ///     Reads back the next <c>CallCommand</c> the context flushed, so a test delivers its
    ///     notifications to the completion slots that are actually on the wire. Consuming the
    ///     frame also releases a paused outbound pipe.
    /// </summary>
    private async Task<Gen.CallCommandMessage> ReadCallCommandAsync()
    {
        _outboundReader ??= new ProtocolReader(_outbound.Reader);
        using var message = await _outboundReader.ReadMessageAsync()
                            ?? throw new InvalidOperationException("no command was written");
        Assert.Equal(MessageType.CallCommand, message.Header.Type);
        return Gen.CallCommandMessage.Parser.ParseFrom(message.Payload);
    }

    private Task DeliverInvocationIdAsync(uint completionId, string invocationId)
    {
        return WriteInboundAsync(MessageType.CallInvocationIdCompletion,
            new Gen.CallInvocationIdCompletionNotificationMessage
            {
                CompletionId = completionId,
                InvocationId = invocationId
            }.ToByteArray());
    }

    private Task DeliverCallResultAsync(uint completionId, object? value)
    {
        return WriteInboundAsync(MessageType.CallCompletion, new Gen.CallCompletionNotificationMessage
        {
            CompletionId = completionId,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(value)) }
        }.ToByteArray());
    }

    private async Task WriteInboundAsync(MessageType type, byte[] payload)
    {
        var header = new byte[MessageHeader.Size];
        MessageHeader.Create(type, MessageFlags.None, (uint)payload.Length).Write(header);
        await _inbound.Writer.WriteAsync(header);
        await _inbound.Writer.WriteAsync(payload);
    }

    [Fact]
    public async Task LiveCallFuture_ReportsTheIdOnceTheNotificationArrives()
    {
        var ctx = StartLive();

        // Two calls, so the second one's result can synchronise the assertions about the first:
        // the reader loop handles notifications in stream order, so once the second call's
        // result has been observed, the first call's invocation-id notification is in.
        var first = ctx.CallFuture<string>("Downstream", "A", (object?)"payload");
        var second = ctx.CallFuture<string>("Downstream", "B", (object?)"payload");

        // Nothing has been delivered yet: the id is unknown, and reading it must not block.
        Assert.Null(first.InvocationId);
        Assert.Null(second.InvocationId);

        var firstCommand = await ReadCallCommandAsync();
        var secondCommand = await ReadCallCommandAsync();

        await DeliverInvocationIdAsync(firstCommand.InvocationIdNotificationIdx, "inv-called-a");
        await DeliverCallResultAsync(secondCommand.ResultCompletionId, "b-done");
        Assert.Equal("b-done", await second.GetResult());

        // The first call is still running — no result was ever delivered for it — and its id is
        // already readable, which is the point of the property.
        Assert.Equal(0L, ((IArrivalOrderedFuture)first).ArrivalOrder);
        Assert.Equal("inv-called-a", first.InvocationId);

        // The second call's own id was never delivered, so it stays null.
        Assert.Null(second.InvocationId);
    }

    [Fact]
    public async Task ReplayedCallFuture_ReportsTheIdRecordedInTheJournal()
    {
        var ctx = await StartReplayedCallAsync("inv-called-on-the-first-attempt");

        var replayed = ctx.CallFuture<string>("Downstream", "Get", (object?)null);

        // The notification came back with the replayed journal, so the id is known before the
        // handler awaits anything.
        Assert.Equal("inv-called-on-the-first-attempt", replayed.InvocationId);
    }

    [Fact]
    public async Task LazilyCreatedCallFuture_ReportsTheId()
    {
        // A pipe that pauses its writer keeps the call command's flush in flight, so the context
        // cannot hand back an already-set-up future and wraps the pending setup instead.
        var ctx = StartLive(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));

        var future = ctx.CallFuture<string>("Downstream", "Get", (object?)"payload");
        Assert.IsType<LazyCallFuture<string>>(future);
        Assert.Null(future.InvocationId);

        var command = await ReadCallCommandAsync();
        await DeliverInvocationIdAsync(command.InvocationIdNotificationIdx, "inv-called-lazily");
        await DeliverCallResultAsync(command.ResultCompletionId, "done");

        // The slot is allocated before the command is flushed, so a lazily created future
        // reports the same id an eagerly created one does.
        Assert.Equal("done", await future.GetResult());
        Assert.Equal("inv-called-lazily", future.InvocationId);
    }

    [Fact]
    public void TimerRunAndSignalFutures_ReportNoInvocationId()
    {
        var ctx = StartLive();

        var timer = ctx.Timer(TimeSpan.FromMinutes(5));
        var run = ctx.RunAsync("effect", () => Task.FromResult("done"));
        var namedSignal = ctx.Signal<string>("approval");
        var unnamedSignal = ctx.Signal<string>();

        // None of these started an invocation, so none of them has an id to report.
        Assert.Null(((IDurableFuture<bool>)timer).InvocationId);
        Assert.Null(run.InvocationId);
        Assert.Null(namedSignal.InvocationId);
        Assert.Null(unnamedSignal.InvocationId);
    }
}
