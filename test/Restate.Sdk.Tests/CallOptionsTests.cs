namespace Restate.Sdk.Tests;

public class CallOptionsTests
{
    [Fact]
    public void Default_HasNullIdempotencyKey()
    {
        var options = new CallOptions();

        Assert.Null(options.IdempotencyKey);
    }

    [Fact]
    public void WithIdempotencyKey_SetsKey()
    {
        var options = CallOptions.WithIdempotencyKey("my-key-123");

        Assert.Equal("my-key-123", options.IdempotencyKey);
    }

    [Fact]
    public void IsValueType()
    {
        Assert.True(typeof(CallOptions).IsValueType);
    }

    [Fact]
    public void InitSyntax_Works()
    {
        var options = new CallOptions { IdempotencyKey = "init-key" };

        Assert.Equal("init-key", options.IdempotencyKey);
    }

    // G20 — custom command name on CallOptions.
    [Fact]
    public void Default_HasNullName()
    {
        Assert.Null(new CallOptions().Name);
    }

    [Fact]
    public void WithName_SetsName()
    {
        Assert.Equal("charge-card", CallOptions.WithName("charge-card").Name);
    }

    [Fact]
    public void WithHeaders_SetsHeaders()
    {
        var headers = new Dictionary<string, string> { ["x"] = "y" };
        Assert.Same(headers, CallOptions.WithHeaders(headers).Headers);
    }
}
