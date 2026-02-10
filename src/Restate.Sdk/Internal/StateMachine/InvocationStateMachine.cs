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
    private int _nextSignalIndex;

    // Reusable buffer for serialization — avoids allocating ArrayBufferWriter<byte> per call.
    // The returned ReadOnlyMemory is only valid until the next Serialize call.
    // Thread-safe: each InvocationStateMachine handles a single invocation with no concurrent access.
    private readonly ArrayBufferWriter<byte> _serializeBuffer = new(256);
    private readonly ProtocolWriter _writer;
    private Dictionary<string, ReadOnlyMemory<byte>>? _initialState;

    // Reusable Utf8JsonWriter — avoids allocating a new writer per Serialize call.
    // Reset() is called before each use to point at _serializeBuffer.
    private Utf8JsonWriter? _jsonWriter;

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
        _journal.Dispose();
        _jsonWriter?.Dispose();
        State = InvocationState.Closed;
    }

    public void Initialize(string invocationId, string key, ulong randomSeed,
        int knownEntries, Dictionary<string, ReadOnlyMemory<byte>>? initialState = null) =>
        Initialize(invocationId, [], key, randomSeed, knownEntries, initialState);

    public void Initialize(string invocationId, byte[] rawInvocationId, string key, ulong randomSeed,
        int knownEntries,
        Dictionary<string, ReadOnlyMemory<byte>>? initialState = null)
    {
        if (State != InvocationState.WaitingStart)
            ThrowInvalidState(State, "initialize");

        InvocationId = invocationId;
        RawInvocationId = rawInvocationId;
        Key = key;
        RandomSeed = randomSeed;
        _initialState = initialState;
        _journal.Initialize(knownEntries);
        State = knownEntries > 0 ? InvocationState.Replaying : InvocationState.Processing;

        if (State == InvocationState.Replaying)
            Log.ReplayStarted(Logger, InvocationId, knownEntries);
    }

    private void EnsureActive()
    {
        if (State is InvocationState.WaitingStart or InvocationState.Closed)
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