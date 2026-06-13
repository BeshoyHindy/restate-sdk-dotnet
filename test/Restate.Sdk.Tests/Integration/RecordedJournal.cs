using System.IO.Pipelines;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.Integration;

/// <summary>
///     Plan 07 §2.5 — the in-process record-then-replay harness. It builds ON TOP of
///     <see cref="ProtocolTestHarness" /> (frame builders, watchdog, frame-order helper) and drives
///     the REAL <see cref="InvocationHandler.HandleAsync" /> through the source-generated invoker
///     resolved from <see cref="ServiceDefinitionRegistry" /> — the SAME layer as the blueprint §4.9
///     resumed-invocation test. The harness's novelty is RECORDING: attempt 1 runs over live
///     <see cref="Pipe" /> pairs, the script forces suspension by completing the request pipe, every
///     emitted V4 command frame is captured VERBATIM, and attempt 2's known-entries batch is
///     synthesized from those exact bytes plus notifications whose completion ids are PARSED out of
///     the recorded command bytes (never positional guessing) and fed through a FRESH handler.
///
///     Why a live duplex pipe (not a pre-baked MemoryStream like §4.9): forcing a genuine suspension
///     requires the request pipe to stay OPEN while the handler executes its pre-suspension prefix
///     (so a RunAsync can emit its proposal and the script can deliver the matching RunCompletion),
///     and only THEN be completed (EOF) so the parked Sleep/Call/Promise triggers
///     <c>TrySuspendAsync</c>. A static request buffer cannot express that ordering.
/// </summary>
internal static class RecordedJournal
{
    /// <summary>The fresh-invocation Start always declares exactly the Input command (known_entries = 1).</summary>
    private const uint FreshKnownEntries = 1;

    // ── Public record types (plan §2.5 API) ──────────────────────────────────────────────────

    /// <summary>One frame the SDK emitted on the wire, captured verbatim for replay synthesis.</summary>
    internal sealed record RecordedFrame(MessageType Type, byte[] Payload);

    /// <summary>
    ///     The outcome of one HandleAsync attempt: every emitted frame, the command subset in journal
    ///     order, and whether the attempt parked on a Suspension (last frame) — with the parsed
    ///     <see cref="Gen.SuspensionMessage" /> so a scenario can assert exact waiting ids.
    /// </summary>
    internal sealed class AttemptResult
    {
        public required IReadOnlyList<RecordedFrame> Frames { get; init; }
        public IReadOnlyList<RecordedFrame> Commands =>
            Frames.Where(f => f.Type.IsCommand()).ToArray();
        public bool Suspended => Frames.Count > 0 && Frames[^1].Type == MessageType.Suspension;
        public Gen.SuspensionMessage? Suspension =>
            Suspended ? Gen.SuspensionMessage.Parser.ParseFrom(Frames[^1].Payload) : null;

        /// <summary>The first OutputCommand frame, parsed — null when the attempt suspended.</summary>
        public Gen.OutputCommandMessage? Output
        {
            get
            {
                var output = Frames.FirstOrDefault(f => f.Type == MessageType.OutputCommand);
                return output is null ? null : Gen.OutputCommandMessage.Parser.ParseFrom(output.Payload);
            }
        }
    }

    /// <summary>
    ///     The live duplex handle handed to a first-attempt script. It can deliver runtime → SDK
    ///     notifications mid-flight (to release Run proposals before the park), wait until a specific
    ///     frame has actually been flushed by the SDK, and finally close the request pipe (EOF) to
    ///     force the post-fix suspension decision.
    /// </summary>
    internal sealed class AttemptScript
    {
        private readonly PipeWriter _requestWriter;
        private readonly Func<Func<RecordedFrame, bool>, Task<RecordedFrame>> _waitForFrame;

        internal AttemptScript(
            PipeWriter requestWriter, Func<Func<RecordedFrame, bool>, Task<RecordedFrame>> waitForFrame)
        {
            _requestWriter = requestWriter;
            _waitForFrame = waitForFrame;
        }

