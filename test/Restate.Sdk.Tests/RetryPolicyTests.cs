namespace Restate.Sdk.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        var policy = RetryPolicy.Default;

        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.InitialDelay);
        Assert.Equal(2.0, policy.ExponentiationFactor);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.MaxDelay);
        Assert.Null(policy.MaxAttempts);
        Assert.Null(policy.MaxDuration);
    }

    [Fact]
    public void None_HasZeroMaxAttempts()
    {
        var policy = RetryPolicy.None;

        Assert.Equal(0, policy.MaxAttempts);
    }

    [Fact]
    public void FixedAttempts_SetsMaxAttempts()
    {
        var policy = RetryPolicy.FixedAttempts(5);

        Assert.Equal(5, policy.MaxAttempts);
        Assert.Null(policy.MaxDuration);
    }

    [Fact]
    public void WithMaxDuration_SetsMaxDuration()
    {
        var policy = RetryPolicy.WithMaxDuration(TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(2), policy.MaxDuration);
        Assert.Null(policy.MaxAttempts);
    }

    [Fact]
    public void CustomInit_AllPropertiesSettable()
    {
        var policy = new RetryPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            ExponentiationFactor = 3.0,
            MaxDelay = TimeSpan.FromSeconds(30),
            MaxAttempts = 10,
            MaxDuration = TimeSpan.FromMinutes(5)
        };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.InitialDelay);
        Assert.Equal(3.0, policy.ExponentiationFactor);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.MaxDelay);
        Assert.Equal(10, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.MaxDuration);
    }

    [Fact]
    public void IsSealed()
    {
        Assert.True(typeof(RetryPolicy).IsSealed);
    }

    [Fact]
    public void IsRecord_SupportsValueEquality()
    {
        var a = RetryPolicy.FixedAttempts(3);
        var b = RetryPolicy.FixedAttempts(3);
        var c = RetryPolicy.FixedAttempts(5);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void WithSyntax_CreatesModifiedCopy()
    {
        var original = RetryPolicy.Default;
        var modified = original with { MaxAttempts = 3 };

        Assert.Null(original.MaxAttempts);
        Assert.Equal(3, modified.MaxAttempts);
    }
}
