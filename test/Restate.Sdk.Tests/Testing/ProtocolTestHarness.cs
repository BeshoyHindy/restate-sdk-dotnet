using System.IO.Pipelines;
using Google.Protobuf;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.Testing;

/// <summary>
///     Shared protocol test harness for the CoreVM-parity suite (blueprint §4 preamble + §4.2).
///
///     Two driving paths are supported, mirroring the existing conventions:
///       (a) directly against <see cref="InvocationStateMachine" /> over a duplex
///           <see cref="Pipe" /> pair (as in StateMachine/InvocationStateMachineTests.cs), and
///       (b) full-stack <see cref="Internal.InvocationHandler.HandleAsync" /> over framed
///           V4 messages (as in Integration/ProtocolIntegrationTests.cs — WriteFramedMessage /
///           ReadFramedMessage are lifted here unchanged so both call sites share one decoder).
///
///     The synthetic frame builders reuse <see cref="ProtobufCodec" /> factories plus the
///     generated messages directly, so every frame the harness emits is wire-identical to what a
///     conformant SDK / runtime would send.
///
///     Two invariants are load-bearing for the suite and live here so every scenario shares one
///     implementation:
///       * <see cref="AwaitBounded{T}(Task{T})" /> — the 5 s watchdog. Several pre-fix failure
///         modes (B2 sleep-resume hang, B8 suspension deadlock) are infinite hangs that must FAIL
///         the run rather than freeze it; xunit v2 only honors <c>[Fact(Timeout)]</c> for async
///         methods and aborts rather than fails, so the WaitAsync watchdog is the primary
///         mechanism and the attribute is defense-in-depth. No harness wait is a sync block.
///       * <see cref="CountingPipeReader" /> — the single-reader probe (B3). It maintains an
///         Interlocked pending-read counter and asserts it never exceeds 1, proving only one task
///         (StartAsync preflight, then ProcessIncomingMessagesAsync) ever reads the wire.
///       * <see cref="AssertFrameOrder" /> — the wire-order helper (B8). It parses every frame
///         header and asserts header validity, that no frame follows a Suspension frame, and that
///         End, when present, is the final frame.
/// </summary>
internal static class ProtocolTestHarness
{
    /// <summary>The suite-wide watchdog budget. One place to tune; every scenario wait flows through it.</summary>
    public static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(5);

    // ---- Watchdog ----------------------------------------------------------------------------

    /// <summary>
    ///     Bounds a wait so a regression that hangs the SM fails the test in 5 s instead of
    ///     freezing the run. Always async (never Task.Wait/.Result) so the abort actually fires
    ///     under xunit v2.
    /// </summary>
    public static Task<T> AwaitBounded<T>(Task<T> task) => task.WaitAsync(WatchdogTimeout);

    /// <summary>Non-generic overload for value-less waits (pump completion, CompleteAsync, etc.).</summary>
    public static Task AwaitBounded(Task task) => task.WaitAsync(WatchdogTimeout);

    /// <summary>Convenience for the ValueTask-returning SM operations.</summary>
    public static Task<T> AwaitBounded<T>(ValueTask<T> task) => task.AsTask().WaitAsync(WatchdogTimeout);

    public static Task AwaitBounded(ValueTask task) => task.AsTask().WaitAsync(WatchdogTimeout);

    // ---- Duplex pipe rig ---------------------------------------------------------------------

    /// <summary>
    ///     A duplex pair of pipes wired to an <see cref="InvocationStateMachine" />: the inbound
    ///     pipe carries runtime → SDK frames (Start / Input / completions / signals), the outbound
    ///     pipe carries SDK → runtime frames (commands / proposals / output / suspension / end).
    ///     The inbound reader is wrapped in <see cref="CountingPipeReader" /> so every test built
    ///     on the rig gets the single-reader invariant for free.
    /// </summary>
    public sealed class StateMachineRig : IDisposable
    {
        private readonly Pipe _inboundPipe = new();
        private readonly Pipe _outboundPipe = new();

        public StateMachineRig()
        {
            Inbound = new CountingPipeReader(_inboundPipe.Reader);
            var protocolReader = new ProtocolReader(Inbound);
            var protocolWriter = new ProtocolWriter(_outboundPipe.Writer);
            StateMachine = new InvocationStateMachine(protocolReader, protocolWriter);
        }

