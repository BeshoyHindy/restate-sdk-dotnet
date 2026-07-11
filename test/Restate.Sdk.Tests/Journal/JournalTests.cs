using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Tests.Journal;

public class InvocationJournalTests
{
    [Fact]
    public void Append_IncreasesCount()
    {
        using var journal = new InvocationJournal();
        Assert.Equal(0, journal.Count);

        journal.Append(JournalEntry.Completed(JournalEntryType.Input, new byte[] { 1, 2, 3 }));
        Assert.Equal(1, journal.Count);

        journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        Assert.Equal(2, journal.Count);
    }

    [Fact]
    public void Append_ReturnsEntryIndex()
    {
        using var journal = new InvocationJournal();
        Assert.Equal(0, journal.Append(JournalEntry.Completed(JournalEntryType.Input, Array.Empty<byte>())));
        Assert.Equal(1, journal.Append(JournalEntry.Pending(JournalEntryType.Call)));
        Assert.Equal(2, journal.Append(JournalEntry.Pending(JournalEntryType.Sleep)));
    }

    [Fact]
    public void Indexer_ReturnsCorrectEntry()
    {
        using var journal = new InvocationJournal();
        var data = new byte[] { 10, 20, 30 };
        journal.Append(JournalEntry.Completed(JournalEntryType.Run, data, "side-effect"));

        var entry = journal[0];
        Assert.Equal(JournalEntryType.Run, entry.Type);
        Assert.Equal("side-effect", entry.Name);
        Assert.True(entry.IsCompleted);
        Assert.Equal(data, entry.Result.ToArray());
    }

    [Fact]
    public void Indexer_ThrowsOnOutOfRange()
    {
        using var journal = new InvocationJournal();
        Assert.Throws<ArgumentOutOfRangeException>(() => journal[0]);
    }

    [Fact]
    public void Initialize_SetsKnownEntries()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(5);

