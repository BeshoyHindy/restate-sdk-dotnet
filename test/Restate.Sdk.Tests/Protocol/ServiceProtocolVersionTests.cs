using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Tests.Protocol;

public class ServiceProtocolVersionTests
{
    [Theory]
    [InlineData("application/vnd.restate.invocation.v5", 5)]
    [InlineData("application/vnd.restate.invocation.v6", 6)]
    [InlineData("application/vnd.restate.invocation.v7", 7)]
    public void TryParse_SupportedVersions_Succeeds(string contentType, int expected)
    {
        Assert.True(ServiceProtocolVersionExtensions.TryParse(contentType, out var version));
        Assert.Equal((ServiceProtocolVersion)expected, version);
    }

    [Theory]
    [InlineData(" application/vnd.restate.invocation.v7 ")]
    [InlineData("application/vnd.restate.invocation.v7; charset=utf-8")]
    [InlineData("application/vnd.restate.invocation.v7 ; charset=utf-8")]
    public void TryParse_IgnoresWhitespaceAndParameters(string contentType)
    {
        Assert.True(ServiceProtocolVersionExtensions.TryParse(contentType, out var version));
        Assert.Equal(ServiceProtocolVersion.V7, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application/json")]
    [InlineData("application/vnd.restate.invocation.v4")]
    [InlineData("application/vnd.restate.invocation.v8")]
    [InlineData("application/vnd.restate.invocation.v")]
    [InlineData("application/vnd.restate.invocation.v77")]
    [InlineData("application/vnd.restate.endpointmanifest.v3+json")]
    public void TryParse_UnsupportedContentTypes_Fails(string? contentType)
    {
        Assert.False(ServiceProtocolVersionExtensions.TryParse(contentType, out _));
    }

    [Theory]
    [InlineData(5, "application/vnd.restate.invocation.v5")]
    [InlineData(6, "application/vnd.restate.invocation.v6")]
    [InlineData(7, "application/vnd.restate.invocation.v7")]
    public void ToContentType_RoundTrips(int versionNumber, string expected)
    {
        var version = (ServiceProtocolVersion)versionNumber;
        var contentType = version.ToContentType();
        Assert.Equal(expected, contentType);
        Assert.True(ServiceProtocolVersionExtensions.TryParse(contentType, out var parsed));
        Assert.Equal(version, parsed);
    }
}
