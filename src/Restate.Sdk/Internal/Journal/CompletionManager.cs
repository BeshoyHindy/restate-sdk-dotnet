using System.Collections.Concurrent;
using Restate.Sdk.Internal.StateMachine;

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

    // Set once when the input stream closes. After that point no completion can ever
    // arrive, so every pending wait — and every wait registered afterwards — is
    // unresolvable and fails with SuspensionException.
    private volatile bool _poisoned;

    // Set once when the runtime delivers the built-in CANCEL signal. Every pending wait —
    // and every wait registered afterwards — fails with this terminal exception so the
    // handler unwinds deterministically. Takes precedence over poisoning: a cancelled
    // invocation must fail terminally, not suspend into a wake-up loop.
    private volatile TerminalException? _terminalFault;

    public TaskCompletionSource<CompletionResult> Register(int entryIndex)
    {
        var tcs = new TaskCompletionSource<CompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_slots.TryAdd(entryIndex, new CompletionSlot(tcs)))
        {
            // Double-check after insertion: closes the race where Poison()/FailAllWith()
            // enumerates the dictionary concurrently with this registration, and makes waits
            // registered after the input closed (or cancellation) fail immediately.
            if (_terminalFault is { } fault)
                _ = tcs.TrySetException(fault);
            else if (_poisoned)
                FailWithSuspension(tcs);
            return tcs;
        }

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

        if (_terminalFault is { } lateFault)
            _ = tcs.TrySetException(lateFault);
        else if (_poisoned)
            FailWithSuspension(tcs);
        return tcs;
    }

    public TaskCompletionSource<CompletionResult> GetOrRegister(int entryIndex)
    {
        var slot = _slots.GetOrAdd(entryIndex,
            static _ => new CompletionSlot(
                new TaskCompletionSource<CompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously)));

        if (slot.Kind == CompletionSlot.SlotKind.Tcs)
        {
            // Double-check after insertion (see Register). TrySetException is a no-op on
            // slots that already resolved, so early results are never clobbered.
            if (_terminalFault is { } fault)
                _ = slot.Tcs.TrySetException(fault);
            else if (_poisoned)
                FailWithSuspension(slot.Tcs);
            return slot.Tcs;
        }

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

    /// <summary>
    ///     Marks the manager as poisoned (input stream closed) and fails every pending wait
    ///     with <see cref="SuspensionException" />. Idempotent. Slots holding early results
    ///     or failures are untouched — those completions were already delivered.
    /// </summary>
    public void Poison()
    {
        _poisoned = true;

        foreach (var pair in _slots)
        {
            if (pair.Value.Kind == CompletionSlot.SlotKind.Tcs)
                FailWithSuspension(pair.Value.Tcs);
        }
    }

    /// <summary>
    ///     Fails every pending wait — and every wait registered afterwards — with the given
    ///     terminal exception. Used when the runtime delivers the built-in CANCEL signal.
    ///     Slots already holding results are untouched, so replayed completions still resolve.
    /// </summary>
    public void FailAllWith(TerminalException exception)
    {
        _terminalFault = exception;

        foreach (var pair in _slots)
        {
            if (pair.Value.Kind == CompletionSlot.SlotKind.Tcs)
                _ = pair.Value.Tcs.TrySetException(exception);
        }
    }

    /// <summary>
    ///     Collects the ids of every unresolved wait: slots whose task is still incomplete or
    ///     faulted with <see cref="SuspensionException" />. The keys are the protocol completion
    ///     ids / signal indices the runtime must observe to resume the invocation. Sorted for
    ///     deterministic wire output.
    /// </summary>
    public List<int> CollectPendingIds()
    {
        var ids = new List<int>(_slots.Count);
        foreach (var pair in _slots)
        {
            if (pair.Value.Kind != CompletionSlot.SlotKind.Tcs)
                continue;

            var task = pair.Value.Tcs.Task;
            if (!task.IsCompleted || IsSuspensionFault(task))
                ids.Add(pair.Key);
        }

        ids.Sort();
        return ids;
    }

    private static void FailWithSuspension(TaskCompletionSource<CompletionResult> tcs)
    {
        if (tcs.TrySetException(new SuspensionException()))
        {
            // Touch Exception to mark it observed: some poisoned slots are never awaited
            // (e.g. a call's invocation-id notification slot), and an unobserved faulted
            // task would otherwise raise TaskScheduler.UnobservedTaskException on finalization.
            _ = tcs.Task.Exception;
        }
    }

    private static bool IsSuspensionFault(Task task)
    {
        if (!task.IsFaulted)
            return false;

        foreach (var inner in task.Exception!.InnerExceptions)
        {
            if (inner is SuspensionException)
                return true;
        }

        return false;
    }
}
