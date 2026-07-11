using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.Observability;

// Each observability test class drives its own uniquely named services so that
// listener-based assertions (MeterListener / ActivityListener are process-global)
// can filter out invocations from other test classes running in parallel.

[Service(Name = "ObsMetricsGreeter")]
public class ObsMetricsGreeterService
{
    [Handler]
    public Task<string> Greet(Context ctx, string name)
    {
        return Task.FromResult($"Hello, {name}!");
    }
}

[Service(Name = "ObsMetricsFailing")]
public class ObsMetricsFailingService
{
    [Handler]
    public Task<string> Fail(Context ctx, string input)
    {
        throw new TerminalException("Something went wrong");
    }
}

[Service(Name = "ObsActivityGreeter")]
public class ObsActivityGreeterService
{
    [Handler]
    public Task<string> Greet(Context ctx, string name)
    {
        return Task.FromResult($"Hello, {name}!");
    }
}

[Service(Name = "ObsActivityRunner")]
public class ObsActivityRunnerService
{
    [Handler]
    public async Task<int> Compute(Context ctx, string input)
    {
        return await ctx.Run("side-effect", () => 42);
    }
}

[Service(Name = "ObsLogging")]
public class ObsLoggingService
{
    [Handler]
    public Task<string> LogSomething(Context ctx, string input)
    {
        // Direct ILogger.Log call: test projects also build with CA1848 (LoggerMessage) enforced.
        ctx.Logger.Log(LogLevel.Information, default, "hello from handler", null, static (state, _) => state);
        return Task.FromResult("ok");
    }
}

/// <summary>
///     Drives a single invocation through <see cref="InvocationHandler" /> using the
///     in-memory-pipe pattern from Integration/ProtocolIntegrationTests.cs:
///     a binary stream of StartMessage + InputCommand is fed in, the response is discarded.
/// </summary>
internal static class ObservabilityTestDriver
{
    public static async Task DriveAsync<TService>(
        string handlerName, object input, InvocationHandler handler, string invocationId)
        where TService : class, new()
    {
        var startMsg = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8(invocationId),
            DebugId = invocationId,
            KnownEntries = 1,
            Key = "obs-key",
            RandomSeed = 1
        };

        var inputMsg = new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(input)) }
        };

        var requestStream = new MemoryStream();
        WriteFramedMessage(requestStream, MessageType.Start, startMsg.ToByteArray());
        WriteFramedMessage(requestStream, MessageType.InputCommand, inputMsg.ToByteArray());
        requestStream.Position = 0;

        var serviceDef = ServiceDefinitionRegistry.TryGet(typeof(TService))!;
        var handlerDef = serviceDef.Handlers.First(h => h.Name == handlerName);

        await handler.HandleAsync(
            PipeReader.Create(requestStream),
            PipeWriter.Create(new MemoryStream()),
            serviceDef,
            handlerDef,
            new FuncServiceProvider(_ => new TService()),
            CancellationToken.None);
    }

    private static void WriteFramedMessage(MemoryStream stream, MessageType type, byte[] payload)
    {
        Span<byte> header = stackalloc byte[MessageHeader.Size];
        MessageHeader.Create(type, MessageFlags.None, (uint)payload.Length).Write(header);
        stream.Write(header);
        stream.Write(payload);
    }

    private sealed class FuncServiceProvider(Func<Type, object> factory) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return factory(serviceType);
        }
    }
}
