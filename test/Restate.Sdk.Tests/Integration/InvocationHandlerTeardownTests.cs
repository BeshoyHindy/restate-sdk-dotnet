using System.Buffers;
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
///     Plan 07 §1.2 (InvocationHandler hard paths H8/H10) — the residual teardown arms that
///     <see cref="InvocationHandlerEdgeTests" /> reaches the OUTER body of but not the INNER
///     recovery branches the gate flags uncovered (InvocationHandler.cs:82/93/101/108/132/137/138
///     and the CreateContext default arm :184). These are the "stream already broken" inner
///     <c>catch { }</c> blocks each error arm wraps its <c>sm.Fail*Async</c> call in, the pump-fault
///     <c>catch (Exception)</c> arm in the finally, and the impossible-ServiceType default switch arm.
///
///     The inner catches are driven by faulting the OUTBOUND <see cref="PipeWriter" /> on flush: the
///     handler throws its exception, the matching outer catch runs <c>sm.Fail*Async</c>, the terminal
///     frame is buffered, and the flush throws — exercising the inner swallow that exists precisely so
///     a doubly-broken stream never propagates a second exception into Kestrel after the response ended.
/// </summary>
public class InvocationHandlerTeardownTests
{
    private const int WatchdogMs = 10_000;

    private sealed class FuncServiceProvider(Func<Type, object?> factory) : IServiceProvider
    {
        public object? GetService(Type serviceType) => factory(serviceType);
    }

