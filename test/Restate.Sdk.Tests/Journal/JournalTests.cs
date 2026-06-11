using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Tests.Journal;

public class InvocationJournalTests
{
    private static ReplayCommand Run(string? name = null, uint id = 1) => new()
    {
        MessageType = MessageType.RunCommand,
        EntryType = JournalEntryType.Run,
        Name = name,
        ResultCompletionId = id
    };

    [Fact]
    public void RecordCommand_IncreasesCount()
    {
        var journal = new InvocationJournal();
        Assert.Equal(0, journal.Count);

        journal.RecordCommand(JournalEntryType.Input);
        Assert.Equal(1, journal.Count);

        journal.RecordCommand(JournalEntryType.Run, "side-effect");
        Assert.Equal(2, journal.Count);
        Assert.Equal(JournalEntryType.Run, journal.LastCommandType);
        Assert.Equal("side-effect", journal.LastCommandName);
        Assert.Equal(1, journal.CommandIndex);
    }

    [Fact]
    public void Initialize_SetsKnownEntries_WithoutImplyingReplay()
    {
        var journal = new InvocationJournal();
        journal.Initialize(5);

        Assert.Equal(5, journal.KnownEntries);
        // Replay status is driven by the buffered command queue, not by KnownEntries.
        Assert.False(journal.IsReplaying);
    }

    [Fact]
    public void IsReplaying_TrueWhileBufferedCommandsRemain()
    {
        var journal = new InvocationJournal();
        Assert.False(journal.IsReplaying);

        journal.EnqueueReplay(Run("a", 1));
        journal.EnqueueReplay(Run("b", 2));
        Assert.True(journal.IsReplaying);

        journal.DequeueReplay(JournalEntryType.Run, "a");
        Assert.True(journal.IsReplaying);

        journal.DequeueReplay(JournalEntryType.Run, "b");
        Assert.False(journal.IsReplaying);
    }

    [Fact]
    public void DequeueReplay_AdvancesCountAndMetadata()
    {
        var journal = new InvocationJournal();
        journal.RecordCommand(JournalEntryType.Input);   // entry 0
        journal.EnqueueReplay(Run("x", 1));

        var cmd = journal.DequeueReplay(JournalEntryType.Run, "x");
        Assert.Equal(JournalEntryType.Run, cmd.EntryType);
        Assert.Equal(1u, cmd.ResultCompletionId);
        Assert.Equal(2, journal.Count);
        Assert.Equal(JournalEntryType.Run, journal.LastCommandType);
        Assert.Equal("x", journal.LastCommandName);
    }

    [Fact]
    public void DequeueReplay_EmptyQueue_ThrowsUnavailableEntry()
    {
        var journal = new InvocationJournal();
        var ex = Assert.Throws<ProtocolException>(() =>
            journal.DequeueReplay(JournalEntryType.Run, "x"));
        Assert.Contains("Unavailable entry", ex.Message);
    }

    [Fact]
    public void DequeueReplay_TypeMismatch_Throws()
    {
        var journal = new InvocationJournal();
        journal.EnqueueReplay(Run("x", 1));
        var ex = Assert.Throws<ProtocolException>(() =>
            journal.DequeueReplay(JournalEntryType.Sleep, "x"));
        Assert.Contains("type mismatch", ex.Message);
    }

    [Fact]
    public void DequeueReplay_NameMismatch_Throws()
    {
        var journal = new InvocationJournal();
        journal.EnqueueReplay(Run("a", 1));
        var ex = Assert.Throws<ProtocolException>(() =>
            journal.DequeueReplay(JournalEntryType.Run, "b"));
        Assert.Contains("Command mismatch", ex.Message);
    }

    [Fact]
    public void DequeueReplay_NullNameNormalizesToEmpty()
    {
        var journal = new InvocationJournal();
        journal.EnqueueReplay(new ReplayCommand
        {
            MessageType = MessageType.ClearAllStateCommand,
            EntryType = JournalEntryType.ClearAllState
        });
        // Null replayed name vs null expected name → match (both normalize to "").
        var cmd = journal.DequeueReplay(JournalEntryType.ClearAllState);
        Assert.Equal(JournalEntryType.ClearAllState, cmd.EntryType);
    }

    [Fact]
    public void DequeueReplay_TargetTripleValidation()
    {
        var journal = new InvocationJournal();
        journal.EnqueueReplay(new ReplayCommand
        {
            MessageType = MessageType.CallCommand,
            EntryType = JournalEntryType.Call,
            TargetService = "Svc",
            TargetHandler = "Handler",
            TargetKey = "k",
            ResultCompletionId = 2,
            InvocationIdNotificationIdx = 1
        });

        var ex = Assert.Throws<ProtocolException>(() =>
            journal.DequeueReplay(JournalEntryType.Call, null, "Other", "Handler", "k"));
        Assert.Contains("target", ex.Message);
    }
}
