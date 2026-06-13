using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     G33 (LOW) — per-op debug-log suppression on the replay frontier. <c>Log.SideEffectExecuted</c>
///     (EventId 6) must fire only when a side effect closure ACTUALLY ran live (or claimed the replay
///     frontier), never when a buffered/replayed completion is consumed without executing. This mirrors
///     shared-core, which emits the run debug log from <c>propose_run_completion</c> gated on
///     <c>is_processing()</c> — i.e. only when the closure runs (vm/mod.rs:204-210, 1122-1131).
///
///     The state machine is constructed directly (rather than via the rig) so a capturing logger can be
///     injected; both directions are asserted: a LIVE run logs once, a REPLAYED run logs zero times.
/// </summary>
public sealed class RunReplayLogSuppressionTests
{
    private const int WatchdogMs = 10_000;
    private const int SideEffectExecutedEventId = 6;

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static async Task DeliverAsync(PipeWriter inbound, MessageType type, byte[] payload)
    {
        var writer = new ProtocolWriter(inbound);
        writer.WriteMessage(type, payload);
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task LiveRun_ExecutesClosure_LogsSideEffectExecuted()
    {
        var inbound = new Pipe();
        var outbound = new Pipe();
        var logger = new CapturingLogger();
        using var sm = new InvocationStateMachine(
            new ProtocolReader(inbound.Reader), new ProtocolWriter(outbound.Writer), logger: logger);

        // Fresh processing invocation (known_entries = 1) — the run executes live.
        await DeliverAsync(inbound.Writer, MessageType.Start, CreateStartMessage("inv-live", 1).ToByteArray());
        await DeliverAsync(inbound.Writer, MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray());
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var executed = false;
        var runTask = sm.RunAsync<int>("live", () => { executed = true; return Task.FromResult(7); },
            CancellationToken.None).AsTask();
        // The live run parks on its own completion notification; deliver the ack so it resolves.
        await DeliverAsync(inbound.Writer, MessageType.RunCompletion, CreateRunCompletion(1, Json(7)).ToByteArray());
        Assert.Equal(7, await AwaitBounded(runTask));

        Assert.True(executed);   // the closure ran live
        Assert.Equal(1, logger.CountOf(SideEffectExecutedEventId));

        inbound.Writer.Complete();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task ReplayedRun_ConsumesBufferedCompletion_DoesNotLogSideEffectExecuted()
    {
        var inbound = new Pipe();
        var outbound = new Pipe();
        var logger = new CapturingLogger();
        using var sm = new InvocationStateMachine(
            new ProtocolReader(inbound.Reader), new ProtocolWriter(outbound.Writer), logger: logger);

        // Replay batch: [Input, RunCommand{id=1}] + buffered RunCompletion{id=1} (known_entries = 3).
        // The run is consumed from the journal + buffered notification WITHOUT executing the closure.
        await DeliverAsync(inbound.Writer, MessageType.Start, CreateStartMessage("inv-replay", 3).ToByteArray());
        await DeliverAsync(inbound.Writer, MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray());
        await DeliverAsync(inbound.Writer, MessageType.RunCommand, CreateRunCommand("x", 1).ToByteArray());
        await DeliverAsync(inbound.Writer, MessageType.RunCompletion, CreateRunCompletion(1, Json(42)).ToByteArray());
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var executed = false;
        var result = await AwaitBounded(sm.RunAsync<int>("x",
            () => { executed = true; return Task.FromResult(99); }, CancellationToken.None));

        Assert.Equal(42, result);   // value came from the notification, not the closure
        Assert.False(executed);     // replayed run never executed
        // G33: a consumed replay completion must NOT emit the per-op execution debug log.
        Assert.Equal(0, logger.CountOf(SideEffectExecutedEventId));

        inbound.Writer.Complete();
        await AwaitBounded(pump);
    }

    /// <summary>Minimal capturing logger that counts emitted log entries by EventId.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<int> _eventIds = new();

        public int CountOf(int eventId)
        {
            lock (_eventIds) return _eventIds.Count(id => id == eventId);
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_eventIds) _eventIds.Add(eventId.Id);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
