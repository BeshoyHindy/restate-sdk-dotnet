using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Tests.Journal;

public class CompletionManagerTests
{
    [Fact]
    public void Register_CreatesCompletionSource()
    {
        var manager = new CompletionManager();
        var tcs = manager.Register(0);

        Assert.NotNull(tcs);
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public void Register_ThrowsOnDuplicate()
    {
        var manager = new CompletionManager();
        manager.Register(0);

        Assert.Throws<InvalidOperationException>(() => manager.Register(0));
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
        var manager = new CompletionManager();
        // Completion arrives before any registration — should store it for later.
        Assert.True(manager.TryComplete(99, CompletionResult.Success(new byte[] { 42 })));
    }

    [Fact]
    public async Task TryComplete_EarlyCompletionDeliveredOnRegister()
    {
        var manager = new CompletionManager();
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
        var manager = new CompletionManager();
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
        var manager = new CompletionManager();
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
        var manager = new CompletionManager();
        var tcs = manager.Register(0);

        Assert.True(manager.TryFail(0, 409, "Conflict"));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => tcs.Task);
        Assert.Equal("Conflict", ex.Message);
        Assert.Equal(409, ex.Code);
    }

    [Fact]
    public async Task CancelAll_CancelsAllPending()
    {
        var manager = new CompletionManager();
        var tcs1 = manager.Register(0);
        var tcs2 = manager.Register(1);

        manager.CancelAll();

        await Assert.ThrowsAsync<TaskCanceledException>(() => tcs1.Task);
        await Assert.ThrowsAsync<TaskCanceledException>(() => tcs2.Task);
    }

    [Fact]
    public void CancelAll_IsIdempotent()
    {
        var manager = new CompletionManager();
        manager.Register(0);
        manager.CancelAll();
        manager.CancelAll();
    }

    // ------- Poison (suspension on input EOF) -------

    [Fact]
    public async Task Poison_FailsPendingWithSuspensionException()
    {
        var manager = new CompletionManager();
        var tcs = manager.Register(0);

        manager.Poison();

        await Assert.ThrowsAsync<SuspensionException>(() => tcs.Task);
    }

    [Fact]
    public async Task Poison_EarlyResultSurvives()
    {
        var manager = new CompletionManager();
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
        var manager = new CompletionManager();
        manager.TryFail(0, 409, "Conflict");

        manager.Poison();

        var tcs = manager.GetOrRegister(0);
        var ex = await Assert.ThrowsAsync<TerminalException>(() => tcs.Task);
        Assert.Equal(409, ex.Code);
    }

    [Fact]
    public async Task Register_AfterPoison_FailsImmediately()
    {
        var manager = new CompletionManager();
        manager.Poison();

        var tcs = manager.Register(0);

        Assert.True(tcs.Task.IsFaulted);
        await Assert.ThrowsAsync<SuspensionException>(() => tcs.Task);
    }

    [Fact]
    public async Task GetOrRegister_AfterPoison_FailsImmediately()
    {
        var manager = new CompletionManager();
        manager.Poison();

        var tcs = manager.GetOrRegister(0);

        Assert.True(tcs.Task.IsFaulted);
        await Assert.ThrowsAsync<SuspensionException>(() => tcs.Task);
    }

    [Fact]
    public async Task Poison_IsIdempotent()
    {
        var manager = new CompletionManager();
        var tcs = manager.Register(0);

        manager.Poison();
        manager.Poison();

        await Assert.ThrowsAsync<SuspensionException>(() => tcs.Task);
        Assert.Equal(0, Assert.Single(manager.CollectPendingIds()));
    }

    [Fact]
    public void CollectPendingIds_ReturnsPendingAndPoisonedIdsSorted()
    {
        var manager = new CompletionManager();
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
    public void CollectPendingIds_ExcludesResolvedWaits()
    {
        var manager = new CompletionManager();
        var tcs = manager.Register(0);
        manager.TryComplete(0, CompletionResult.Success(new byte[] { 1 }));
        Assert.True(tcs.Task.IsCompletedSuccessfully);

        // An early result consumed via GetOrRegister leaves a resolved TCS in the slot.
        manager.TryComplete(2, CompletionResult.Success(new byte[] { 2 }));
        _ = manager.GetOrRegister(2);

        manager.Poison();

        Assert.Empty(manager.CollectPendingIds());
    }
}