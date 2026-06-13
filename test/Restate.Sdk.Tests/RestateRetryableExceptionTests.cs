namespace Restate.Sdk.Tests;

/// <summary>
///     Unit coverage for <see cref="RestateRetryableException" /> — the retryable counterpart to
///     <see cref="TerminalException" />. The message/inner-exception ctor and the Code/NextRetryDelay
///     accessors have no unit-coverage today (they surface only through a real runtime retry), so
///     both constructors and every property are asserted directly here.
/// </summary>
public class RestateRetryableExceptionTests
{
    [Fact]
    public void MessageCtor_DefaultsCode500_AndNullDelay()
    {
        var ex = new RestateRetryableException("transient");

        Assert.Equal("transient", ex.Message);
        Assert.Equal((ushort)500, ex.Code);
        Assert.Null(ex.NextRetryDelay);
    }

    [Fact]
    public void MessageCtor_CarriesCustomCodeAndNextRetryDelay()
    {
        var ex = new RestateRetryableException("throttled", 503, TimeSpan.FromSeconds(2));

        Assert.Equal((ushort)503, ex.Code);
        Assert.Equal(TimeSpan.FromSeconds(2), ex.NextRetryDelay);
    }

    [Fact]
    public void InnerExceptionCtor_PreservesInnerAndOverrides()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = new RestateRetryableException("wrapped", inner, 429, TimeSpan.FromMilliseconds(750));

        Assert.Equal("wrapped", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal((ushort)429, ex.Code);
        Assert.Equal(TimeSpan.FromMilliseconds(750), ex.NextRetryDelay);
    }

    [Fact]
    public void InnerExceptionCtor_Defaults()
    {
        var inner = new InvalidOperationException("x");
        var ex = new RestateRetryableException("wrapped", inner);

        Assert.Same(inner, ex.InnerException);
        Assert.Equal((ushort)500, ex.Code);
        Assert.Null(ex.NextRetryDelay);
    }
}
