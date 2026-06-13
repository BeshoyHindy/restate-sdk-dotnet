using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Tests.Protocol;

/// <summary>
///     Unit coverage for <see cref="ProtocolVersion" /> — the G12 service-protocol version negotiation
///     primitive mirroring shared-core <c>service_protocol/version.rs</c> + <c>VM::new</c>
///     (vm/mod.rs:214-261). Every branch of TryParse / IsSupported / SupportsErrorMetadata is exercised
///     directly so the negotiation logic is provable independent of the HTTP host wiring.
/// </summary>
public class ProtocolVersionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryParse_NullOrEmpty_ReturnsNull(string? contentType)
    {
        Assert.Null(ProtocolVersion.TryParse(contentType));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    // The prefix matches but the version token is not an integer → unrecognized.
    [InlineData("application/vnd.restate.invocation.vX")]
    public void TryParse_NotAnInvocationContentType_ReturnsNull(string contentType)
    {
        Assert.Null(ProtocolVersion.TryParse(contentType));
    }

    [Theory]
    [InlineData("application/vnd.restate.invocation.v5", 5)]
    [InlineData("application/vnd.restate.invocation.v6", 6)]
    // Versions outside [5,6] still parse to their number — IsSupported is the separate range check.
    [InlineData("application/vnd.restate.invocation.v4", 4)]
    [InlineData("application/vnd.restate.invocation.v99", 99)]
    // Parameters are stripped before the version token is read.
    [InlineData("application/vnd.restate.invocation.v6; charset=utf-8", 6)]
    public void TryParse_WellFormed_ReturnsVersionNumber(string contentType, int expected)
    {
        Assert.Equal(expected, ProtocolVersion.TryParse(contentType));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(4, false)]
    [InlineData(7, false)]
    [InlineData(1, false)]
    public void IsSupported_ChecksRangeInclusive(int version, bool expected)
    {
        Assert.Equal(expected, ProtocolVersion.IsSupported(version));
    }

    [Theory]
    // Terminal-error Failure.metadata is V6+ only (verify_error_metadata_feature_support).
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public void SupportsErrorMetadata_GatesOnV6(int version, bool expected)
    {
        Assert.Equal(expected, ProtocolVersion.SupportsErrorMetadata(version));
    }

    [Fact]
    public void ContentTypeFor_RendersInvocationContentType()
    {
        Assert.Equal("application/vnd.restate.invocation.v6", ProtocolVersion.ContentTypeFor(6));
        Assert.Equal("application/vnd.restate.invocation.v5", ProtocolVersion.ContentTypeFor(5));
    }

    [Fact]
    public void MinMax_MatchSharedCoreV5ToV6()
    {
        Assert.Equal(5, ProtocolVersion.MinimumSupported);
        Assert.Equal(6, ProtocolVersion.MaximumSupported);
    }
}
