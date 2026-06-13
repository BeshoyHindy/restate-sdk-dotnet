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

    // ---- Internal GetDelay / ShouldRetry (plan 07 §1.3 hosting-client lane, lines 61-77) -------
    // These drive the backoff math the Run-retry loop consumes; the public property tests above
    // never call them, leaving the whole computation uncovered.

    [Fact]
    public void GetDelay_GrowsExponentially_FromInitialDelay()
    {
        var policy = new RetryPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(100),
            ExponentiationFactor = 2.0,
            MaxDelay = TimeSpan.FromSeconds(60)
        };

        // attempt 0 → 100 * 2^0, attempt 1 → 100 * 2^1, attempt 2 → 100 * 2^2.
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(400), policy.GetDelay(2));
    }

    [Fact]
    public void GetDelay_IsCappedAtMaxDelay()
    {
        var policy = new RetryPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(100),
            ExponentiationFactor = 10.0,
            MaxDelay = TimeSpan.FromMilliseconds(500)
        };

        // 100 * 10^3 = 100_000ms but the Math.Min cap clamps it to MaxDelay.
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.GetDelay(3));
    }

    [Fact]
    public void ShouldRetry_ReturnsTrue_WhenNoLimitsConfigured()
    {
        // RetryPolicy.Default has neither MaxAttempts nor MaxDuration → always retry.
        Assert.True(RetryPolicy.Default.ShouldRetry(1000, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ShouldRetry_StopsAtMaxAttempts()
    {
        var policy = RetryPolicy.FixedAttempts(3);

        Assert.True(policy.ShouldRetry(2, TimeSpan.Zero));
        Assert.False(policy.ShouldRetry(3, TimeSpan.Zero));
        Assert.False(policy.ShouldRetry(4, TimeSpan.Zero));
    }

    [Fact]
    public void ShouldRetry_StopsAtMaxDuration()
    {
        var policy = RetryPolicy.WithMaxDuration(TimeSpan.FromSeconds(10));

        Assert.True(policy.ShouldRetry(0, TimeSpan.FromSeconds(9)));
        Assert.False(policy.ShouldRetry(0, TimeSpan.FromSeconds(10)));
        Assert.False(policy.ShouldRetry(0, TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void ShouldRetry_None_NeverRetries()
    {
        // MaxAttempts == 0 means even attempt 0 is past the limit.
        Assert.False(RetryPolicy.None.ShouldRetry(0, TimeSpan.Zero));
    }
}
