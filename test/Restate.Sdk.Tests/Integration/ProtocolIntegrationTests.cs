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
///     A service that blocks on an awakeable — used by the suspension tests to verify that
///     pending signal indices are reported in the SuspensionMessage.
/// </summary>
[Service(Name = "AwakeableGreeter")]
public class AwakeableGreeterService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        var approval = ctx.Awakeable<string>();
        var value = await approval.Value;
        return $"{value}, {name}!";
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
///     A service that fans out two calls and settles them with <c>ctx.AllSettled</c> — used to
///     verify that input EOF suspends the invocation instead of settling the journaled calls
///     as fabricated failures.
/// </summary>
[Service(Name = "FanOutGreeter")]
public class FanOutGreeterService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        var first = ctx.CallFuture<string>("Downstream", "A", name);
        var second = ctx.CallFuture<string>("Downstream", "B", name);

        var settled = await ctx.AllSettled(first, second);
        return string.Join(",", settled.Select(r => r.IsSuccess ? r.Value : "failed"));
    }
}

/// <summary>
///     A service that cancels another invocation and then sleeps — used to verify that a
///     journaled SendSignalCommand (ctx.CancelInvocation) replays instead of failing the drain.
/// </summary>
[Service(Name = "CancelThenSleep")]
public class CancelThenSleepService
{
    [Handler]
    public async Task<string> Run(Context ctx, string input)
    {
        await ctx.CancelInvocation("inv-to-cancel");
        await ctx.Sleep(TimeSpan.FromMinutes(5));
        return $"cancelled-then-rested-{input}";
    }
}

/// <summary>
///     A virtual object used by the eager-state tests: reads a counter, increments it,
///     and returns the new value.
/// </summary>
[VirtualObject(Name = "EagerCounter")]
public class EagerCounterObject
{
    private static readonly StateKey<int> Count = new("count");

    [Handler]
    public async Task<int> Add(ObjectContext ctx, int delta)
    {
        var current = await ctx.Get(Count);
        var next = current + delta;
        ctx.Set(Count, next);
        return next;
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

    private static byte[] BuildStartMessagePayloadWithState(
        string debugId, uint knownEntries, string key, ulong randomSeed,
        bool partialState, params (string Key, byte[] Value)[] state)
    {
        var msg = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8(debugId),
            DebugId = debugId,
            KnownEntries = knownEntries,
            Key = key,
            RandomSeed = randomSeed,
            PartialState = partialState
        };
        foreach (var (stateKey, stateValue) in state)
        {
            msg.StateMap.Add(new Gen.StartMessage.Types.StateEntry
            {
                Key = ByteString.CopyFromUtf8(stateKey),
                Value = ByteString.CopyFrom(stateValue)
            });
        }

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
    public async Task HandleAsync_TrailingMalformedFrame_KeepsCompletedResponse()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        var startPayload = BuildStartMessagePayload("trailing-junk-1", 1, "test-key", 42);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);

