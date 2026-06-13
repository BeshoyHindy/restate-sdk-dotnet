using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Inbound-CANCEL parity (SignalNotification idx=1 cancels THIS invocation).
///
///     Ground truth: shared-core tags CANCEL NotificationMetadata::Cancellation
///     (vm/transitions/async_results.rs:88-93) and CoreVM::do_progress returns
///     DoProgressResponse::CancelSignalReceived (vm/mod.rs:432-492). Shared-core defines NO
///     cancellation error code and emits no terminal frame itself — the SDK layer owns the surface.
///     This SDK follows the Restate cross-SDK convention: parked durable awaits throw
///     TerminalException(409, "cancelled"), the handler unwinds through the existing
///     `catch (TerminalException)` arm, and the SDK writes a terminal OutputCommand{failure:409} +
///     End (NOT a clean empty Output, NOT a retryable Error/Suspension frame). Child sub-invocation
///     cancel (SendSignalCommand idx=1) is user-driven via CancelInvocationAsync AND automatic on
///     inbound CANCEL for tracked Call children (Rust mod.rs:445-476) — see ChildCancelTests for that
///     path; this file covers the THIS-invocation terminal-frame behavior.
///
///     Each handler signals a deterministic "ready" gate the moment it has parked / entered its run
///     loop; the harness delivers the CANCEL frame only after that gate, so there is NO wall-clock
///     race. Every wait is bounded by <see cref="ProtocolTestHarness.AwaitBounded{T}(Task{T})" />.
/// </summary>
public sealed class CancellationTests
{
    /// <summary>
    ///     (a) A handler parked on Sleep receives an inbound CANCEL → it unwinds PROMPTLY (no hang),
    ///     the SDK emits a terminal OutputCommand carrying the 409 cancellation failure + End, and
    ///     NO Suspension or retryable Error frame is written (the attempt does not retry).
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task ParkedOnSleep_InboundCancel_EmitsTerminal409Output()
    {
        using var ready = new ReadyGate();
        var harness = new CancelHarness(CancelServices.SleepDef, () => new CancelServices(ready));

        await AwaitBounded(harness.RunWithCancelAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready));

