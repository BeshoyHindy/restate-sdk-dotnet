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
///     A service whose only outstanding work is a named signal — used by the suspension test that
///     checks a v7 stream advertises pending signal ids and releases the connection.
/// </summary>
[Service(Name = "SignalGreeter")]
public class SignalGreeterService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        var approval = await ctx.Signal<string>("approval").GetResult();
        return $"{approval}, {name}!";
    }
}

/// <summary>A service awaiting an unnamed signal, addressed by the index the SDK allocates.</summary>
[Service(Name = "UnnamedSignalGreeter")]
public class UnnamedSignalGreeterService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        var approval = await ctx.Signal<string>().GetResult();
        return $"{approval}, {name}!";
    }
}

/// <summary>
///     A service that resolves or rejects a signal on another invocation through its handle —
///     the sending half of the end-to-end signal tests.
/// </summary>
[Service(Name = "SignalResolver")]
public class SignalResolverService
{
    [Handler]
    public async Task<string> Resolve(Context ctx, string targetInvocationId)
    {
        await new InvocationHandle(targetInvocationId).ResolveSignal(ctx, "approval", "granted");
        return "sent";
    }

    [Handler]
    public async Task<string> Reject(Context ctx, string targetInvocationId)
    {
        await new InvocationHandle(targetInvocationId).RejectSignal(ctx, "approval", "not approved");
        return "sent";
    }
}

/// <summary>
///     A sleeping service whose durable sleep is guarded by a saga-style compensation —
///     used to verify that a journal mismatch is not catchable as a <see cref="TerminalException" />.
/// </summary>
[Service(Name = "CompensatingSleeper")]
public class CompensatingSleeperService
{
    /// <summary>True once the compensation block ran, which a journal mismatch must never trigger.</summary>
    public bool CompensationRan { get; private set; }

    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        try
        {
            await ctx.Sleep(TimeSpan.FromMinutes(5));
        }
        catch (TerminalException)
        {
            CompensationRan = true;
            return "compensated";
        }

        return $"Rested hello, {name}!";
    }
}

/// <summary>
///     A sleeping service that swallows every exception around its durable sleep — unsupported
///     in handler code, and used to verify that the recorded journal mismatch still decides the
///     outcome of the attempt.
/// </summary>
[Service(Name = "SwallowingSleeper")]
public class SwallowingSleeperService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        try
        {
            await ctx.Sleep(TimeSpan.FromMinutes(5));
        }
        catch (Exception)
        {
            return $"swallowed hello, {name}!";
        }

        return $"Rested hello, {name}!";
    }
}

/// <summary>
///     A sleeping service that swallows every exception around its durable sleep and then fails
///     terminally — unsupported in handler code, and used to verify that the recorded journal
///     mismatch still wins over the terminal failure the handler reports instead.
/// </summary>
[Service(Name = "FailingSleeper")]
public class FailingSleeperService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        try
        {
            await ctx.Sleep(TimeSpan.FromMinutes(5));
        }
        catch (Exception)
        {
            throw new TerminalException($"gave up on {name}", 409);
        }

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
        // Cast: with three string arguments the keyed overload (service, key, handler) wins, and
        // this call is to a stateless service — the journal below records it unkeyed.
        var downstream = await ctx.Call<string>("Downstream", "Get", (object?)input);
        await ctx.Sleep(TimeSpan.FromMinutes(5));
        return $"{downstream}-done";
    }
}

/// <summary>
///     A service that makes a single request/response call — used to verify that the ignored
///     invocation-id notification slot is not advertised in the SuspensionMessage.
/// </summary>
[Service(Name = "CallGreeter")]
public class CallGreeterService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        return await ctx.Call<string>("Downstream", "Get", name);
    }
}

/// <summary>
///     A service that calls a downstream handler inside a concurrency scope — used to verify
///     that a stream below V7, which cannot carry the scope, fails the attempt instead of
///     dropping the flow control the handler asked for.
/// </summary>
[Service(Name = "ScopedCallGreeter")]
public class ScopedCallGreeterService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        return await ctx.Call<string>("Downstream", "Get", name,
            options: CallOptions.WithScope("tenant-a", "customer-7"));
    }
}

/// <summary>
///     A service with a durable side effect — used to verify Run replay semantics: a stored
///     completion resolves without re-executing, a missing one re-executes and re-proposes.
/// </summary>
[Service(Name = "RunGreeter")]
public class RunGreeterService
{
    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        return await ctx.Run("effect", () => Task.FromResult($"fresh-{name}"));
    }
}

