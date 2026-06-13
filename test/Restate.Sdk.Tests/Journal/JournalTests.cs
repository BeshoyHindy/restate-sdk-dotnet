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

    // ---- Triple overload: full branch matrix (InvocationJournal.cs:85-87, 11/14 → 14/14) --------

    private static ReplayCommand Call(string? service, string? handler, string? key) => new()
    {
        MessageType = MessageType.CallCommand,
        EntryType = JournalEntryType.Call,
        TargetService = service,
        TargetHandler = handler,
        TargetKey = key,
        ResultCompletionId = 2,
        InvocationIdNotificationIdx = 1
    };

    [Fact]
    public void DequeueReplay_TargetTriple_AllMatch_ReturnsCommand()
    {
        // The happy path: all three operands of the compound `||` evaluate equal, so the throw is
        // skipped and the command is returned — the FALSE arm of every comparison.
        var journal = new InvocationJournal();
        journal.EnqueueReplay(Call("Svc", "Handler", "k"));

        var cmd = journal.DequeueReplay(JournalEntryType.Call, null, "Svc", "Handler", "k");
        Assert.Equal("Svc", cmd.TargetService);
        Assert.Equal("Handler", cmd.TargetHandler);
        Assert.Equal("k", cmd.TargetKey);
    }

    [Theory]
    // Each row mismatches exactly one operand so every operand of the `||` is independently the TRUE
    // (mismatch) arm at least once — the handler and key operands the single existing test never hits.
    [InlineData("Other", "Handler", "k")]   // service operand mismatches (handler/key short-circuit past)
    [InlineData("Svc", "Other", "k")]       // handler operand mismatches
    [InlineData("Svc", "Handler", "other")] // key operand mismatches
    public void DequeueReplay_TargetTriple_SingleOperandMismatch_Throws(
        string expService, string expHandler, string expKey)
    {
        var journal = new InvocationJournal();
        journal.EnqueueReplay(Call("Svc", "Handler", "k"));

        var ex = Assert.Throws<ProtocolException>(() =>
            journal.DequeueReplay(JournalEntryType.Call, null, expService, expHandler, expKey));
        Assert.Contains("target", ex.Message);
    }

    [Fact]
    public void DequeueReplay_TargetTriple_NullJournaledTargets_NormalizeToEmpty_AndMatchEmptyExpected()
    {
        // The `command.TargetService ?? ""` / `TargetHandler ?? ""` / `TargetKey ?? ""` null-coalescing
        // arms: a journaled command with NULL targets (a keyless OneWayCall to a plain service) must
        // normalize to "" and MATCH empty-string expectations — the null branch of each `??`.
        var journal = new InvocationJournal();
        journal.EnqueueReplay(Call(null, null, null));

        var cmd = journal.DequeueReplay(JournalEntryType.Call, null, "", "", null);
        Assert.Null(cmd.TargetService);
    }

    [Fact]
    public void DequeueReplay_TargetTriple_NullExpectedKey_NormalizesToEmpty()
    {
        // The `expectedKey ?? ""` null arm on the EXPECTED side: a keyless live call (null expectedKey)
        // against a journaled empty-string key matches.
        var journal = new InvocationJournal();
        journal.EnqueueReplay(Call("Svc", "Handler", ""));

        var cmd = journal.DequeueReplay(JournalEntryType.Call, null, "Svc", "Handler", null);
        Assert.Equal("", cmd.TargetKey);
    }
}
