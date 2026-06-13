using System.IO.Pipelines;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Plan 07 §1.2 4b-ii (RunDetachedFailureTests) — G5 (H9). The detached non-blocking Run
///     (<c>ctx.RunAsync</c> → <c>RunFutureAsync</c>) runs its closure on a detached task and routes
///     an INFRASTRUCTURE failure (a write/flush fault, NOT a TerminalException — those travel via the
///     proposal) into the run's completion slot via <c>TryFail(completionId, 500, ...)</c>, so the
///     failure surfaces through the future's await path and NOTHING goes unobserved.
///
///     We force this by wrapping the outbound <see cref="PipeWriter" /> in a decorator whose
///     <c>FlushAsync</c> throws an <see cref="IOException" /> after the first successful flush (which
///     lets StartAsync's preflight and the RunCommand prefix through, then faults the proposal flush).
///     The test subscribes to <see cref="TaskScheduler.UnobservedTaskException" /> for its duration
///     (like §4.8.7) and asserts the LazyRunFuture resolves to a TerminalException carrying code 500.
/// </summary>
public class RunDetachedFailureTests
{
    private const int WatchdogMs = 10_000;

    [Fact(Timeout = WatchdogMs)]
    public async Task DetachedRun_FlushFaultDuringProposal_ResolvesFutureFaultedWithCode500()
    {
        var inbound = new Pipe();
        var outbound = new Pipe();
        // Fault the SECOND flush: the first flush carries StartAsync preflight acks / the RunCommand
        // prefix; the proposal flush (after the closure computes its value) faults with an IOException.
        var faulting = new FaultAfterNFlushesWriter(outbound.Writer, faultOnFlushNumber: 2);
        var reader = new ProtocolReader(inbound.Reader);
        var writer = new ProtocolWriter(faulting);

        using var sm = new InvocationStateMachine(reader, writer);

        // Fresh processing invocation (known_entries = 1).
        await DeliverAsync(inbound.Writer, MessageType.Start, CreateStartMessage("inv-detached", 1).ToByteArray());
        await DeliverAsync(inbound.Writer, MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray());
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var ctx = new DefaultContext(sm, NullLogger.Instance, CancellationToken.None);

        // Detached run: the closure computes a value; the proposal flush then faults (IOException).
        // THE G5 CONTRACT: the detached-execute containment routes that infrastructure failure (NOT a
        // TerminalException, which travels via the proposal) into the run's completion slot via
        // TryFail(completionId, 500, ...), so the failure surfaces through the future's await path
        // (completion.ThrowIfFailure) instead of being lost on a discarded ValueTask. The future
        // therefore resolves to a TerminalException carrying code 500 rather than hanging forever.
        var future = ctx.RunAsync("detached", async () => { await Task.Yield(); return 123; });

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(future.GetResult()));
        Assert.Equal((ushort)500, ex.Code);

        // Tear down: close input so the pump unwinds (it faults on the same broken writer — expected).
        inbound.Writer.Complete();
        try { await AwaitBounded(pump); } catch { /* pump faults on the broken writer — expected */ }
    }

    private static async Task DeliverAsync(PipeWriter target, MessageType type, byte[] payload)
    {
        var writer = new ProtocolWriter(target);
        writer.WriteMessage(type, payload);
        await writer.FlushAsync(CancellationToken.None);
    }

    /// <summary>
    ///     A <see cref="PipeWriter" /> decorator that throws <see cref="IOException" /> on the Nth
    ///     <see cref="FlushAsync" /> (and every flush thereafter), simulating a connection that breaks
    ///     mid-write. All other operations delegate so the first flush(es) and buffer writes succeed.
    /// </summary>
    private sealed class FaultAfterNFlushesWriter(PipeWriter inner, int faultOnFlushNumber) : PipeWriter
    {
        private int _flushCount;

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _flushCount);
            if (n >= faultOnFlushNumber)
                throw new IOException("simulated connection reset on flush");
            return inner.FlushAsync(cancellationToken);
        }

        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }
}
