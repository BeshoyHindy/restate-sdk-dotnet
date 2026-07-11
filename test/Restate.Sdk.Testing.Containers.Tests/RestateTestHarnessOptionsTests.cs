namespace Restate.Sdk.Testing.Containers.Tests;

/// <summary>
///     Option validation happens in the setters, so misconfiguration fails at the assignment
///     site instead of surfacing obscurely from deep inside <c>StartAsync</c> (no Docker needed).
/// </summary>
public class RestateTestHarnessOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var options = new RestateTestHarnessOptions();

        Assert.Equal(RestateBuilder.RestateImage, options.Image);
        Assert.Equal(TimeSpan.FromMinutes(2), options.StartupTimeout);
    }

    [Fact]
    public void Image_ValidValue_IsStored()
    {
        var options = new RestateTestHarnessOptions { Image = "docker.restate.dev/restatedev/restate:1.5" };

        Assert.Equal("docker.restate.dev/restatedev/restate:1.5", options.Image);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Image_Empty_Throws(string image)
    {
        var options = new RestateTestHarnessOptions();

        Assert.Throws<ArgumentException>(() => options.Image = image);
    }

    [Fact]
    public void Image_Null_Throws()
    {
        var options = new RestateTestHarnessOptions();

        Assert.Throws<ArgumentNullException>(() => options.Image = null!);
    }

    [Fact]
    public void StartupTimeout_Positive_IsStored()
    {
        var options = new RestateTestHarnessOptions { StartupTimeout = TimeSpan.FromSeconds(30) };

        Assert.Equal(TimeSpan.FromSeconds(30), options.StartupTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void StartupTimeout_ZeroOrNegative_Throws(int seconds)
    {
        var options = new RestateTestHarnessOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.StartupTimeout = TimeSpan.FromSeconds(seconds));
    }
}