        /// <summary>Frames one runtime-origin message onto the request pipe and flushes it to the pump.</summary>
        public async Task DeliverAsync(MessageType type, IMessage message)
        {
            var writer = new ProtocolWriter(_requestWriter);
            writer.WriteMessage(type, message.ToByteArray());
            await writer.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>Blocks (bounded) until the SDK has emitted a frame matching <paramref name="predicate" />.</summary>
        public Task<RecordedFrame> WaitForFrameAsync(Func<RecordedFrame, bool> predicate) =>
            _waitForFrame(predicate);

        /// <summary>Convenience: wait for the next emitted frame of an exact type.</summary>
        public Task<RecordedFrame> WaitForAsync(MessageType type) =>
            _waitForFrame(f => f.Type == type);

        /// <summary>
        ///     Completes the request pipe — the EOF that drives the post-fix suspension decision (B8).
        ///     After this the parked completable op resolves to a SuspensionMessage instead of hanging.
        /// </summary>
        public void CloseInput() => _requestWriter.Complete();
    }

    // ── Attempt 1: run the real handler and RECORD ────────────────────────────────────────────

    /// <summary>
    ///     Runs attempt 1: Start{known_entries=1} + InputCommand through the real HandleAsync over a
    ///     LIVE duplex pipe. The <paramref name="script" /> receives an <see cref="AttemptScript" />
    ///     to deliver notifications mid-flight and call <c>CloseInput()</c> to force suspension; it
    ///     runs concurrently with the handler. Returns every emitted frame for replay synthesis.
    /// </summary>
    public static async Task<AttemptResult> RunFirstAttemptAsync(
        Type serviceType, string handlerName, byte[] inputJson,
        Func<AttemptScript, Task> script, string? key = null)
    {
        var start = CreateStartMessage("inv-recorded", FreshKnownEntries, key, partialState: false);
        var input = CreateInputCommand(inputJson);
        return await RunAttemptAsync(serviceType, handlerName, start, input,
            replayBatch: [], script: script).ConfigureAwait(false);
    }

    // ── Attempt 2: synthesize the known-entries batch from RECORDED bytes and replay ──────────

    /// <summary>
    ///     Synthesizes attempt 2's known-entries batch:
    ///     Start{known_entries = 1 + commands.Count + notifications.Length, partial_state per arg} +
    ///     InputCommand + the recorded commands (VERBATIM bytes — never re-encoded) followed by the
    ///     given notifications, then runs HandleAsync on a FRESH handler and returns the same shape.
    ///     An optional <paramref name="script" /> can deliver further notifications mid-flight (used
    ///     by P4 to answer a lazy GetState command the replay emits) and/or close the input again.
    /// </summary>
    public static async Task<AttemptResult> RunResumeAttemptAsync(
        Type serviceType, string handlerName, byte[] inputJson,
        IReadOnlyList<RecordedFrame> commands,
        RecordedFrame[] notifications,
        bool partialState = false,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? eagerState = null,
        Func<AttemptScript, Task>? script = null, string? key = null)
    {
        // known_entries counts the Input command, every replayed command, AND every notification
        // (protocol.proto:60-61) — the exact inflation that hung the pre-fix SDK in Replaying (B2).
        var knownEntries = FreshKnownEntries + (uint)commands.Count + (uint)notifications.Length;
        var start = CreateStartMessage("inv-recorded-resume", knownEntries, key,
            partialState: partialState, eagerState: eagerState);
        var input = CreateInputCommand(inputJson);

        var batch = commands
            .Select(c => (c.Type, c.Payload))
            .Concat(notifications.Select(n => (n.Type, n.Payload)))
            .ToArray();

        // No script ⇒ the batch is self-contained: deliver everything, then EOF so the resumed
        // handler runs to completion (or re-suspends if the batch is deliberately incomplete, P2).
        var resumeScript = script ?? (s => { s.CloseInput(); return Task.CompletedTask; });
        return await RunAttemptAsync(serviceType, handlerName, start, input, batch, resumeScript)
            .ConfigureAwait(false);
    }

