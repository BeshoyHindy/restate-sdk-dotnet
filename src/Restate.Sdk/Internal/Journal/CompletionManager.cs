using System.Collections.Concurrent;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Internal.Journal;

/// <summary>
///     Monotonic counter, shared by the completion managers of one invocation, that numbers the
///     results the runtime delivers. Replayed notifications are numbered as the resume drain
///     reads them, so a wait settled before the handler resumed still knows where it sat in the
///     journaled completion order — which is what lets a combinator replay the branch the
///     original attempt took. Positions start at 1; 0 means no result was ever delivered.
/// </summary>
internal sealed class ArrivalClock
{
    private long _last;

    public long Next()
    {
        return Interlocked.Increment(ref _last);
    }
}

/// <summary>
///     A durable wait, plus the position at which the runtime delivered its result.
/// </summary>
internal sealed class CompletionSource : TaskCompletionSource<CompletionResult>
{
    private long _arrivalOrder;

    public CompletionSource() : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
    }

    /// <summary>
    ///     The <see cref="ArrivalClock" /> position of the delivered result, or 0 when no result
    ///     was delivered: still pending, poisoned, cancelled, or resolved locally (a re-executed
    ///     Run proposing its own result).
    /// </summary>
    public long ArrivalOrder
    {
        get => Interlocked.Read(ref _arrivalOrder);
        set => Interlocked.Exchange(ref _arrivalOrder, value);
    }
}

/// <summary>
///     Discriminated union that avoids boxing CompletionResult (a struct) when stored
///     in ConcurrentDictionary. Holds either a TCS, a CompletionResult, or a TerminalException.
///     Early results and failures carry the arrival position they were delivered at, so it
///     survives the hand-over to the TCS the handler registers later.
/// </summary>
internal readonly struct CompletionSlot
{
    private readonly object? _ref;
    private readonly CompletionResult _result;
    public SlotKind Kind { get; }

    public enum SlotKind : byte { Tcs, Result, Failure }

    public CompletionSlot(CompletionSource tcs)
    {
        _ref = tcs;
        _result = default;
        Kind = SlotKind.Tcs;
        ArrivalOrder = 0;
    }

    public CompletionSlot(CompletionResult result, long arrivalOrder)
    {
        _ref = null;
        _result = result;
        Kind = SlotKind.Result;
        ArrivalOrder = arrivalOrder;
    }

    public CompletionSlot(TerminalException ex, long arrivalOrder)
    {
        _ref = ex;
        _result = default;
        Kind = SlotKind.Failure;
        ArrivalOrder = arrivalOrder;
    }

    public CompletionSource Tcs => (CompletionSource)_ref!;
    public CompletionResult Result => _result;
    public TerminalException Exception => (TerminalException)_ref!;

    /// <summary>The arrival position of the stored result or failure; 0 for a pending TCS.</summary>
    public long ArrivalOrder { get; }
}

// ConcurrentDictionary is required here: the handler thread calls GetOrRegister while
// ProcessIncomingMessagesAsync (running on a separate Task) calls TryComplete/TryFail.
// Early completions are stored so notifications arriving before registration are not lost.
/// <typeparam name="TKey">
///     The key the runtime addresses a wait by: a completion id, a signal index, or a signal name.
/// </typeparam>
internal sealed class CompletionManager<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CompletionSlot> _slots = new();

    // Shared with the other managers of the same invocation, so results delivered to different
    // managers (a call and an awakeable raced against each other) stay comparable.
    private readonly ArrivalClock _arrivals;

    // Set once when the input stream closes. After that point no completion can ever
    // arrive, so every pending wait — and every wait registered afterwards — is
    // unresolvable and fails with SuspensionException.
    private volatile bool _poisoned;

    // Set once when the runtime delivers the built-in CANCEL signal. Every pending wait —
    // and every wait registered afterwards — fails with this terminal exception so the
    // handler unwinds deterministically. Takes precedence over poisoning: a cancelled
    // invocation must fail terminally, not suspend into a wake-up loop.
    private volatile TerminalException? _terminalFault;

    /// <param name="arrivals">
    ///     The clock numbering delivered results, shared with the invocation's other managers.
    ///     A manager given none numbers its own arrivals, which is enough when nothing compares
    ///     its waits with another manager's.
    /// </param>
    public CompletionManager(ArrivalClock? arrivals = null)
    {
        _arrivals = arrivals ?? new ArrivalClock();
    }

    public CompletionSource Register(TKey entryIndex)
    {
        var tcs = new CompletionSource();
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

            // Early completion arrived before registration — resolve the TCS immediately,
            // carrying over the position the result was delivered at.
            tcs.ArrivalOrder = slot.ArrivalOrder;
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

    public CompletionSource GetOrRegister(TKey entryIndex)
    {
        var slot = _slots.GetOrAdd(entryIndex, static _ => new CompletionSlot(new CompletionSource()));

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

        // An early completion arrived before we registered — create a pre-resolved TCS that
        // keeps the position the result was delivered at.
        var earlyTcs = new CompletionSource { ArrivalOrder = slot.ArrivalOrder };
        if (slot.Kind == CompletionSlot.SlotKind.Result)
            earlyTcs.SetResult(slot.Result);
        else if (slot.Kind == CompletionSlot.SlotKind.Failure)
            earlyTcs.SetException(slot.Exception);

        // Replace the stored value with the TCS (not strictly required, but keeps the dictionary clean).
        _slots.TryUpdate(entryIndex, new CompletionSlot(earlyTcs), slot);
        return earlyTcs;
    }

    public bool TryComplete(TKey entryIndex, CompletionResult result)
    {
        // Stamped before the wait is resolved, so a handler woken by it already observes the
        // position this result was delivered at.
        var arrivalOrder = _arrivals.Next();

        if (_slots.TryRemove(entryIndex, out var slot))
        {
            if (slot.Kind == CompletionSlot.SlotKind.Tcs)
            {
                slot.Tcs.ArrivalOrder = arrivalOrder;
                return slot.Tcs.TrySetResult(result);
            }
        }

        // No handler registered yet — store the result for later delivery.
        _slots.TryAdd(entryIndex, new CompletionSlot(result, arrivalOrder));
        return true;
    }

    public bool TryFail(TKey entryIndex, ushort code, string message)
    {
        var arrivalOrder = _arrivals.Next();

        if (_slots.TryRemove(entryIndex, out var slot))
        {
            if (slot.Kind == CompletionSlot.SlotKind.Tcs)
            {
                slot.Tcs.ArrivalOrder = arrivalOrder;
                return slot.Tcs.TrySetException(new TerminalException(message, code));
            }
        }

        // No handler registered yet — store the failure for later delivery.
        _slots.TryAdd(entryIndex, new CompletionSlot(new TerminalException(message, code), arrivalOrder));
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
    public List<TKey> CollectPendingIds()
    {
        var ids = new List<TKey>(_slots.Count);
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
