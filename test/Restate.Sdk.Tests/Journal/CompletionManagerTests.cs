using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Tests.Journal;

public class CompletionManagerTests
{
    [Fact]
    public void Register_CreatesCompletionSource()
    {
        var manager = new CompletionManager<int>();
        var tcs = manager.Register(0);

        Assert.NotNull(tcs);
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public void Register_ThrowsOnDuplicate()
    {
        var manager = new CompletionManager<int>();
        manager.Register(0);

        Assert.Throws<InvalidOperationException>(() => manager.Register(0));
    }

    [Fact]
    public void GetOrRegister_ReturnsSameForDuplicate()
    {
        var manager = new CompletionManager<int>();
        var tcs1 = manager.GetOrRegister(0);
        var tcs2 = manager.GetOrRegister(0);

        Assert.Same(tcs1, tcs2);
    }

    [Fact]
    public async Task TryComplete_ResolvesTask()
    {
        var manager = new CompletionManager<int>();
        var tcs = manager.Register(0);

        var result = CompletionResult.Success(new byte[] { 1, 2, 3 });
        Assert.True(manager.TryComplete(0, result));

        var completed = await tcs.Task;
        Assert.True(completed.IsSuccess);
        Assert.Equal(new byte[] { 1, 2, 3 }, completed.Value.ToArray());
    }

    [Fact]
    public void TryComplete_StoresEarlyCompletion()
    {
        var manager = new CompletionManager<int>();
        // Completion arrives before any registration — should store it for later.
        Assert.True(manager.TryComplete(99, CompletionResult.Success(new byte[] { 42 })));
    }

    [Fact]
    public async Task TryComplete_EarlyCompletionDeliveredOnRegister()
    {
        var manager = new CompletionManager<int>();
        // Completion arrives before registration.
        manager.TryComplete(0, CompletionResult.Success(new byte[] { 7, 8 }));

        // Now register — the TCS should already be resolved.
        var tcs = manager.Register(0);
        Assert.True(tcs.Task.IsCompleted);
        var completed = await tcs.Task;
        Assert.True(completed.IsSuccess);
        Assert.Equal(new byte[] { 7, 8 }, completed.Value.ToArray());
    }

    [Fact]
    public async Task TryComplete_EarlyCompletionDeliveredOnGetOrRegister()
    {
        var manager = new CompletionManager<int>();
        // Completion arrives before registration.
        manager.TryComplete(0, CompletionResult.Success(new byte[] { 9 }));

        // GetOrRegister should detect and deliver the early completion.
        var tcs = manager.GetOrRegister(0);
        Assert.True(tcs.Task.IsCompleted);
        var completed = await tcs.Task;
        Assert.True(completed.IsSuccess);
        Assert.Equal(new byte[] { 9 }, completed.Value.ToArray());
    }

    [Fact]
    public async Task TryFail_EarlyFailureDeliveredOnRegister()
    {
        var manager = new CompletionManager<int>();
        // Failure arrives before registration.
        manager.TryFail(0, 409, "Conflict");

        var tcs = manager.Register(0);
        Assert.True(tcs.Task.IsFaulted);
        var ex = await Assert.ThrowsAsync<TerminalException>(() => tcs.Task);
        Assert.Equal("Conflict", ex.Message);
        Assert.Equal(409, ex.Code);
    }

    [Fact]
    public async Task TryFail_SetsTerminalException()
    {
        var manager = new CompletionManager<int>();
        var tcs = manager.Register(0);

        Assert.True(manager.TryFail(0, 409, "Conflict"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => tcs.Task);
        Assert.Equal("Conflict", ex.Message);
        Assert.Equal(409, ex.Code);
    }

    [Fact]
    public async Task CancelAll_CancelsAllPending()
    {
        var manager = new CompletionManager<int>();
        var tcs1 = manager.Register(0);
        var tcs2 = manager.Register(1);

        manager.CancelAll();

        await Assert.ThrowsAsync<TaskCanceledException>(() => tcs1.Task);
        await Assert.ThrowsAsync<TaskCanceledException>(() => tcs2.Task);
    }

    [Fact]
    public void CancelAll_IsIdempotent()
    {
        var manager = new CompletionManager<int>();
        manager.Register(0);
        manager.CancelAll();
        manager.CancelAll();
    }

    // ------- Poison (suspension on input EOF) -------

    [Fact]
    public async Task Poison_FailsPendingWithSuspensionException()
    {
        var manager = new CompletionManager<int>();
        var tcs = manager.Register(0);

        manager.Poison();

        await Assert.ThrowsAsync<SuspensionException>(() => tcs.Task);
    }

    [Fact]
    public async Task Poison_EarlyResultSurvives()
    {
        var manager = new CompletionManager<int>();
        // A completion delivered before EOF must stay consumable after poisoning.
        manager.TryComplete(0, CompletionResult.Success(new byte[] { 7 }));

        manager.Poison();

        var tcs = manager.GetOrRegister(0);
        Assert.True(tcs.Task.IsCompletedSuccessfully);
        var completed = await tcs.Task;
        Assert.Equal(new byte[] { 7 }, completed.Value.ToArray());
    }

    [Fact]
    public async Task Poison_EarlyFailureSurvives()
    {
        var manager = new CompletionManager<int>();
        manager.TryFail(0, 409, "Conflict");

        manager.Poison();

        var tcs = manager.GetOrRegister(0);
        var ex = await Assert.ThrowsAsync<TerminalException>(() => tcs.Task);
        Assert.Equal(409, ex.Code);
    }

    [Fact]
    public async Task Register_AfterPoison_FailsImmediately()
    {
        var manager = new CompletionManager<int>();
        manager.Poison();

        var tcs = manager.Register(0);

        Assert.True(tcs.Task.IsFaulted);
        await Assert.ThrowsAsync<SuspensionException>(() => tcs.Task);
    }

    [Fact]
    public async Task GetOrRegister_AfterPoison_FailsImmediately()
    {
        var manager = new CompletionManager<int>();
        manager.Poison();

        var tcs = manager.GetOrRegister(0);

        Assert.True(tcs.Task.IsFaulted);
        await Assert.ThrowsAsync<SuspensionException>(() => tcs.Task);
    }

    [Fact]
    public async Task Poison_IsIdempotent()
    {
        var manager = new CompletionManager<int>();
        var tcs = manager.Register(0);

        manager.Poison();
        manager.Poison();

        await Assert.ThrowsAsync<SuspensionException>(() => tcs.Task);
        Assert.Equal(0, Assert.Single(manager.CollectPendingIds()));
    }

    [Fact]
    public void CollectPendingIds_ReturnsPendingAndPoisonedIdsSorted()
    {
        var manager = new CompletionManager<int>();
        manager.Register(3);
        manager.Register(1);
        // Early result delivered before registration — not pending.
        manager.TryComplete(2, CompletionResult.Success(new byte[] { 1 }));

        // Incomplete waits are pending even before poisoning.
        int[] expectedBeforePoison = [1, 3];
        Assert.Equal(expectedBeforePoison, manager.CollectPendingIds());

        manager.Poison();
        var late = manager.GetOrRegister(4);
        Assert.True(late.Task.IsFaulted);

        int[] expectedAfterPoison = [1, 3, 4];
        Assert.Equal(expectedAfterPoison, manager.CollectPendingIds());
    }

    [Fact]
    public void TryComplete_NumbersArrivalsInDeliveryOrder()
    {
        var manager = new CompletionManager<int>();
        var first = manager.Register(1);
        var second = manager.Register(2);

        // The runtime delivers the second wait's result first — the arrival positions must
        // record that, since the combinators replay the journaled completion order from them.
        manager.TryComplete(2, CompletionResult.Success(new byte[] { 2 }));
        manager.TryComplete(1, CompletionResult.Success(new byte[] { 1 }));

        Assert.True(second.ArrivalOrder < first.ArrivalOrder);
        Assert.True(second.ArrivalOrder > 0);
    }

    [Fact]
    public void GetOrRegister_EarlyCompletion_KeepsItsArrivalOrder()
    {
        var manager = new CompletionManager<int>();

        // Both results arrive during the resume drain, before the handler registers anything.
        manager.TryComplete(2, CompletionResult.Success(new byte[] { 2 }));
        manager.TryFail(1, 500, "boom");

        var first = manager.GetOrRegister(1);
        var second = manager.GetOrRegister(2);

        Assert.True(second.ArrivalOrder < first.ArrivalOrder);
        Assert.True(second.ArrivalOrder > 0);
    }

    [Fact]
    public void SharedArrivalClock_NumbersResultsAcrossManagers()
    {
        // A combinator may be handed waits from different managers (a call raced against a
        // signal), so one invocation numbers all of its arrivals on a single clock.
        var arrivals = new ArrivalClock();
        var completions = new CompletionManager<int>(arrivals);
        var signals = new CompletionManager<string>(arrivals);

        var signal = signals.Register("approval");
        var completion = completions.Register(1);

        signals.TryComplete("approval", CompletionResult.Success(new byte[] { 1 }));
        completions.TryComplete(1, CompletionResult.Success(new byte[] { 2 }));

        Assert.True(signal.ArrivalOrder < completion.ArrivalOrder);
    }

    [Fact]
    public void CollectPendingIds_ExcludesResolvedWaits()
    {
        var manager = new CompletionManager<int>();
        var tcs = manager.Register(0);
        manager.TryComplete(0, CompletionResult.Success(new byte[] { 1 }));
        Assert.True(tcs.Task.IsCompletedSuccessfully);

        // An early result consumed via GetOrRegister leaves a resolved TCS in the slot.
        manager.TryComplete(2, CompletionResult.Success(new byte[] { 2 }));
        _ = manager.GetOrRegister(2);

        manager.Poison();

        Assert.Empty(manager.CollectPendingIds());
    }

    [Fact]
    public void TryPeekResult_ReadsAnEarlyResultWithoutRegistering()
    {
        var manager = new CompletionManager<int>();

        // What a call's invocation-id notification does: delivered with nothing waiting on it.
        manager.TryComplete(1, CompletionResult.SuccessString("inv-called"));

        Assert.True(manager.TryPeekResult(1, out var peeked));
        Assert.Equal("inv-called", peeked.StringValue);

        // Peeking registers no wait, so poisoning the manager advertises nothing and leaves the
        // result readable — the invocation must not be resumed over a notification it never
        // asked to be woken for.
        manager.Poison();
        Assert.Empty(manager.CollectPendingIds());
        Assert.True(manager.TryPeekResult(1, out _));
    }

    [Fact]
    public void TryPeekResult_IsFalseWithoutADeliveredResult()
    {
        var manager = new CompletionManager<int>();

        Assert.False(manager.TryPeekResult(7, out _));

        // A registered wait holds the slot, and a failure is not a result to read.
        _ = manager.Register(7);
        Assert.False(manager.TryPeekResult(7, out _));

        manager.TryFail(8, 500, "boom");
        Assert.False(manager.TryPeekResult(8, out _));
    }
}