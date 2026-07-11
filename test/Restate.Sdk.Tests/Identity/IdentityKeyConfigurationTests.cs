using Restate.Sdk.Hosting;
using Restate.Sdk.Tests.Endpoint;

namespace Restate.Sdk.Tests.Identity;

/// <summary>
///     Eager validation on the three <c>WithIdentityKeys</c> configuration surfaces:
///     malformed keys fail at the configuring call site (not later inside Build/AddRestate),
///     empty key sets fail loudly instead of silently disabling verification, and the Lambda
///     surface rejects late calls that the already-built verifier would silently ignore.
/// </summary>
public class IdentityKeyConfigurationTests
{
    private static readonly string ValidKey = IdentityTestHelpers.CreateKeyPair().SerializedKey;

    // ── RestateOptions ──

    [Fact]
    public void Options_WithIdentityKeys_ValidKey_IsStored()
    {
        var options = new RestateOptions().WithIdentityKeys(ValidKey);

        Assert.Equal([ValidKey], options.IdentityKeys);
    }

    [Fact]
    public void Options_WithIdentityKeys_MalformedKey_ThrowsEagerly()
    {
        var options = new RestateOptions();

        Assert.Throws<ArgumentException>(() => options.WithIdentityKeys("not-a-key"));
    }

    [Fact]
    public void Options_WithIdentityKeys_Empty_ThrowsInsteadOfSilentlyDisabling()
    {
        var options = new RestateOptions();

        Assert.Throws<ArgumentException>(() => options.WithIdentityKeys());
    }

    [Fact]
    public void Options_WithIdentityKeys_Null_Throws()
    {
        var options = new RestateOptions();

        Assert.Throws<ArgumentNullException>(() => options.WithIdentityKeys(null!));
    }

    // ── RestateHostBuilder ──

    [Fact]
    public void HostBuilder_WithIdentityKeys_MalformedKey_ThrowsEagerly()
    {
        var builder = RestateHost.CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.WithIdentityKeys("publickeyv1_0OIl"));
    }

    [Fact]
    public void HostBuilder_WithIdentityKeys_Empty_ThrowsInsteadOfSilentlyDisabling()
    {
        var builder = RestateHost.CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.WithIdentityKeys());
    }

    // ── RestateLambdaHandler ──

    [Fact]
    public void Lambda_WithIdentityKeys_FromRegister_Succeeds()
    {
        _ = new RegisterTimeKeysHandler();
    }

    [Fact]
    public void Lambda_WithIdentityKeys_MalformedKey_ThrowsEagerly()
    {
        Assert.Throws<ArgumentException>(() => new MalformedKeyHandler());
    }

    [Fact]
    public void Lambda_WithIdentityKeys_AfterConstruction_Throws()
    {
        // Calling WithIdentityKeys from a derived constructor body runs AFTER the base
        // constructor built the identity verifier; the keys would be silently ignored and
        // the endpoint would accept unsigned requests. It must throw, like UseLoggerFactory.
        Assert.Throws<InvalidOperationException>(() => new LateKeysHandler());
    }

    private sealed class RegisterTimeKeysHandler : RestateLambdaHandler
    {
        public override void Register()
        {
            Bind<GreeterService>();
            WithIdentityKeys(ValidKey);
        }
    }

    private sealed class MalformedKeyHandler : RestateLambdaHandler
    {
        public override void Register()
        {
            Bind<GreeterService>();
            WithIdentityKeys("not-a-key");
        }
    }

    private sealed class LateKeysHandler : RestateLambdaHandler
    {
        public LateKeysHandler()
        {
            WithIdentityKeys(ValidKey);
        }

        public override void Register()
        {
            Bind<GreeterService>();
        }
    }
}