    /// <summary>
    ///     A <see cref="PipeWriter" /> decorator that buffers writes into a real inner pipe writer
    ///     (so the SM can stage its terminal frame) but throws on <see cref="FlushAsync" /> after the
    ///     first <paramref name="throwAfterFlushes" /> flushes succeed. This models a transport whose
    ///     socket breaks AFTER the handler has produced output — the exact state the inner
    ///     <c>catch { }</c> arms exist to absorb.
    /// </summary>
    private sealed class FlushFaultingPipeWriter(PipeWriter inner, int throwAfterFlushes = 0) : PipeWriter
    {
        private int _flushCount;

        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            // Let the first N flushes through so any pre-error buffering commits, then break the
            // pipe so the error arm's flush surfaces the IOException into the inner catch.
            if (_flushCount++ >= throwAfterFlushes)
                throw new IOException("response transport broken on flush");
            return inner.FlushAsync(cancellationToken);
        }
    }

    private static ServiceDefinition Service(ServiceType type, HandlerInvoker invoker) =>
        new()
        {
            Name = "TeardownService",
            Type = type,
            Factory = _ => new object(),
            Handlers =
            [
                new HandlerDefinition
                {
                    Name = "Run",
                    IsShared = false,
                    Invoker = invoker,
                    HasInput = true,
                    InputDeserializer = seq => JsonSerializer.Deserialize<string>(seq.ToArray())
                }
            ]
        };

    // Frames Start + Input (+ any extra trailing frames) onto the request pipe, runs HandleAsync
    // against the given outbound writer, and returns whatever frames were decodable from the
    // outbound side. The handler call itself is bounded so a regression that hangs fails fast.
    private static async Task RunHandlerAsync(
        ServiceDefinition service, PipeWriter responseWriter,
        IEnumerable<(MessageType Type, byte[] Payload)>? trailingRequestFrames = null,
        CancellationToken ct = default)
    {
        var request = new Pipe();

        var start = CreateStartMessage("inv-teardown", 1);
        var inputMsg = new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("hi")) }
        };

        var requestWriter = new ProtocolWriter(request.Writer);
        requestWriter.WriteMessage(MessageType.Start, start.ToByteArray());
        requestWriter.WriteMessage(MessageType.InputCommand, inputMsg.ToByteArray());
        if (trailingRequestFrames is not null)
            foreach (var (type, payload) in trailingRequestFrames)
                requestWriter.WriteMessage(type, payload);
        await request.Writer.FlushAsync(CancellationToken.None);
        await request.Writer.CompleteAsync();

        var handler = new InvocationHandler();
        await AwaitBounded(handler.HandleAsync(
            request.Reader, responseWriter, service, service.Handlers[0],
            new FuncServiceProvider(_ => new object()), ct));
    }

    // ---- Inner "stream already broken" catch arms (Fail*Async throwing on flush) -------------

    [Fact(Timeout = WatchdogMs)]
    public async Task TerminalException_WithBrokenFlush_SwallowsSecondaryFailure()
    {
        // InvocationHandler.cs:81-82 — the TerminalException arm runs FailTerminalAsync, whose flush
        // throws (transport already broken). The inner catch swallows it so HandleAsync still unwinds
        // cleanly instead of propagating a second exception into Kestrel.
        var response = new Pipe();
        var faulting = new FlushFaultingPipeWriter(response.Writer);

        // Must not throw — the inner catch absorbs the broken-flush IOException.
        await RunHandlerAsync(
            Service(ServiceType.Service, (_, _, _, _) => throw new TerminalException("business", 409)),
            faulting);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task ProtocolException_WithBrokenFlush_SwallowsSecondaryFailure()
    {
        // InvocationHandler.cs:92-93 — the ProtocolException arm's FailAsync flush throws; inner catch swallows.
        var response = new Pipe();
        var faulting = new FlushFaultingPipeWriter(response.Writer);

        await RunHandlerAsync(
            Service(ServiceType.Service, (_, _, _, _) => throw new ProtocolException("bad protocol")),
            faulting);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task RestateRetryableException_WithBrokenFlush_SwallowsSecondaryFailure()
    {
        // InvocationHandler.cs:100-101 — the retryable arm's FailAsync(code, msg, ct, delay) flush
        // throws; inner catch swallows. Carries a next-retry-delay so the delay-bearing overload runs.
        var response = new Pipe();
        var faulting = new FlushFaultingPipeWriter(response.Writer);

        await RunHandlerAsync(
            Service(ServiceType.Service,
                (_, _, _, _) => throw new RestateRetryableException("retry", 503, TimeSpan.FromMilliseconds(750))),
            faulting);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task GenericException_WithBrokenFlush_SwallowsSecondaryFailure()
    {
        // InvocationHandler.cs:107-108 — the catch-all arm's FailAsync flush throws; inner catch swallows.
        var response = new Pipe();
        var faulting = new FlushFaultingPipeWriter(response.Writer);

        await RunHandlerAsync(
            Service(ServiceType.Service, (_, _, _, _) => throw new InvalidOperationException("kaboom")),
            faulting);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task RunRedrive_WithBrokenFlush_SwallowsSecondaryFailure()
    {
        // The RunRedriveException arm's FailAsync flush throws; the inner catch swallows it (G15/G16/G17).
        // A no-policy ctx.Run that fails non-terminally journals a RunCommand (prefix flush) then unwinds
        // with RunRedriveException; we let the prefix flush through and break the TERMINAL FailAsync flush
        // so the redrive arm's "stream already broken" inner catch runs.
        var response = new Pipe();
        // On the blocking redrive path the RunCommand stays BUFFERED (the closure throws RunRedriveException
        // before any proposal/flush), so the FIRST and only flush is the terminal FailAsync flush — break it
        // (throwAfterFlushes=0) so the redrive arm's "stream already broken" inner catch runs, exactly like
        // the other Fail*Async-broken-flush arms above.
        var faulting = new FlushFaultingPipeWriter(response.Writer, throwAfterFlushes: 0);

        await RunHandlerAsync(
            Service(ServiceType.Service, async (_, ctx, _, _) =>
            {
                await ctx.Run<int>("redrive-run",
                    async () => { await Task.Yield(); throw new InvalidOperationException("transient"); });
                return null;
            }),
            faulting);
    }

    // ---- Pump-fault arm in the finally (incoming task faults non-cancellation) ---------------

    [Fact(Timeout = WatchdogMs)]
    public async Task IncomingPumpFault_AfterHandlerCompletes_IsLoggedNotRethrown()
    {
        // InvocationHandler.cs:132-138 — a command-typed frame delivered AFTER preflight makes
        // ProcessIncomingMessagesAsync throw ProtocolException (HandleIncomingMessage's
        // command-after-replay guard). The handler itself completes normally, so no outer catch arm
        // runs; the fault is only observed when the finally awaits incomingTask, hitting the
        // catch (Exception) arm (132) that logs (137-138) instead of rethrowing into Kestrel.
        var response = new Pipe();

        // A bare RunCommand frame is command-typed (IsCommand()) and illegal after the replay batch.
        var stray = ProtobufCodec.CreateRunCommand("stray", 1).ToByteArray();

        await RunHandlerAsync(
            Service(ServiceType.Service, (_, _, _, _) => Task.FromResult<object?>("ok")),
            response.Writer,
            trailingRequestFrames: [(MessageType.RunCommand, stray)]);

        // The handler still produced its Output despite the pump fault — proof the fault was
        // contained to the logging arm and did not abort the response.
        await response.Writer.CompleteAsync();
        var reader = new ProtocolReader(response.Reader);
        var sawOutput = false;
        while (await reader.ReadMessageAsync(CancellationToken.None) is { } message)
        {
            if (message.Header.Type == MessageType.OutputCommand) sawOutput = true;
            message.Dispose();
        }

        Assert.True(sawOutput, "handler Output frame should survive a contained incoming-pump fault");
    }

    // ---- CreateContext default switch arm (impossible ServiceType) ---------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task CreateContext_UnknownServiceType_FallsBackToDefaultContext()
    {
        // InvocationHandler.cs:184 — the switch's default arm guards against an out-of-range
        // ServiceType the three known cases (Service/VirtualObject/Workflow) cannot select. A crafted
        // ServiceDefinition with a cast-out-of-range value drives the fallback to DefaultContext; the
        // handler ignores the context and completes, proving the arm constructs a usable context.
        var response = new Pipe();

        await RunHandlerAsync(
            Service((ServiceType)999, (_, _, _, _) => Task.FromResult<object?>("ok")),
            response.Writer);

        await response.Writer.CompleteAsync();
        var reader = new ProtocolReader(response.Reader);
        var sawOutput = false;
        while (await reader.ReadMessageAsync(CancellationToken.None) is { } message)
        {
            if (message.Header.Type == MessageType.OutputCommand) sawOutput = true;
            message.Dispose();
        }

        Assert.True(sawOutput, "the default-arm DefaultContext should let the handler complete normally");
    }
}
