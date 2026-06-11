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
    bool PartialState);                                      // StartMessage field 5 — no longer discarded

/// <summary>
///     Parsed completion notification fields from the Restate protocol.
/// </summary>
internal readonly record struct CompletionNotification(
    uint CompletionId,
    ReadOnlyMemory<byte>? Value,
    ushort? FailureCode,
    string? FailureMessage,
    bool IsVoid,
    string? InvocationId = null)
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
    bool IsVoid)
{
    public bool IsSuccess => Value is not null || IsVoid;
    public bool IsFailure => FailureCode is not null;
}