        // Trailing garbage beyond known_entries: a header announcing a payload that never
        // arrives. The concurrent reader faults on it, but the handler's outcome was already
        // decided — the fault must be logged, not rethrown out of HandleAsync.
        var junkHeader = new byte[MessageHeader.Size];
        MessageHeader.Create(MessageType.SleepCompletion, MessageFlags.None, 64).Write(junkHeader);
        requestStream.Write(junkHeader);
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
        Assert.Equal("\"Hello, World!\"", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

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

    [Fact]
    public async Task HandleAsync_ResumedJournalWithSendSignal_ReplaysAndCompletes()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        // Resumed journal of CancelThenSleepService: Input + SendSignalCommand (from
        // ctx.CancelInvocation) + SleepCommand + SleepCompletionNotification = 4 known entries.
        // The drain must map the replayed SendSignalCommand to a journal entry instead of
        // throwing ProtocolException (which would brick the invocation in a retry loop).
        var startPayload = BuildStartMessagePayload("resume-cancel-1", 4, "test-key", 7);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);
        var sendSignalPayload = ProtobufCodec.CreateCancelInvocationCommand("inv-to-cancel").ToByteArray();
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
        WriteFramedMessage(requestStream, MessageType.SendSignalCommand, sendSignalPayload);
        WriteFramedMessage(requestStream, MessageType.SleepCommand, sleepCommandPayload);
        WriteFramedMessage(requestStream, MessageType.SleepCompletion, sleepCompletionPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(CancelThenSleepService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Run");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new CancelThenSleepService()),
            ServiceProtocolVersion.V6,
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        var offset = 0;

        // No replayed command is re-sent — the invocation completes directly.
        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal("\"cancelled-then-rested-World\"", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    // ------- Eager state -------

    [Fact]
    public async Task HandleAsync_CompleteStateMap_EmitsEagerStateCommand()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes(1);

        // A complete state map (partial_state == false) makes every key locally known:
        // the read must be journaled as a GetEagerStateCommand with the value embedded,
        // not as a GetLazyStateCommand round-trip.
        var startPayload = BuildStartMessagePayloadWithState(
            "eager-inv-1", 1, "counter-key", 42, partialState: false,
            ("count", JsonSerializer.SerializeToUtf8Bytes(41)));
        var inputCommandPayload = BuildInputCommandPayload(inputJson);

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(EagerCounterObject))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Add");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new EagerCounterObject()),
            ServiceProtocolVersion.V7,
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        var offset = 0;

        var (eagerHeader, eagerPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.GetEagerStateCommand, eagerHeader.Type);
        var eagerMsg = Gen.GetEagerStateCommandMessage.Parser.ParseFrom(eagerPayload);
        Assert.Equal("count", eagerMsg.Key.ToStringUtf8());
        Assert.Equal(Gen.GetEagerStateCommandMessage.ResultOneofCase.Value, eagerMsg.ResultCase);
        Assert.Equal(41, JsonSerializer.Deserialize<int>(eagerMsg.Value.Content.Span));

        var (setHeader, setPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.SetStateCommand, setHeader.Type);
        var setMsg = Gen.SetStateCommandMessage.Parser.ParseFrom(setPayload);
        Assert.Equal(42, JsonSerializer.Deserialize<int>(setMsg.Value.Content.Span));

        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal("42", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_PartialStateMapMissingKey_FallsBackToLazyStateCommand()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes(1);

        // Partial state map without the key: only the runtime knows whether it exists, so
        // the SDK must issue a lazy read and await the completion notification.
        var startPayload = BuildStartMessagePayloadWithState(
            "eager-inv-2", 1, "counter-key", 42, partialState: true);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);
        var lazyCompletionPayload = new Gen.GetLazyStateCompletionNotificationMessage
        {
            CompletionId = 1,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(41)) }
        }.ToByteArray();

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        WriteFramedMessage(requestStream, MessageType.GetLazyStateCompletion, lazyCompletionPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(EagerCounterObject))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Add");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new EagerCounterObject()),
            ServiceProtocolVersion.V7,
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        var offset = 0;

        var (lazyHeader, lazyPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.GetLazyStateCommand, lazyHeader.Type);
        var lazyMsg = Gen.GetLazyStateCommandMessage.Parser.ParseFrom(lazyPayload);
        Assert.Equal("count", lazyMsg.Key.ToStringUtf8());
        Assert.Equal(1u, lazyMsg.ResultCompletionId);

        var (setHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.SetStateCommand, setHeader.Type);

        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal("42", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_ReplayedEagerStateCommand_ResolvesFromEmbeddedValue()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes(1);

        // Replay of a journal the SDK itself would have produced: the eager read's value is
        // embedded in the replayed command, and the replayed SetState just advances the cursor.
        // The state map is deliberately DIFFERENT (count=100) — replay must win over it.
        var startPayload = BuildStartMessagePayloadWithState(
            "eager-replay-1", 3, "counter-key", 42, partialState: false,
            ("count", JsonSerializer.SerializeToUtf8Bytes(100)));
        var inputCommandPayload = BuildInputCommandPayload(inputJson);
        var eagerCommandPayload = ProtobufCodec
            .CreateGetEagerStateCommand("count", JsonSerializer.SerializeToUtf8Bytes(41))
            .ToByteArray();
        var setCommandPayload = ProtobufCodec
            .CreateSetStateCommand("count", JsonSerializer.SerializeToUtf8Bytes(42))
            .ToByteArray();

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        WriteFramedMessage(requestStream, MessageType.GetEagerStateCommand, eagerCommandPayload);
        WriteFramedMessage(requestStream, MessageType.SetStateCommand, setCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(EagerCounterObject))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Add");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new EagerCounterObject()),
            ServiceProtocolVersion.V7,
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        var offset = 0;

        // No replayed command is re-sent — the invocation completes directly.
        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal("42", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    // ------- Suspension (poison-on-EOF) -------

    /// <summary>
    ///     Runs an invocation whose request stream contains only Start + Input and then hits
    ///     EOF — simulating the runtime half-closing the stream while the handler awaits a
    ///     durable result — and returns the raw response bytes.
    /// </summary>
    private static async Task<byte[]> RunUntilInputClosedAsync<TService>(
        string handlerName, ServiceProtocolVersion version, Func<TService> factory)
        where TService : class
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");
        var startPayload = BuildStartMessagePayload("suspend-inv-1", 1, "test-key", 42);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(TService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == handlerName);

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => factory()),
            version,
            CancellationToken.None);

