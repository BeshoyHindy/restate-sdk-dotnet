using System.Buffers;
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
///     Awakeables as a handler sees them: awaited through <see cref="Awakeable{T}.Value" />,
///     resolved by a <c>SignalNotification</c> addressed to the index the awakeable's id encodes,
///     and handed to the durable future combinators, which the awakeable composes with because it
///     is itself an <see cref="IDurableFuture{T}" />.
/// </summary>
public class AwakeableTests : IDisposable
{
    /// <summary>The signal index the first awakeable of an invocation allocates.</summary>
    private const uint AwakeableIndex = InvocationStateMachine.FirstUserSignalIndex;

    /// <summary>The completion ids a resumed journal records for its single replayed call.</summary>
    private const uint ReplayedInvocationIdIndex = 1;

    private const uint ReplayedResultCompletionId = 2;

    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private Context _ctx = null!;
    private Task? _incoming;
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
    private Context StartLive()
    {
        var sm = CreateContext();
        sm.Initialize("inv-awakeable", [0xAB], "", 0, 0);
        _incoming = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        return _ctx;
    }

    /// <summary>
    ///     Resumes an invocation whose journal holds one call, the awakeable's notification and
    ///     the call's result — in that order, so the awakeable is the earlier delivery of the two.
    /// </summary>
    private async Task<Context> StartReplayedAsync()
    {
        var sm = CreateContext();

        await WriteInboundAsync(MessageType.Start, new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8("inv-awakeable"),
            DebugId = "inv-awakeable",
            KnownEntries = 4,
            RandomSeed = 0
        }.ToByteArray());
        await WriteInboundAsync(MessageType.InputCommand, new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("World")) }
        }.ToByteArray());
        await WriteInboundAsync(MessageType.CallCommand,
            ProtobufCodec.CreateCallCommand("Downstream", "Get", null, ReadOnlySpan<byte>.Empty,
                ReplayedResultCompletionId, ReplayedInvocationIdIndex).ToByteArray());
        await WriteInboundAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Idx = AwakeableIndex,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("awoken")) }
        }.ToByteArray());
        await WriteInboundAsync(MessageType.CallCompletion, new Gen.CallCompletionNotificationMessage
        {
            CompletionId = ReplayedResultCompletionId,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("called")) }
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
    ///     result to the completion slot that is actually on the wire.
    /// </summary>
    private async Task<Gen.CallCommandMessage> ReadCallCommandAsync()
    {
        _outboundReader ??= new ProtocolReader(_outbound.Reader);
        using var message = await _outboundReader.ReadMessageAsync()
                            ?? throw new InvalidOperationException("no command was written");
        Assert.Equal(MessageType.CallCommand, message.Header.Type);
        return Gen.CallCommandMessage.Parser.ParseFrom(message.Payload);
    }

    private Task ResolveAsync(uint signalIndex, object? value)
    {
        return WriteInboundAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Idx = signalIndex,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(value)) }
        }.ToByteArray());
    }

    private Task ResolveAsync(string name, object? value)
    {
        return WriteInboundAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Name = name,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(value)) }
        }.ToByteArray());
    }

    private Task RejectAsync(uint signalIndex, uint code, string message)
    {
        return WriteInboundAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Idx = signalIndex,
            Failure = new Gen.Failure { Code = code, Message = message }
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
    public async Task Value_ResolvesWithTheDeserializedPayload()
    {
        var ctx = StartLive();

        var awakeable = ctx.Awakeable<string>();

        await ResolveAsync(AwakeableIndex, "granted");

        Assert.Equal("granted", await awakeable.Value);
    }

    [Fact]
    public async Task ResolvedWithoutAValue_YieldsTheDefault()
    {
        var ctx = StartLive();

        var awakeable = ctx.Awakeable<string>();

        await WriteInboundAsync(MessageType.SignalNotification, new Gen.SignalNotificationMessage
        {
            Idx = AwakeableIndex,
            Void = new Gen.Void()
        }.ToByteArray());

        // The runtime delivered no payload, which is the default rather than a decoding error.
        Assert.Null(await awakeable.Value);
    }

    [Fact]
    public async Task Rejected_ThrowsTheSameTerminalExceptionFromTheValueAndTheFuture()
    {
        var ctx = StartLive();

        var awakeable = ctx.Awakeable<string>();

        await RejectAsync(AwakeableIndex, 403, "denied");

        var fromFuture = await Assert.ThrowsAsync<TerminalException>(() => awakeable.GetResult().AsTask());
        Assert.Equal(403, fromFuture.Code);
        Assert.Equal("denied", fromFuture.Message);

        // The value and the future are one outcome, decoded once: the second await rethrows the
        // exception the first one observed rather than building another.
        var fromValue = await Assert.ThrowsAsync<TerminalException>(async () => await awakeable.Value);
        Assert.Same(fromFuture, fromValue);
    }

    [Fact]
    public void InvocationId_IsNull_BecauseAnAwakeableStartsNoInvocation()
    {
        var ctx = StartLive();

        var awakeable = ctx.Awakeable<string>();

        Assert.Null(awakeable.InvocationId);
        Assert.Null(awakeable.Future.InvocationId);
    }

    [Fact]
    public async Task Race_AgainstACallFuture_TakesTheEarlierDelivery()
    {
        var ctx = StartLive();

        var awakeable = ctx.Awakeable<string>();
        var call = ctx.CallFuture<string>("Downstream", "Get", (object?)"payload");

        // Started before anything is delivered, so the race parks on both deliveries.
        var race = ctx.Race(call, awakeable).AsTask();

        var command = await ReadCallCommandAsync();
        await ResolveAsync(AwakeableIndex, "awoken");
        await DeliverCallResultAsync(command.ResultCompletionId, "called");

        // The awakeable was delivered first, so it wins even though the call is the first argument.
        Assert.Equal("awoken", await race);
    }

    [Fact]
    public async Task All_AlongsideACallFuture_ReturnsBothResultsInInputOrder()
    {
        var ctx = StartLive();

        var awakeable = ctx.Awakeable<string>();
        var call = ctx.CallFuture<string>("Downstream", "Get", (object?)"payload");

        var all = ctx.All(awakeable, call).AsTask();

        var command = await ReadCallCommandAsync();
        await DeliverCallResultAsync(command.ResultCompletionId, "called");
        await ResolveAsync(AwakeableIndex, "awoken");

        Assert.Equal(["awoken", "called"], await all);
    }

    [Fact]
    public async Task WaitAll_AgainstATimer_YieldsTheAwakeableThatBeatTheTimeout()
    {
        var ctx = StartLive();

        var awakeable = ctx.Awakeable<string>();
        // A timer is the non-generic IDurableFuture, which the typed combinators cannot take, so
        // a timeout is composed through WaitAll.
        var timeout = ctx.Timer(TimeSpan.FromMinutes(5));

        await ResolveAsync(AwakeableIndex, "in-time");

        IDurableFuture? first = null;
        await foreach (var (settled, error) in ctx.WaitAll(awakeable, timeout))
        {
            Assert.Null(error);
            first = settled;
            break;
        }

        // The timer never fired, so the awakeable is the only delivery and carries the payload.
        var yielded = Assert.IsType<Awakeable<string>>(first);
        Assert.Equal("in-time", await yielded.GetResult());
    }

    [Fact]
    public async Task Payload_IsDecodedOnTheFirstAwaitOfTheFuture_AndOnlyOnce()
    {
        var ctx = StartLive();
        var serde = new CountingSerde();

        var awakeable = ctx.Awakeable<string>(serde);
        var barrier = ctx.Signal<string>("barrier");

        await ResolveAsync(AwakeableIndex, "payload");
        await ResolveAsync("barrier", "arrived");

        // The reader handles frames in order, so the awakeable's notification is in once the
        // barrier's is: the runtime delivered the payload and nothing has decoded it.
        Assert.Equal("arrived", await barrier.GetResult());
        Assert.True(((IArrivalOrderedFuture)awakeable).Completion.IsSettled);
        Assert.Equal(0, serde.Reads);

        Assert.Equal("payload", await awakeable.GetResult());
        Assert.Equal(1, serde.Reads);

        // The value is the same outcome as the future, so awaiting it decodes nothing more.
        Assert.Equal("payload", await awakeable.Value);
        Assert.Equal(1, serde.Reads);
    }

    [Fact]
    public async Task Payload_IsDecodedOnTheFirstAwaitOfTheValue_AndOnlyOnce()
    {
        var ctx = StartLive();
        var serde = new CountingSerde();

        var awakeable = ctx.Awakeable<string>(serde);
        var barrier = ctx.Signal<string>("barrier");

        await ResolveAsync(AwakeableIndex, "payload");
        await ResolveAsync("barrier", "arrived");

        Assert.Equal("arrived", await barrier.GetResult());
        Assert.Equal(0, serde.Reads);

        Assert.Equal("payload", await awakeable.Value);
        Assert.Equal(1, serde.Reads);

        Assert.Equal("payload", await awakeable.GetResult());
        Assert.Equal(1, serde.Reads);
    }

    [Fact]
    public async Task ReplayedJournal_RacesToTheWinnerTheOriginalAttemptTook()
    {
        var ctx = await StartReplayedAsync();

        var awakeable = ctx.Awakeable<string>();
        var call = ctx.CallFuture<string>("Downstream", "Get", (object?)null);

        // Both are settled before the handler reaches the race, and the journal recorded the
        // awakeable's notification ahead of the call's result, so the race takes the awakeable
        // even though the call is the first argument.
        Assert.Equal("awoken", await ctx.Race(call, awakeable));
    }

    /// <summary>Counts how often the awakeable's payload is decoded.</summary>
    private sealed class CountingSerde : ISerde<string>
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public string ContentType => "application/json";

        public void Serialize(IBufferWriter<byte> writer, string value)
        {
            throw new NotSupportedException();
        }

        public string Deserialize(ReadOnlySequence<byte> data)
        {
            Interlocked.Increment(ref _reads);
            var reader = new Utf8JsonReader(data);
            return JsonSerializer.Deserialize<string>(ref reader)!;
        }
    }
}
