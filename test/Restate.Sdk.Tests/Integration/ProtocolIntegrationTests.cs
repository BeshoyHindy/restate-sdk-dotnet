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
///     A service that durably sleeps before responding — used by the resume tests,
///     which replay a journal whose sleep already completed.
/// </summary>
[Service(Name = "SleepyGreeter")]
public class SleepyGreeterService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        await ctx.Sleep(TimeSpan.FromMinutes(5));
        return $"Rested hello, {name}!";
    }
}

/// <summary>
///     A service that calls a downstream service and then sleeps — used to verify that
///     completion ids allocated after a resumed replay do not collide with replayed ids.
/// </summary>
[Service(Name = "CallThenSleep")]
public class CallThenSleepService
{
    [Handler]
    public async Task<string> Orchestrate(Context ctx, string input)
    {
        var downstream = await ctx.Call<string>("Downstream", "Get", input);
        await ctx.Sleep(TimeSpan.FromMinutes(5));
        return $"{downstream}-done";
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
            ServiceProtocolVersion.V6,
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
            ServiceProtocolVersion.V6,
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
            ServiceProtocolVersion.V6,
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
            ServiceProtocolVersion.V6,
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

    [Fact]
    public async Task HandleAsync_ResumedJournalWithNotifications_CompletesWithOutputAndEnd()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        // Resumed journal: known_entries counts commands AND notifications.
        // Input(command) + SleepCommand + SleepCompletionNotification = 3 known entries,
        // but only 2 of them are commands — the replay boundary the handler must re-traverse.
        var startPayload = BuildStartMessagePayload("resume-inv-1", 3, "test-key", 7);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);
        var sleepCommandPayload = new Gen.SleepCommandMessage
        {
            WakeUpTime = 123_456_789UL,
            ResultCompletionId = 1
        }.ToByteArray();
        var sleepCompletionPayload = new Gen.SleepCompletionNotificationMessage
        {
            CompletionId = 1,
            Void = new Gen.Void()
        }.ToByteArray();

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        WriteFramedMessage(requestStream, MessageType.SleepCommand, sleepCommandPayload);
        WriteFramedMessage(requestStream, MessageType.SleepCompletion, sleepCompletionPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(SleepyGreeterService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Greet");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new SleepyGreeterService()),
            ServiceProtocolVersion.V6,
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        Assert.True(responseData.Length > 0, "Response stream should not be empty");

        var offset = 0;

        // The resumed invocation must complete directly: no replayed command is re-sent,
        // and the replayed sleep resolves from its replayed completion notification.
        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);

        var resultContent = ExtractOutputContent(outputPayload);
        Assert.Equal("\"Rested hello, World!\"", Encoding.UTF8.GetString(resultContent));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_ResumedJournalWithCall_UsesFreshCompletionIdsAfterReplay()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        // Original attempt: Input (command 0), then a call that consumed completion ids 1
        // (invocation-id notification) and 2 (result). The resumed journal replays the call
        // command plus both notifications; the handler then sleeps LIVE. The new SleepCommand
        // must use a completion id beyond every replayed id (3) — reusing id 1 or 2 would
        // resolve the sleep with the replayed call's result.
        var startPayload = BuildStartMessagePayload("resume-inv-2", 4, "test-key", 7);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);
        var callCommandPayload = new Gen.CallCommandMessage
        {
            ServiceName = "Downstream",
            HandlerName = "Get",
            Parameter = ByteString.CopyFrom(inputJson),
            InvocationIdNotificationIdx = 1,
            ResultCompletionId = 2
        }.ToByteArray();
        var callInvocationIdPayload = new Gen.CallInvocationIdCompletionNotificationMessage
        {
            CompletionId = 1,
            InvocationId = "inv-downstream"
        }.ToByteArray();
        var callCompletionPayload = new Gen.CallCompletionNotificationMessage
        {
            CompletionId = 2,
            Value = new Gen.Value
            {
                Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("downstream-result"))
            }
        }.ToByteArray();
        // Live notification (beyond known_entries) completing the post-replay sleep.
        var sleepCompletionPayload = new Gen.SleepCompletionNotificationMessage
        {
            CompletionId = 3,
            Void = new Gen.Void()
        }.ToByteArray();

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        WriteFramedMessage(requestStream, MessageType.CallCommand, callCommandPayload);
        WriteFramedMessage(requestStream, MessageType.CallInvocationIdCompletion, callInvocationIdPayload);
        WriteFramedMessage(requestStream, MessageType.CallCompletion, callCompletionPayload);
        WriteFramedMessage(requestStream, MessageType.SleepCompletion, sleepCompletionPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(CallThenSleepService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Orchestrate");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new CallThenSleepService()),
            ServiceProtocolVersion.V6,
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        var offset = 0;

        var (sleepHeader, sleepPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.SleepCommand, sleepHeader.Type);
        var sleepMsg = Gen.SleepCommandMessage.Parser.ParseFrom(sleepPayload);
        Assert.Equal(3u, sleepMsg.ResultCompletionId);

        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal("\"downstream-result-done\"", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    private sealed class FuncServiceProvider(Func<Type, object> factory) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return factory(serviceType);
        }
    }
}
