using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.Serde;

namespace Restate.Sdk.Internal.StateMachine;

[UnconditionalSuppressMessage("AOT",
    "IL2026:RequiresUnreferencedCode",
    Justification = "JSON serialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
[UnconditionalSuppressMessage("AOT",
    "IL3050:RequiresDynamicCode",
    Justification = "JSON serialization is AOT-safe when users register a source-generated JsonSerializerContext.")]
[SkipLocalsInit]
internal sealed partial class InvocationStateMachine : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = FrozenDictionary<string, string>.Empty;
    private static readonly JsonWriterOptions WriterOptions = new() { SkipValidation = true };
    private readonly CompletionManager _completions = new();
    private readonly CompletionManager _signalCompletions = new();

    // Named-signal waiters (Rust NotificationId::SignalName, mod.rs:940). Keyed by NAME because the
    // wire delivers a named signal by name with no numeric idx — it can never share the numeric
    // signal-id space, so it gets its own string-keyed manager. Creating a named-signal handle (the
    // await side) consumes NEITHER the completion-id NOR the signal-id counter: Rust
    // create_signal_handle does not call next_*_notification_id. It is purely local — no command, no
    // journal entry — so it is replay-safe by construction (nothing to dequeue on a later attempt).
    private readonly NamedCompletionManager _namedSignals = new();
    private readonly InvocationJournal _journal = new();
    private readonly ProtocolReader _reader;

    // Implicit child-cancellation registry — the .NET twin of Rust's tracked_invocation_ids
    // (vm/mod.rs:95). Each entry is the completion id of a child's CallInvocationIdCompletion
    // notification (the wire invocation_id_notification_idx); the resolved invocation-id STRING is
    // already parked in _completions by HandleIncomingMessage's `notification.InvocationId is not null`
    // branch, so no separate id field is needed here. Appended STRICTLY in Call/Send journal order
    // under _commandLock (the same section that allocates the id and journals/dequeues the command),
    // so registry order == journal order == id-allocation order on every attempt — the invariant that
    // makes the child-cancel emission below deterministic across replay. On inbound CANCEL the single
    // terminal writer (FailTerminalAsync) walks this list and emits one cancel SendSignalCommand per
    // RESOLVED child, matching Rust do_progress (mod.rs:445-476).
    private readonly List<uint> _trackedChildren = new();

    // Resolved child invocation-ids snapshotted at inbound-CANCEL time, in registry (== journal) order.
    // CAPTURED inside _commandLock by TriggerCancellation BEFORE _completions.FailAll clears the table —
    // the resolved id strings live in _completions and FailAll wipes them, so we must read them first.
    // EmitChildCancelsLocked (the terminal writer, same lock) drains this list to emit one cancel
    // SendSignal per resolved child. Null until a CANCEL fires; ordering preserved so replay determinism
    // holds (the snapshot reflects the SAME resolved set on every attempt — see EmitChildCancelsLocked).
    private List<string>? _cancelledChildInvocationIds;

    // Gate mirroring VMOptions::default() (lib.rs:255-258): Rust pushes onto tracked_invocation_ids
    // only when cancel_children_calls=true (sys_call, mod.rs:766-777) and cancel_children_one_way_calls
    // =true (sys_send, mod.rs:843-854). The defaults are { calls: true, one_way: false }. We bake those
    // defaults in: CallPrefixAsync ALWAYS appends to _trackedChildren, SendPrefix NEVER does
    // (scope-limitation #2, documented at EmitChildCancelsLocked). Carrying a runtime `if (false)` Send
    // branch would be both an uncoverable runtime path and a CS0162/CA1805 build error, so the gate is
    // expressed by code structure — the precise, intentional divergence from Rust's configurable gate.

    // Mirrors sdk-shared-core vm/context.rs Journal::default():
    //   completion_index: 1  ("Clever trick for protobuf here" — 0 means field-unset)
    //   signal_index: 17     ("1 to 16 are reserved!" — BuiltInSignal.CANCEL = 1, 2-15 reserved)
    private const uint FirstCompletionId = 1;
    private const uint FirstUserSignalId = 17;
    internal const uint CancelSignalId = 1;

    // Restate's cross-SDK cancellation status code (HTTP 409 Conflict). Shared-core defines NO
    // cancellation code (errors.rs codes are protocol/journal errors); CANCEL surfaces only as the
    // control enum DoProgressResponse::CancelSignalReceived and the SDK layer picks 409 "cancelled"
    // — the convention used by the Rust/Java/TS SDKs — for the terminal OutputCommand failure.
    internal const ushort CancelledStatusCode = 409;

    private uint _nextCompletionId = FirstCompletionId;
    private uint _nextSignalId = FirstUserSignalId;

    // Plain (non-Interlocked) BY DESIGN: both counters are only ever touched inside _commandLock,
    // the same section that journals/dequeues the command — that is what makes id order == journal
    // order under fan-out. Rust next_completion_notification_id()/next_signal_notification_id().
    private uint NextCompletionId() => _nextCompletionId++;
    private uint NextSignalId() => _nextSignalId++;

    internal enum NotificationKind { Completion, Signal }

    // ONE mutual-exclusion domain for ALL VM state — the .NET analogue of Rust's &mut self.
    // Guards: _nextCompletionId/_nextSignalId, _journal (replay queue + counters + RecordCommand),
    // _serializeBuffer, WriteCommand (sync PipeWriter buffer copies), State/_suspended transitions,
    // _inputClosed/_executingRuns/_awaiting (suspension condition), _eagerState/_eagerStateIsPartial.
    // NEVER held across any await. All sections are short sync buffer work.
    private readonly object _commandLock = new();

    // Serializes FlushAsync calls only (PipeWriter does not allow concurrent flushes). Frame ORDER
    // is fixed at WriteCommand time inside _commandLock; a flush pushes everything buffered so far.
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    // Suspension state — ALL guarded by _commandLock (no volatile/Interlocked needed: the lock
    // supplies the fences a volatile store-load pair could not).
    private bool _inputClosed;
    private bool _suspended;
    private int _executingRuns;
    private readonly HashSet<(uint Id, NotificationKind Kind)> _awaiting = new();

    // Parked named-signal waits (string-keyed twin of _awaiting). Guarded by _commandLock just like
    // _awaiting; feeds SuspensionMessage.waiting_named_signals (proto field 3) in TrySuspendAsync.
    private readonly HashSet<string> _awaitingNamed = new(StringComparer.Ordinal);

    // Inbound-CANCEL state (SignalNotification idx=1 cancels THIS invocation). Shared-core surfaces
    // CANCEL purely as the control enum DoProgressResponse::CancelSignalReceived (lib.rs:275) — it
    // defines NO cancellation error code and emits no terminal frame itself; the SDK layer owns the
    // user-visible shape. This SDK follows the Restate cross-SDK convention: parked durable awaits
    // throw TerminalException(409, "cancelled"), the handler unwinds through the EXISTING
    // TerminalException catch arm, and the SDK writes a terminal OutputCommand{failure:409} + End.
    // _cancelled is guarded by _commandLock; _cancelCts is the handler-token path so non-awaiting
    // (CPU/loop) handler code observes cancel cooperatively (a parked await observes it via the
    // faulted TCS — a bare `await tcs.Task` cannot observe a CancellationToken).
    private bool _cancelled;
    private readonly CancellationTokenSource _cancelCts = new();
    internal CancellationToken CancelToken => _cancelCts.Token;

    // True once an inbound CANCEL has fired. InvocationHandler reads this to translate a non-terminal
    // unwind (e.g., an OperationCanceledException thrown by non-awaiting handler code observing the
    // cancelled handler token) into the 409 terminal cancel Output instead of a 500 Error frame.
    internal bool IsCancellationRequested => _cancelCts.IsCancellationRequested;

    // Reusable buffer for serialization — avoids allocating ArrayBufferWriter<byte> per call.
    // The returned ReadOnlyMemory is only valid until the next Serialize call.
    // Thread-safe: only mutated inside _commandLock with bytes copied before the lock is released.
    private readonly ArrayBufferWriter<byte> _serializeBuffer = new(256);
    private readonly ProtocolWriter _writer;

    // Eager state: { is_partial, Dictionary<key, value?> } — Rust EagerState (vm/context.rs:373-435).
    // Value null = known-cleared marker (Rust None). Absent key + partial = Unknown;
    // absent + complete = Empty.
    private readonly Dictionary<string, ReadOnlyMemory<byte>?> _eagerState = new();
    private bool _eagerStateIsPartial = true;   // EagerState::default() => is_partial: true

    // Reusable Utf8JsonWriter — avoids allocating a new writer per Serialize call.
    // Reset() is called before each use to point at _serializeBuffer.
    private Utf8JsonWriter? _jsonWriter;

    // Tracks ArrayPool rentals from CopyToPooled for batch return on Dispose.
    private List<byte[]>? _rentedBuffers;

    public InvocationStateMachine(ProtocolReader reader, ProtocolWriter writer,
        JsonSerializerOptions? jsonOptions = null, ILogger? logger = null)
    {
        _reader = reader;
        _writer = writer;
        JsonOptions = jsonOptions ?? JsonSerde.SerializerOptions;
        Logger = logger ?? NullLogger.Instance;
    }

    public InvocationState State { get; private set; } = InvocationState.WaitingStart;

    public string InvocationId { get; private set; } = "";

    public byte[] RawInvocationId { get; private set; } = [];

    public string Key { get; private set; } = "";

    public ulong RandomSeed { get; private set; }

    public JsonSerializerOptions JsonOptions { get; }

    public bool IsReplaying => _journal.IsReplaying;

    // Lazy headers: raw pairs stored on parse, FrozenDictionary built only on first access.
    private Dictionary<string, string>? _rawHeaders;
    private IReadOnlyDictionary<string, string>? _headers;
    public IReadOnlyDictionary<string, string> Headers =>
        _headers ??= _rawHeaders is not null
            ? _rawHeaders.ToFrozenDictionary()
            : EmptyHeaders;

    public ILogger Logger { get; }

    // G12: the service-protocol version the runtime negotiated for this invocation, parsed from the
    // /invoke request Content-Type and validated to be within [V5,V6] BEFORE the SM runs. Defaults to
    // the max we speak so direct SM tests (no HTTP layer) behave as the highest negotiated version; the
    // host overwrites it per request. Mirrors Context.negotiated_protocol_version (vm/mod.rs).
    public int NegotiatedProtocolVersion { get; set; } = ProtocolVersion.MaximumSupported;

    // G11: terminal-error Failure.metadata is a V6 feature. The verify_error_metadata_feature_support
    // analogue (vm/mod.rs:118-124) — emission of metadata is gated on the negotiated version; on a
    // sub-V6 negotiation metadata is silently dropped from the outgoing Failure.
    private bool SupportsErrorMetadata => ProtocolVersion.SupportsErrorMetadata(NegotiatedProtocolVersion);

    /// <summary>
    ///     Returns a terminal exception's metadata to serialize onto an outgoing Failure, or
    ///     <see langword="null" /> when the negotiated version cannot carry it (sub-V6) or there is
    ///     none. Centralizes the V6 gate so every Failure emit path (output / run / awakeable /
    ///     promise / named-signal) drops metadata identically on older protocols.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? OutgoingFailureMetadata(TerminalException? ex) =>
        ex is { Metadata.Count: > 0 } && SupportsErrorMetadata ? ex.Metadata : null;

    public void Dispose()
    {
        _completions.CancelAll();
        _signalCompletions.CancelAll();
        _namedSignals.CancelAll();
        _jsonWriter?.Dispose();
        _flushGate.Dispose();
        _cancelCts.Dispose();

        if (_rentedBuffers is not null)
        {
            foreach (var buf in _rentedBuffers)
                ArrayPool<byte>.Shared.Return(buf);
            _rentedBuffers = null;
        }

        State = InvocationState.Closed;
    }

    /// <summary>
    ///     Copies serialized bytes to a pooled buffer, tracked for batch return on Dispose.
    ///     Use instead of .ToArray() when the source references the reusable _serializeBuffer.
    /// </summary>
    private ReadOnlyMemory<byte> CopyToPooled(ReadOnlyMemory<byte> source)
    {
        var rented = ArrayPool<byte>.Shared.Rent(source.Length);
        source.Span.CopyTo(rented);
        (_rentedBuffers ??= new List<byte[]>(8)).Add(rented);
        return rented.AsMemory(0, source.Length);
    }

    public void Initialize(string invocationId, string key, ulong randomSeed,
        int knownEntries, Dictionary<string, ReadOnlyMemory<byte>?>? eagerState = null,
        bool partialState = true) =>
        Initialize(invocationId, [], key, randomSeed, knownEntries, eagerState, partialState);

    public void Initialize(string invocationId, byte[] rawInvocationId, string key, ulong randomSeed,
        int knownEntries, Dictionary<string, ReadOnlyMemory<byte>?>? eagerState = null,
        bool partialState = true)
    {
        if (State != InvocationState.WaitingStart)
            ThrowInvalidState(State, "initialize");

        // KNOWN_ENTRIES_IS_ZERO parity (vm/transitions/input.rs:66 — resolved decision 4): the
        // batch must contain at least the input entry.
        if (knownEntries < 1)
            throw new ProtocolException("known_entries is zero; expected at least the input entry");

        InvocationId = invocationId;
        RawInvocationId = rawInvocationId;
        Key = key;
        RandomSeed = randomSeed;
        if (eagerState is not null)
            foreach (var pair in eagerState)
                _eagerState[pair.Key] = pair.Value;
        _eagerStateIsPartial = partialState;
        _journal.Initialize(knownEntries);
        // Provisional: entry 0 is the Input consumed by StartAsync itself, so > 1 commands/notifications
        // mean a replay batch. StartAsync finalizes State after buffering.
        State = knownEntries > 1 ? InvocationState.Replaying : InvocationState.Processing;

        if (State == InvocationState.Replaying)
            Log.ReplayStarted(Logger, InvocationId, knownEntries);
    }

    private void EnsureActive()
    {
        // Suspension-aware and LOCKED: State+_suspended are only coherent under _commandLock — a
        // fan-out closure finishing concurrently with suspension must see SuspendedException, not a
        // stale InvalidOperationException.
        lock (_commandLock)
        {
            if (State == InvocationState.Closed && _suspended) throw new SuspendedException();
            if (State is InvocationState.WaitingStart or InvocationState.Closed)
                ThrowInvalidState(State, "perform operations");
        }
    }

    // Caller MUST hold _commandLock.
    private void ThrowIfClosedLocked()
    {
        if (State == InvocationState.Closed)
            throw _suspended ? new SuspendedException()
                             : new InvalidOperationException("Invocation already closed");
    }

    [DoesNotReturn]
    private static void ThrowInvalidState(InvocationState state, string operation)
    {
        throw new InvalidOperationException($"Cannot {operation} in state {state}");
    }

    /// <summary>
    ///     Writes a command from a Google.Protobuf IMessage, serializing directly into the protocol writer's buffer.
    /// </summary>
    private void WriteCommand(MessageType type, IMessage message)
    {
        var size = message.CalculateSize();
        Log.WritingCommand(Logger, InvocationId, type, size);
        var memory = _writer.GetPayloadMemory(type, MessageFlags.None, size);
        message.WriteTo(memory.Span[..size]);
        _writer.AdvancePayload(size);
    }

    internal ValueTask FlushAsync(CancellationToken ct)
    {
        Log.Flushing(Logger, InvocationId);

        // PipeWriter.FlushAsync commits the buffered segment SYNCHRONOUSLY (advancing committed
        // bytes / signalling the reader) before returning the backpressure task. That commit shares
        // the Pipe's reserved-byte accounting with WriteCommand's GetMemory/Advance, and the Pipe
        // permits at most one writer with no overlap between buffer-fill and flush. WriteCommand
        // runs under _commandLock, so the flush INVOCATION must take the same lock — otherwise a
        // fan-out thread filling the buffer races this flush's commit and corrupts the accounting
        // (Advance throws ArgumentOutOfRangeException). The returned task is awaited OUTSIDE the
        // lock so backpressure never blocks _commandLock; _flushGate (FlushGatedAsync) still
        // serializes concurrent flushes, which the Pipe also forbids.
        ValueTask<FlushResult> task;
        lock (_commandLock)
            task = _writer.FlushAsync(ct);

        if (task.IsCompletedSuccessfully)
        {
            _ = task.Result;
            Log.FlushCompleted(Logger, InvocationId);
            return ValueTask.CompletedTask;
        }

        return AwaitFlush(task);
    }

    private async ValueTask AwaitFlush(ValueTask<FlushResult> task)
    {
        await task.ConfigureAwait(false);
        Log.FlushCompleted(Logger, InvocationId);
    }

    private Utf8JsonWriter GetJsonWriter()
    {
        _serializeBuffer.ResetWrittenCount();
        if (_jsonWriter is null)
            _jsonWriter = new Utf8JsonWriter(_serializeBuffer, WriterOptions);
        else
            _jsonWriter.Reset(_serializeBuffer);
        return _jsonWriter;
    }

    /// <summary>Serializes a value using generic <see cref="JsonSerializer" /> — no boxing, AOT-safe.</summary>
    private ReadOnlyMemory<byte> Serialize<T>(T value)
    {
        var writer = GetJsonWriter();
        JsonSerializer.Serialize(writer, value, JsonOptions);
        writer.Flush();
        return _serializeBuffer.WrittenMemory;
    }

    /// <summary>Deserializes a value using generic <see cref="JsonSerializer" /> — no typeof, AOT-safe.</summary>
    internal T Deserialize<T>(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty) return default!;
        var reader = new Utf8JsonReader(data.Span);
        return JsonSerializer.Deserialize<T>(ref reader, JsonOptions)!;
    }

    /// <summary>
    ///     Serializes an object value. Used for untyped Call/Send where request is <c>object?</c>.
    ///     With source-generated <c>JsonSerializerContext</c>, this resolves the type at runtime
    ///     but uses the generated serializer — AOT-safe when all types are registered.
    /// </summary>
    internal ReadOnlyMemory<byte> SerializeObject(object? value)
    {
        if (value is null)
            return ReadOnlyMemory<byte>.Empty;
        var writer = GetJsonWriter();
        JsonSerializer.Serialize(writer, value, value.GetType(), JsonOptions);
        writer.Flush();
        return _serializeBuffer.WrittenMemory;
    }

    /// <summary>
    ///     Serializes a value using a typed serde or falls back to the default JSON serializer.
    ///     Returns bytes that are valid until the next Serialize call.
    /// </summary>
    internal ReadOnlyMemory<byte> SerializeWithSerde<T>(T value, ISerde<T>? serde)
    {
        if (serde is not null)
        {
            _serializeBuffer.ResetWrittenCount();
            serde.Serialize(_serializeBuffer, value);
            return _serializeBuffer.WrittenMemory;
        }

        return Serialize(value);
    }

    // ── Suspension / park API (B8) ────────────────────────────────────

    /// <summary>
    ///     Single park point for all notification waits (Rust do_progress analogue): registers the
    ///     awaited id, enforces the replay-mutation guard, evaluates the suspension condition at the
    ///     await site, and deregisters on unpark.
    /// </summary>
    internal async ValueTask<CompletionResult> AwaitNotificationAsync(uint id, NotificationKind kind)
    {
        var manager = kind == NotificationKind.Completion ? _completions : _signalCompletions;
        var tcs = manager.GetOrRegister((int)id);
        if (tcs.Task.IsCompleted) return await tcs.Task.ConfigureAwait(false);

        lock (_commandLock)
        {
            // UncompletedDoProgressDuringReplay parity (async_results.rs:50-112): a missing
            // completion while journaled commands remain proves an added await point.
            if (_journal.IsReplaying && !tcs.Task.IsCompleted)
                throw new ProtocolException(
                    $"Uncompleted await during replay (journal mutation / added await point): " +
                    $"awaiting {kind} id {id}; last command " +
                    $"{_journal.LastCommandType} '{_journal.LastCommandName}' " +
                    $"at index {_journal.CommandIndex}");
            _awaiting.Add((id, kind));
        }

        try
        {
            Log.AwaitingCompletion(Logger, InvocationId, (int)id);
            // EOF-before-park coverage: the pump may have closed input before we got here.
            await TrySuspendAsync().ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_commandLock) _awaiting.Remove((id, kind));
        }
    }

    /// <summary>
    ///     Registers a named-signal handle (Rust create_signal_handle, mod.rs:935-942). PURELY LOCAL:
    ///     no command is written, no journal entry is recorded, and neither id counter advances — a
    ///     named handle consumes neither the completion-id nor the signal-id space (Rust does not call
    ///     next_*_notification_id here). Idempotent registration so awaiting the same name twice (or a
    ///     re-registration on a replay attempt) reuses one slot. Replay-safe by construction: with no
    ///     journaled command there is nothing to dequeue across attempts.
    /// </summary>
    public void RegisterNamedSignal(string name)
    {
        EnsureActive();
        _namedSignals.GetOrRegister(name);
    }

    /// <summary>
    ///     The named-signal analogue of <see cref="AwaitNotificationAsync" />: parks on a string name
    ///     rather than a numeric id. Same single-park discipline — register under _commandLock, enforce
    ///     the replay-mutation guard (a missing named result while journaled commands remain proves an
    ///     added await point), evaluate the suspension condition, await the name's TCS, deregister in a
    ///     finally. No journal command is ever written for the await side, so it is replay-safe with
    ///     nothing to dequeue.
    /// </summary>
    internal async ValueTask<CompletionResult> AwaitNamedSignalAsync(string name)
    {
        var tcs = _namedSignals.GetOrRegister(name);
        if (tcs.Task.IsCompleted) return await tcs.Task.ConfigureAwait(false);

        lock (_commandLock)
        {
            // UncompletedDoProgressDuringReplay parity (async_results.rs:50-112): an unresolved named
            // signal while journaled commands remain proves the code added an await point on replay.
            if (_journal.IsReplaying && !tcs.Task.IsCompleted)
                throw new ProtocolException(
                    $"Uncompleted await during replay (journal mutation / added await point): " +
                    $"awaiting named signal '{name}'; last command " +
                    $"{_journal.LastCommandType} '{_journal.LastCommandName}' " +
                    $"at index {_journal.CommandIndex}");
            _awaitingNamed.Add(name);
        }

        try
        {
            await TrySuspendAsync().ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_commandLock) _awaitingNamed.Remove(name);
        }
    }

    /// <summary>
    ///     HitSuspensionPoint (terminal.rs:18-53): suspend iff input is closed, no Run closure is
    ///     mid-flight (any_executing/WaitingPendingRun guard), and at least one parked awaiter's id
    ///     is still unresolved. The whole condition AND the Suspension write happen under
    ///     _commandLock, so suspension is atomic w.r.t. every other state transition (no Dekker
    ///     store-load race, no Error/End-after-Suspension interleaving, no waiter
    ///     registered-but-omitted window).
    /// </summary>
    internal async ValueTask TrySuspendAsync()
    {
        List<uint> waitingCompletions;
        List<uint> waitingSignals;
        List<string> waitingNamedSignals;
        lock (_commandLock)
        {
            if (State == InvocationState.Closed) return;   // already completed/aborted/suspended
            // Cancel wins a concurrent suspension: an inbound CANCEL must NOT race a Suspension frame
            // ahead of the terminal cancel Output. _cancelled is set under _commandLock by
            // TriggerCancellation, so this check is coherent with it; the parked await already unwinds
            // via the faulted TCS into FailTerminalAsync(409). Exactly one terminal frame is written.
            if (_cancelled) return;
            if (!_inputClosed || _executingRuns > 0) return;
            waitingCompletions = new List<uint>();
            waitingSignals = new List<uint>();
            waitingNamedSignals = new List<string>();
            foreach (var (id, kind) in _awaiting)
            {
                var manager = kind == NotificationKind.Completion ? _completions : _signalCompletions;
                if (manager.HasResultFor((int)id)) continue;   // resolved — that waiter will unpark
                if (kind == NotificationKind.Completion) waitingCompletions.Add(id);
                else waitingSignals.Add(id);
            }

            // Named-signal waits feed waiting_named_signals (proto field 3) — Rust fills it from
            // NotificationId::SignalName (terminal.rs:43-46). A name already resolved (early delivery)
            // is skipped exactly like a resolved numeric waiter so the runtime is not asked to wake on
            // a signal that already landed.
            foreach (var name in _awaitingNamed)
                if (!_namedSignals.HasResultFor(name))
                    waitingNamedSignals.Add(name);

            // The suspension condition spans ALL three wait kinds — a handler parked solely on a named
            // signal must still suspend (between the lists there MUST be at least one element, proto:92).
            if (waitingCompletions.Count == 0 && waitingSignals.Count == 0
                && waitingNamedSignals.Count == 0) return;   // nobody truly parked
            waitingCompletions.Sort();
            waitingSignals.Sort();
            waitingNamedSignals.Sort(StringComparer.Ordinal);
            WriteCommand(MessageType.Suspension,
                ProtobufCodec.CreateSuspensionMessage(waitingCompletions, waitingSignals, waitingNamedSignals));
            State = InvocationState.Closed;
            _suspended = true;
        }

        Log.InvocationSuspended(Logger, InvocationId);
        await FlushGatedAsync(CancellationToken.None).ConfigureAwait(false);   // NO End frame after suspension
        _completions.FailAll(new SuspendedException());
        _signalCompletions.FailAll(new SuspendedException());
        _namedSignals.FailAll(new SuspendedException());
    }

    internal void MarkInputClosed()
    {
        lock (_commandLock) _inputClosed = true;
    }

    /// <summary>
    ///     Inbound-CANCEL entry point (SignalNotification idx=1 → CANCEL_SIGNAL_ID). Faults every
    ///     parked durable await with TerminalException(409, "cancelled") — the EXACT B8 suspension
    ///     unwind mechanism, but with a terminal exception instead of SuspendedException so the await
    ///     unwinds through InvocationHandler's existing `catch (TerminalException)` arm
    ///     (FailTerminalAsync → OutputCommand{failure:409} + End). A bare `await tcs.Task` cannot
    ///     observe a CancellationToken, so faulting the TCSs is the only way parked awaits unwind.
    ///     The FailAll _terminal latch makes any await registered AFTER cancel born-faulted, so a
    ///     straggler fan-out closure parking post-cancel unwinds immediately. _cancelCts cancels the
    ///     handler token so non-awaiting (CPU/loop) handler code stops cooperatively. The _cancelled
    ///     flag (set under _commandLock) suppresses a racing Suspension frame in TrySuspendAsync so
    ///     exactly one terminal frame is written and cancel wins.
    /// </summary>
    private void TriggerCancellation()
    {
        lock (_commandLock)
        {
            _cancelled = true;
            // Snapshot the RESOLVED tracked children NOW, before FailAll below clears _completions.
            // The resolved invocation-id string is parked in _completions under the child's
            // invocation-id completion id (HandleIncomingMessage InvocationId branch); we read it via
            // the non-consuming TryGetResult, in registry (== journal) order. An unresolved (or failed)
            // child yields false and is omitted — scope-limitation #1 (skip, never suspend). The single
            // terminal writer (FailTerminalAsync → EmitChildCancelsLocked) drains this list under the
            // same lock, so the cancel SendSignals are deterministic across replay.
            foreach (var childCompletionId in _trackedChildren)
                if (_completions.TryGetResult((int)childCompletionId, out var resolved))
                {
                    var childId = resolved.StringValue
                                  ?? System.Text.Encoding.UTF8.GetString(resolved.Value.Span);
                    (_cancelledChildInvocationIds ??= new List<string>()).Add(childId);
                }
        }

        // OUTSIDE the lock: FailAll resolves TCSs whose continuations run async
        // (RunContinuationsAsynchronously), and _cancelCts.Cancel() may run synchronous registrations;
        // neither may run while holding _commandLock. The shared _terminal latch makes a concurrent
        // suspension's FailAll a no-op — whichever fires first wins.
        _completions.FailAll(new TerminalException("cancelled", CancelledStatusCode));
        _signalCompletions.FailAll(new TerminalException("cancelled", CancelledStatusCode));
        _namedSignals.FailAll(new TerminalException("cancelled", CancelledStatusCode));
        _cancelCts.Cancel();
    }

    /// <summary>
    ///     Serializes FlushAsync calls (PipeWriter forbids concurrent flushes). Frame order is fixed
    ///     at WriteCommand time inside _commandLock; this only pushes everything buffered so far.
    /// </summary>
    private async ValueTask FlushGatedAsync(CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try { await FlushAsync(ct).ConfigureAwait(false); }
        finally { _flushGate.Release(); }
    }

    /// <summary>
    ///     Gated flush for callers outside this class (e.g., the awakeable pre-park flush in
    ///     DefaultContext): pushes everything buffered so far through the single flush gate so it can
    ///     never race a detached Run proposal's flush.
    /// </summary>
    internal ValueTask FlushPendingAsync(CancellationToken ct) => FlushGatedAsync(ct);
}