/// <summary>
///     A service whose durable side effect fails twice before succeeding, under a retry policy —
///     used to verify that a Run re-executed after replay still retries.
/// </summary>
[Service(Name = "RetryingRunGreeter")]
public class RetryingRunGreeterService
{
    private static readonly RetryPolicy ThreeAttempts = new()
    {
        MaxAttempts = 3,
        InitialDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(1)
    };

    public int Attempts { get; private set; }

    [Handler]
    public async Task<string> Greet(Context ctx, string name)
    {
        return await ctx.Run("effect", () =>
        {
            if (++Attempts < 3)
                throw new InvalidOperationException("transient");
            return Task.FromResult($"retried-{name}");
        }, ThreeAttempts);
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
///     A service whose handlers combine two durable futures — used by the combinator replay
///     tests, which resume a journal whose completions were delivered in an order that differs
///     from the order the futures are passed in.
/// </summary>
[Service(Name = "CombinatorGreeter")]
public class CombinatorGreeterService
{
    // Casts throughout: with three string arguments the keyed overload (service, key, handler)
    // wins, and these calls are to a stateless service — the journals below record them unkeyed.
    [Handler]
    public async Task<string> Race(Context ctx, string name)
    {
        var first = ctx.CallFuture<string>("Downstream", "A", (object?)name);
        var second = ctx.CallFuture<string>("Downstream", "B", (object?)name);

        return await ctx.Race(first, second);
    }

    [Handler]
    public async Task<string> Any(Context ctx, string name)
    {
        var first = ctx.CallFuture<string>("Downstream", "A", (object?)name);
        var second = ctx.CallFuture<string>("Downstream", "B", (object?)name);

        return await ctx.Any(first, second);
    }

    [Handler]
    public async Task<string> Wait(Context ctx, string name)
    {
        var call = ctx.CallFuture<string>("Downstream", "Get", (object?)name);
        var timer = ctx.Timer(TimeSpan.FromMinutes(5));

        var order = new List<string>();
        await foreach (var (future, _) in ctx.WaitAll(call, timer))
            order.Add(ReferenceEquals(future, call) ? "call" : "timer");

        return string.Join(",", order);
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
    public async Task HandleAsync_CancelledAttempt_EndsBodyWithRetryableError()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");
        var startPayload = BuildStartMessagePayload("cancelled-inv-1", 1, "test-key", 42);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(TestGreeterService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Greet");

        var handler = new InvocationHandler();

        // The attempt deadline already elapsed (e.g. Lambda RemainingTime margin fired).
        // The body must still end with a terminal frame — a torso without one is a protocol
        // violation when returned as HTTP 200.
        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new TestGreeterService()),
            ServiceProtocolVersion.V6,
            new CancellationToken(canceled: true));

        var responseData = responseStream.ToArray();
        var offset = 0;

        var (errorHeader, errorPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Error, errorHeader.Type);
        Assert.Equal(500u, Gen.ErrorMessage.Parser.ParseFrom(errorPayload).Code);

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_ScopedCallOnV6_EndsWithRetryableErrorAndNoCallCommand()
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        var startPayload = BuildStartMessagePayload("scoped-inv-1", 1, "test-key", 42);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(ScopedCallGreeterService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Greet");

        var handler = new InvocationHandler();

        // CallCommand.scope arrived in V7: on a V6 stream the handler asked for flow control the
        // server cannot see. The ArgumentException is a usage error, so it surfaces the way any
        // unhandled exception does — a retryable ErrorMessage 500 ending the attempt. The user
        // keeps retrying until the code or the server changes, which beats a silent drop.
        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new ScopedCallGreeterService()),
            ServiceProtocolVersion.V6,
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        var offset = 0;

        var (errorHeader, errorPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Error, errorHeader.Type);
        var (code, message) = ExtractErrorFields(errorPayload);
        Assert.Equal(500u, code);
        Assert.Contains("V7", message, StringComparison.Ordinal);
        Assert.Contains("V6", message, StringComparison.Ordinal);

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        // Error + End is the whole response: no CallCommand was written, so the scope was not
        // dropped on the way to a server that would have ignored it.
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

    /// <summary>
    ///     Replays a journal of Input + one command into a handler that does something else, so
    ///     the replayed prefix no longer matches, and returns the raw response bytes. The command
    ///     defaults to the RunCommand the kind-mismatch tests replay into sleeping handlers.
    /// </summary>
    private static async Task<byte[]> RunMismatchingReplayAsync<TService>(
        ServiceProtocolVersion version, Func<TService> factory,
        MessageType commandType = MessageType.RunCommand, byte[]? commandPayload = null,
        string handlerName = "Greet")
        where TService : class
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        var startPayload = BuildStartMessagePayload("mismatch-inv-1", 2, "test-key", 7);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);
        commandPayload ??= ProtobufCodec.CreateRunCommand("effect", 1).ToByteArray();

        // A pipe rather than a MemoryStream: the request stream stays open, so no EOF can
        // unpark the handler. Before the mismatch check, replaying the wrong command made the
        // handler await a completion id the runtime will never send — an indefinite hang.
        var request = new Pipe();
        var requestBuffer = new MemoryStream();
        WriteFramedMessage(requestBuffer, MessageType.Start, startPayload);
        WriteFramedMessage(requestBuffer, MessageType.InputCommand, inputCommandPayload);
        WriteFramedMessage(requestBuffer, commandType, commandPayload);
        await request.Writer.WriteAsync(requestBuffer.ToArray());

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(TService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == handlerName);

        var handleTask = new InvocationHandler().HandleAsync(
            request.Reader,
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => factory()),
            version,
            CancellationToken.None);

