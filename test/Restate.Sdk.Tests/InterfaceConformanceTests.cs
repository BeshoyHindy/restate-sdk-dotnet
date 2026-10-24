using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests;

public class InterfaceConformanceTests
{
    // ──────────────────────────────────────────────
    // 1. Abstract classes implement expected interfaces
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(Context), typeof(IContext))]
    [InlineData(typeof(SharedObjectContext), typeof(ISharedObjectContext))]
    [InlineData(typeof(SharedObjectContext), typeof(IContext))]
    [InlineData(typeof(ObjectContext), typeof(IObjectContext))]
    [InlineData(typeof(ObjectContext), typeof(ISharedObjectContext))]
    [InlineData(typeof(ObjectContext), typeof(IContext))]
    [InlineData(typeof(SharedWorkflowContext), typeof(ISharedWorkflowContext))]
    [InlineData(typeof(SharedWorkflowContext), typeof(ISharedObjectContext))]
    [InlineData(typeof(WorkflowContext), typeof(IWorkflowContext))]
    [InlineData(typeof(WorkflowContext), typeof(IObjectContext))]
    [InlineData(typeof(WorkflowContext), typeof(ISharedWorkflowContext))]
    [InlineData(typeof(WorkflowContext), typeof(ISharedObjectContext))]
    [InlineData(typeof(WorkflowContext), typeof(IContext))]
    public void AbstractClass_ImplementsExpectedInterface(Type classType, Type interfaceType)
    {
        Assert.True(interfaceType.IsAssignableFrom(classType),
            $"{classType.Name} should implement {interfaceType.Name}");
    }

    [Fact]
    public void IContext_Logger_HasDefaultImplementation()
    {
        // Logger was added after IContext shipped; a default interface implementation keeps
        // external direct implementations of IContext compiling (mirroring Context.Logger's
        // virtual NullLogger default) instead of breaking them with CS0535.
        var getter = typeof(IContext).GetProperty(nameof(IContext.Logger))!.GetGetMethod()!;
        Assert.False(getter.IsAbstract);
    }

    [Theory]
    [InlineData(typeof(ISharedObjectContext), nameof(ISharedObjectContext.Get))]
    [InlineData(typeof(ISharedObjectContext), nameof(ISharedObjectContext.StateKeys))]
    [InlineData(typeof(IObjectContext), nameof(IObjectContext.Set))]
    [InlineData(typeof(IObjectContext), nameof(IObjectContext.Clear))]
    [InlineData(typeof(IObjectContext), nameof(IObjectContext.ClearAll))]
    public void StateOperations_NamedOverloads_HaveDefaultImplementations(Type interfaceType, string methodName)
    {
        // Entry names were added after the state API shipped; like IContext.Logger, the named
        // overloads are default interface implementations that drop the name, so external direct
        // implementations keep compiling instead of breaking with CS0535.
        var named = interfaceType
            .GetMethods()
            .Where(m => m.Name == methodName)
            .Where(m => m.GetParameters() is [.., { ParameterType.FullName: "System.String" }] parameters
                        && parameters[^1].Name == "entryName")
            .ToList();

        Assert.All(named, m => Assert.False(m.IsAbstract));
        Assert.Single(named);
    }

    // ──────────────────────────────────────────────
    // 2. Mock contexts implement expected interfaces (runtime instances)
    // ──────────────────────────────────────────────

    [Fact]
    public void MockContext_ImplementsExpectedInterfaces()
    {
        Assert.IsAssignableFrom<IContext>(new MockContext());
    }

    [Fact]
    public void MockObjectContext_ImplementsExpectedInterfaces()
    {
        var ctx = new MockObjectContext();
        Assert.IsAssignableFrom<IObjectContext>(ctx);
        Assert.IsAssignableFrom<ISharedObjectContext>(ctx);
        Assert.IsAssignableFrom<IContext>(ctx);
    }

    [Fact]
    public void MockSharedObjectContext_ImplementsExpectedInterfaces()
    {
        var ctx = new MockSharedObjectContext();
        Assert.IsAssignableFrom<ISharedObjectContext>(ctx);
        Assert.IsAssignableFrom<IContext>(ctx);
    }

    [Fact]
    public void MockWorkflowContext_ImplementsExpectedInterfaces()
    {
        var ctx = new MockWorkflowContext();
        Assert.IsAssignableFrom<IWorkflowContext>(ctx);
        Assert.IsAssignableFrom<IObjectContext>(ctx);
        Assert.IsAssignableFrom<ISharedWorkflowContext>(ctx);
    }

    [Fact]
    public void MockSharedWorkflowContext_ImplementsExpectedInterfaces()
    {
        var ctx = new MockSharedWorkflowContext();
        Assert.IsAssignableFrom<ISharedWorkflowContext>(ctx);
        Assert.IsAssignableFrom<ISharedObjectContext>(ctx);
        Assert.IsAssignableFrom<IContext>(ctx);
    }

    // ──────────────────────────────────────────────
    // 3. Mock contexts can be passed to methods accepting interfaces
    // ──────────────────────────────────────────────

    [Fact]
    public async Task MockContext_CanBePassedAsIContext()
    {
        var mock = new MockContext();
        var result = await AcceptIContext(mock);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task MockObjectContext_CanBePassedAsIObjectContext()
    {
        var mock = new MockObjectContext();
        var result = await AcceptIObjectContext(mock);
        Assert.Equal("test-key", result);
    }

    [Fact]
    public async Task MockWorkflowContext_CanBePassedAsIObjectContext()
    {
        var mock = new MockWorkflowContext();
        var result = await AcceptIObjectContext(mock);
        Assert.Equal("test-key", result);
    }

    [Fact]
    public async Task MockWorkflowContext_CanBePassedAsISharedWorkflowContext()
    {
        var mock = new MockWorkflowContext();
        var result = await AcceptISharedWorkflowContext(mock);
        Assert.Equal("test-key", result);
    }

    // ──────────────────────────────────────────────
    // 4. IContext exposes new feature-parity members
    // ──────────────────────────────────────────────

    [Fact]
    public void IContext_HasRunWithRetryPolicy_VoidAsyncOverload()
    {
        var method = typeof(IContext).GetMethod(
            "Run",
            [typeof(string), typeof(Func<Task>), typeof(RetryPolicy)]
        );
        Assert.NotNull(method);
    }

    [Fact]
    public void IContext_HasCallWithCallOptions_ServiceOverload()
    {
        var methods = typeof(IContext)
            .GetMethods()
            .Where(
                m =>
                    m.Name == "Call"
                    && m.GetParameters().Length == 4
                    && m.GetParameters()[3].ParameterType == typeof(CallOptions)
            )
            .ToList();

        Assert.NotEmpty(methods);
    }

    [Fact]
    public void IContext_HasCallWithCallOptions_KeyedOverload()
    {
        var methods = typeof(IContext)
            .GetMethods()
            .Where(
                m =>
                    m.Name == "Call"
                    && m.GetParameters().Length == 5
                    && m.GetParameters()[4].ParameterType == typeof(CallOptions)
            )
            .ToList();

        Assert.NotEmpty(methods);
    }

    [Fact]
    public void IContext_HasCancelInvocation()
    {
        var method = typeof(IContext).GetMethod("CancelInvocation", [typeof(string)]);
        Assert.NotNull(method);
    }

    // Helper methods simulating handler signatures that accept interfaces

    private static async Task<string> AcceptIContext(IContext ctx)
    {
        await ctx.Sleep(TimeSpan.FromMilliseconds(1));
        return ctx.InvocationId;
    }

    private static Task<string> AcceptIObjectContext(IObjectContext ctx)
    {
        return Task.FromResult(ctx.Key);
    }

    private static Task<string> AcceptISharedWorkflowContext(ISharedWorkflowContext ctx)
    {
        return Task.FromResult(ctx.Key);
    }
}
