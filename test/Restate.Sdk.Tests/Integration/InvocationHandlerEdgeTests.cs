using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.Integration;

/// <summary>
///     Plan 07 §1.2 (InvocationHandler hard paths H8/H10) — the <see cref="InvocationHandler" />
///     catch-arm matrix and context-creation switch that §4.2.1/§4.9 exercise only on the happy
///     path. Drives the REAL <see cref="InvocationHandler.HandleAsync" /> over a duplex pipe with
///     HAND-BUILT <see cref="ServiceDefinition" />/<see cref="HandlerDefinition" /> whose invokers
///     throw each distinct exception type, asserting the correct terminal frame (or absence of one)
///     on the wire:
///       * <see cref="ProtocolException" /> → Error frame (code 500);
///       * <see cref="RestateRetryableException" /> → Error frame carrying the next-retry-delay;
///       * a generic exception → Error frame (code 500);
///       * <see cref="SuspendedException" /> raised by the handler → NO Error/Output frame;
///       * the trace-parent activity path (StartActivity tags);
///       * every <see cref="ServiceType" /> × shared arm of CreateContext.
/// </summary>
public class InvocationHandlerEdgeTests
{
    private const int WatchdogMs = 10_000;

    private sealed class FuncServiceProvider(Func<Type, object?> factory) : IServiceProvider
    {
        public object? GetService(Type serviceType) => factory(serviceType);
    }

    private static ServiceDefinition Service(ServiceType type, HandlerInvoker invoker,
        bool isShared = false, bool hasInput = true) =>
        new()
        {
            Name = "EdgeService",
            Type = type,
            Factory = _ => new object(),
            Handlers =
            [
                new HandlerDefinition
                {
                    Name = "Run",
                    IsShared = isShared,
                    Invoker = invoker,
                    HasInput = hasInput,
                    InputDeserializer = hasInput
                        ? seq => JsonSerializer.Deserialize<string>(seq.ToArray())
                        : null
                }
            ]
        };

