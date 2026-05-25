using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Restate.Sdk.Identity;

/// <summary>
///     Registration extensions enabling Restate request-identity verification.
/// </summary>
public static class RestateIdentityServiceCollectionExtensions
{
    /// <summary>
    ///     Enables request-identity verification, rejecting any discovery or invocation request that is
    ///     not signed by one of the given Restate runtime public keys
    ///     (<c>publickeyv1_&lt;base58&gt;</c>). Replaces the default pass-through verifier.
    /// </summary>
    /// <example>
    ///     <code>
    /// builder.Services.AddRestate(o =&gt; o.AddService&lt;Greeter&gt;())
    ///                 .AddRestateRequestIdentity("publickeyv1_w7YHemBVpQTGE...");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException">No keys were provided, or a key is malformed.</exception>
    public static IServiceCollection AddRestateRequestIdentity(
        this IServiceCollection services, params string[] publicKeys)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (publicKeys is null || publicKeys.Length == 0)
            throw new ArgumentException("At least one public key is required.", nameof(publicKeys));

        var verifier = new Ed25519RequestIdentityVerifier(publicKeys);
        services.RemoveAll<IRequestIdentityVerifier>();
        services.AddSingleton<IRequestIdentityVerifier>(verifier);
        return services;
    }
}
