using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.Integration;

/// <summary>
///     A minimal service used for integration tests.
///     The "Simple" handler avoids side effects (no Run/Sleep/Call),
///     so it can complete without any additional protocol messages.
/// </summary>
[Service(Name = "TestGreeter")]
public class TestGreeterService
{
    [Handler]
    public Task<string> Greet(Context ctx, string name)
    {
        return Task.FromResult($"Hello, {name}!");
    }
}

[Service(Name = "FailingService")]
public class FailingService
{
    [Handler]
    public Task<string> Fail(Context ctx, string input)
    {
        throw new TerminalException("Something went wrong");
    }
}

/// <summary>
///     A service with a single side effect, used for the resumed-invocation end-to-end (§4.9).
///     On replay the journaled RunCommand carries a buffered RunCompletion, so the closure must NOT
///     execute and the handler result must come from the notification. The closure deliberately
///     returns a sentinel that would FAIL the assertion if it ran — proving the value is
///     notification-derived (blueprint §1.7 case 1).
/// </summary>
[Service(Name = "ResumableService")]
public class ResumableService
{
    internal const string ClosureSentinel = "closure-executed-on-replay-BUG";

    [Handler]
    public async Task<string> Compute(Context ctx, string input)
    {
        return await ctx.Run("step", () => ClosureSentinel);
    }
}

