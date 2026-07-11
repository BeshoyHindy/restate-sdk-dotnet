using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
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

    // Signal indices 0-16 are reserved for protocol built-in signals (SIGNAL_UNKNOWN = 0,
    // CANCEL = 1, the rest held back for future built-ins); user signals (awakeables) start
    // at 17, matching the official SDKs. Allocating from 0 would collide with the reserved
    // range: a runtime CANCEL signal would resolve a user awakeable with fabricated data.
    internal const int CancelSignalIndex = 1;
    internal const int FirstUserSignalIndex = 17;
    private int _nextSignalIndex = FirstUserSignalIndex;

    // Next completion id for live commands. Completion ids are SDK-chosen opaque keys echoed
    // back by the runtime in notifications. On resume, StartAsync seeds this past every id
    // found in the replayed command prefix so live commands never collide with replayed ones.
    private uint _nextCompletionId;

    // Reusable buffer for serialization — avoids allocating ArrayBufferWriter<byte> per call.
    // The returned ReadOnlyMemory is only valid until the next Serialize call.
    // Thread-safe: each InvocationStateMachine handles a single invocation with no concurrent access.
    private readonly ArrayBufferWriter<byte> _serializeBuffer = new(256);
    private readonly ProtocolWriter _writer;
    private Dictionary<string, ReadOnlyMemory<byte>>? _initialState;

    // True when the StartMessage state map is partial: only the keys it contains are locally
    // known; absence from the map is NOT definitive and must fall back to a lazy state read.
    private bool _stateIsPartial;

    // Reusable Utf8JsonWriter — avoids allocating a new writer per Serialize call.
    // Reset() is called before each use to point at _serializeBuffer.
    private Utf8JsonWriter? _jsonWriter;

    // Tracks ArrayPool rentals from CopyToPooled for batch return on Dispose.
    private List<byte[]>? _rentedBuffers;

    // When true, durable operations (Run/Call/Sleep) start opt-in child activities.
    private readonly bool _enableOperationActivities;

    public InvocationStateMachine(ProtocolReader reader, ProtocolWriter writer,
        JsonSerializerOptions? jsonOptions = null, ILogger? logger = null,
        ServiceProtocolVersion negotiatedVersion = ServiceProtocolVersion.V6,
        bool enableOperationActivities = false)
    {
        _reader = reader;
        _writer = writer;
        JsonOptions = jsonOptions ?? JsonSerde.SerializerOptions;
        Logger = logger ?? NullLogger.Instance;
        NegotiatedVersion = negotiatedVersion;
        _enableOperationActivities = enableOperationActivities;
    }

    public InvocationState State { get; private set; } = InvocationState.WaitingStart;

    /// <summary>
    ///     The service protocol version negotiated from the request content type.
    ///     Determines version-dependent wire encodings (e.g. the SuspensionMessage shape).
    /// </summary>
    public ServiceProtocolVersion NegotiatedVersion { get; }

    /// <summary>
    ///     True once the request input stream has reached EOF. After that point no completion
    ///     or signal can ever arrive, so pending durable waits are poisoned with
    ///     <see cref="SuspensionException" /> and the invocation suspends.
    /// </summary>
    public bool InputClosed { get; private set; }

    public string InvocationId { get; private set; } = "";

    public byte[] RawInvocationId { get; private set; } = [];

    public string Key { get; private set; } = "";

    public ulong RandomSeed { get; private set; }

    /// <summary>
    ///     The scope this invocation was called within (StartMessage V7 field 10), or null.
    ///     Captured for diagnostics; no public API is built on top of it yet.
    /// </summary>
    public string? Scope { get; private set; }

    /// <summary>
    ///     The concurrency limit key of this invocation (StartMessage V7 field 11), or null.
    ///     Only meaningful when <see cref="Scope" /> is set.
    /// </summary>
    public string? LimitKey { get; private set; }

    /// <summary>
    ///     The idempotency key this invocation was submitted with (StartMessage V7 field 12), or null.
    /// </summary>
    public string? IdempotencyKey { get; private set; }

    public JsonSerializerOptions JsonOptions { get; }

    public bool IsReplaying => _journal.IsReplaying;

    /// <summary>Number of journal commands recorded for this invocation so far.</summary>
    public int JournalCommandCount => _journal.Count;

    /// <summary>
    ///     Number of journal commands the server replayed at start (<c>known_entries</c>),
    ///     including the input command. Greater than 1 means this attempt replayed prior progress.
    /// </summary>
    public int ReplayedCommandCount => _journal.KnownEntries;

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
        _journal.Dispose();
        _jsonWriter?.Dispose();

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

    /// <summary>
    ///     Starts an opt-in child activity for a durable operation (Run/Call/Sleep).
    ///     Returns null unless per-operation activities are enabled AND a listener is attached
    ///     to the <c>Restate.Sdk</c> source — zero cost on the hot path when off.
    ///     Replay branches never start activities: no user code executes during replay.
    /// </summary>
    private Activity? StartOperationActivity(string name)
    {
        return _enableOperationActivities && InvocationHandler.ActivitySource.HasListeners()
            ? InvocationHandler.ActivitySource.StartActivity(name)
            : null;
    }

    public void Initialize(string invocationId, string key, ulong randomSeed,
        int knownEntries, Dictionary<string, ReadOnlyMemory<byte>>? initialState = null,
        bool stateIsPartial = false) =>
        Initialize(invocationId, [], key, randomSeed, knownEntries, initialState, stateIsPartial);

    public void Initialize(string invocationId, byte[] rawInvocationId, string key, ulong randomSeed,
        int knownEntries,
        Dictionary<string, ReadOnlyMemory<byte>>? initialState = null,
        bool stateIsPartial = false)
    {
        if (State != InvocationState.WaitingStart)
            ThrowInvalidState(State, "initialize");

        InvocationId = invocationId;
        RawInvocationId = rawInvocationId;
        Key = key;
        RandomSeed = randomSeed;
        _initialState = initialState;
        _stateIsPartial = stateIsPartial;
        _journal.Initialize(knownEntries);
        State = knownEntries > 0 ? InvocationState.Replaying : InvocationState.Processing;

        if (State == InvocationState.Replaying)
            Log.ReplayStarted(Logger, InvocationId, knownEntries);
    }

    /// <summary>Allocates the next completion id for a live command.</summary>
    private uint NextCompletionId() => _nextCompletionId++;

    private void EnsureActive()
    {
        if (State is InvocationState.WaitingStart or InvocationState.Suspended or InvocationState.Closed)
            ThrowInvalidState(State, "perform operations");
    }

    [DoesNotReturn]
    private static void ThrowInvalidState(InvocationState state, string operation)
    {
        throw new InvalidOperationException($"Cannot {operation} in state {state}");
    }

    private void WriteCommand(MessageType type, ReadOnlySpan<byte> payload)
    {
        Log.WritingCommand(Logger, InvocationId, type, payload.Length);
        _writer.WriteMessage(type, payload);
    }

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
        var task = _writer.FlushAsync(ct);
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
}