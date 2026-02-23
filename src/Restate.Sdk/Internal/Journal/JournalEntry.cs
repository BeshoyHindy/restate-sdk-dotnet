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

    private JournalEntry(JournalEntryType type, string? name, ReadOnlyMemory<byte> result, bool completed)
    {
        Type = type;
        Name = name;
        Result = result;
        IsCompleted = completed;
    }

    public static JournalEntry Completed(JournalEntryType type, ReadOnlyMemory<byte> result, string? name = null)
    {
        return new JournalEntry(type, name, result, true);
    }

    public static JournalEntry Pending(JournalEntryType type, string? name = null)
    {
        return new JournalEntry(type, name, ReadOnlyMemory<byte>.Empty, false);
    }

    public JournalEntry WithCompletion(ReadOnlyMemory<byte> result)
    {
        return new JournalEntry(Type, Name, result, true);
    }
}