/// <summary>
///     Integration tests that exercise the full Restate binary protocol flow:
///     construct a proper binary stream (StartMessage + InputCommand),
///     send it to InvocationHandler via in-memory streams,
///     and verify the response contains the expected OutputCommand + End messages.
/// </summary>
public class ProtocolIntegrationTests
{
    private static byte[] BuildStartMessagePayload(
        string debugId, uint knownEntries, string key, ulong randomSeed)
    {
        var msg = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8(debugId),
            DebugId = debugId,
            KnownEntries = knownEntries,
            Key = key,
            RandomSeed = randomSeed
        };
        return msg.ToByteArray();
    }

    private static byte[] BuildInputCommandPayload(byte[] inputBytes)
    {
        var msg = new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(inputBytes) }
        };
        return msg.ToByteArray();
    }

    private static void WriteFramedMessage(MemoryStream stream, MessageType type, byte[] payload)
    {
        Span<byte> header = stackalloc byte[MessageHeader.Size];
        MessageHeader.Create(type, MessageFlags.None, (uint)payload.Length).Write(header);
        stream.Write(header);
        stream.Write(payload);
    }

    private static (MessageHeader Header, byte[] Payload) ReadFramedMessage(byte[] data, ref int offset)
    {
        Assert.True(offset + MessageHeader.Size <= data.Length,
            $"Not enough data for message header at offset {offset}. Data length: {data.Length}");

        var header = MessageHeader.Read(data.AsSpan(offset, MessageHeader.Size));
        offset += MessageHeader.Size;

        var payload = new byte[header.Length];
        if (header.Length > 0)
        {
            Assert.True(offset + (int)header.Length <= data.Length,
                $"Not enough data for message payload at offset {offset}. Need {header.Length}, have {data.Length - offset}");
            Array.Copy(data, offset, payload, 0, (int)header.Length);
            offset += (int)header.Length;
        }

        return (header, payload);
    }

    private static byte[] ExtractOutputContent(byte[] outputPayload)
    {
        var msg = Gen.OutputCommandMessage.Parser.ParseFrom(outputPayload);
        return msg.Value is not null ? msg.Value.Content.ToByteArray() : [];
    }

    private static (uint Code, string Message) ExtractErrorFields(byte[] errorPayload)
    {
        var msg = Gen.ErrorMessage.Parser.ParseFrom(errorPayload);
        return (msg.Code, msg.Message);
    }

    [Fact]
    public async Task HandleAsync_SimpleGreeter_ProducesOutputAndEnd()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        var startPayload = BuildStartMessagePayload("test-inv-1", 1, "test-key", 42);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(TestGreeterService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Greet");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new TestGreeterService()),
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        Assert.True(responseData.Length > 0, "Response stream should not be empty");

        var offset = 0;

        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);

        var resultContent = ExtractOutputContent(outputPayload);
        var resultJson = Encoding.UTF8.GetString(resultContent);
        Assert.Equal("\"Hello, World!\"", resultJson);

        var (endHeader, endPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);
        Assert.Equal(0u, endHeader.Length);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_NullInput_ProducesError()
    {
        var startPayload = BuildStartMessagePayload("test-inv-2", 1, "test-key", 99);

        var nullJson = JsonSerializer.SerializeToUtf8Bytes<string>(null!);
        var inputCommandPayload = BuildInputCommandPayload(nullJson);

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(TestGreeterService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Greet");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new TestGreeterService()),
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        Assert.True(responseData.Length > 0, "Response stream should not be empty");

        var offset = 0;
        var (errorHeader, _) = ReadFramedMessage(responseData, ref offset);

        // Null JSON input should be rejected by the generated deserializer null check
        Assert.Equal(MessageType.Error, errorHeader.Type);
    }

    [Fact]
    public async Task HandleAsync_VerifiesStartMessageParsing()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("Test");
        var startPayload = BuildStartMessagePayload("inv-abc-123", 1, "my-key", 12345);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(TestGreeterService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Greet");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new TestGreeterService()),
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        var offset = 0;

        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);

        var resultContent = ExtractOutputContent(outputPayload);
        var resultJson = Encoding.UTF8.GetString(resultContent);
        Assert.Equal("\"Hello, Test!\"", resultJson);

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);
    }

    [Fact]
    public async Task HandleAsync_HandlerThrowsTerminalException_ProducesOutputFailure()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("fail");
        var startPayload = BuildStartMessagePayload("test-inv-err", 1, "test-key", 42);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(FailingService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Fail");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new FailingService()),
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        Assert.True(responseData.Length > 0, "Response stream should not be empty");

        var offset = 0;
        // TerminalException produces OutputCommand with failure oneof (non-retryable)
        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);

        var msg = Gen.OutputCommandMessage.Parser.ParseFrom(outputPayload);
        Assert.Equal(Gen.OutputCommandMessage.ResultOneofCase.Failure, msg.ResultCase);
        Assert.Equal(500u, msg.Failure.Code);
        Assert.Equal("Something went wrong", msg.Failure.Message);

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);
    }

    /// <summary>
    ///     Resumed-invocation end-to-end (blueprint §4.9): a journal of
    ///     Start{known_entries=3} + Input + RunCommand{id=1} + RunCompletion{id=1, value} replays the
    ///     side effect WITHOUT re-executing its closure. The RunCommand is the replay frontier and a
    ///     buffered completion exists, so the handler resolves the run from the notification (§1.7
    ///     case 1). The emitted Output must equal the notification-derived value — never the closure's
    ///     sentinel — and no RunCommand or ProposeRunCompletion may be re-emitted on the wire.
    ///     Pre-fix (B1/B2) this path either re-derived the wrong completion id or fed the replayed
    ///     protobuf command bytes into the JSON deserializer; both surface here.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task HandleAsync_ResumedInvocation_OutputEqualsNotificationValue()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("ignored");
        // known_entries = Input(1) + RunCommand(command) + RunCompletion(notification) = 3.
        var startPayload = BuildStartMessagePayload("inv-resumed", 3, "test-key", 7);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);

        // The journaled RunCommand and its buffered completion. The completion carries the durable
        // result the runtime stored on the first attempt — distinct from the closure sentinel.
        var notificationValue = JsonSerializer.SerializeToUtf8Bytes("resumed-from-notification");
        var runCommandPayload = new Gen.RunCommandMessage { Name = "step", ResultCompletionId = 1 }.ToByteArray();
        var runCompletionPayload = new Gen.RunCompletionNotificationMessage
        {
            CompletionId = 1,
            Value = new Gen.Value { Content = ByteString.CopyFrom(notificationValue) }
        }.ToByteArray();

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        WriteFramedMessage(requestStream, MessageType.RunCommand, runCommandPayload);
        WriteFramedMessage(requestStream, MessageType.RunCompletion, runCompletionPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(ResumableService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Compute");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new ResumableService()),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var responseData = responseStream.ToArray();
        Assert.True(responseData.Length > 0, "Response stream should not be empty");

        // Walk every frame: the Output must carry the notification value, and the journaled RunCommand
        // must NOT be re-emitted (replay consumed it) nor a proposal generated (closure never ran).
        var offset = 0;
        string? outputJson = null;
        while (offset < responseData.Length)
        {
            var (header, payload) = ReadFramedMessage(responseData, ref offset);
            Assert.NotEqual(MessageType.RunCommand, header.Type);
            Assert.NotEqual(MessageType.ProposeRunCompletion, header.Type);
            if (header.Type == MessageType.OutputCommand)
                outputJson = Encoding.UTF8.GetString(ExtractOutputContent(payload));
        }

        Assert.Equal("\"resumed-from-notification\"", outputJson);
        Assert.DoesNotContain(ResumableService.ClosureSentinel, outputJson);
    }

    private sealed class FuncServiceProvider(Func<Type, object> factory) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return factory(serviceType);
        }
    }
}
