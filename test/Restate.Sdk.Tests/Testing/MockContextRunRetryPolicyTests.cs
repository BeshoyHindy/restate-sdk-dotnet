using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Testing;

public class MockContextRunRetryPolicyTests
{
    #region Async overloads

    [Fact]
    public async Task RunAsync_WithRetryPolicy_ExecutesInline()
    {
        var ctx = new MockContext();
        Func<Task<int>> action = () => Task.FromResult(42);

        var result = await ctx.Run("test-run", action, RetryPolicy.Default);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsyncVoid_WithRetryPolicy_ExecutesAction()
    {
        var ctx = new MockContext();
        var executed = false;

        Func<Task> action = () =>
        {
            executed = true;
            return Task.CompletedTask;
        };
        await ctx.Run("test-run", action, RetryPolicy.FixedAttempts(10));

        Assert.True(executed);
    }

    [Fact]
    public async Task RunAsync_WithRetryPolicy_PropagatesException()
    {
        var ctx = new MockContext();
        Func<Task<int>> action = () =>
            Task.FromException<int>(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ctx.Run("fail-run", action, RetryPolicy.FixedAttempts(3))
        );
    }

    #endregion

    #region Sync overloads

    [Fact]
    public async Task RunSync_WithRetryPolicy_ExecutesInline()
    {
        var ctx = new MockContext();
        Func<string> action = () => "hello";

        var result = await ctx.Run("test-sync-run", action, RetryPolicy.Default);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task RunSync_WithRetryPolicy_ReturnsComputedValue()
    {
        var ctx = new MockContext();
        Func<int> action = () => 2 + 3;

        var result = await ctx.Run("compute", action, RetryPolicy.FixedAttempts(5));

        Assert.Equal(5, result);
    }

    [Fact]
    public async Task RunSync_WithRetryPolicy_PropagatesException()
    {
        var ctx = new MockContext();
        Func<int> action = () => throw new InvalidOperationException("sync-boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ctx.Run("fail-sync", action, RetryPolicy.Default)
        );
    }

    #endregion

    #region Keyed contexts

    [Theory]
    [InlineData(typeof(MockObjectContext))]
    [InlineData(typeof(MockSharedObjectContext))]
    [InlineData(typeof(MockWorkflowContext))]
    [InlineData(typeof(MockSharedWorkflowContext))]
    public async Task KeyedMock_RunAsync_WithRetryPolicy_ExecutesInline(Type mockType)
    {
        var ctx = (Context)Activator.CreateInstance(mockType, "test-key", null)!;
        Func<Task<string>> action = () => Task.FromResult("ok");

        var result = await ctx.Run("retry-run", action, RetryPolicy.Default);

        Assert.Equal("ok", result);
    }

    [Theory]
    [InlineData(typeof(MockObjectContext))]
    [InlineData(typeof(MockSharedObjectContext))]
    [InlineData(typeof(MockWorkflowContext))]
    [InlineData(typeof(MockSharedWorkflowContext))]
    public async Task KeyedMock_RunSync_WithRetryPolicy_ExecutesInline(Type mockType)
    {
        var ctx = (Context)Activator.CreateInstance(mockType, "test-key", null)!;
        Func<int> action = () => 99;

        var result = await ctx.Run("retry-sync-run", action, RetryPolicy.FixedAttempts(3));

        Assert.Equal(99, result);
    }

    #endregion
}