        /// <summary>The state machine under test, reading inbound and writing outbound.</summary>
        public InvocationStateMachine StateMachine { get; }

        /// <summary>The single-reader probe wrapping the inbound reader — assert on it post-run.</summary>
        public CountingPipeReader Inbound { get; }

        /// <summary>Frames written here are delivered to the SM's pump (runtime → SDK direction).</summary>
        public PipeWriter InboundWriter => _inboundPipe.Writer;

        /// <summary>Drain the SM's emitted frames (SDK → runtime direction) for assertions.</summary>
        public PipeReader OutboundReader => _outboundPipe.Reader;

        /// <summary>Signals end-of-input to the SM — the EOF that drives a suspension decision (B8).</summary>
        public void CompleteInbound() => _inboundPipe.Writer.Complete();

        /// <summary>Writes one already-framed message to the inbound pipe and flushes it to the pump.</summary>
        public async Task DeliverAsync(MessageType type, byte[] payload)
        {
            var writer = new ProtocolWriter(_inboundPipe.Writer);
            writer.WriteMessage(type, payload);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        public Task DeliverAsync(MessageType type, IMessage message) => DeliverAsync(type, message.ToByteArray());

        /// <summary>
        ///     Reads every frame the SM has emitted so far on the outbound pipe, blocking until the
        ///     writer side completes (so it must be called after the SM has finished/closed). Each
        ///     returned tuple owns a copied payload, safe to parse after the rig is disposed.
        /// </summary>
        public async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> ReadAllOutboundAsync()
        {
            var protocolReader = new ProtocolReader(_outboundPipe.Reader);
            var frames = new List<(MessageHeader, byte[])>();
            while (await protocolReader.ReadMessageAsync().ConfigureAwait(false) is { } message)
            {
                frames.Add((message.Header, message.Payload.ToArray()));
                message.Dispose();
            }

            return frames;
        }

        public void Dispose()
        {
            StateMachine.Dispose();
            _inboundPipe.Writer.Complete();
            _inboundPipe.Reader.Complete();
            _outboundPipe.Writer.Complete();
            _outboundPipe.Reader.Complete();
        }
    }

    // ---- Single-reader probe (B3) ------------------------------------------------------------

    /// <summary>
    ///     A <see cref="PipeReader" /> decorator that counts in-flight reads. The CoreVM-parity
    ///     redesign requires that the inbound wire have exactly one reader at any instant — the
    ///     StartAsync preflight, then ProcessIncomingMessagesAsync, never both. This probe makes
    ///     a violation (two concurrent <see cref="ReadAsync" /> calls) fail the assertion the
    ///     moment it happens and records the peak for a post-run check.
    /// </summary>
    public sealed class CountingPipeReader(PipeReader inner) : PipeReader
    {
        private int _pendingReads;
        private int _peakPendingReads;

        /// <summary>Highest number of simultaneously in-flight reads observed (must stay &lt;= 1).</summary>
        public int PeakPendingReads => Volatile.Read(ref _peakPendingReads);

        /// <summary>Total <see cref="ReadAsync" /> calls — a liveness signal for tests.</summary>
        public int TotalReads { get; private set; }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            TotalReads++;
            var pending = Interlocked.Increment(ref _pendingReads);
            UpdatePeak(pending);
            // A second concurrent reader is the exact B3 bug; surface it where it happens, not later.
            Assert.True(pending <= 1, $"Concurrent wire read detected: {pending} simultaneous ReadAsync calls");
            try
            {
                return await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingReads);
            }
        }

