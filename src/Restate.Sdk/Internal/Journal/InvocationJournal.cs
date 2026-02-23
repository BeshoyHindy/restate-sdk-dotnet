using System.Buffers;

namespace Restate.Sdk.Internal.Journal;

internal sealed class InvocationJournal : IDisposable
{
    private const int DefaultCapacity = 4;

    private JournalEntry[] _entries;
    private List<byte[]>? _pooledBuffers;

    public InvocationJournal()
    {
        _entries = ArrayPool<JournalEntry>.Shared.Rent(DefaultCapacity);
    }

    public int KnownEntries { get; private set; }
    public int Count { get; private set; }

    public bool IsReplaying => Count < KnownEntries;

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
        }
    }

    public void Initialize(int knownEntries)
    {
        KnownEntries = knownEntries;
        if (knownEntries > _entries.Length)
            Grow(knownEntries);
    }

    public int Append(JournalEntry entry)
    {
        if (Count == _entries.Length)
            Grow(_entries.Length * 2);

        var index = Count;
        _entries[index] = entry;
        Count++;
        return index;
    }

    private void Grow(int minCapacity)
    {
        var newArray = ArrayPool<JournalEntry>.Shared.Rent(minCapacity);
        _entries.AsSpan(0, Count).CopyTo(newArray);
        ArrayPool<JournalEntry>.Shared.Return(_entries, true);
        _entries = newArray;
    }
}