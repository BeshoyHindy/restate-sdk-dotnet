using System.Globalization;

namespace Restate.Sdk.Internal.Protocol;

/// <summary>
///     The identifying fields of a journal command — what the operation targets, not what it
///     carries. Replay fills one of these from the command the previous execution journaled and
///     one from the operation the handler is performing now, and compares them: a difference
///     means the handler would be handed another operation's recorded result.
///     Fields a command does not have stay <see langword="null" /> on both sides, and an unset
///     protobuf string reads back as empty, so <see langword="null" /> and <c>""</c> compare equal.
///     Deliberately absent: request and state payloads, and anything the SDK derives from the
///     clock (a sleep's wake-up time, a delayed send's invoke time) — a correct replay at a
///     different wall-clock time would otherwise fail.
/// </summary>
internal readonly record struct CommandFields
{
    /// <summary>A command with no identifying fields of its own (ClearAllState, GetStateKeys).</summary>
    public static CommandFields None => default;

    /// <summary>Entry name, as carried by Run and Sleep commands.</summary>
    public string? Name { get; init; }

    /// <summary>Target service of a call or a send.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Target handler of a call or a send.</summary>
    public string? HandlerName { get; init; }

    /// <summary>Key of a state entry, a promise, or a call to a keyed service.</summary>
    public string? Key { get; init; }

    /// <summary>Flow-control scope of a call or a send (V7).</summary>
    public string? Scope { get; init; }

    /// <summary>Flow-control limit key of a call or a send (V7).</summary>
    public string? LimitKey { get; init; }

    /// <summary>
    ///     The invocation or awakeable a command addresses: the attach / get-output target, the
    ///     awakeable id, or the invocation a signal is sent to.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>Name of the signal a SendSignal command resolves, when addressed by name.</summary>
    public string? SignalName { get; init; }

    /// <summary>Index of the signal a SendSignal command resolves, when addressed by index.</summary>
    public uint? SignalIndex { get; init; }

    /// <summary>
    ///     Returns the first field on which the journaled command and the operation the handler is
    ///     performing disagree, or <see langword="null" /> when they agree.
    ///     <paramref name="commandType" /> only selects the reported name of
    ///     <see cref="Target" />, which is a different protobuf field per command.
    /// </summary>
    public static CommandFieldDifference? FindDifference(
        MessageType commandType, in CommandFields recorded, in CommandFields attempted)
    {
        if (!SameText(recorded.Name, attempted.Name))
            return new CommandFieldDifference("name", recorded.Name, attempted.Name);

        if (!SameText(recorded.ServiceName, attempted.ServiceName))
            return new CommandFieldDifference("service_name", recorded.ServiceName, attempted.ServiceName);

        if (!SameText(recorded.HandlerName, attempted.HandlerName))
            return new CommandFieldDifference("handler_name", recorded.HandlerName, attempted.HandlerName);

        if (!SameText(recorded.Key, attempted.Key))
            return new CommandFieldDifference("key", recorded.Key, attempted.Key);

        if (!SameText(recorded.Scope, attempted.Scope))
            return new CommandFieldDifference("scope", recorded.Scope, attempted.Scope);

        if (!SameText(recorded.LimitKey, attempted.LimitKey))
            return new CommandFieldDifference("limit_key", recorded.LimitKey, attempted.LimitKey);

        if (!SameText(recorded.Target, attempted.Target))
            return new CommandFieldDifference(TargetFieldName(commandType), recorded.Target, attempted.Target);

        if (!SameText(recorded.SignalName, attempted.SignalName) || recorded.SignalIndex != attempted.SignalIndex)
            return new CommandFieldDifference("signal_id", DisplaySignalId(recorded), DisplaySignalId(attempted));

        return null;
    }

    /// <summary>
    ///     The protobuf field <see cref="Target" /> was read from, so the mismatch message names
    ///     the field an operator will find in the command.
    /// </summary>
    private static string TargetFieldName(MessageType commandType)
    {
        return commandType switch
        {
            MessageType.CompleteAwakeableCommand => "awakeable_id",
            MessageType.SendSignalCommand => "target_invocation_id",
            _ => "target"
        };
    }

    private static string? DisplaySignalId(in CommandFields fields)
    {
        return fields.SignalName ?? fields.SignalIndex?.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>An unset protobuf string parses back as empty, so empty and null are the same value.</summary>
    private static bool SameText(string? left, string? right)
    {
        return string.IsNullOrEmpty(left) ? string.IsNullOrEmpty(right) : string.Equals(left, right, StringComparison.Ordinal);
    }
}

/// <summary>
///     One field on which a replayed command and the operation the handler is performing
///     disagree. <paramref name="Recorded" /> is the journal's value, <paramref name="Attempted" />
///     the handler's; they are never swapped.
/// </summary>
internal readonly record struct CommandFieldDifference(string Field, string? Recorded, string? Attempted);
