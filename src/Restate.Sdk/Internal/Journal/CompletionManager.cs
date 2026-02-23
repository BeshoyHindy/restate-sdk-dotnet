using System.Collections.Concurrent;

namespace Restate.Sdk.Internal.Journal;

/// <summary>
///     Discriminated union that avoids boxing CompletionResult (a struct) when stored
///     in ConcurrentDictionary. Holds either a TCS, a CompletionResult, or a TerminalException.
/// </summary>
internal readonly struct CompletionSlot
{
    private readonly object? _ref;
    private readonly CompletionResult _result;
    public SlotKind Kind { get; }

    public enum SlotKind : byte { Tcs, Result, Failure }

    public CompletionSlot(TaskCompletionSource<CompletionResult> tcs)
    {
        _ref = tcs;
        _result = default;
        Kind = SlotKind.Tcs;
    }

    public CompletionSlot(CompletionResult result)
    {
        _ref = null;
        _result = result;
        Kind = SlotKind.Result;
    }

    public CompletionSlot(TerminalException ex)
    {
        _ref = ex;
        _result = default;
        Kind = SlotKind.Failure;
    }

    public TaskCompletionSource<CompletionResult> Tcs => (TaskCompletionSource<CompletionResult>)_ref!;
    public CompletionResult Result => _result;
    public TerminalException Exception => (TerminalException)_ref!;
}

// ConcurrentDictionary is required here: the handler thread calls GetOrRegister while
// ProcessIncomingMessagesAsync (running on a separate Task) calls TryComplete/TryFail.
// Early completions are stored so notifications arriving before registration are not lost.
internal sealed class CompletionManager
{
    private readonly ConcurrentDictionary<int, CompletionSlot> _slots = new();

    public TaskCompletionSource<CompletionResult> Register(int entryIndex)
    {
        var tcs = new TaskCompletionSource<CompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_slots.TryAdd(entryIndex, new CompletionSlot(tcs)))
            return tcs;

        // Slot already occupied — either an early completion or a duplicate registration.
        if (_slots.TryRemove(entryIndex, out var slot))
        {
            if (slot.Kind == CompletionSlot.SlotKind.Tcs)
                throw new InvalidOperationException($"Entry {entryIndex} already registered");

            // Early completion arrived before registration — resolve the TCS immediately.
            if (slot.Kind == CompletionSlot.SlotKind.Result)
                tcs.SetResult(slot.Result);
            else if (slot.Kind == CompletionSlot.SlotKind.Failure)
                tcs.SetException(slot.Exception);

            _slots.TryAdd(entryIndex, new CompletionSlot(tcs));
        }

        return tcs;
    }

    public TaskCompletionSource<CompletionResult> GetOrRegister(int entryIndex)
    {
        var slot = _slots.GetOrAdd(entryIndex,
            static _ => new CompletionSlot(
                new TaskCompletionSource<CompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously)));

        if (slot.Kind == CompletionSlot.SlotKind.Tcs)
            return slot.Tcs;

        // An early completion arrived before we registered — create a pre-resolved TCS.
        var earlyTcs = new TaskCompletionSource<CompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (slot.Kind == CompletionSlot.SlotKind.Result)
            earlyTcs.SetResult(slot.Result);
        else if (slot.Kind == CompletionSlot.SlotKind.Failure)
            earlyTcs.SetException(slot.Exception);

        // Replace the stored value with the TCS (not strictly required, but keeps the dictionary clean).
        _slots.TryUpdate(entryIndex, new CompletionSlot(earlyTcs), slot);
        return earlyTcs;
    }

    public bool TryComplete(int entryIndex, CompletionResult result)
    {
        if (_slots.TryRemove(entryIndex, out var slot))
        {
            if (slot.Kind == CompletionSlot.SlotKind.Tcs)
                return slot.Tcs.TrySetResult(result);
        }

        // No handler registered yet — store the result for later delivery.
        _slots.TryAdd(entryIndex, new CompletionSlot(result));
        return true;
    }

    public bool TryFail(int entryIndex, ushort code, string message)
    {
        if (_slots.TryRemove(entryIndex, out var slot))
        {
            if (slot.Kind == CompletionSlot.SlotKind.Tcs)
                return slot.Tcs.TrySetException(new TerminalException(message, code));
        }

        // No handler registered yet — store the failure for later delivery.
        _slots.TryAdd(entryIndex, new CompletionSlot(new TerminalException(message, code)));
        return true;
    }

    public void CancelAll()
    {
        foreach (var pair in _slots)
        {
            if (pair.Value.Kind == CompletionSlot.SlotKind.Tcs)
                pair.Value.Tcs.TrySetCanceled();
        }

        _slots.Clear();
    }
}