        var frames = AssertFrameOrder(harness.Response);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Suspension);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Error);
        AssertCancelOutput(frames);
    }

    /// <summary>
    ///     (b) CANCEL while a handler is mid-Run (value-returning ctx.Run): the Run body observes the
    ///     cancelled handler token and throws OperationCanceledException. The SM translates it into a
    ///     TerminalException(409) at the Run boundary, so the handler emits the SAME 409 terminal
    ///     cancel Output — never a 500 Error frame.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task MidRun_ValueRun_InboundCancel_EmitsTerminal409Output()
    {
        using var ready = new ReadyGate();
        var harness = new CancelHarness(CancelServices.RunValueDef, () => new CancelServices(ready));

        await AwaitBounded(harness.RunWithCancelAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready));

        var frames = AssertFrameOrder(harness.Response);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Error);
        AssertCancelOutput(frames);
    }

    /// <summary>
    ///     (b') CANCEL while a handler is mid-Run (VOID ctx.Run): exercises the void-Run OCE→Terminal
    ///     translation branch, mirroring the value-Run case.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task MidRun_VoidRun_InboundCancel_EmitsTerminal409Output()
    {
        using var ready = new ReadyGate();
        var harness = new CancelHarness(CancelServices.RunVoidDef, () => new CancelServices(ready));

        await AwaitBounded(harness.RunWithCancelAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready));

        var frames = AssertFrameOrder(harness.Response);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Error);
        AssertCancelOutput(frames);
    }

    /// <summary>
    ///     (b'') CANCEL while a handler is in a pure CPU loop (NOT inside ctx.Run) that observes
    ///     ctx.Aborted directly: the OCE escapes to the handler, where the SM-internal-cancel OCE arm
    ///     (`when sm.IsCancellationRequested`) translates it into the 409 terminal cancel Output.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task CpuLoopObservingToken_InboundCancel_EmitsTerminal409Output()
    {
        using var ready = new ReadyGate();
        var harness = new CancelHarness(CancelServices.CpuLoopDef, () => new CancelServices(ready));

        await AwaitBounded(harness.RunWithCancelAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready));

        var frames = AssertFrameOrder(harness.Response);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Error);
        AssertCancelOutput(frames);
    }

    /// <summary>
    ///     (c is in SignalIndexTests) — (d) cancel-vs-suspension exclusivity: a parked handler is
    ///     cancelled while EOF is also delivered, so cancel and the suspension condition race. The
    ///     _cancelled guard in TrySuspendAsync suppresses the Suspension frame so EXACTLY ONE terminal
    ///     frame is written — cancel wins. The stream never contains a Suspension frame, never an
    ///     Error frame, and never a frame after a Suspension.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task CancelRacingSuspension_WritesExactlyOneTerminalFrame()
    {
        using var ready = new ReadyGate();
        var harness = new CancelHarness(CancelServices.SleepDef, () => new CancelServices(ready));

        // After the handler parks, deliver CANCEL AND EOF together so cancel races the suspension
        // condition. The _cancelled guard in TrySuspendAsync must make cancel win.
        await AwaitBounded(harness.RunWithCancelThenEofAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready));

        var frames = AssertFrameOrder(harness.Response);   // enforces "no frame after Suspension"
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Suspension);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Error);
        AssertCancelOutput(frames);
    }

    /// <summary>
    ///     If user code CATCHES the 409 TerminalException and returns normally, the SDK writes a
    ///     NORMAL Output (cooperative-cancellation parity with Rust) — not the 409 failure.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task CaughtCancel_ReturnsNormally_WritesNormalOutput()
    {
        using var ready = new ReadyGate();
        var harness = new CancelHarness(CancelServices.SleepCatchDef, () => new CancelServices(ready));

        await AwaitBounded(harness.RunWithCancelAsync(JsonSerializer.SerializeToUtf8Bytes("x"), ready));

        var frames = AssertFrameOrder(harness.Response);
        Assert.DoesNotContain(frames, frame => frame.Header.Type == MessageType.Error);
        var output = Assert.Single(frames, frame => frame.Header.Type == MessageType.OutputCommand);
        var msg = Gen.OutputCommandMessage.Parser.ParseFrom(output.Payload);
        // The handler swallowed the cancel and returned "recovered" — a normal value Output.
        Assert.Equal(Gen.OutputCommandMessage.ResultOneofCase.Value, msg.ResultCase);
    }

    private static void AssertCancelOutput(IReadOnlyList<(MessageHeader Header, byte[] Payload)> frames)
    {
        var output = Assert.Single(frames, frame => frame.Header.Type == MessageType.OutputCommand);
        var msg = Gen.OutputCommandMessage.Parser.ParseFrom(output.Payload);
        Assert.Equal(Gen.OutputCommandMessage.ResultOneofCase.Failure, msg.ResultCase);
        Assert.Equal(409u, msg.Failure.Code);
        Assert.Equal("cancelled", msg.Failure.Message);
        Assert.Contains(frames, frame => frame.Header.Type == MessageType.End);
    }

    // ---- Deterministic ready gate ------------------------------------------------------------

    /// <summary>
    ///     A one-shot signal the handler sets once it has parked / entered its run loop, so the
    ///     harness delivers CANCEL only after the handler is genuinely waiting — no wall-clock race.
    /// </summary>
    private sealed class ReadyGate : IDisposable
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Signal() => _tcs.TrySetResult();
        public Task Wait() => _tcs.Task;
        public void Dispose() => _tcs.TrySetResult();   // unblock any awaiter if the test tore down early
    }

    // ---- Full-stack harness that delivers a CANCEL signal frame mid-flight -------------------

    /// <summary>
    ///     Drives <see cref="InvocationHandler.HandleAsync" /> over a duplex pipe with a hand-built
    ///     service. After the handler signals the ready gate it delivers a SignalNotification(idx=1)
    ///     CANCEL frame so the handler observes inbound cancellation while parked or computing.
    /// </summary>
    private sealed class CancelHarness(ServiceDefinition service, Func<object> instanceFactory)
    {
        private readonly System.IO.Pipelines.Pipe _request = new();
        private readonly System.IO.Pipelines.Pipe _response = new();

        public byte[] Response { get; private set; } = [];

        public Task RunWithCancelAsync(byte[] input, ReadyGate ready) =>
            RunAsync(input, ready, deliverEofWithCancel: false);

        public Task RunWithCancelThenEofAsync(byte[] input, ReadyGate ready) =>
            RunAsync(input, ready, deliverEofWithCancel: true);

        private async Task RunAsync(byte[] input, ReadyGate ready, bool deliverEofWithCancel)
        {
            var handler = new InvocationHandler();
            var handlerDef = service.Handlers[0];

            var start = CreateStartMessage(service.Name, knownEntries: 1);
            var writer = new ProtocolWriter(_request.Writer);
            writer.WriteMessage(MessageType.Start, start.ToByteArray());
            writer.WriteMessage(MessageType.InputCommand, CreateInputCommand(input).ToByteArray());
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);

            var drain = DrainResponseAsync();
            var handlerTask = handler.HandleAsync(_request.Reader, _response.Writer, service, handlerDef,
                new SingleInstanceProvider(instanceFactory()), CancellationToken.None);

            // Deterministic: wait until the handler has parked / entered its loop before cancelling.
            await ready.Wait().WaitAsync(WatchdogTimeout).ConfigureAwait(false);

            var cancelFrame = CreateSignalNotification(InvocationStateMachine.CancelSignalId);
            var cancelWriter = new ProtocolWriter(_request.Writer);
            cancelWriter.WriteMessage(MessageType.SignalNotification, cancelFrame.ToByteArray());
            await cancelWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            if (deliverEofWithCancel)
                await _request.Writer.CompleteAsync().ConfigureAwait(false);

            await handlerTask.ConfigureAwait(false);
            await _response.Writer.CompleteAsync().ConfigureAwait(false);
            Response = await drain.ConfigureAwait(false);

            if (!deliverEofWithCancel)
                try { await _request.Writer.CompleteAsync().ConfigureAwait(false); } catch { /* already torn */ }
        }

        private async Task<byte[]> DrainResponseAsync()
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                var read = await _response.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var segment in read.Buffer) buffer.Write(segment.Span);
                _response.Reader.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted) break;
            }

            return buffer.ToArray();
        }
    }

    private sealed class SingleInstanceProvider(object instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) => instance;
    }

    /// <summary>Non-generated services whose handlers exercise the inbound-cancel paths.</summary>
    private sealed class CancelServices(CancellationTests.ReadyGate ready)
    {
        private readonly CancellationTests.ReadyGate _ready = ready;

        // Parks on a long Sleep — inbound CANCEL faults the durable await with TerminalException(409).
        public static readonly ServiceDefinition SleepDef =
            BuildService("CancelSleepSvc", async (instance, ctx) =>
            {
                ((CancelServices)instance)._ready.Signal();
                await ctx.Sleep(TimeSpan.FromHours(1)).ConfigureAwait(false);
                return "unreached";
            });

        // Value-returning ctx.Run whose body waits on the handler token — cancel → OCE → 409.
        public static readonly ServiceDefinition RunValueDef =
            BuildService("CancelRunValueSvc", async (instance, ctx) =>
            {
                var inst = (CancelServices)instance;
                await ctx.Run("loop", async () =>
                {
                    inst._ready.Signal();
                    await Task.Delay(TimeSpan.FromHours(1), ctx.Aborted).ConfigureAwait(false);
                    return 0;
                }).ConfigureAwait(false);
                return "unreached";
            });

        // VOID ctx.Run whose body waits on the handler token — cancel → OCE → 409 (void branch).
        public static readonly ServiceDefinition RunVoidDef =
            BuildService("CancelRunVoidSvc", async (instance, ctx) =>
            {
                var inst = (CancelServices)instance;
                await ctx.Run("loop", async () =>
                {
                    inst._ready.Signal();
                    await Task.Delay(TimeSpan.FromHours(1), ctx.Aborted).ConfigureAwait(false);
                }).ConfigureAwait(false);
                return "unreached";
            });

        // Pure CPU loop (NOT inside ctx.Run) observing ctx.Aborted directly — cancel → OCE escapes to
        // the handler's SM-internal-cancel OCE arm.
        public static readonly ServiceDefinition CpuLoopDef =
            BuildService("CancelCpuLoopSvc", async (instance, ctx) =>
            {
                ((CancelServices)instance)._ready.Signal();
                await Task.Delay(TimeSpan.FromHours(1), ctx.Aborted).ConfigureAwait(false);
                return "unreached";
            });

        // Catches the 409 cancel and returns normally — cooperative-cancellation parity.
        public static readonly ServiceDefinition SleepCatchDef =
            BuildService("CancelCatchSvc", async (instance, ctx) =>
            {
                ((CancelServices)instance)._ready.Signal();
                try
                {
                    await ctx.Sleep(TimeSpan.FromHours(1)).ConfigureAwait(false);
                    return "unreached";
                }
                catch (TerminalException ex) when (ex.Code == 409)
                {
                    return "recovered";
                }
            });

        private static ServiceDefinition BuildService(
            string name, Func<object, Context, Task<object?>> body) =>
            new()
            {
                Name = name,
                Type = ServiceType.Service,
                Factory = provider => provider.GetService(typeof(CancelServices))!,
                Handlers = new[]
                {
                    new HandlerDefinition
                    {
                        Name = "Run",
                        IsShared = false,
                        HasInput = true,
                        HasOutput = true,
                        InputDeserializer = data => JsonSerializer.Deserialize<string>(data.FirstSpan),
                        Invoker = (instance, ctx, _, _) => body(instance, ctx)
                    }
                }
            };
    }
}
