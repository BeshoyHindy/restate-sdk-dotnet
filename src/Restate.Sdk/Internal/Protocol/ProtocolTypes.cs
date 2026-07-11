namespace Restate.Sdk.Internal.Protocol;

/// <summary>
///     Parsed StartMessage fields. <paramref name="EagerState" /> holds the state map sent by the
///     runtime (null when empty); <paramref name="IsPartialState" /> tells whether that map is
///     complete — when false, a key absent from the map is definitively unset. The V7 scope
///     fields (<paramref name="Scope" />, <paramref name="LimitKey" />,
///     <paramref name="IdempotencyKey" />) are null when the runtime did not send them.
/// </summary>
internal readonly record struct StartMessageFields(
    byte[] RawId,
    string InvocationId,
    string? Key,
    uint KnownEntries,
    ulong RandomSeed,
    Dictionary<string, ReadOnlyMemory<byte>>? EagerState,
    bool IsPartialState,
    string? Scope,
    string? LimitKey,
    string? IdempotencyKey);

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
