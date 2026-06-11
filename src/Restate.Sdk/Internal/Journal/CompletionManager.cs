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

/// <summary>
///     Per-invocation completion table keyed by WIRE completion id (or wire signal id for the
///     signal instance) — never by journal index. Exactly two tasks contend (handler + pump),
///     so a plain Dictionary under one lock mirrors the Rust VM's exclusive &amp;mut self access
///     and removes the four ConcurrentDictionary TOCTOU windows (the lossy one: a TryComplete
///     whose failed TryRemove raced GetOrRegister, silently dropping the only notification).
///     TCSs use RunContinuationsAsynchronously, so resolving inside the lock never runs user
///     continuations inline.
/// </summary>
internal sealed class CompletionManager
{
    private readonly Dictionary<int, CompletionSlot> _slots = new();
    private readonly HashSet<int> _claimed = new();   // Run ids claimed for local execution
    private readonly object _gate = new();
    private Exception? _terminal;                      // latch: set by FailAll/CancelAll

    public TaskCompletionSource<CompletionResult> GetOrRegister(int completionId)
    {
        lock (_gate)
        {
            // LATCH: after FailAll/CancelAll any new registration is born faulted, so a
            // straggler continuation that parks post-suspension/post-abort unwinds immediately
            // instead of waiting on a slot nobody can ever resolve.
            if (_terminal is not null)
            {
                var faulted = NewTcs();
                faulted.SetException(_terminal);
                _ = faulted.Task.Exception;            // pre-observe (see FailAll)
                return faulted;
            }

            if (_slots.TryGetValue(completionId, out var slot))
            {
                if (slot.Kind == CompletionSlot.SlotKind.Tcs) return slot.Tcs;
                var resolved = NewTcs();
                if (slot.Kind == CompletionSlot.SlotKind.Result) resolved.SetResult(slot.Result);
                else resolved.SetException(slot.Exception);
                _slots[completionId] = new CompletionSlot(resolved);
                return resolved;
            }

            var tcs = NewTcs();
            _slots[completionId] = new CompletionSlot(tcs);
            return tcs;
        }
    }

    public bool TryComplete(int completionId, CompletionResult result)
    {
        lock (_gate)
        {
            if (_terminal is not null) return false;   // latched — drop late deliveries
            if (_slots.TryGetValue(completionId, out var slot))
                return slot.Kind == CompletionSlot.SlotKind.Tcs
                       && slot.Tcs.TrySetResult(result);   // duplicate redelivery → false, no overwrite
            _slots[completionId] = new CompletionSlot(result);   // early completion, parked for later
            return true;
        }
    }

    public bool TryFail(int completionId, ushort code, string message)
    {
        lock (_gate)
        {
            if (_terminal is not null) return false;
            if (_slots.TryGetValue(completionId, out var slot))
                return slot.Kind == CompletionSlot.SlotKind.Tcs
                       && slot.Tcs.TrySetException(new TerminalException(message, code));
            _slots[completionId] = new CompletionSlot(new TerminalException(message, code));
            return true;
        }
    }

    /// <summary>
    ///     A buffered or already-delivered result exists for this id (Run replay dedup — the
    ///     analogue of async_results.non_deterministic_find_id).
    /// </summary>
    public bool HasResultFor(int completionId)
    {
        lock (_gate)
        {
            return _slots.TryGetValue(completionId, out var slot)
                   && (slot.Kind != CompletionSlot.SlotKind.Tcs || slot.Tcs.Task.IsCompleted);
        }
    }

    /// <summary>
    ///     Atomic execute-vs-await decision for Run replay (1.7 case 2): returns false if a
    ///     result exists, was delivered, or the id was already claimed; otherwise marks the id
    ///     claimed-for-local-execution and returns true. Closes the TOCTOU where a late
    ///     RunCompletionNotification delivered by the pump between a bare HasResultFor check and
    ///     closure start would cause a duplicate side-effect execution + duplicate
    ///     ProposeRunCompletion. TryComplete still resolves a claimed slot's TCS normally.
    /// </summary>
    public bool TryClaimForExecution(int completionId)
    {
        lock (_gate)
        {
            if (_terminal is not null) return false;
            if (_slots.TryGetValue(completionId, out var slot)
                && (slot.Kind != CompletionSlot.SlotKind.Tcs || slot.Tcs.Task.IsCompleted))
                return false;
            return _claimed.Add(completionId);
        }
    }

    /// <summary>
    ///     Faults every pending waiter (suspension / abort unwind), clears the table, and LATCHES
    ///     the manager: all later GetOrRegister calls return pre-faulted TCSs.
    /// </summary>
    public void FailAll(Exception exception)
    {
        lock (_gate)
        {
            _terminal ??= exception;
            foreach (var pair in _slots)
                if (pair.Value.Kind == CompletionSlot.SlotKind.Tcs
                    && pair.Value.Tcs.TrySetException(exception))
                    _ = pair.Value.Tcs.Task.Exception;   // mark observed: slots nobody awaits
                                                         // (e.g., un-awaited call results) must not
                                                         // raise UnobservedTaskException; awaiting
                                                         // still rethrows normally.
            _slots.Clear();
        }
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            _terminal ??= new TaskCanceledException("Invocation completed");
            foreach (var pair in _slots)
                if (pair.Value.Kind == CompletionSlot.SlotKind.Tcs)
                    pair.Value.Tcs.TrySetCanceled();
            _slots.Clear();
        }
    }

    private static TaskCompletionSource<CompletionResult> NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
