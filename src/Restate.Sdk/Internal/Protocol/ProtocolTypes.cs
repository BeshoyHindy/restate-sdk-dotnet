namespace Restate.Sdk.Internal.Protocol;

/// <summary>
///     Parsed StartMessage fields.
/// </summary>
internal readonly record struct StartMessageFields(
    byte[] RawId,
    string InvocationId,
    string? Key,
    uint KnownEntries,
    ulong RandomSeed,
    Dictionary<string, ReadOnlyMemory<byte>?> EagerState,   // ALWAYS materialized (may be empty)
    bool PartialState,                                       // StartMessage field 5 — no longer discarded
                                                             // G2/G3 — durable retry accounting seeds (StartMessage fields 7/8). These carry the runtime's
                                                             // cumulative retry count + elapsed duration since the last STORED journal entry, so the first run
                                                             // we try to commit after replay resumes the retry loop where the previous re-drive left off
                                                             // (vm/context.rs infer_entry_retry_info, StartInfo.retry_count/duration_since_last_stored_entry).
    uint RetryCountSinceLastStoredEntry = 0,
    ulong DurationSinceLastStoredEntryMillis = 0);

/// <summary>
///     Parsed completion notification fields from the Restate protocol.
/// </summary>
internal readonly record struct CompletionNotification(
    uint CompletionId,
    ReadOnlyMemory<byte>? Value,
    ushort? FailureCode,
    string? FailureMessage,
    bool IsVoid,
    string? InvocationId = null,
    IReadOnlyDictionary<string, string>? FailureMetadata = null)
{
    public bool IsSuccess => Value is not null || IsVoid;
    public bool IsFailure => FailureCode is not null;
}

/// <summary>
///     Parsed signal notification fields from the Restate protocol.
/// </summary>
internal readonly record struct SignalNotification(
    uint? Idx,
    string? Name,
    ReadOnlyMemory<byte>? Value,
    ushort? FailureCode,
    string? FailureMessage,
    bool IsVoid,
    IReadOnlyDictionary<string, string>? FailureMetadata = null)
{
    public bool IsSuccess => Value is not null || IsVoid;
    public bool IsFailure => FailureCode is not null;
}