        return responseStream.ToArray();
    }

    [Fact]
    public async Task HandleAsync_InputClosesDuringSleep_V7_ProducesSuspensionWithAwaitingOn()
    {
        var responseData = await RunUntilInputClosedAsync<SleepyGreeterService>(
            "Greet", ServiceProtocolVersion.V7, () => new SleepyGreeterService());

        var offset = 0;

        // The live sleep command is flushed before the handler parks on its completion.
        var (sleepHeader, sleepPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.SleepCommand, sleepHeader.Type);
        var sleepMsg = Gen.SleepCommandMessage.Parser.ParseFrom(sleepPayload);
        Assert.Equal(1u, sleepMsg.ResultCompletionId);

        // V7 encodes the await point as an awaiting_on Future.
        var (suspensionHeader, suspensionPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Suspension, suspensionHeader.Type);
        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(suspensionPayload);
        Assert.NotNull(suspension.AwaitingOn);
        Assert.Equal(Gen.CombinatorType.FirstCompleted, suspension.AwaitingOn.CombinatorType);
        Assert.Equal(new uint[] { 1 }, suspension.AwaitingOn.WaitingCompletions);
        Assert.Empty(suspension.AwaitingOn.WaitingSignals);
        Assert.Empty(suspension.WaitingCompletions);
        Assert.Empty(suspension.WaitingSignals);

        // SuspensionMessage is terminal on its own: no End, Error, or Output may follow.
        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_InputClosesDuringAwakeable_ProducesSuspensionWithPendingSignal()
    {
        var responseData = await RunUntilInputClosedAsync<AwakeableGreeterService>(
            "Greet", ServiceProtocolVersion.V7, () => new AwakeableGreeterService());

        var offset = 0;

        // Awakeables write no command — the suspension frame is the only output.
        var (suspensionHeader, suspensionPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Suspension, suspensionHeader.Type);
        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(suspensionPayload);
        Assert.NotNull(suspension.AwaitingOn);
        Assert.Empty(suspension.AwaitingOn.WaitingCompletions);
        Assert.Equal(new uint[] { 0 }, suspension.AwaitingOn.WaitingSignals);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_InputClosesDuringAllSettled_SuspendsInsteadOfSettlingFailures()
    {
        var responseData = await RunUntilInputClosedAsync<FanOutGreeterService>(
            "Greet", ServiceProtocolVersion.V7, () => new FanOutGreeterService());

        var offset = 0;

        // Both fanned-out call commands are flushed before the handler parks on AllSettled.
        var (callAHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.CallCommand, callAHeader.Type);
        var (callBHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.CallCommand, callBHeader.Type);

        // EOF poisons the pending call results. AllSettled must NOT settle them as failures
        // and complete the handler with fabricated output — the invocation must suspend.
        var (suspensionHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Suspension, suspensionHeader.Type);

        // SuspensionMessage is terminal on its own: no Output, Error, or End may follow.
        Assert.Equal(responseData.Length, offset);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task HandleAsync_InputClosesDuringSleep_LegacyVersions_UseLegacyWaitingLists(int versionNumber)
    {
        var responseData = await RunUntilInputClosedAsync<SleepyGreeterService>(
            "Greet", (ServiceProtocolVersion)versionNumber, () => new SleepyGreeterService());

        var offset = 0;

        var (sleepHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.SleepCommand, sleepHeader.Type);

        // Pre-V7 encodes the pending ids in the legacy waiting_* lists; awaiting_on stays unset.
        var (suspensionHeader, suspensionPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Suspension, suspensionHeader.Type);
        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(suspensionPayload);
        Assert.Null(suspension.AwaitingOn);
        Assert.Equal(new uint[] { 1 }, suspension.WaitingCompletions);
        Assert.Empty(suspension.WaitingSignals);

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
