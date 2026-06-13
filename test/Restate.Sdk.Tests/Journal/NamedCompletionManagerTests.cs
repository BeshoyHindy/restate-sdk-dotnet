using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Journal;

namespace Restate.Sdk.Tests.Journal;

/// <summary>
///     Branch coverage for <see cref="NamedCompletionManager" /> — the string-keyed twin of
///     <see cref="CompletionManager" /> backing named signals. Mirrors the numeric manager's tests,
///     exercising early completion / failure, the resolved-slot re-materialization on a second
///     GetOrRegister, duplicate-delivery rejection, HasResultFor, and the FailAll/CancelAll latch
///     that makes post-terminal registrations born faulted.
/// </summary>
public class NamedCompletionManagerTests
{
    [Fact]
    public void GetOrRegister_ReturnsSameForDuplicate()
    {
        var manager = new NamedCompletionManager();
        var first = manager.GetOrRegister("a");
        var second = manager.GetOrRegister("a");
        Assert.Same(first, second);
    }

    [Fact]
    public async Task TryComplete_ResolvesParkedWaiter()
    {
        var manager = new NamedCompletionManager();
        var tcs = manager.GetOrRegister("a");
        Assert.True(manager.TryComplete("a", CompletionResult.Success(new byte[] { 1, 2 })));
        var result = await tcs.Task;
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 1, 2 }, result.Value.ToArray());
    }

    [Fact]
    public async Task TryComplete_EarlyDelivery_ThenGetOrRegister_MaterializesResult()
    {
        var manager = new NamedCompletionManager();
        // Deliver BEFORE any waiter — the value parks in the slot.
        Assert.True(manager.TryComplete("a", CompletionResult.Success(new byte[] { 9 })));
        Assert.True(manager.HasResultFor("a"));

        // GetOrRegister now materializes a resolved TCS from the buffered Result slot.
        var tcs = manager.GetOrRegister("a");
        Assert.True(tcs.Task.IsCompleted);
        var result = await tcs.Task;
        Assert.Equal(new byte[] { 9 }, result.Value.ToArray());

        // A second register returns the same already-resolved TCS (now a Tcs slot).
        Assert.Same(tcs, manager.GetOrRegister("a"));
    }

    [Fact]
    public async Task TryFail_EarlyDelivery_ThenGetOrRegister_MaterializesFaultedTcs()
    {
        var manager = new NamedCompletionManager();
        Assert.True(manager.TryFail("a", 409, "nope"));
        Assert.True(manager.HasResultFor("a"));

        var tcs = manager.GetOrRegister("a");
        Assert.True(tcs.Task.IsFaulted);
        var ex = await Assert.ThrowsAsync<TerminalException>(async () => await tcs.Task);
        Assert.Equal(409, ex.Code);
        Assert.Equal("nope", ex.Message);
    }

    [Fact]
    public void TryComplete_DuplicateDelivery_ReturnsFalse()
    {
        var manager = new NamedCompletionManager();
        var tcs = manager.GetOrRegister("a");
        Assert.True(manager.TryComplete("a", CompletionResult.Success(new byte[] { 1 })));
        // A second delivery to the now-resolved Tcs slot is rejected (no overwrite).
        Assert.False(manager.TryComplete("a", CompletionResult.Success(new byte[] { 2 })));
        _ = tcs;
    }

    [Fact]
    public void TryFail_ParkedWaiter_FaultsAndRejectsRedelivery()
    {
        var manager = new NamedCompletionManager();
        var tcs = manager.GetOrRegister("a");
        Assert.True(manager.TryFail("a", 500, "boom"));
        Assert.True(tcs.Task.IsFaulted);
        Assert.False(manager.TryFail("a", 500, "again"));
    }

    [Fact]
    public void HasResultFor_UnknownName_ReturnsFalse()
    {
        var manager = new NamedCompletionManager();
        manager.GetOrRegister("a"); // parked, unresolved
        Assert.False(manager.HasResultFor("a"));
        Assert.False(manager.HasResultFor("missing"));
    }

    [Fact]
    public async Task FailAll_FaultsWaiters_AndLatchesSoLaterRegisterIsBornFaulted()
    {
        var manager = new NamedCompletionManager();
        var tcs = manager.GetOrRegister("a");
        manager.FailAll(new TerminalException("cancelled", 409));
        await Assert.ThrowsAsync<TerminalException>(async () => await tcs.Task);

        // LATCH: any registration after FailAll is born faulted.
        var late = manager.GetOrRegister("b");
        Assert.True(late.Task.IsFaulted);
        await Assert.ThrowsAsync<TerminalException>(async () => await late.Task);

        // Late deliveries are dropped after the latch.
        Assert.False(manager.TryComplete("c", CompletionResult.Success(new byte[] { 1 })));
        Assert.False(manager.TryFail("c", 500, "x"));
    }

    [Fact]
    public void CancelAll_CancelsWaiters_AndLatches()
    {
        var manager = new NamedCompletionManager();
        var tcs = manager.GetOrRegister("a");
        manager.CancelAll();
        Assert.True(tcs.Task.IsCanceled);

        // Latched: a later register is born faulted (with the TaskCanceledException sentinel).
        var late = manager.GetOrRegister("b");
        Assert.True(late.Task.IsFaulted);
    }
}
