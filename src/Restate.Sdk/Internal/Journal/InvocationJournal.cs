using System.Buffers;

namespace Restate.Sdk.Internal.Journal;

internal sealed class InvocationJournal : IDisposable
{
    private const int DefaultCapacity = 4;

    private JournalEntry[] _entries;
    private List<byte[]>? _pooledBuffers;

    // Number of physically populated slots in _entries. During replay this can exceed Count:
    // StageReplay fills slots ahead of the handler's progress, and TakeReplayEntry consumes them.
    private int _staged;

    public InvocationJournal()
    {
        _entries = ArrayPool<JournalEntry>.Shared.Rent(DefaultCapacity);
    }

    /// <summary>
    ///     Total journal entries announced by <c>StartMessage.known_entries</c>. This counts
    ///     commands <b>and</b> notifications and therefore only serves as a capacity hint and
    ///     a provisional replay boundary until <see cref="SetReplayBoundary" /> is called.
    /// </summary>
    public int KnownEntries { get; private set; }

    /// <summary>Number of command entries processed so far (appended live or consumed from replay).</summary>
    public int Count { get; private set; }

    /// <summary>
    ///     Number of replayed <b>commands</b> the handler must re-traverse before executing live.
    ///     Set provisionally to <see cref="KnownEntries" /> by <see cref="Initialize" /> and refined
    ///     via <see cref="SetReplayBoundary" /> once the replayed prefix has been drained and
    ///     commands separated from notifications.
    /// </summary>
    public int ReplayBoundary { get; private set; }

    public bool IsReplaying => Count < ReplayBoundary;

    public JournalEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _entries[index];
        }
    }

    /// <summary>
    ///     Tracks a pooled buffer (detached from RawMessage) for batch return on Dispose.
    /// </summary>
    public void TrackPooledBuffer(byte[] buffer)
    {
        (_pooledBuffers ??= new List<byte[]>(8)).Add(buffer);
    }

    public void Dispose()
    {
        if (_pooledBuffers is not null)
        {
            foreach (var buf in _pooledBuffers)
                ArrayPool<byte>.Shared.Return(buf);
            _pooledBuffers = null;
        }

        if (_entries.Length > 0)
        {
            ArrayPool<JournalEntry>.Shared.Return(_entries, true);
            _entries = [];
            Count = 0;
            _staged = 0;
            ReplayBoundary = 0;
        }
    }

    public void Initialize(int knownEntries)
    {
        KnownEntries = knownEntries;
        ReplayBoundary = knownEntries;
        if (knownEntries > _entries.Length)
            Grow(knownEntries);
    }

    /// <summary>
    ///     Fixes the replay boundary to the number of replayed commands. Called after the start-up
    ///     drain, which separates commands (staged for replay) from notifications (routed to the
    ///     completion manager). <c>known_entries</c> alone over-counts the boundary whenever the
    ///     replayed journal contains notifications — exactly the shape of a resumed journal.
    /// </summary>
    public void SetReplayBoundary(int commandCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(commandCount);
        ReplayBoundary = commandCount;
    }

    /// <summary>
    ///     Stages a replayed command entry ahead of the handler's progress without advancing
    ///     <see cref="Count" />. Staged entries are consumed in order by <see cref="TakeReplayEntry" />.
    /// </summary>
    public void StageReplay(JournalEntry entry)
    {
        if (_staged == _entries.Length)
            Grow(_entries.Length * 2);

        _entries[_staged] = entry;
        _staged++;
    }

    /// <summary>
    ///     Consumes the next replayed command entry and advances <see cref="Count" />.
    ///     Returns a default entry when nothing was staged at the current position
    ///     (only possible when the journal was initialized without a start-up drain, e.g. in tests).
    /// </summary>
    public JournalEntry TakeReplayEntry()
    {
        if (!IsReplaying)
            throw new InvalidOperationException("No replay entries left to take");

        var entry = Count < _staged ? _entries[Count] : default;
        Count++;
        if (_staged < Count)
            _staged = Count;
        return entry;
    }

    public int Append(JournalEntry entry)
    {
        if (Count == _entries.Length)
            Grow(_entries.Length * 2);

        var index = Count;
        _entries[index] = entry;
        Count++;
        if (_staged < Count)
            _staged = Count;
        return index;
    }

    private void Grow(int minCapacity)
    {
        var newArray = ArrayPool<JournalEntry>.Shared.Rent(minCapacity);
        _entries.AsSpan(0, _staged).CopyTo(newArray);
        ArrayPool<JournalEntry>.Shared.Return(_entries, true);
        _entries = newArray;
    }
}