    // Frames Start + Input onto a duplex pipe, runs HandleAsync, and returns the response frames.
    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> RunHandlerAsync(
        ServiceDefinition service, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var request = new Pipe();
        var response = new Pipe();

        var start = CreateStartMessage("inv-handler-edge", 1);
        var inputMsg = new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("hi")) }
        };
        if (headers is not null)
            foreach (var (k, v) in headers)
                inputMsg.Headers.Add(new Gen.Header { Key = k, Value = v });

        // Build the request frames with an UNCANCELLED token — the test's cancellation is meant to
        // exercise HandleAsync's OperationCanceledException arm, not break the request setup itself.
        var requestWriter = new ProtocolWriter(request.Writer);
        requestWriter.WriteMessage(MessageType.Start, start.ToByteArray());
        requestWriter.WriteMessage(MessageType.InputCommand, inputMsg.ToByteArray());
        await request.Writer.FlushAsync(CancellationToken.None);
        await request.Writer.CompleteAsync();

        var handler = new InvocationHandler();
        await AwaitBounded(handler.HandleAsync(
            request.Reader, response.Writer, service, service.Handlers[0],
            new FuncServiceProvider(_ => new object()), ct));

        await response.Writer.CompleteAsync();
        var reader = new ProtocolReader(response.Reader);
        var frames = new List<(MessageHeader, byte[])>();
        while (await reader.ReadMessageAsync(CancellationToken.None) is { } message)
        {
            frames.Add((message.Header, message.Payload.ToArray()));
            message.Dispose();
        }

        return frames;
    }

    // ---- Catch-arm matrix --------------------------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task ProtocolException_FromHandler_EmitsErrorFrame()
    {
        var frames = await RunHandlerAsync(Service(ServiceType.Service,
            (_, _, _, _) => throw new ProtocolException("bad protocol")));

        var error = frames.Single(f => f.Header.Type == MessageType.Error);
        var parsed = Gen.ErrorMessage.Parser.ParseFrom(error.Payload);
        Assert.Equal(500u, parsed.Code);
        Assert.Contains("bad protocol", parsed.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task RestateRetryableException_FromHandler_EmitsErrorWithRetryDelay()
    {
        var frames = await RunHandlerAsync(Service(ServiceType.Service,
            (_, _, _, _) => throw new RestateRetryableException("retry me", 503, TimeSpan.FromMilliseconds(1500))));

        var error = frames.Single(f => f.Header.Type == MessageType.Error);
        var parsed = Gen.ErrorMessage.Parser.ParseFrom(error.Payload);
        Assert.Equal(503u, parsed.Code);
        Assert.True(parsed.HasNextRetryDelay);
        Assert.Equal(1500ul, parsed.NextRetryDelay);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task GenericException_FromHandler_EmitsErrorFrame500()
    {
        var frames = await RunHandlerAsync(Service(ServiceType.Service,
            (_, _, _, _) => throw new InvalidOperationException("kaboom")));

        var error = frames.Single(f => f.Header.Type == MessageType.Error);
        var parsed = Gen.ErrorMessage.Parser.ParseFrom(error.Payload);
        Assert.Equal(500u, parsed.Code);
        Assert.Contains("kaboom", parsed.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task TerminalException_FromHandler_EmitsFailureOutput()
    {
        var frames = await RunHandlerAsync(Service(ServiceType.Service,
            (_, _, _, _) => throw new TerminalException("business rule", 409)));

        var output = frames.Single(f => f.Header.Type == MessageType.OutputCommand);
        var parsed = Gen.OutputCommandMessage.Parser.ParseFrom(output.Payload);
        Assert.NotNull(parsed.Failure);
        Assert.Equal(409u, parsed.Failure.Code);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SuspendedException_FromHandler_EmitsNoErrorOrOutputFrame()
    {
        // The SuspendedException catch arm MUST precede every wire-writing arm: a handler that raises
        // it (mimicking the SM's park-then-suspend) leaves the wire with NO Error and NO Output.
        var frames = await RunHandlerAsync(Service(ServiceType.Service,
            (_, _, _, _) => throw new SuspendedException()));

        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.Error);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.OutputCommand);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CallerCancellation_BeforeRun_EmitsNoTerminalFrame()
    {
        // An already-cancelled external token makes HandleAsync unwind through the
        // OperationCanceledException arm (ct.IsCancellationRequested) without writing a terminal frame.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var frames = await RunHandlerAsync(
            Service(ServiceType.Service, (_, _, _, _) => Task.FromResult<object?>("never")),
            ct: cts.Token);

        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.Error);
    }

    // ---- Catch arms WITH an active Activity (the non-null activity?.SetStatus(Error) arm) -------

    // Runs the handler with a registered ActivityListener so ActivitySource.StartActivity returns a
    // NON-null Activity — the catch arms' `activity?.SetStatus(ActivityStatusCode.Error, ...)` then
    // take their non-null branch (InvocationHandler.cs:80/91/99/106), which the listener-less
    // catch-arm tests above never reach.
    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> RunHandlerWithListenerAsync(
        ServiceDefinition service)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Restate.Sdk",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return await RunHandlerAsync(service);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task TerminalException_WithActivity_SetsErrorStatus_AndEmitsFailureOutput()
    {
        var frames = await RunHandlerWithListenerAsync(Service(ServiceType.Service,
            (_, _, _, _) => throw new TerminalException("business rule", 409)));

        var output = frames.Single(f => f.Header.Type == MessageType.OutputCommand);
        var parsed = Gen.OutputCommandMessage.Parser.ParseFrom(output.Payload);
        Assert.Equal(409u, parsed.Failure.Code);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task ProtocolException_WithActivity_SetsErrorStatus_AndEmitsErrorFrame()
    {
        var frames = await RunHandlerWithListenerAsync(Service(ServiceType.Service,
            (_, _, _, _) => throw new ProtocolException("bad protocol")));

        var error = frames.Single(f => f.Header.Type == MessageType.Error);
        Assert.Equal(500u, Gen.ErrorMessage.Parser.ParseFrom(error.Payload).Code);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task RestateRetryableException_WithActivity_SetsErrorStatus_AndEmitsRetryDelay()
    {
        var frames = await RunHandlerWithListenerAsync(Service(ServiceType.Service,
            (_, _, _, _) => throw new RestateRetryableException("retry me", 503, TimeSpan.FromMilliseconds(1500))));

        var error = frames.Single(f => f.Header.Type == MessageType.Error);
        var parsed = Gen.ErrorMessage.Parser.ParseFrom(error.Payload);
        Assert.Equal(503u, parsed.Code);
        Assert.Equal(1500ul, parsed.NextRetryDelay);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task GenericException_WithActivity_SetsErrorStatus_AndEmitsErrorFrame500()
    {
        var frames = await RunHandlerWithListenerAsync(Service(ServiceType.Service,
            (_, _, _, _) => throw new InvalidOperationException("kaboom")));

        var error = frames.Single(f => f.Header.Type == MessageType.Error);
        Assert.Equal(500u, Gen.ErrorMessage.Parser.ParseFrom(error.Payload).Code);
    }

    // ---- Trace-parent activity path ----------------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task TraceParentHeader_IsParsedIntoActivity()
    {
        // A listener forces ActivitySource.StartActivity to return a non-null Activity so the
        // SetTag block (StartActivity) executes; the traceparent header drives ActivityContext.TryParse.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Restate.Sdk",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["tracestate"] = "vendor=value"
        };

        var frames = await RunHandlerAsync(
            Service(ServiceType.Service, (_, _, _, _) => Task.FromResult<object?>("ok")),
            headers);

        Assert.Contains(frames, f => f.Header.Type == MessageType.OutputCommand);
    }

    // ---- CreateContext switch: every ServiceType × shared arm --------------------------------

    [Theory(Timeout = WatchdogMs)]
    [InlineData(ServiceType.Service, false)]
    [InlineData(ServiceType.VirtualObject, false)]
    [InlineData(ServiceType.VirtualObject, true)]
    [InlineData(ServiceType.Workflow, false)]
    [InlineData(ServiceType.Workflow, true)]
    public async Task CreateContext_EveryServiceTypeAndSharedArm_ProducesOutput(
        ServiceType type, bool isShared)
    {
        // Each (type, isShared) pair selects a distinct Default*Context in CreateContext; the handler
        // ignores the context and completes, so each arm is constructed and the invocation closes cleanly.
        var frames = await RunHandlerAsync(Service(type,
            (_, _, _, _) => Task.FromResult<object?>("ok"), isShared));

        Assert.Contains(frames, f => f.Header.Type == MessageType.OutputCommand);
        Assert.Contains(frames, f => f.Header.Type == MessageType.End);
    }
}
