using Restate.Sdk.Internal.Journal;

namespace Restate.Sdk.Tests.Journal;

public class CompletionManagerTests
{
    [Fact]
    public void GetOrRegister_CreatesCompletionSource()
    {
        var manager = new CompletionManager();
        var tcs = manager.GetOrRegister(0);

        Assert.NotNull(tcs);
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public void GetOrRegister_ReturnsSameForDuplicate()
    {
        var manager = new CompletionManager();
        var tcs1 = manager.GetOrRegister(0);
        var tcs2 = manager.GetOrRegister(0);

        Assert.Same(tcs1, tcs2);
    }

    [Fact]
    public async Task TryComplete_ResolvesTask()
    {
        var manager = new CompletionManager();
        var tcs = manager.GetOrRegister(0);

        var result = CompletionResult.Success(new byte[] { 1, 2, 3 });
        Assert.True(manager.TryComplete(0, result));

        var completed = await tcs.Task;
        Assert.True(completed.IsSuccess);
        Assert.Equal(new byte[] { 1, 2, 3 }, completed.Value.ToArray());
    }

    [Fact]
    public void TryComplete_StoresEarlyCompletion()
    {
        var manager = new CompletionManager();
        // Completion arrives before any registration — should store it for later.
        Assert.True(manager.TryComplete(99, CompletionResult.Success(new byte[] { 42 })));
    }

    [Fact]
    public async Task TryComplete_EarlyCompletionDeliveredOnGetOrRegister()
    {
        var manager = new CompletionManager();
        manager.TryComplete(0, CompletionResult.Success(new byte[] { 9 }));

        var tcs = manager.GetOrRegister(0);
        Assert.True(tcs.Task.IsCompleted);
        var completed = await tcs.Task;
        Assert.True(completed.IsSuccess);
        Assert.Equal(new byte[] { 9 }, completed.Value.ToArray());
    }

    [Fact]
    public async Task TryFail_EarlyFailureDeliveredOnGetOrRegister()
    {
        var manager = new CompletionManager();
        manager.TryFail(0, 409, "Conflict");

        var tcs = manager.GetOrRegister(0);
        Assert.True(tcs.Task.IsFaulted);
        var ex = await Assert.ThrowsAsync<TerminalException>(() => tcs.Task);
        Assert.Equal("Conflict", ex.Message);
        Assert.Equal(409, ex.Code);
    }

    [Fact]
    public async Task TryFail_SetsTerminalException()
    {
        var manager = new CompletionManager();
        var tcs = manager.GetOrRegister(0);

        Assert.True(manager.TryFail(0, 409, "Conflict"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => tcs.Task);
        Assert.Equal("Conflict", ex.Message);
        Assert.Equal(409, ex.Code);
    }

    [Fact]
    public void TryComplete_DuplicateRedelivery_DoesNotOverwrite()
    {
        var manager = new CompletionManager();
        manager.GetOrRegister(5);
        Assert.True(manager.TryComplete(5, CompletionResult.Success(new byte[] { 1 })));
        // Second delivery on a resolved slot → false, no overwrite, no throw.
        Assert.False(manager.TryComplete(5, CompletionResult.Success(new byte[] { 2 })));
    }

    [Fact]
    public void HasResultFor_ReflectsDeliveryAndRegistration()
    {
        var manager = new CompletionManager();
        Assert.False(manager.HasResultFor(1));      // unknown
        manager.GetOrRegister(1);
        Assert.False(manager.HasResultFor(1));      // registered but unresolved
        manager.TryComplete(1, CompletionResult.Success(ReadOnlyMemory<byte>.Empty));
        Assert.True(manager.HasResultFor(1));       // delivered
        // Early completion (no registration) is also a result.
        manager.TryComplete(2, CompletionResult.Success(ReadOnlyMemory<byte>.Empty));
        Assert.True(manager.HasResultFor(2));
    }

    [Fact]
    public void TryGetResult_ReturnsOnlyBufferedSuccessResults()
    {
        var manager = new CompletionManager();

        // Absent id → false (the child-cancel path treats this as "unresolved, skip").
        Assert.False(manager.TryGetResult(1, out _));

        // Early-completion success is parked as a Result-kind slot → readable WITHOUT a waiter, which
        // is exactly how a tracked child's invocation-id string is read at CANCEL time.
        manager.TryComplete(1, CompletionResult.SuccessString("inv_child"));
        Assert.True(manager.TryGetResult(1, out var resolved));
        Assert.Equal("inv_child", resolved.StringValue);

        // A registered-but-unresolved waiter is a Tcs-kind slot (not Result-kind) → false.
        manager.GetOrRegister(2);
        Assert.False(manager.TryGetResult(2, out _));

        // A failure is stored Failure-kind, never Result-kind → reported as "no result" (skip).
        manager.TryFail(3, 500, "boom");
        Assert.False(manager.TryGetResult(3, out _));
    }

    [Fact]
    public void TryClaimForExecution_OnlyOnce_AndNotAfterDelivery()
    {
        var manager = new CompletionManager();
        Assert.True(manager.TryClaimForExecution(7));    // first claim wins
        Assert.False(manager.TryClaimForExecution(7));   // already claimed

        var other = new CompletionManager();
        other.TryComplete(8, CompletionResult.Success(ReadOnlyMemory<byte>.Empty));
        Assert.False(other.TryClaimForExecution(8));      // a delivered result blocks the claim
    }

    [Fact]
    public async Task FailAll_LatchesManager()
    {
        var manager = new CompletionManager();
        var parked = manager.GetOrRegister(0);

        manager.FailAll(new SuspendedExceptionStub());

        await Assert.ThrowsAsync<SuspendedExceptionStub>(() => parked.Task);

        // After the latch, new registrations are born faulted with the same exception.
        var later = manager.GetOrRegister(1);
        Assert.True(later.Task.IsFaulted);
        await Assert.ThrowsAsync<SuspendedExceptionStub>(() => later.Task);

        // TryComplete after the latch is a no-op.
        Assert.False(manager.TryComplete(2, CompletionResult.Success(ReadOnlyMemory<byte>.Empty)));
    }

    [Fact]
    public async Task CancelAll_CancelsAllPending()
    {
        var manager = new CompletionManager();
        var tcs1 = manager.GetOrRegister(0);
        var tcs2 = manager.GetOrRegister(1);

        manager.CancelAll();

        await Assert.ThrowsAsync<TaskCanceledException>(() => tcs1.Task);
        await Assert.ThrowsAsync<TaskCanceledException>(() => tcs2.Task);
    }

    [Fact]
    public void CancelAll_IsIdempotent()
    {
        var manager = new CompletionManager();
        manager.GetOrRegister(0);
        manager.CancelAll();
        manager.CancelAll();
    }

    // ---- Residual branch closure (plan 07 core-branches lane) ----------------------------------

    [Fact]
    public void TryComplete_OnBufferedResultSlot_ReturnsFalse_NoOverwrite()
    {
        // CompletionManager.cs:94 — the `slot.Kind == Tcs` guard's FALSE arm: a second early
        // TryComplete (no GetOrRegister between them) lands on the buffered Result slot the first one
        // created, so the `&&` short-circuits and the second delivery is dropped without overwrite.
        var manager = new CompletionManager();
        Assert.True(manager.TryComplete(3, CompletionResult.Success(new byte[] { 1 })));   // buffers a Result slot
        Assert.False(manager.TryComplete(3, CompletionResult.Success(new byte[] { 2 })));  // hits Kind != Tcs arm
    }

    [Fact]
    public void TryFail_AfterLatch_ReturnsFalse()
    {
        // CompletionManager.cs:105 — TryFail's latch guard. After FailAll the manager is latched, so a
        // straggler failure delivery is dropped (returns false) rather than touching a cleared table.
        var manager = new CompletionManager();
        manager.FailAll(new SuspendedExceptionStub());
        Assert.False(manager.TryFail(0, 500, "late"));
    }

    [Fact]
    public void TryFail_OnBufferedResultSlot_ReturnsFalse_NoOverwrite()
    {
        // CompletionManager.cs:107 — TryFail's `slot.Kind == Tcs` FALSE arm: an early TryComplete
        // buffers a Result slot, then a racing TryFail for the same id hits the non-Tcs branch and is
        // dropped (the buffered success wins; no double-resolution).
        var manager = new CompletionManager();
        Assert.True(manager.TryComplete(4, CompletionResult.Success(new byte[] { 7 })));
        Assert.False(manager.TryFail(4, 500, "late-failure"));
    }

    [Fact]
    public void TryClaimForExecution_AfterLatch_ReturnsFalse()
    {
        // CompletionManager.cs:139 — TryClaimForExecution's latch guard. After CancelAll (which also
        // latches) a Run that tries to claim its id for local execution is refused, so the unwinding
        // closure never re-runs a side effect against a dead manager.
        var manager = new CompletionManager();
        manager.CancelAll();
        Assert.False(manager.TryClaimForExecution(7));
    }

    [Fact]
    public void TryClaimForExecution_OnCompletedTcsSlot_ReturnsFalse()
    {
        // CompletionManager.cs:140 — the `slot.Tcs.Task.IsCompleted` sub-branch of the claim guard: an
        // id whose TCS was registered AND then resolved must NOT be claimable for local execution (the
        // pump already delivered its notification), so the claim is refused even though Kind == Tcs.
        var manager = new CompletionManager();
        manager.GetOrRegister(9);                                                   // a Tcs slot
        Assert.True(manager.TryComplete(9, CompletionResult.Success(ReadOnlyMemory<byte>.Empty)));  // now completed
        Assert.False(manager.TryClaimForExecution(9));                              // completed Tcs → no claim
    }

    private sealed class SuspendedExceptionStub : Exception;
}
