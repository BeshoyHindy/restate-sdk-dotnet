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
    private readonly InvocationJournal _journal = new();
    private readonly ProtocolReader _reader;

    // Mirrors sdk-shared-core vm/context.rs Journal::default():
    //   completion_index: 1  ("Clever trick for protobuf here" — 0 means field-unset)
    //   signal_index: 17     ("1 to 16 are reserved!" — BuiltInSignal.CANCEL = 1, 2-15 reserved)
    private const uint FirstCompletionId = 1;
    private const uint FirstUserSignalId = 17;
    internal const uint CancelSignalId = 1;

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

    public void Dispose()
    {
        _completions.CancelAll();
        _signalCompletions.CancelAll();
        _jsonWriter?.Dispose();
        _flushGate.Dispose();

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

    // Plan 07 §1.3 escape hatch: these two raw-bytes WriteCommand overloads have ZERO call sites —
    // every command is written through the IMessage overload below (the codec factories all return
    // Gen.* messages), so no internal test seam can reach them without re-introducing dead code. They
    // are retained as a symmetric low-level API surface; excluded from coverage so the dead lines do
    // not block the StateMachine 100% line target. See the "Coverage exclusions" appendix in
    // docs/research/shared-core/07-coverage-and-e2e-plan.md.
    [ExcludeFromCodeCoverage(Justification =
        "Unused raw-span WriteCommand overload — every caller writes via the IMessage overload; no reachable trigger.")]
    private void WriteCommand(MessageType type, ReadOnlySpan<byte> payload)
    {
        Log.WritingCommand(Logger, InvocationId, type, payload.Length);
        _writer.WriteMessage(type, payload);
    }

    [ExcludeFromCodeCoverage(Justification =
        "Unused raw-memory WriteCommand overload — every caller writes via the IMessage overload; no reachable trigger.")]
    private void WriteCommand(MessageType type, ReadOnlyMemory<byte> payload)
    {
        WriteCommand(type, payload.Span);
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
        lock (_commandLock)
        {
            if (State == InvocationState.Closed) return;   // already completed/aborted/suspended
            if (!_inputClosed || _executingRuns > 0) return;
            waitingCompletions = new List<uint>();
            waitingSignals = new List<uint>();
            foreach (var (id, kind) in _awaiting)
            {
                var manager = kind == NotificationKind.Completion ? _completions : _signalCompletions;
                if (manager.HasResultFor((int)id)) continue;   // resolved — that waiter will unpark
                if (kind == NotificationKind.Completion) waitingCompletions.Add(id);
                else waitingSignals.Add(id);
            }

            if (waitingCompletions.Count == 0 && waitingSignals.Count == 0) return;   // nobody truly parked
            waitingCompletions.Sort();
            waitingSignals.Sort();
            WriteCommand(MessageType.Suspension,
                ProtobufCodec.CreateSuspensionMessage(waitingCompletions, waitingSignals));
            State = InvocationState.Closed;
            _suspended = true;
        }

        Log.InvocationSuspended(Logger, InvocationId);
        await FlushGatedAsync(CancellationToken.None).ConfigureAwait(false);   // NO End frame after suspension
        _completions.FailAll(new SuspendedException());
        _signalCompletions.FailAll(new SuspendedException());
    }

    internal void MarkInputClosed()
    {
        lock (_commandLock) _inputClosed = true;
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