        Assert.Equal(5, journal.KnownEntries);
        Assert.True(journal.IsReplaying);
    }

    [Fact]
    public void IsReplaying_FalseWhenCountReachesKnownEntries()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(2);

        Assert.True(journal.IsReplaying);

        journal.Append(JournalEntry.Completed(JournalEntryType.Input, Array.Empty<byte>()));
        Assert.True(journal.IsReplaying);

        journal.Append(JournalEntry.Completed(JournalEntryType.Run, Array.Empty<byte>()));
        Assert.False(journal.IsReplaying);
    }

    [Fact]
    public void IsReplaying_FalseWhenKnownEntriesIsZero()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(0);
        Assert.False(journal.IsReplaying);
    }

    [Fact]
    public void Grows_BeyondInitialCapacity()
    {
        using var journal = new InvocationJournal();

        for (var i = 0; i < 64; i++)
            journal.Append(JournalEntry.Completed(JournalEntryType.Run, new[] { (byte)i }));

        Assert.Equal(64, journal.Count);
        Assert.Equal(63, journal[63].Result.Span[0]);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var journal = new InvocationJournal();
        journal.Append(JournalEntry.Completed(JournalEntryType.Input, Array.Empty<byte>()));
        journal.Dispose();
        journal.Dispose();
    }

    // ------- Replay boundary -------

    [Fact]
    public void Initialize_SetsProvisionalReplayBoundary()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(5);

        Assert.Equal(5, journal.ReplayBoundary);
        Assert.True(journal.IsReplaying);
    }

    [Fact]
    public void SetReplayBoundary_SeparatesCommandsFromKnownEntries()
    {
        using var journal = new InvocationJournal();

        // Wire journal: Input + Sleep commands plus 2 notifications = 4 known entries.
        journal.Initialize(4);
        journal.SetReplayBoundary(2);

        Assert.Equal(4, journal.KnownEntries);
        Assert.Equal(2, journal.ReplayBoundary);

        journal.Append(JournalEntry.Completed(JournalEntryType.Input, ReadOnlyMemory<byte>.Empty));
        Assert.True(journal.IsReplaying);

        // Notifications must not keep the journal replaying once every command is re-traversed.
        journal.Append(JournalEntry.Completed(JournalEntryType.Sleep, ReadOnlyMemory<byte>.Empty));
        Assert.False(journal.IsReplaying);
    }

    [Fact]
    public void SetReplayBoundary_Zero_DisablesReplay()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(3);
        journal.SetReplayBoundary(0);

        Assert.False(journal.IsReplaying);
    }

    [Fact]
    public void SetReplayBoundary_Negative_Throws()
    {
        using var journal = new InvocationJournal();
        Assert.Throws<ArgumentOutOfRangeException>(() => journal.SetReplayBoundary(-1));
    }

    [Fact]
    public void StageReplay_TakeReplayEntry_ConsumesInOrder()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(3);

        journal.Append(JournalEntry.Completed(JournalEntryType.Input, ReadOnlyMemory<byte>.Empty));
        journal.StageReplay(JournalEntry.Replayed(
            JournalEntryType.Sleep, MessageType.SleepCommand, new byte[] { 1 }));
        journal.StageReplay(JournalEntry.Replayed(
            JournalEntryType.Run, MessageType.RunCommand, new byte[] { 2 }));
        journal.SetReplayBoundary(3);

        var first = journal.TakeReplayEntry();
        Assert.Equal(JournalEntryType.Sleep, first.Type);
        Assert.Equal(MessageType.SleepCommand, first.CommandType);
        Assert.Equal(1, first.Result.Span[0]);
        Assert.Equal(2, journal.Count);
        Assert.True(journal.IsReplaying);

        var second = journal.TakeReplayEntry();
        Assert.Equal(JournalEntryType.Run, second.Type);
        Assert.Equal(MessageType.RunCommand, second.CommandType);
        Assert.Equal(3, journal.Count);
        Assert.False(journal.IsReplaying);
    }

    [Fact]
    public void TakeReplayEntry_WhenNotReplaying_Throws()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(0);

        Assert.Throws<InvalidOperationException>(() => journal.TakeReplayEntry());
    }

    [Fact]
    public void TakeReplayEntry_WithoutStagedEntry_ReturnsDefaultAndAdvances()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(1);

        // Journal initialized without a start-up drain (no staged entries) — legacy/test path.
        var entry = journal.TakeReplayEntry();

        Assert.False(entry.IsCompleted);
        Assert.True(entry.Result.IsEmpty);
        Assert.Equal(1, journal.Count);
        Assert.False(journal.IsReplaying);
    }

    [Fact]
    public void StageReplay_GrowsBeyondInitialCapacity()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(2);

        journal.Append(JournalEntry.Completed(JournalEntryType.Input, ReadOnlyMemory<byte>.Empty));
        for (var i = 0; i < 64; i++)
            journal.StageReplay(JournalEntry.Replayed(
                JournalEntryType.Run, MessageType.RunCommand, new[] { (byte)i }));
        journal.SetReplayBoundary(65);

        for (var i = 0; i < 64; i++)
        {
            var entry = journal.TakeReplayEntry();
            Assert.Equal((byte)i, entry.Result.Span[0]);
        }

        Assert.False(journal.IsReplaying);
        Assert.Equal(65, journal.Count);
    }

    [Fact]
    public void Append_AfterReplayConsumed_ContinuesFromBoundary()
    {
        using var journal = new InvocationJournal();
        journal.Initialize(2);

        journal.Append(JournalEntry.Completed(JournalEntryType.Input, ReadOnlyMemory<byte>.Empty));
        journal.StageReplay(JournalEntry.Replayed(
            JournalEntryType.Sleep, MessageType.SleepCommand, ReadOnlyMemory<byte>.Empty));
        journal.SetReplayBoundary(2);

        journal.TakeReplayEntry();
        Assert.False(journal.IsReplaying);

        var index = journal.Append(JournalEntry.Pending(JournalEntryType.Call));
        Assert.Equal(2, index);
        Assert.Equal(3, journal.Count);
    }
}