        await handleTask.WaitAsync(TimeSpan.FromSeconds(30));
        await request.Writer.CompleteAsync();

        return responseStream.ToArray();
    }

    /// <summary>
    ///     Asserts that the response is exactly a journal-mismatch ErrorMessage followed by End —
    ///     consuming every byte proves no OutputCommand was written — and returns the error.
    /// </summary>
    private static Gen.ErrorMessage AssertJournalMismatchResponse(byte[] responseData)
    {
        var offset = 0;

        var (errorHeader, errorPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Error, errorHeader.Type);

        var error = Gen.ErrorMessage.Parser.ParseFrom(errorPayload);
        Assert.Equal(570u, error.Code);
        Assert.Equal(Gen.ErrorBehavior.Retry, error.Behavior);
        Assert.Contains("journal mismatch", error.Message);
        Assert.Contains("journal index 1", error.Message);

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);
        Assert.Equal(responseData.Length, offset);

        return error;
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task HandleAsync_ReplayedCommandTypeMismatch_FailsWithRetryableJournalMismatch(int protocolVersion)
    {
        var responseData = await RunMismatchingReplayAsync(
            (ServiceProtocolVersion)protocolVersion, () => new SleepyGreeterService());

        // The mismatch reaches the runtime as a retryable ErrorMessage, not a terminal
        // OutputCommand failure: redeploying the handler code the journal was written by has to
        // be able to recover the invocation.
        var error = AssertJournalMismatchResponse(responseData);

        // Which side is which, pinned: the handler "expects" the operation this attempt issued,
        // the journal "recorded" what the previous execution wrote. Swapping the two would send
        // an operator looking at the wrong code.
        Assert.Contains("the handler expects Sleep", error.Message);
        Assert.Contains("previous execution recorded Run (RunCommand)", error.Message);
        Assert.Equal(1u, error.RelatedCommandIndex);
    }

    [Fact]
    public async Task HandleAsync_JournalMismatch_IsNotCaughtByHandlerCompensation()
    {
        var service = new CompensatingSleeperService();

        var responseData = await RunMismatchingReplayAsync(ServiceProtocolVersion.V7, () => service);

        AssertJournalMismatchResponse(responseData);
        Assert.False(service.CompensationRan, "A journal mismatch must not surface as a TerminalException");
    }

    [Fact]
    public async Task HandleAsync_JournalMismatchSwallowedByHandler_StillFailsTheAttempt()
    {
        // The handler catches every exception around its durable sleep and returns normally.
        // The mismatch recorded on the state machine still decides the outcome, so the attempt
        // ends with the 570 ErrorMessage instead of journaling an output against a journal the
        // handler no longer matches.
        var responseData = await RunMismatchingReplayAsync(
            ServiceProtocolVersion.V7, () => new SwallowingSleeperService());

        AssertJournalMismatchResponse(responseData);
    }

    [Fact]
    public async Task HandleAsync_JournalMismatchSwallowedThenTerminalFailure_StillFailsTheAttemptAsMismatch()
    {
        // The handler catches every exception around its durable sleep and throws a
        // TerminalException instead. The recorded mismatch still decides the outcome: a terminal
        // OutputCommand would end the invocation for good, and redeploying the code the journal
        // was written by could no longer recover it.
        var responseData = await RunMismatchingReplayAsync(
            ServiceProtocolVersion.V7, () => new FailingSleeperService());

        AssertJournalMismatchResponse(responseData);
    }

    [Fact]
    public async Task HandleAsync_ReplayedRunOfAnotherName_FailsWithRetryableJournalMismatch()
    {
        // Same command kind, different side effect: the journal recorded a run named "charge",
        // the deployed handler runs "effect". Without the field check the handler would be
        // handed the charge's recorded result.
        var responseData = await RunMismatchingReplayAsync(
            ServiceProtocolVersion.V7, () => new RunGreeterService(),
            MessageType.RunCommand, ProtobufCodec.CreateRunCommand("charge", 1).ToByteArray());

        var error = AssertJournalMismatchResponse(responseData);

        Assert.Contains("the Run command differs on name", error.Message);
        Assert.Contains("recorded 'charge', attempted 'effect'", error.Message);
        Assert.Equal(1u, error.RelatedCommandIndex);
    }

    [Fact]
    public async Task HandleAsync_ReplayedCallToAnotherHandler_FailsWithRetryableJournalMismatch()
    {
        // The journal recorded a call to Downstream/Fetch; the deployed handler calls
        // Downstream/Get.
        var journaledCall = new Gen.CallCommandMessage
        {
            ServiceName = "Downstream",
            HandlerName = "Fetch",
            InvocationIdNotificationIdx = 1,
            ResultCompletionId = 2
        }.ToByteArray();

        var responseData = await RunMismatchingReplayAsync(
            ServiceProtocolVersion.V7, () => new CallThenSleepService(),
            MessageType.CallCommand, journaledCall, "Orchestrate");

        var error = AssertJournalMismatchResponse(responseData);

        Assert.Contains("the Call command differs on handler_name", error.Message);
        Assert.Contains("recorded 'Fetch', attempted 'Get'", error.Message);
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
    public async Task HandleAsync_ReplayedRace_PicksTheWinnerTheJournalRecorded()
    {
        // The journal delivered the second future's completion first, so the original attempt
        // returned its result. Replay must pick the same winner — with every completion already
        // settled, argument order must not decide the race.
        var responseData = await RunReplayedTwoCallJournalAsync("Race", 4, 2);

        AssertCompletedWith(responseData, "\"from-b\"");
    }

    [Fact]
    public async Task HandleAsync_ReplayedRace_FirstCompletionFirst_PicksTheFirstFuture()
    {
        // Mirror of the test above: the journal delivered the first future's completion first,
        // so that future wins — the branch follows the journal rather than being flipped.
        var responseData = await RunReplayedTwoCallJournalAsync("Race", 2, 4);

        AssertCompletedWith(responseData, "\"from-a\"");
    }

    [Fact]
    public async Task HandleAsync_ReplayedAny_PicksTheWinnerTheJournalRecorded()
    {
        var responseData = await RunReplayedTwoCallJournalAsync("Any", 4, 2);

        AssertCompletedWith(responseData, "\"from-b\"");
    }

    [Fact]
    public async Task HandleAsync_ReplayedAny_FirstCompletionFirst_PicksTheFirstFuture()
    {
        var responseData = await RunReplayedTwoCallJournalAsync("Any", 2, 4);

        AssertCompletedWith(responseData, "\"from-a\"");
    }

    [Fact]
    public async Task HandleAsync_ReplayedWaitAll_YieldsInJournaledCompletionOrder()
    {
        // The journal delivered the sleep completion (id 3) before the call completion (id 2),
        // so the original attempt saw the timer first. The replayed enumeration must match.
        var responseData = await RunReplayedCallAndTimerJournalAsync(3, 2);

        AssertCompletedWith(responseData, "\"timer,call\"");
    }

    [Fact]
    public async Task HandleAsync_ReplayedWaitAll_CallCompletionFirst_YieldsCallFirst()
    {
        var responseData = await RunReplayedCallAndTimerJournalAsync(2, 3);

        AssertCompletedWith(responseData, "\"call,timer\"");
    }

    /// <summary>
    ///     Runs a <see cref="CombinatorGreeterService" /> handler against a resumed journal of
    ///     <paramref name="knownEntries" /> entries: Start, the InputCommand, then whatever
    ///     <paramref name="writeJournal" /> appends.
    /// </summary>
    private static async Task<byte[]> RunReplayedCombinatorJournalAsync(string handlerName,
        Action<MemoryStream> writeJournal, uint knownEntries)
    {
        var startPayload = BuildStartMessagePayload("combinator-inv-1", knownEntries, "test-key", 7);
        var inputCommandPayload = BuildInputCommandPayload(JsonSerializer.SerializeToUtf8Bytes("World"));

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        writeJournal(requestStream);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(CombinatorGreeterService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == handlerName);

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new CombinatorGreeterService()),
            ServiceProtocolVersion.V6,
            CancellationToken.None);

        return responseStream.ToArray();
    }

    /// <summary>
    ///     Replays a journal of two call futures: both CallCommands, both invocation-id
    ///     notifications, then the two result notifications in
    ///     <paramref name="resultCompletionOrder" /> — the order the runtime delivered them on
    ///     the original attempt. Completion id 2 is the first future ("from-a"), id 4 the
    ///     second ("from-b").
    /// </summary>
    private static Task<byte[]> RunReplayedTwoCallJournalAsync(string handlerName,
        params uint[] resultCompletionOrder)
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        return RunReplayedCombinatorJournalAsync(handlerName, requestStream =>
        {
            WriteFramedMessage(requestStream, MessageType.CallCommand, new Gen.CallCommandMessage
            {
                ServiceName = "Downstream",
                HandlerName = "A",
                Parameter = ByteString.CopyFrom(inputJson),
                InvocationIdNotificationIdx = 1,
                ResultCompletionId = 2
            }.ToByteArray());
            WriteFramedMessage(requestStream, MessageType.CallCommand, new Gen.CallCommandMessage
            {
                ServiceName = "Downstream",
                HandlerName = "B",
                Parameter = ByteString.CopyFrom(inputJson),
                InvocationIdNotificationIdx = 3,
                ResultCompletionId = 4
            }.ToByteArray());

            uint[] invocationIdNotifications = [1, 3];
            foreach (var completionId in invocationIdNotifications)
            {
                WriteFramedMessage(requestStream, MessageType.CallInvocationIdCompletion,
                    new Gen.CallInvocationIdCompletionNotificationMessage
                    {
                        CompletionId = completionId,
                        InvocationId = $"inv-downstream-{completionId}"
                    }.ToByteArray());
            }

            foreach (var completionId in resultCompletionOrder)
            {
                WriteFramedMessage(requestStream, MessageType.CallCompletion,
                    new Gen.CallCompletionNotificationMessage
                    {
                        CompletionId = completionId,
                        Value = new Gen.Value
                        {
                            Content = ByteString.CopyFrom(
                                JsonSerializer.SerializeToUtf8Bytes(completionId == 2 ? "from-a" : "from-b"))
                        }
                    }.ToByteArray());
            }
        }, 7);
    }

    /// <summary>
    ///     Replays a resumed journal of a call future (completion id 2) and a timer
    ///     (completion id 3) through <see cref="CombinatorGreeterService" />'s WaitAll handler,
    ///     with the two result notifications in <paramref name="resultCompletionOrder" />.
    /// </summary>
    private static Task<byte[]> RunReplayedCallAndTimerJournalAsync(params uint[] resultCompletionOrder)
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        return RunReplayedCombinatorJournalAsync("Wait", requestStream =>
        {
            WriteFramedMessage(requestStream, MessageType.CallCommand, new Gen.CallCommandMessage
            {
                ServiceName = "Downstream",
                HandlerName = "Get",
                Parameter = ByteString.CopyFrom(inputJson),
                InvocationIdNotificationIdx = 1,
                ResultCompletionId = 2
            }.ToByteArray());
            WriteFramedMessage(requestStream, MessageType.SleepCommand, new Gen.SleepCommandMessage
            {
                WakeUpTime = (ulong)DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
                ResultCompletionId = 3
            }.ToByteArray());
            WriteFramedMessage(requestStream, MessageType.CallInvocationIdCompletion,
                new Gen.CallInvocationIdCompletionNotificationMessage
                {
                    CompletionId = 1,
                    InvocationId = "inv-downstream"
                }.ToByteArray());

            foreach (var completionId in resultCompletionOrder)
            {
                if (completionId == 3)
                {
                    WriteFramedMessage(requestStream, MessageType.SleepCompletion,
                        new Gen.SleepCompletionNotificationMessage
                        {
                            CompletionId = 3,
                            Void = new Gen.Void()
                        }.ToByteArray());
                }
                else
                {
                    WriteFramedMessage(requestStream, MessageType.CallCompletion,
                        new Gen.CallCompletionNotificationMessage
                        {
                            CompletionId = completionId,
                            Value = new Gen.Value
                            {
                                Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("from-call"))
                            }
                        }.ToByteArray());
                }
            }
        }, 6);
    }

    /// <summary>Asserts the response is exactly an OutputCommand carrying the given JSON, then End.</summary>
    private static void AssertCompletedWith(byte[] responseData, string expectedJson)
    {
        var offset = 0;

        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal(expectedJson, Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    private async Task<byte[]> RunReplayedRunJournalAsync(bool includeStoredCompletion)
    {
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        var knownEntries = includeStoredCompletion ? 3u : 2u;
        var startPayload = BuildStartMessagePayload("resume-run-1", knownEntries, "test-key", 7);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);
        var runCommandPayload = ProtobufCodec.CreateRunCommand("effect", 1).ToByteArray();

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        WriteFramedMessage(requestStream, MessageType.RunCommand, runCommandPayload);
        if (includeStoredCompletion)
        {
            var runCompletionPayload = new Gen.RunCompletionNotificationMessage
            {
                CompletionId = 1,
                Value = new Gen.Value
                {
                    Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("journaled-World"))
                }
            }.ToByteArray();
            WriteFramedMessage(requestStream, MessageType.RunCompletion, runCompletionPayload);
        }

        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(RunGreeterService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Greet");

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new RunGreeterService()),
            ServiceProtocolVersion.V6,
            CancellationToken.None);

        return responseStream.ToArray();
    }

    [Fact]
    public async Task HandleAsync_ReplayedRunWithStoredCompletion_ResolvesWithoutReExecuting()
    {
        var responseData = await RunReplayedRunJournalAsync(includeStoredCompletion: true);
        var offset = 0;

        // The stored completion wins: the closure is NOT re-executed ("fresh-World" would
        // betray re-execution) and no new proposal is written.
        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal("\"journaled-World\"", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_ReplayedRunWithoutCompletion_ReExecutesAndProposes()
    {
        // The previous attempt crashed after the runtime persisted the RunCommand but before
        // it persisted the ProposeRunCompletion. Awaiting a notification would suspend forever
        // (only the SDK can produce it) — the closure must re-execute and propose again.
        var responseData = await RunReplayedRunJournalAsync(includeStoredCompletion: false);
        var offset = 0;

        var (proposalHeader, proposalPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.ProposeRunCompletion, proposalHeader.Type);
        var proposal = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(proposalPayload);
        Assert.Equal(1u, proposal.ResultCompletionId);
        Assert.Equal("\"fresh-World\"", proposal.Value.ToStringUtf8());

        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal("\"fresh-World\"", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

        var (endHeader, _) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.End, endHeader.Type);

        Assert.Equal(responseData.Length, offset);
    }

    /// <summary>Run re-execution after a lost ProposeRunCompletion honours RetryPolicy.</summary>
    [Fact]
    public async Task HandleAsync_ReplayedRunWithoutCompletion_ReExecutesWithRetryPolicy()
    {
        // Same crash window as above, but the Run carries a retry policy: the re-executed closure
        // must retry its transient failures instead of failing the invocation on the first throw.
        var inputJson = JsonSerializer.SerializeToUtf8Bytes("World");

        var startPayload = BuildStartMessagePayload("resume-run-retry-1", 2, "test-key", 7);
        var inputCommandPayload = BuildInputCommandPayload(inputJson);
        var runCommandPayload = ProtobufCodec.CreateRunCommand("effect", 1).ToByteArray();

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        WriteFramedMessage(requestStream, MessageType.RunCommand, runCommandPayload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(RetryingRunGreeterService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == "Greet");
        var service = new RetryingRunGreeterService();

        var handler = new InvocationHandler();

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => service),
            ServiceProtocolVersion.V6,
            CancellationToken.None);

        var responseData = responseStream.ToArray();
        var offset = 0;

        Assert.Equal(3, service.Attempts);

        var (proposalHeader, proposalPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.ProposeRunCompletion, proposalHeader.Type);
        var proposal = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(proposalPayload);
        Assert.Equal(1u, proposal.ResultCompletionId);
        Assert.Equal("\"retried-World\"", proposal.Value.ToStringUtf8());

        var (outputHeader, outputPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal("\"retried-World\"", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));

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

    /// <summary>
    ///     Runs an invocation whose request stream carries Start + Input and then the given live
    ///     frames (notifications the runtime would deliver), and returns the raw response bytes.
    /// </summary>
    private static async Task<byte[]> RunWithFramesAsync<TService>(
        string handlerName, object input, Func<TService> factory,
        params (MessageType Type, byte[] Payload)[] frames)
        where TService : class
    {
        var startPayload = BuildStartMessagePayload("signal-e2e", 1, "", 42);
        var inputCommandPayload = BuildInputCommandPayload(JsonSerializer.SerializeToUtf8Bytes(input));

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startPayload);
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputCommandPayload);
        foreach (var (type, payload) in frames)
            WriteFramedMessage(requestStream, type, payload);
        requestStream.Position = 0;

        var responseStream = new MemoryStream();
        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(TService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == handlerName);

        await new InvocationHandler().HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(responseStream),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => factory()),
            ServiceProtocolVersion.V7,
            CancellationToken.None);

        return responseStream.ToArray();
    }

    [Fact]
    public async Task HandleAsync_SignalResolvedThroughHandle_ReachesTheAwaitingHandler()
    {
        // 1. The resolving handler addresses the target invocation through its handle.
        var resolverResponse = await RunWithFramesAsync<SignalResolverService>(
            "Resolve", "inv-awaiter", () => new SignalResolverService());

        var offset = 0;
        var (commandHeader, commandPayload) = ReadFramedMessage(resolverResponse, ref offset);
        Assert.Equal(MessageType.SendSignalCommand, commandHeader.Type);
        var command = Gen.SendSignalCommandMessage.Parser.ParseFrom(commandPayload);
        Assert.Equal("inv-awaiter", command.TargetInvocationId);
        Assert.Equal("approval", command.Name);
        Assert.Equal("\"granted\"", command.Value.Content.ToStringUtf8());

        // 2. The runtime turns that command into a notification for the awaiting invocation.
        var notification = new Gen.SignalNotificationMessage { Name = command.Name, Value = command.Value };
        var awaitingResponse = await RunWithFramesAsync<SignalGreeterService>(
            "Greet", "World", () => new SignalGreeterService(),
            (MessageType.SignalNotification, notification.ToByteArray()));

        offset = 0;
        var (outputHeader, outputPayload) = ReadFramedMessage(awaitingResponse, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        Assert.Equal("\"granted, World!\"", Encoding.UTF8.GetString(ExtractOutputContent(outputPayload)));
    }

    [Fact]
    public async Task HandleAsync_SignalRejectedThroughHandle_FailsTheAwaitingHandler()
    {
        var resolverResponse = await RunWithFramesAsync<SignalResolverService>(
            "Reject", "inv-awaiter", () => new SignalResolverService());

        var offset = 0;
        var (commandHeader, commandPayload) = ReadFramedMessage(resolverResponse, ref offset);
        Assert.Equal(MessageType.SendSignalCommand, commandHeader.Type);
        var command = Gen.SendSignalCommandMessage.Parser.ParseFrom(commandPayload);
        Assert.Equal(Gen.SendSignalCommandMessage.ResultOneofCase.Failure, command.ResultCase);
        Assert.Equal("not approved", command.Failure.Message);

        // The awaiting handler sees the rejection as a terminal failure, not a value.
        var notification = new Gen.SignalNotificationMessage { Name = command.Name, Failure = command.Failure };
        var awaitingResponse = await RunWithFramesAsync<SignalGreeterService>(
            "Greet", "World", () => new SignalGreeterService(),
            (MessageType.SignalNotification, notification.ToByteArray()));

        offset = 0;
        var (outputHeader, outputPayload) = ReadFramedMessage(awaitingResponse, ref offset);
        Assert.Equal(MessageType.OutputCommand, outputHeader.Type);
        var output = Gen.OutputCommandMessage.Parser.ParseFrom(outputPayload);
        Assert.Equal(Gen.OutputCommandMessage.ResultOneofCase.Failure, output.ResultCase);
        Assert.Equal("not approved", output.Failure.Message);
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
        // The first user awakeable uses signal index 17 (0-16 are reserved built-ins).
        Assert.Equal(new uint[] { 17 }, suspension.AwaitingOn.WaitingSignals);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_InputClosesDuringNamedSignal_ProducesSuspensionWithSignalName()
    {
        var responseData = await RunUntilInputClosedAsync<SignalGreeterService>(
            "Greet", ServiceProtocolVersion.V7, () => new SignalGreeterService());

        var offset = 0;

        // Awaiting a signal writes no command — the suspension frame is the only output.
        var (suspensionHeader, suspensionPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Suspension, suspensionHeader.Type);
        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(suspensionPayload);
        Assert.NotNull(suspension.AwaitingOn);
        Assert.Empty(suspension.AwaitingOn.WaitingCompletions);
        Assert.Empty(suspension.AwaitingOn.WaitingSignals);
        // A named signal is advertised by name, so the runtime knows what to resume on.
        Assert.Equal("approval", Assert.Single(suspension.AwaitingOn.WaitingNamedSignals));

        // SuspensionMessage is terminal on its own: the connection is released with nothing after it.
        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_InputClosesDuringUnnamedSignal_ProducesSuspensionWithSignalIndex()
    {
        var responseData = await RunUntilInputClosedAsync<UnnamedSignalGreeterService>(
            "Greet", ServiceProtocolVersion.V7, () => new UnnamedSignalGreeterService());

        var offset = 0;

        var (suspensionHeader, suspensionPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Suspension, suspensionHeader.Type);
        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(suspensionPayload);
        // The first user signal index is 17 (0-16 are reserved built-ins).
        Assert.Equal(new uint[] { 17 }, suspension.AwaitingOn.WaitingSignals);
        Assert.Empty(suspension.AwaitingOn.WaitingNamedSignals);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_InputClosesDuringCall_SuspendsOnResultIdOnly()
    {
        var responseData = await RunUntilInputClosedAsync<CallGreeterService>(
            "Greet", ServiceProtocolVersion.V7, () => new CallGreeterService());

        var offset = 0;

        var (callHeader, callPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.CallCommand, callHeader.Type);
        var callMsg = Gen.CallCommandMessage.Parser.ParseFrom(callPayload);
        Assert.Equal(1u, callMsg.InvocationIdNotificationIdx);
        Assert.Equal(2u, callMsg.ResultCompletionId);

        // Only the awaited result id may be advertised. Including the never-awaited
        // invocation-id slot (1) would let the runtime complete it immediately, causing a
        // spurious instant resume of every suspended call.
        var (suspensionHeader, suspensionPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Suspension, suspensionHeader.Type);
        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(suspensionPayload);
        Assert.NotNull(suspension.AwaitingOn);
        Assert.Equal(new uint[] { 2 }, suspension.AwaitingOn.WaitingCompletions);
        Assert.Empty(suspension.AwaitingOn.WaitingSignals);
        // Stated against the slot the command itself carries: no wait is registered for it, so
        // it cannot appear here however the SDK reads the invocation id back.
        Assert.DoesNotContain(callMsg.InvocationIdNotificationIdx, suspension.AwaitingOn.WaitingCompletions);

        Assert.Equal(responseData.Length, offset);
    }

    [Fact]
    public async Task HandleAsync_InputClosesDuringAllSettled_SuspendsInsteadOfSettlingFailures()
    {
        var responseData = await RunUntilInputClosedAsync<FanOutGreeterService>(
            "Greet", ServiceProtocolVersion.V7, () => new FanOutGreeterService());

        var offset = 0;

        // Both fanned-out call commands are flushed before the handler parks on AllSettled.
        var (callAHeader, callAPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.CallCommand, callAHeader.Type);
        var callA = Gen.CallCommandMessage.Parser.ParseFrom(callAPayload);
        var (callBHeader, callBPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.CallCommand, callBHeader.Type);
        var callB = Gen.CallCommandMessage.Parser.ParseFrom(callBPayload);

        // EOF poisons the pending call results. AllSettled must NOT settle them as failures
        // and complete the handler with fabricated output — the invocation must suspend.
        var (suspensionHeader, suspensionPayload) = ReadFramedMessage(responseData, ref offset);
        Assert.Equal(MessageType.Suspension, suspensionHeader.Type);

        // Call futures read the called invocation's id from its notification slot without
        // registering a wait on it, so only the two result ids may be advertised here.
        var suspension = Gen.SuspensionMessage.Parser.ParseFrom(suspensionPayload);
        Assert.Equal(new[] { callA.ResultCompletionId, callB.ResultCompletionId },
            suspension.AwaitingOn.WaitingCompletions);

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
