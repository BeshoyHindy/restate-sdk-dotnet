using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests;

/// <summary>
///     Signals as a handler sees them: awaited through <see cref="Context.Signal{T}(string)" />,
///     resolved by a <c>SignalNotification</c> from the runtime, and composed with the durable
///     future combinators.
/// </summary>
public class SignalTests : IDisposable
{
    private readonly Context _ctx;
    private readonly Pipe _inbound = new();
    private readonly Task _incoming;
    private readonly Pipe _outbound = new();
    private readonly InvocationStateMachine _sm;

    public SignalTests()
    {
        _sm = new InvocationStateMachine(
            new ProtocolReader(_inbound.Reader), new ProtocolWriter(_outbound.Writer),
            negotiatedVersion: ServiceProtocolVersion.V7);
        _sm.Initialize("inv-signals", [0xAB], "", 0, 0);
        _ctx = new DefaultContext(_sm, NullLogger.Instance, CancellationToken.None);

        // The reader runs concurrently with the handler, exactly as InvocationHandler drives it.
        _incoming = _sm.ProcessIncomingMessagesAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _inbound.Writer.Complete();
        _inbound.Reader.Complete();
        _outbound.Writer.Complete();
        _outbound.Reader.Complete();
        _sm.Dispose();
    }

    private async Task ResolveAsync(string name, object? value)
    {
        await WriteInboundAsync(new Gen.SignalNotificationMessage
        {
            Name = name,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(value)) }
        });
    }

    private async Task ResolveAsync(int index, object? value)
    {
        await WriteInboundAsync(new Gen.SignalNotificationMessage
        {
            Idx = (uint)index,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(value)) }
        });
    }

    private async Task WriteInboundAsync(Gen.SignalNotificationMessage notification)
    {
        var payload = notification.ToByteArray();
        var header = new byte[MessageHeader.Size];
        MessageHeader.Create(MessageType.SignalNotification, MessageFlags.None, (uint)payload.Length).Write(header);
        await _inbound.Writer.WriteAsync(header);
        await _inbound.Writer.WriteAsync(payload);
    }

    [Fact]
    public async Task NamedSignal_ResolvesWithTheDeserializedValue()
    {
        var approval = _ctx.Signal<string>("approval");

        await ResolveAsync("approval", "granted");

        Assert.Equal("granted", await approval.GetResult());
    }

    [Fact]
    public async Task NamedSignal_Rejected_ThrowsTerminalException()
    {
        var approval = _ctx.Signal<string>("approval");

        await WriteInboundAsync(new Gen.SignalNotificationMessage
        {
            Name = "approval",
            Failure = new Gen.Failure { Code = 403, Message = "denied" }
        });

        var ex = await Assert.ThrowsAsync<TerminalException>(() => approval.GetResult().AsTask());
        Assert.Equal(403, ex.Code);
        Assert.Equal("denied", ex.Message);
    }

    [Fact]
    public async Task UnnamedSignal_ResolvesFromTheIndexItAllocated()
    {
        // The first user signal index is 17; 0-16 are reserved for built-in signals.
        var first = _ctx.Signal<int>();
        var second = _ctx.Signal<int>();

        await ResolveAsync(InvocationStateMachine.FirstUserSignalIndex + 1, 2);
        await ResolveAsync(InvocationStateMachine.FirstUserSignalIndex, 1);

        Assert.Equal(1, await first.GetResult());
        Assert.Equal(2, await second.GetResult());
    }

    [Fact]
    public async Task UnnamedSignal_SharesTheAwakeableIndexSpace()
    {
        var awakeable = _ctx.Awakeable<string>();
        var signal = _ctx.Signal<string>();

        // The awakeable took index 17, so the signal must have taken 18 rather than colliding.
        await ResolveAsync(InvocationStateMachine.FirstUserSignalIndex, "from-awakeable");
        await ResolveAsync(InvocationStateMachine.FirstUserSignalIndex + 1, "from-signal");

        Assert.Equal("from-awakeable", await awakeable.Value);
        Assert.Equal("from-signal", await signal.GetResult());
    }

    [Fact]
    public async Task Signals_ComposeWithAll()
    {
        var first = _ctx.Signal<string>("first");
        var second = _ctx.Signal<string>("second");

        var all = _ctx.All(first, second);

        await ResolveAsync("first", "a");
        await ResolveAsync("second", "b");

        Assert.Equal(["a", "b"], await all);
    }

    [Fact]
    public async Task Signals_ComposeWithRace()
    {
        var slow = _ctx.Signal<string>("slow");
        var quick = _ctx.Signal<string>("quick");

        var race = _ctx.Race(slow, quick);

        await ResolveAsync("quick", "won");

        Assert.Equal("won", await race);
    }

    [Fact]
    public async Task SameNameAwaitedTwice_ResolvesBothFutures()
    {
        // Two handlers paths can await the same rendezvous name; one notification settles both.
        var first = _ctx.Signal<string>("approval");
        var second = _ctx.Signal<string>("approval");

        await ResolveAsync("approval", "granted");

        Assert.Equal("granted", await first.GetResult());
        Assert.Equal("granted", await second.GetResult());
    }

    [Fact]
    public void EmptySignalName_Throws()
    {
        Assert.Throws<ArgumentException>(() => _ctx.Signal<string>(""));
        Assert.Throws<ArgumentNullException>(() => _ctx.Signal<string>(null!));
    }
}
