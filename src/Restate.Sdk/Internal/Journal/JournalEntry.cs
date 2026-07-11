using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Internal.Journal;

internal enum JournalEntryType
{
    Input,
    Output,
    GetState,
    SetState,
    ClearState,
    ClearAllState,
    GetStateKeys,
    Sleep,
    Call,
    OneWayCall,
    Awakeable,
    CompleteAwakeable,
    Run,
    GetPromise,
    PeekPromise,
    CompletePromise,
    AttachInvocation,
    GetInvocationOutput,
    SendSignal
}

internal readonly struct JournalEntry
{
    public JournalEntryType Type { get; }
    public string? Name { get; }
    public ReadOnlyMemory<byte> Result { get; }
    public bool IsCompleted { get; }

    /// <summary>
    ///     For replayed entries, the wire command type this entry was created from
    ///     (e.g. distinguishes <see cref="MessageType.GetLazyStateCommand" /> from
    ///     <see cref="MessageType.GetEagerStateCommand" />, which map to the same
    ///     <see cref="JournalEntryType" />). For locally created entries this is
    ///     <see cref="MessageType.Start" /> (the default) and must not be read.
    /// </summary>
    public MessageType CommandType { get; }

    private JournalEntry(JournalEntryType type, string? name, ReadOnlyMemory<byte> result, bool completed,
        MessageType commandType = MessageType.Start)
    {
        Type = type;
        Name = name;
        Result = result;
        IsCompleted = completed;
        CommandType = commandType;
    }

    public static JournalEntry Completed(JournalEntryType type, ReadOnlyMemory<byte> result, string? name = null)
    {
        return new JournalEntry(type, name, result, true);
    }

    public static JournalEntry Pending(JournalEntryType type, string? name = null)
    {
        return new JournalEntry(type, name, ReadOnlyMemory<byte>.Empty, false);
    }

    /// <summary>
    ///     Creates an entry for a replayed command. <paramref name="commandPayload" /> holds the raw
    ///     protobuf command bytes (not the operation's result value — results arrive as notifications).
    /// </summary>
    public static JournalEntry Replayed(JournalEntryType type, MessageType commandType,
        ReadOnlyMemory<byte> commandPayload, string? name = null)
    {
        return new JournalEntry(type, name, commandPayload, true, commandType);
    }

    public JournalEntry WithCompletion(ReadOnlyMemory<byte> result)
    {
        return new JournalEntry(Type, Name, result, true, CommandType);
    }
}
