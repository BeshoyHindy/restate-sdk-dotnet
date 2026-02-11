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
}