    // ── Shared attempt driver ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Drives HandleAsync over a live duplex pipe: writes Start + Input + the replay batch, starts
    ///     a background collector that records every outbound frame and signals waiters, runs the
    ///     script concurrently, and returns the recorded frames once the handler unwinds. The collector
    ///     reads the outbound pipe — the SDK is the SOLE writer of it, so recording never perturbs the
    ///     single-reader invariant on the INBOUND wire that the SM cares about.
    /// </summary>
    private static async Task<AttemptResult> RunAttemptAsync(
        Type serviceType, string handlerName,
        Gen.StartMessage start, Gen.InputCommandMessage input,
        (MessageType Type, byte[] Payload)[] replayBatch,
        Func<AttemptScript, Task> script)
    {
        var serviceDef = ServiceDefinitionRegistry.TryGet(serviceType)
            ?? throw new InvalidOperationException(
                $"No generated service definition for '{serviceType.Name}'. Is the source generator enabled?");
        var handlerDef = serviceDef.Handlers.First(h => h.Name == handlerName);

        var request = new Pipe();
        var response = new Pipe();

        // Seed the request with Start + Input + any pre-baked replay batch BEFORE running the handler,
        // so the StartAsync preflight sees a complete known-entries batch on a fresh attempt-2 replay.
        var requestWriter = new ProtocolWriter(request.Writer);
        requestWriter.WriteMessage(MessageType.Start, start.ToByteArray());
        requestWriter.WriteMessage(MessageType.InputCommand, input.ToByteArray());
        foreach (var (type, payload) in replayBatch)
            requestWriter.WriteMessage(type, payload);
        await requestWriter.FlushAsync().ConfigureAwait(false);

        using var collector = new OutboundCollector(response.Reader);
        var collectorTask = collector.RunAsync();

        var attemptScript = new AttemptScript(request.Writer, collector.WaitForFrameAsync);

        var handler = new InvocationHandler();
        var handleTask = handler.HandleAsync(
            request.Reader, response.Writer, serviceDef, handlerDef,
            new FuncServiceProvider(t => Activator.CreateInstance(t)!), CancellationToken.None);

        // The script runs concurrently with the handler (it must, to react to flushed proposals and
        // then close input). Surface a script fault before the watchdog so failures are diagnosable.
        var scriptTask = script(attemptScript);

        await AwaitBounded(handleTask).ConfigureAwait(false);
        await AwaitBounded(scriptTask).ConfigureAwait(false);

        // The handler completed the response writer in its finally; draining the collector now yields
        // the full recorded frame list. Completing the request reader releases the pump.
        await response.Writer.CompleteAsync().ConfigureAwait(false);
        var frames = await AwaitBounded(collectorTask).ConfigureAwait(false);
        await request.Reader.CompleteAsync().ConfigureAwait(false);

        return new AttemptResult { Frames = frames };
    }

    /// <summary>
    ///     Reads the SDK's outbound frames into a growing list, signalling each arrival so a script can
    ///     wait until a specific proposal/command lands before delivering its ack (the realistic
    ///     protocol ordering — the runtime acks only AFTER receiving the proposal).
    /// </summary>
    private sealed class OutboundCollector(PipeReader reader) : IDisposable
    {
        private readonly List<RecordedFrame> _frames = [];
        private readonly SemaphoreSlim _signal = new(0);
        private readonly Lock _gate = new();

        public void Dispose() => _signal.Dispose();

        public async Task<IReadOnlyList<RecordedFrame>> RunAsync()
        {
            var protocolReader = new ProtocolReader(reader);
            while (await protocolReader.ReadMessageAsync().ConfigureAwait(false) is { } message)
            {
                var frame = new RecordedFrame(message.Header.Type, message.Payload.ToArray());
                message.Dispose();
                lock (_gate) _frames.Add(frame);
                _signal.Release();
            }

            lock (_gate) return _frames.ToArray();
        }

