namespace Restate.Sdk.Internal.Journal;

/// <summary>
///     String-keyed twin of <see cref="CompletionManager" /> for NAMED signals (Rust
///     NotificationId::SignalName, vm/mod.rs:940). The numeric <see cref="CompletionManager" />
///     keys by wire completion/signal id, but the wire delivers a named signal by its NAME with no
///     numeric idx (SendSignalCommandMessage.signal_id = Name oneof, protocol.proto:486), so a name
///     can never be mapped onto the numeric id space — a separate string-keyed table is the only
///     correct structure. Every latch/early-completion semantic is identical to the numeric manager:
///     <list type="bullet">
///         <item>GetOrRegister parks a TCS (or returns an already-delivered early-completion slot);</item>
///         <item>TryComplete/TryFail resolve a parked waiter OR buffer an early delivery for a name
///             nobody is (yet) awaiting — so a named signal that arrives before, or without, any
///             waiter is harmless and strands no one (the "ignored when no API waiter" case);</item>
///         <item>HasResultFor drives the suspension-skip decision (a resolved name is not re-listed);</item>
///         <item>FailAll/CancelAll fault every waiter and LATCH the table so a straggler registering
///             post-suspension/post-cancel is born faulted (parity with the numeric manager).</item>
///     </list>
///     One lock, two contenders (handler + pump) — the same exclusive-&amp;mut-self model as the VM.
///     There is no Run-claim concept for named signals (they are non-completable from the handler
///     side), so the <c>_claimed</c> / TryClaimForExecution machinery is intentionally absent.
/// </summary>
internal sealed class NamedCompletionManager
{
    private readonly Dictionary<string, CompletionSlot> _slots = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Exception? _terminal; // latch: set by FailAll/CancelAll

    public TaskCompletionSource<CompletionResult> GetOrRegister(string name)
    {
        lock (_gate)
        {
            // LATCH (see CompletionManager.GetOrRegister): post-FailAll/CancelAll any new
            // registration is born faulted so a straggler waiter parking after suspension/cancel
            // unwinds immediately instead of hanging on a slot nobody can resolve.
            if (_terminal is not null)
            {
                var faulted = NewTcs();
                faulted.SetException(_terminal);
                _ = faulted.Task.Exception; // pre-observe (see FailAll)
                return faulted;
            }

            if (_slots.TryGetValue(name, out var slot))
            {
                if (slot.Kind == CompletionSlot.SlotKind.Tcs) return slot.Tcs;
                var resolved = NewTcs();
                if (slot.Kind == CompletionSlot.SlotKind.Result) resolved.SetResult(slot.Result);
                else resolved.SetException(slot.Exception);
                _slots[name] = new CompletionSlot(resolved);
                return resolved;
            }

            var tcs = NewTcs();
            _slots[name] = new CompletionSlot(tcs);
            return tcs;
        }
    }

    public bool TryComplete(string name, CompletionResult result)
    {
        lock (_gate)
        {
            if (_terminal is not null) return false; // latched — drop late deliveries
            if (_slots.TryGetValue(name, out var slot))
                return slot.Kind == CompletionSlot.SlotKind.Tcs
                       && slot.Tcs.TrySetResult(result); // duplicate redelivery → false, no overwrite
            _slots[name] = new CompletionSlot(result); // early completion (or no-waiter), parked
            return true;
        }
    }

    public bool TryFail(string name, ushort code, string message)
    {
        lock (_gate)
        {
            if (_terminal is not null) return false;
            if (_slots.TryGetValue(name, out var slot))
                return slot.Kind == CompletionSlot.SlotKind.Tcs
                       && slot.Tcs.TrySetException(new TerminalException(message, code));
            _slots[name] = new CompletionSlot(new TerminalException(message, code));
            return true;
        }
    }

    /// <summary>
    ///     A buffered or already-delivered result exists for this name — drives the suspension-skip
    ///     decision so a resolved name is not re-listed in waiting_named_signals.
    /// </summary>
    public bool HasResultFor(string name)
    {
        lock (_gate)
        {
            return _slots.TryGetValue(name, out var slot)
                   && (slot.Kind != CompletionSlot.SlotKind.Tcs || slot.Tcs.Task.IsCompleted);
        }
    }

    public void FailAll(Exception exception)
    {
        lock (_gate)
        {
            _terminal ??= exception;
            foreach (var pair in _slots)
                if (pair.Value.Kind == CompletionSlot.SlotKind.Tcs
                    && pair.Value.Tcs.TrySetException(exception))
                    _ = pair.Value.Tcs.Task.Exception; // mark observed (see CompletionManager.FailAll)
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
