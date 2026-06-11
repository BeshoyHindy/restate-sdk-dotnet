using Restate.Sdk.Internal.Journal;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.Journal;

[Collection(StateMachine.StressCollection.Name)]
public sealed class CompletionManagerRaceTests
{
    private const int RaceIterations = 10_000;
    private const int ClaimRaceIterations = 1_000;

    [Fact(Timeout = 10_000)]
    public async Task TryComplete_RacingGetOrRegister_NeverDropsNotification()
    {
        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var manager = new CompletionManager();
            var payload = new byte[] { (byte)(iteration & 0xFF) };

            var register = Task.Run(() => manager.GetOrRegister(5));
            var complete = Task.Run(() => manager.TryComplete(5, CompletionResult.Success(payload)));

            var registered = await AwaitBounded(register);
            await AwaitBounded(complete);

            var resolved = await AwaitBounded(registered.Task);
            Assert.True(resolved.IsSuccess);
            Assert.Equal(payload, resolved.Value.ToArray());
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task TryFail_RacingGetOrRegister_NeverDropsFailure()
    {
        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var manager = new CompletionManager();

            var register = Task.Run(() => manager.GetOrRegister(7));
            var fail = Task.Run(() => manager.TryFail(7, 409, "Conflict"));

            var registered = await AwaitBounded(register);
            await AwaitBounded(fail);

            var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(registered.Task));
            Assert.Equal(409, ex.Code);
            Assert.Equal("Conflict", ex.Message);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task GetOrRegister_RacingPreStoredEarlyCompletion_AdoptsResult()
    {
        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var manager = new CompletionManager();
            var payload = new byte[] { 0xCA, (byte)(iteration & 0xFF) };
            manager.TryComplete(9, CompletionResult.Success(payload));

            var first = Task.Run(() => manager.GetOrRegister(9));
            var second = Task.Run(() => manager.GetOrRegister(9));
            var firstTcs = await AwaitBounded(first);
            var secondTcs = await AwaitBounded(second);

            var a = await AwaitBounded(firstTcs.Task);
            var b = await AwaitBounded(secondTcs.Task);
            Assert.Equal(payload, a.Value.ToArray());
            Assert.Equal(payload, b.Value.ToArray());
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task TryClaimForExecution_RacingTryComplete_NeverBothExecuteAndDeliverFirst()
    {
        for (var iteration = 0; iteration < ClaimRaceIterations; iteration++)
        {
            var manager = new CompletionManager();

            bool claimed = false, delivered = false;
            var claimTask = Task.Run(() => claimed = manager.TryClaimForExecution(3));
            var completeTask = Task.Run(() =>
                delivered = manager.TryComplete(3, CompletionResult.Success(ReadOnlyMemory<byte>.Empty)));

            await AwaitBounded(Task.WhenAll(claimTask, completeTask));

            Assert.True(delivered);
            if (claimed)
                Assert.False(manager.TryClaimForExecution(3));
            else
                Assert.True(manager.HasResultFor(3));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task FailAll_Latch_RacingRegistrations_AllFaultAndLateCompletesDrop()
    {
        var manager = new CompletionManager();
        var parked = manager.GetOrRegister(1);

        var registrations = Enumerable.Range(2, 64)
            .Select(id => Task.Run(() => manager.GetOrRegister(id)))
            .ToArray();
        var latch = Task.Run(() => manager.FailAll(new SuspendedLatchProbe()));

        await AwaitBounded(Task.WhenAll(registrations.Cast<Task>().Append(latch)));

        await Assert.ThrowsAsync<SuspendedLatchProbe>(() => AwaitBounded(parked.Task));

        // Await each creator task for its handed-out TCS (no blocking .Result — xUnit1031).
        foreach (var registration in registrations)
        {
            var tcs = await AwaitBounded(registration);
            await Assert.ThrowsAsync<SuspendedLatchProbe>(() => AwaitBounded(tcs.Task));
        }

        var afterLatch = manager.GetOrRegister(1000);
        Assert.True(afterLatch.Task.IsFaulted);
        await Assert.ThrowsAsync<SuspendedLatchProbe>(() => AwaitBounded(afterLatch.Task));

        Assert.False(manager.TryComplete(2000, CompletionResult.Success(ReadOnlyMemory<byte>.Empty)));
    }

    [Fact(Timeout = 10_000)]
    public async Task TryComplete_DuplicateRedelivery_KeepsFirstValue()
    {
        var manager = new CompletionManager();
        var tcs = manager.GetOrRegister(5);

        Assert.True(manager.TryComplete(5, CompletionResult.Success(new byte[] { 1 })));
        Assert.False(manager.TryComplete(5, CompletionResult.Success(new byte[] { 2 })));

        var resolved = await AwaitBounded(tcs.Task);
        Assert.Equal(new byte[] { 1 }, resolved.Value.ToArray());
    }

    [Fact(Timeout = 10_000)]
    public async Task FailAll_OverUnawaitedSlots_RaisesNoUnobservedTaskException()
    {
        Exception? unobserved = null;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs args) => unobserved = args.Exception;
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            CompletionManager manager = new();
            var awaited = manager.GetOrRegister(0);
            for (var id = 1; id <= 32; id++)
                manager.GetOrRegister(id);

            manager.FailAll(new SuspendedLatchProbe());

            await Assert.ThrowsAsync<SuspendedLatchProbe>(() => AwaitBounded(awaited.Task));

            manager = null!;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.Null(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    private sealed class SuspendedLatchProbe : Exception;
}