        public async Task<RecordedFrame> WaitForFrameAsync(Func<RecordedFrame, bool> predicate)
        {
            while (true)
            {
                lock (_gate)
                {
                    var match = _frames.FirstOrDefault(predicate);
                    if (match is not null) return match;
                }

                await AwaitBounded(_signal.WaitAsync()).ConfigureAwait(false);
            }
        }
    }

    private sealed class FuncServiceProvider(Func<Type, object> factory) : IServiceProvider
    {
        public object? GetService(Type serviceType) => factory(serviceType);
    }

    // ── Notification synthesis (ids PARSED from the recorded command bytes) ───────────────────

    /// <summary>
    ///     Parses the recorded command frame at <paramref name="commandType" /> + name into a
    ///     <see cref="ReplayCommand" /> so synthesis can read the EXACT completion/notification ids the
    ///     SDK allocated on attempt 1 — never a positional guess. This is the load-bearing honesty
    ///     check of the harness: notifications are keyed by the SDK's own ids, so a regression that
    ///     re-derives ids differently across attempts surfaces as a mismatch, not a silent pass.
    /// </summary>
    public static ReplayCommand ParseCommand(
        IReadOnlyList<RecordedFrame> commands, MessageType commandType, string? name = null)
    {
        var frame = commands.First(c =>
            c.Type == commandType &&
            (name is null || ProtobufCodec.ParseReplayCommand(c.Type, c.Payload).Name == name));
        return ProtobufCodec.ParseReplayCommand(frame.Type, frame.Payload);
    }

    /// <summary>The result_completion_id the SDK allocated for the named command (parsed, never guessed).</summary>
    public static uint CompletionIdOf(
        IReadOnlyList<RecordedFrame> commands, MessageType commandType, string? name = null) =>
        ParseCommand(commands, commandType, name).ResultCompletionId;

    /// <summary>The invocation_id_notification_idx the SDK allocated for a Call/OneWayCall command.</summary>
    public static uint InvocationIdIdxOf(
        IReadOnlyList<RecordedFrame> commands, MessageType commandType, string? name = null) =>
        ParseCommand(commands, commandType, name).InvocationIdNotificationIdx;

    public static RecordedFrame RunCompletion(uint id, object value) =>
        new(MessageType.RunCompletion,
            CreateRunCompletion(id, JsonSerializer.SerializeToUtf8Bytes(value)).ToByteArray());

    public static RecordedFrame RunFailure(uint id, uint code, string msg) =>
        new(MessageType.RunCompletion, CreateRunCompletionFailure(id, code, msg).ToByteArray());

    public static RecordedFrame SleepCompletion(uint id) =>
        new(MessageType.SleepCompletion, CreateSleepCompletion(id).ToByteArray());

    public static RecordedFrame CallCompletion(uint id, object value) =>
        new(MessageType.CallCompletion,
            CreateCallCompletion(id, JsonSerializer.SerializeToUtf8Bytes(value)).ToByteArray());

    public static RecordedFrame CallInvocationId(uint idx, string invocationId) =>
        new(MessageType.CallInvocationIdCompletion,
            CreateCallInvocationIdCompletion(idx, invocationId).ToByteArray());

    /// <summary>GetLazyStateCompletion: value == null encodes the known-absent (Void) answer.</summary>
    public static RecordedFrame StateCompletion(uint id, object? value) =>
        new(MessageType.GetLazyStateCompletion,
            CreateGetStateCompletion(id, value is null
                ? null
                : (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(value)).ToByteArray());

    public static RecordedFrame PromiseCompletion(uint id, object value) =>
        new(MessageType.GetPromiseCompletion, new Gen.GetPromiseCompletionNotificationMessage
        {
            CompletionId = id,
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(value)) }
        }.ToByteArray());
}