        public override bool TryRead(out ReadResult result)
        {
            var pending = Interlocked.Increment(ref _pendingReads);
            UpdatePeak(pending);
            Assert.True(pending <= 1, $"Concurrent wire read detected: {pending} simultaneous reads");
            try
            {
                return inner.TryRead(out result);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingReads);
            }
        }

        private void UpdatePeak(int pending)
        {
            int observed;
            while (pending > (observed = Volatile.Read(ref _peakPendingReads)))
                if (Interlocked.CompareExchange(ref _peakPendingReads, pending, observed) == observed)
                    break;
        }

        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) =>
            inner.AdvanceTo(consumed, examined);

        public override void CancelPendingRead() => inner.CancelPendingRead();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }

    // ---- Wire-order helper (B8) --------------------------------------------------------------

    /// <summary>
    ///     Parses every frame header in a raw response stream and asserts the structural wire
    ///     invariants: (a) each header is valid (parsed type round-trips, declared length present),
    ///     (b) NO frame of any kind follows a Suspension frame (HitSuspensionPoint sends the
    ///     suspension then EOF only), and (c) End, when present, is the final frame. Returns the
    ///     parsed frame list so callers can make further per-frame assertions.
    /// </summary>
    public static IReadOnlyList<(MessageHeader Header, byte[] Payload)> AssertFrameOrder(byte[] responseStream)
    {
        var frames = ParseFrames(responseStream);
        for (var index = 0; index < frames.Count; index++)
        {
            var (header, _) = frames[index];
            var isLast = index == frames.Count - 1;

            if (header.Type == MessageType.Suspension)
                Assert.True(isLast, $"A frame ({frames.ElementAtOrDefault(index + 1).Header.Type}) " +
                                    "follows a Suspension frame; suspension must be terminal");

            if (header.Type == MessageType.End)
                Assert.True(isLast, "End is present but not the final frame");
        }

        return frames;
    }

    /// <summary>
    ///     Splits a raw response stream into framed messages, validating each header against the
    ///     declared payload length (the same decode the integration tests do by hand).
    /// </summary>
    public static IReadOnlyList<(MessageHeader Header, byte[] Payload)> ParseFrames(byte[] responseStream)
    {
        var frames = new List<(MessageHeader, byte[])>();
        var offset = 0;
        while (offset < responseStream.Length)
        {
            Assert.True(offset + MessageHeader.Size <= responseStream.Length,
                $"Truncated frame header at offset {offset}; stream length {responseStream.Length}");
            var header = MessageHeader.Read(responseStream.AsSpan(offset, MessageHeader.Size));
            offset += MessageHeader.Size;

            var payload = new byte[header.Length];
            if (header.Length > 0)
            {
                Assert.True(offset + (int)header.Length <= responseStream.Length,
                    $"Truncated frame payload at offset {offset}; need {header.Length}, " +
                    $"have {responseStream.Length - offset}");
                Array.Copy(responseStream, offset, payload, 0, (int)header.Length);
                offset += (int)header.Length;
            }

            frames.Add((header, payload));
        }

        return frames;
    }

    // ---- Framed-message stream helpers (lifted from ProtocolIntegrationTests) ----------------

    /// <summary>Appends one length-prefixed frame to a <see cref="MemoryStream" /> request buffer.</summary>
    public static void WriteFramedMessage(MemoryStream stream, MessageType type, byte[] payload)
    {
        Span<byte> header = stackalloc byte[MessageHeader.Size];
        MessageHeader.Create(type, MessageFlags.None, (uint)payload.Length).Write(header);
        stream.Write(header);
        stream.Write(payload);
    }

    /// <summary>Reads one framed message from a raw byte buffer, advancing <paramref name="offset" />.</summary>
    public static (MessageHeader Header, byte[] Payload) ReadFramedMessage(byte[] data, ref int offset)
    {
        Assert.True(offset + MessageHeader.Size <= data.Length,
            $"Not enough data for message header at offset {offset}. Data length: {data.Length}");

        var header = MessageHeader.Read(data.AsSpan(offset, MessageHeader.Size));
        offset += MessageHeader.Size;

        var payload = new byte[header.Length];
        if (header.Length > 0)
        {
            Assert.True(offset + (int)header.Length <= data.Length,
                $"Not enough data for message payload at offset {offset}. " +
                $"Need {header.Length}, have {data.Length - offset}");
            Array.Copy(data, offset, payload, 0, (int)header.Length);
            offset += (int)header.Length;
        }

        return (header, payload);
    }

    /// <summary>Builds a complete framed request body (Start + Input [+ extra frames]) as a stream.</summary>
    public static MemoryStream BuildRequestStream(byte[] startPayload, byte[] inputPayload,
        params (MessageType Type, byte[] Payload)[] extras)
    {
        var stream = new MemoryStream();
        WriteFramedMessage(stream, MessageType.Start, startPayload);
        WriteFramedMessage(stream, MessageType.InputCommand, inputPayload);
        foreach (var (type, payload) in extras)
            WriteFramedMessage(stream, type, payload);
        stream.Position = 0;
        return stream;
    }

    // ---- Synthetic V4 message builders -------------------------------------------------------
    // Reuse ProtobufCodec factories where they exist (commands) and the generated messages
    // directly for the runtime-origin frames the codec has no factory for (Start/Input/
    // completions/signals). Every builder returns the protobuf message; callers frame it via
    // DeliverAsync / WriteFramedMessage / message.ToByteArray().

    /// <summary>
    ///     StartMessage: known_entries counts commands AND notifications (protocol.proto:60-61),
    ///     so a replay batch sets it accordingly. <paramref name="eagerState" /> seeds the eager
    ///     state map; a null value encodes a known-cleared marker (Void on the wire) — but the
    ///     StateMap on the wire only carries present keys, so nulls are skipped here.
    /// </summary>
    public static Gen.StartMessage CreateStartMessage(string invocationId, uint knownEntries,
        string? key = null, ulong randomSeed = 0, bool partialState = true,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? eagerState = null,
        uint retryCountSinceLastStoredEntry = 0, ulong durationSinceLastStoredEntryMillis = 0)
    {
        var msg = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8(invocationId),
            DebugId = invocationId,
            KnownEntries = knownEntries,
            Key = key ?? string.Empty,
            RandomSeed = randomSeed,
            PartialState = partialState,
            // G2/G3 — durable retry accounting seeds (StartMessage fields 7/8).
            RetryCountSinceLastStoredEntry = retryCountSinceLastStoredEntry,
            DurationSinceLastStoredEntry = durationSinceLastStoredEntryMillis
        };
        if (eagerState is not null)
            foreach (var (entryKey, entryValue) in eagerState)
                msg.StateMap.Add(new Gen.StartMessage.Types.StateEntry
                {
                    Key = ByteString.CopyFromUtf8(entryKey),
                    Value = ByteString.CopyFrom(entryValue.Span)
                });

        return msg;
    }

    /// <summary>InputCommand carrying the handler argument bytes.</summary>
    public static Gen.InputCommandMessage CreateInputCommand(byte[] inputBytes) =>
        new() { Value = new Gen.Value { Content = ByteString.CopyFrom(inputBytes) } };

    public static Gen.InputCommandMessage CreateInputCommand(ReadOnlyMemory<byte> inputBytes) =>
        new() { Value = new Gen.Value { Content = ByteString.CopyFrom(inputBytes.Span) } };

    /// <summary>RunCommand{name, result_completion_id} — the journaled replay frame for a Run.</summary>
    public static Gen.RunCommandMessage CreateRunCommand(string name, uint completionId) =>
        ProtobufCodec.CreateRunCommand(name, completionId);

    /// <summary>SleepCommand{result_completion_id} for the given wake-up time.</summary>
    public static Gen.SleepCommandMessage CreateSleepCommand(uint completionId, ulong wakeUpTime = 0) =>
        ProtobufCodec.CreateSleepCommand(wakeUpTime, completionId);

    /// <summary>GetLazyStateCommand{key, result_completion_id}.</summary>
    public static Gen.GetLazyStateCommandMessage CreateGetStateCommand(string key, uint completionId) =>
        ProtobufCodec.CreateGetStateCommand(key, completionId);

    /// <summary>SetStateCommand{key, value}.</summary>
    public static Gen.SetStateCommandMessage CreateSetStateCommand(string key, ReadOnlyMemory<byte> value) =>
        ProtobufCodec.CreateSetStateCommand(key, value.Span);

    /// <summary>OutputCommand carrying a success value — the replay frame for §4.1.19.</summary>
    public static Gen.OutputCommandMessage CreateOutputCommand(ReadOnlyMemory<byte> content) =>
        ProtobufCodec.CreateOutputCommand(content.Span);

    /// <summary>
    ///     RunCompletionNotification{id}. value == null → empty-success (unset result oneof, the
    ///     ack-only shape); a non-null value → Value result.
    /// </summary>
    public static Gen.RunCompletionNotificationMessage CreateRunCompletion(
        uint completionId, ReadOnlyMemory<byte>? value = null)
    {
        var msg = new Gen.RunCompletionNotificationMessage { CompletionId = completionId };
        if (value is { } content)
            msg.Value = new Gen.Value { Content = ByteString.CopyFrom(content.Span) };
        return msg;
    }

    /// <summary>RunCompletionNotification{id} carrying a terminal failure (B10b replay direction).</summary>
    public static Gen.RunCompletionNotificationMessage CreateRunCompletionFailure(
        uint completionId, uint code, string message) =>
        new()
        {
            CompletionId = completionId,
            Failure = new Gen.Failure { Code = code, Message = message }
        };

    /// <summary>SleepCompletionNotification{id} — the durable timer ack (Void result).</summary>
    public static Gen.SleepCompletionNotificationMessage CreateSleepCompletion(uint completionId) =>
        new() { CompletionId = completionId, Void = new Gen.Void() };

    /// <summary>
    ///     GetLazyStateCompletionNotification{id}. value == null → Void (known-absent); a non-null
    ///     value → Value result.
    /// </summary>
    public static Gen.GetLazyStateCompletionNotificationMessage CreateGetStateCompletion(
        uint completionId, ReadOnlyMemory<byte>? value = null)
    {
        var msg = new Gen.GetLazyStateCompletionNotificationMessage { CompletionId = completionId };
        if (value is { } content)
            msg.Value = new Gen.Value { Content = ByteString.CopyFrom(content.Span) };
        else
            msg.Void = new Gen.Void();
        return msg;
    }

    /// <summary>CallCompletionNotification{id} — success carrying the callee's return value.</summary>
    public static Gen.CallCompletionNotificationMessage CreateCallCompletion(
        uint completionId, ReadOnlyMemory<byte> value) =>
        new()
        {
            CompletionId = completionId,
            Value = new Gen.Value { Content = ByteString.CopyFrom(value.Span) }
        };

    /// <summary>CallCompletionNotification{id} carrying a terminal failure.</summary>
    public static Gen.CallCompletionNotificationMessage CreateCallCompletionFailure(
        uint completionId, uint code, string message) =>
        new()
        {
            CompletionId = completionId,
            Failure = new Gen.Failure { Code = code, Message = message }
        };

    /// <summary>CallInvocationIdCompletionNotification{id, invocation_id} — lazy send-handle id (B6).</summary>
    public static Gen.CallInvocationIdCompletionNotificationMessage CreateCallInvocationIdCompletion(
        uint completionId, string invocationId) =>
        new() { CompletionId = completionId, InvocationId = invocationId };

    /// <summary>
    ///     SignalNotification by index (awakeable completion path, B4). idx >= 17 for user signals;
    ///     idx 1 is the CANCEL built-in. value == null → Void; a non-null value → Value result.
    /// </summary>
    public static Gen.SignalNotificationMessage CreateSignalNotification(
        uint idx, ReadOnlyMemory<byte>? value = null)
    {
        var msg = new Gen.SignalNotificationMessage { Idx = idx };
        if (value is { } content)
            msg.Value = new Gen.Value { Content = ByteString.CopyFrom(content.Span) };
        else
            msg.Void = new Gen.Void();
        return msg;
    }

    /// <summary>
    ///     SignalNotification by NAME (the oneof name branch). This SDK has no named-signal user API,
    ///     so such a frame must be handled (logged + ignored) without crashing and without resolving
    ///     any numeric awakeable. value == null → Void; a non-null value → Value result.
    /// </summary>
    public static Gen.SignalNotificationMessage CreateNamedSignalNotification(
        string? name, ReadOnlyMemory<byte>? value = null)
    {
        var msg = new Gen.SignalNotificationMessage();
        // name == null leaves the signal_id oneof UNSET (a degenerate frame with neither idx nor
        // name) so callers can exercise the null-name defensive path; a non-null name sets the oneof.
        if (name is not null)
            msg.Name = name;
        if (value is { } content)
            msg.Value = new Gen.Value { Content = ByteString.CopyFrom(content.Span) };
        else
            msg.Void = new Gen.Void();
        return msg;
    }

    /// <summary>SignalNotification carrying a terminal failure (rejected awakeable).</summary>
    public static Gen.SignalNotificationMessage CreateSignalNotificationFailure(
        uint idx, uint code, string message) =>
        new()
        {
            Idx = idx,
            Failure = new Gen.Failure { Code = code, Message = message }
        };
}
