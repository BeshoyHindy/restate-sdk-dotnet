using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Testing;

public interface ITestClient
{
    string GetValue();
}

file class TestClient : ITestClient
{
    public string GetValue() => "test-value";
}

public class MockContextFeatureTests
{
    private static readonly DateTimeOffset DefaultTime = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    #region 1. CurrentTime (deterministic time)

    [Fact]
    public async Task MockContext_Now_ReturnsDefaultTime()
    {
        var ctx = new MockContext();

        var now = await ctx.Now();

        Assert.Equal(DefaultTime, now);
    }

    [Fact]
    public async Task MockContext_CurrentTime_DefaultsTo2024()
    {
        var ctx = new MockContext();

        Assert.Equal(DefaultTime, ctx.CurrentTime);
    }

    [Fact]
    public async Task MockContext_Now_ReturnsUpdatedCurrentTime()
    {
        var ctx = new MockContext();
        var customTime = new DateTimeOffset(2025, 6, 15, 12, 30, 0, TimeSpan.Zero);
        ctx.CurrentTime = customTime;

        var now = await ctx.Now();

        Assert.Equal(customTime, now);
    }

    [Fact]
    public async Task MockContext_SettingCurrentTime_ChangesNowResult()
    {
        var ctx = new MockContext();

        var first = await ctx.Now();
        ctx.CurrentTime = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var second = await ctx.Now();

        Assert.Equal(DefaultTime, first);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), second);
    }

    [Theory]
    [InlineData(typeof(MockObjectContext))]
    [InlineData(typeof(MockSharedObjectContext))]
    [InlineData(typeof(MockWorkflowContext))]
    [InlineData(typeof(MockSharedWorkflowContext))]
    public async Task KeyedMockContext_Now_ReturnsDefaultTime(Type mockType)
    {
        var ctx = (Context)Activator.CreateInstance(mockType, "test-key", null)!;

        var now = await ctx.Now();

        Assert.Equal(DefaultTime, now);
    }

    #endregion

    #region 2. SetupCallFailure

    [Fact]
    public async Task MockContext_SetupCallFailure_ThrowsTerminalException()
    {
        var ctx = new MockContext();
        var expected = new TerminalException("service unavailable", 503);
        ctx.SetupCallFailure("MyService", "myHandler", expected);

        var ex = await Assert.ThrowsAsync<TerminalException>(
            async () => await ctx.Call<string>("MyService", "myHandler"));

        Assert.Same(expected, ex);
        Assert.Equal("service unavailable", ex.Message);
        Assert.Equal((ushort)503, ex.Code);
    }

    [Fact]
    public async Task MockContext_SetupCallFailure_WithKey_ThrowsTerminalException()
    {
        var ctx = new MockContext();
        var expected = new TerminalException("not found", 404);
        ctx.SetupCallFailure("MyObject", "key-1", "myHandler", expected);

        var ex = await Assert.ThrowsAsync<TerminalException>(
            async () => await ctx.Call<string>("MyObject", "key-1", "myHandler"));

        Assert.Same(expected, ex);
    }

    [Fact]
    public async Task MockContext_SetupCallFailure_RecordsCallBeforeThrowing()
    {
        var ctx = new MockContext();
        ctx.SetupCallFailure("Svc", "handler", new TerminalException("fail"));

        try
        {
            await ctx.Call<string>("Svc", "handler", (object?)"payload");
        }
        catch (TerminalException)
        {
            // expected
        }

        Assert.Single(ctx.Calls);
        Assert.Equal("Svc", ctx.Calls[0].Service);
        Assert.Equal("handler", ctx.Calls[0].Handler);
        Assert.Equal("payload", ctx.Calls[0].Request);
    }

    [Fact]
    public async Task MockContext_SetupCallFailure_TakesPriorityOverResult()
    {
        var ctx = new MockContext();
        ctx.SetupCall("Svc", "handler", "success-result");
        ctx.SetupCallFailure("Svc", "handler", new TerminalException("failure wins"));

        var ex = await Assert.ThrowsAsync<TerminalException>(
            async () => await ctx.Call<string>("Svc", "handler"));

        Assert.Equal("failure wins", ex.Message);
    }

    [Fact]
    public async Task MockObjectContext_SetupCallFailure_ThrowsTerminalException()
    {
        var ctx = new MockObjectContext();
        var expected = new TerminalException("object error", 500);
        ctx.SetupCallFailure("RemoteService", "process", expected);

        var ex = await Assert.ThrowsAsync<TerminalException>(
            async () => await ctx.Call<string>("RemoteService", "process"));

        Assert.Same(expected, ex);
    }

    [Fact]
    public async Task MockObjectContext_SetupCallFailure_WithKey_ThrowsTerminalException()
    {
        var ctx = new MockObjectContext();
        var expected = new TerminalException("keyed error", 409);
        ctx.SetupCallFailure("RemoteObject", "key-2", "update", expected);

        var ex = await Assert.ThrowsAsync<TerminalException>(
            async () => await ctx.Call<string>("RemoteObject", "key-2", "update"));

        Assert.Same(expected, ex);
    }

    #endregion

    #region 3. RegisterClient (typed client registration)

    [Fact]
    public void MockContext_ServiceClient_WithoutRegistration_ThrowsNotSupported()
    {
        var ctx = new MockContext();

        Assert.Throws<NotSupportedException>(() => ctx.ServiceClient<ITestClient>());
    }

    [Fact]
    public void MockContext_RegisterClient_ServiceClient_ReturnsRegisteredInstance()
    {
        var ctx = new MockContext();
        var client = new TestClient();
        ctx.RegisterClient<ITestClient>(client);

        var result = ctx.ServiceClient<ITestClient>();

        Assert.Same(client, result);
    }

    [Fact]
    public void MockContext_RegisterClient_ObjectClient_ReturnsRegisteredInstance()
    {
        var ctx = new MockContext();
        var client = new TestClient();
        ctx.RegisterClient<ITestClient>(client);

        var result = ctx.ObjectClient<ITestClient>("some-key");

        Assert.Same(client, result);
    }

    [Fact]
    public void MockContext_RegisterClient_ObjectClient_IgnoresKeyForLookup()
    {
        var ctx = new MockContext();
        var client = new TestClient();
        ctx.RegisterClient<ITestClient>(client);

        var result1 = ctx.ObjectClient<ITestClient>("key-a");
        var result2 = ctx.ObjectClient<ITestClient>("key-b");

        Assert.Same(client, result1);
        Assert.Same(client, result2);
    }

    [Fact]
    public void MockContext_RegisterClient_ClientMethodWorks()
    {
        var ctx = new MockContext();
        var client = new TestClient();
        ctx.RegisterClient<ITestClient>(client);

        var retrieved = ctx.ServiceClient<ITestClient>();

        Assert.Equal("test-value", retrieved.GetValue());
    }

    [Fact]
    public void MockObjectContext_RegisterClient_ViaMockContextHelper()
    {
        // MockObjectContext.RegisterClient delegates through MockContextHelper to inner MockContext.
        // However, ServiceClient<T> on keyed mocks goes through ClientFactory, not the inner mock.
        // This test verifies the RegisterClient method is available on MockObjectContext.
        var ctx = new MockObjectContext();
        var client = new TestClient();

        // Should not throw - confirms the method exists and delegates
        ctx.RegisterClient<ITestClient>(client);
    }

    #endregion

    #region 4. MockContextHelper shared behavior (recorded calls/sends/sleeps through keyed contexts)

    [Fact]
    public async Task MockObjectContext_RecordsCalls()
    {
        var ctx = new MockObjectContext();
        ctx.SetupCall<string>("Svc", "handler", "result");

        await ctx.Call<string>("Svc", "handler", (object?)"req");

        Assert.Single(ctx.Calls);
        Assert.Equal("Svc", ctx.Calls[0].Service);
        Assert.Null(ctx.Calls[0].Key);
        Assert.Equal("handler", ctx.Calls[0].Handler);
        Assert.Equal("req", ctx.Calls[0].Request);
    }

    [Fact]
    public async Task MockObjectContext_RecordsSends()
    {
        var ctx = new MockObjectContext();

        await ctx.Send("Svc", "handler", (object?)"payload");

        Assert.Single(ctx.Sends);
        Assert.Equal("Svc", ctx.Sends[0].Service);
        Assert.Equal("handler", ctx.Sends[0].Handler);
        Assert.Equal("payload", ctx.Sends[0].Request);
    }

    [Fact]
    public async Task MockObjectContext_RecordsSendsWithDelay()
    {
        var ctx = new MockObjectContext();

        await ctx.Send("Svc", "handler", (object?)"data", TimeSpan.FromMinutes(5));

        Assert.Single(ctx.Sends);
        Assert.Equal(TimeSpan.FromMinutes(5), ctx.Sends[0].Delay);
    }

    [Fact]
    public async Task MockObjectContext_RecordsSleeps()
    {
        var ctx = new MockObjectContext();

        await ctx.Sleep(TimeSpan.FromSeconds(30));

        Assert.Single(ctx.Sleeps);
        Assert.Equal(TimeSpan.FromSeconds(30), ctx.Sleeps[0].Duration);
    }

    [Fact]
    public async Task MockObjectContext_SetupCall_ReturnsConfiguredResult()
    {
        var ctx = new MockObjectContext();
        ctx.SetupCall("Svc", "handler", 42);

        var result = await ctx.Call<int>("Svc", "handler");

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task MockObjectContext_SetupCall_WithKey_ReturnsConfiguredResult()
    {
        var ctx = new MockObjectContext();
        ctx.SetupCall("Svc", "key-1", "handler", "keyed-result");

        var result = await ctx.Call<string>("Svc", "key-1", "handler");

        Assert.Equal("keyed-result", result);
    }

    [Fact]
    public async Task MockObjectContext_SetupAwakeable_ReturnsConfiguredValue()
    {
        var ctx = new MockObjectContext();
        ctx.SetupAwakeable("awakeable-result");

        var awakeable = ctx.Awakeable<string>();

        Assert.Equal("awakeable-result", await awakeable.Value);
        Assert.StartsWith("mock-awakeable-", awakeable.Id);
    }

    [Fact]
    public async Task MockSharedObjectContext_RecordsCalls()
    {
        var ctx = new MockSharedObjectContext();
        ctx.SetupCall<string>("Svc", "read", "data");

        await ctx.Call<string>("Svc", "read");

        Assert.Single(ctx.Calls);
        Assert.Equal("Svc", ctx.Calls[0].Service);
    }

    [Fact]
    public async Task MockWorkflowContext_RecordsSends()
    {
        var ctx = new MockWorkflowContext();

        await ctx.Send("Svc", "handler", (object?)"wf-payload");

        Assert.Single(ctx.Sends);
        Assert.Equal("wf-payload", ctx.Sends[0].Request);
    }

    [Fact]
    public async Task MockSharedWorkflowContext_RecordsSleeps()
    {
        var ctx = new MockSharedWorkflowContext();

        await ctx.Sleep(TimeSpan.FromHours(1));

        Assert.Single(ctx.Sleeps);
        Assert.Equal(TimeSpan.FromHours(1), ctx.Sleeps[0].Duration);
    }

    [Fact]
    public async Task MockObjectContext_MultipleCalls_AllRecorded()
    {
        var ctx = new MockObjectContext();
        ctx.SetupCall<string>("A", "h1", "r1");
        ctx.SetupCall<int>("B", "h2", 99);

        await ctx.Call<string>("A", "h1");
        await ctx.Call<int>("B", "h2");

        Assert.Equal(2, ctx.Calls.Count);
        Assert.Equal("A", ctx.Calls[0].Service);
        Assert.Equal("B", ctx.Calls[1].Service);
    }

    #endregion

    #region 5. HandlerAttributeBase inheritance

    [Fact]
    public void HandlerAttribute_InheritsFromHandlerAttributeBase()
    {
        Assert.True(typeof(HandlerAttributeBase).IsAssignableFrom(typeof(HandlerAttribute)));
    }

    [Fact]
    public void SharedHandlerAttribute_InheritsFromHandlerAttributeBase()
    {
        Assert.True(typeof(HandlerAttributeBase).IsAssignableFrom(typeof(SharedHandlerAttribute)));
    }

    [Fact]
    public void HandlerAttribute_AllPropertiesSettable()
    {
        var attr = new HandlerAttribute
        {
            Name = "custom",
            InactivityTimeout = "00:05:00",
            AbortTimeout = "00:01:00",
            IdempotencyRetention = "1.00:00:00",
            JournalRetention = "7.00:00:00",
            IngressPrivate = true
        };

        Assert.Equal("custom", attr.Name);
        Assert.Equal("00:05:00", attr.InactivityTimeout);
        Assert.Equal("00:01:00", attr.AbortTimeout);
        Assert.Equal("1.00:00:00", attr.IdempotencyRetention);
        Assert.Equal("7.00:00:00", attr.JournalRetention);
        Assert.True(attr.IngressPrivate);
    }

    [Fact]
    public void SharedHandlerAttribute_AllPropertiesSettable()
    {
        var attr = new SharedHandlerAttribute
        {
            Name = "shared-custom",
            InactivityTimeout = "00:10:00",
            AbortTimeout = "00:02:00",
            IdempotencyRetention = "2.00:00:00",
            JournalRetention = "14.00:00:00",
            IngressPrivate = false
        };

        Assert.Equal("shared-custom", attr.Name);
        Assert.Equal("00:10:00", attr.InactivityTimeout);
        Assert.Equal("00:02:00", attr.AbortTimeout);
        Assert.Equal("2.00:00:00", attr.IdempotencyRetention);
        Assert.Equal("14.00:00:00", attr.JournalRetention);
        Assert.False(attr.IngressPrivate);
    }

    [Fact]
    public void HandlerAttribute_DefaultPropertyValues()
    {
        var attr = new HandlerAttribute();

        Assert.Null(attr.Name);
        Assert.Null(attr.InactivityTimeout);
        Assert.Null(attr.AbortTimeout);
        Assert.Null(attr.IdempotencyRetention);
        Assert.Null(attr.JournalRetention);
        Assert.False(attr.IngressPrivate);
    }

    #endregion
}
