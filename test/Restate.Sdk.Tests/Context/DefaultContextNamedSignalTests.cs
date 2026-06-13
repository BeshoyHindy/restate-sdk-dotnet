using System.Buffers;
using System.IO.Pipelines;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.ContextSurface;

/// <summary>
///     End-to-end coverage of the named-signal CONTEXT surface (DefaultContext.NamedSignal /
///     SendSignal / SendSignalFailure) driven through a real <see cref="InvocationStateMachine" /> over
///     a duplex pipe. Asserts the await side deserializes via JSON OR a custom serde, and the send side
///     emits a SendSignalCommand carrying the NAME oneof + the right value/failure result.
/// </summary>
public class DefaultContextNamedSignalTests
{
    private const int Timeout = 10_000;

    private static (InvocationStateMachine Sm, DefaultContext Ctx, Pipe Inbound, Pipe Outbound) NewRig()
    {
        var inbound = new Pipe();
        var outbound = new Pipe();
        var sm = new InvocationStateMachine(
            new ProtocolReader(inbound.Reader), new ProtocolWriter(outbound.Writer));
        sm.Initialize("inv-ctx-named", "key", 0, 1);
        var ctx = new DefaultContext(sm, NullLogger.Instance, CancellationToken.None);
        return (sm, ctx, inbound, outbound);
    }

    private static async Task DeliverAsync(Pipe inbound, MessageType type, IMessage message)
    {
        var writer = new ProtocolWriter(inbound.Writer);
        writer.WriteMessage(type, message.ToByteArray());
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [Fact(Timeout = Timeout)]
    public async Task NamedSignal_ResumesAndDeserializesJson()
    {
        var (sm, ctx, inbound, outbound) = NewRig();
        using var _ = sm;

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var signal = ctx.NamedSignal<string>("approval");
        Assert.Equal("approval", signal.Name);

        await DeliverAsync(inbound, MessageType.SignalNotification,
            CreateNamedSignalNotification("approval", "\"yes\""u8.ToArray()));

        var value = await AwaitBounded(signal.Value);
        Assert.Equal("yes", value);

        inbound.Writer.Complete();
        await AwaitBounded(pump);
        outbound.Writer.Complete();
    }

    [Fact(Timeout = Timeout)]
    public async Task NamedSignal_WithSerde_DeserializesViaSerde()
    {
        var (sm, ctx, inbound, outbound) = NewRig();
        using var _ = sm;

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var signal = ctx.NamedSignal("approval", new AsciiSerde());

        // The serde lower-cases on deserialize, proving the serde branch (not JSON) ran.
        await DeliverAsync(inbound, MessageType.SignalNotification,
            CreateNamedSignalNotification("approval", "HELLO"u8.ToArray()));

        var value = await AwaitBounded(signal.Value);
        Assert.Equal("hello", value);

        inbound.Writer.Complete();
        await AwaitBounded(pump);
        outbound.Writer.Complete();
    }

    [Fact(Timeout = Timeout)]
    public async Task SendSignal_EmitsSendSignalCommandWithNameAndValue()
    {
        var (sm, ctx, inbound, outbound) = NewRig();
        using var _ = sm;

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(ctx.SendSignal("inv_target", "approval", "payload"));

        inbound.Writer.Complete();
        await AwaitBounded(pump);
        await AwaitBounded(sm.CompleteAsync(Array.Empty<byte>(), CancellationToken.None));

        var command = await FirstOutboundAsync(outbound, MessageType.SendSignalCommand);
        var parsed = Gen.SendSignalCommandMessage.Parser.ParseFrom(command);
        Assert.Equal("inv_target", parsed.TargetInvocationId);
        Assert.Equal("approval", parsed.Name);
        Assert.Equal(Gen.SendSignalCommandMessage.ResultOneofCase.Value, parsed.ResultCase);
        Assert.Equal("\"payload\""u8.ToArray(), parsed.Value.Content.ToByteArray());
    }

    [Fact(Timeout = Timeout)]
    public async Task SendSignalWithSerde_EmitsSendSignalCommandWithSerdeBytes()
    {
        var (sm, ctx, inbound, outbound) = NewRig();
        using var _ = sm;

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(ctx.SendSignal("inv_target", "approval", "abc", new AsciiSerde()));

        inbound.Writer.Complete();
        await AwaitBounded(pump);
        await AwaitBounded(sm.CompleteAsync(Array.Empty<byte>(), CancellationToken.None));

        var command = await FirstOutboundAsync(outbound, MessageType.SendSignalCommand);
        var parsed = Gen.SendSignalCommandMessage.Parser.ParseFrom(command);
        // The serde upper-cases on serialize, proving SerializeWithSerde used IT (not JSON quoting).
        Assert.Equal("ABC"u8.ToArray(), parsed.Value.Content.ToByteArray());
    }

    [Fact(Timeout = Timeout)]
    public async Task SendSignalFailure_EmitsSendSignalCommandWithFailure()
    {
        var (sm, ctx, inbound, outbound) = NewRig();
        using var _ = sm;

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(ctx.SendSignalFailure("inv_target", "approval", "denied"));

        inbound.Writer.Complete();
        await AwaitBounded(pump);
        await AwaitBounded(sm.CompleteAsync(Array.Empty<byte>(), CancellationToken.None));

        var command = await FirstOutboundAsync(outbound, MessageType.SendSignalCommand);
        var parsed = Gen.SendSignalCommandMessage.Parser.ParseFrom(command);
        Assert.Equal("approval", parsed.Name);
        Assert.Equal(Gen.SendSignalCommandMessage.ResultOneofCase.Failure, parsed.ResultCase);
        Assert.Equal(500u, parsed.Failure.Code);
        Assert.Equal("denied", parsed.Failure.Message);
    }

    private static async Task<byte[]> FirstOutboundAsync(Pipe outbound, MessageType type)
    {
        var reader = new ProtocolReader(outbound.Reader);
        while (true)
        {
            var message = await AwaitBounded(reader.ReadMessageAsync(CancellationToken.None).AsTask());
            if (message is not { } frame) break;
            var header = frame.Header;
            var payload = frame.Payload.ToArray();
            frame.Dispose();
            if (header.Type == type) return payload;
            if (header.Type == MessageType.End) break;
        }

        Assert.Fail($"No outbound frame of type {type} was emitted");
        return Array.Empty<byte>();
    }

    /// <summary>ASCII serde: serialize upper-cases, deserialize lower-cases — distinct from JSON.</summary>
    private sealed class AsciiSerde : ISerde<string>
    {
        public string ContentType => "application/octet-stream";

        public void Serialize(IBufferWriter<byte> writer, string value)
        {
            var span = writer.GetSpan(value.Length);
            for (var i = 0; i < value.Length; i++)
                span[i] = (byte)char.ToUpperInvariant(value[i]);
            writer.Advance(value.Length);
        }

        public string Deserialize(ReadOnlySequence<byte> data) =>
            System.Text.Encoding.ASCII.GetString(data.ToArray()).ToLowerInvariant();
    }
}
