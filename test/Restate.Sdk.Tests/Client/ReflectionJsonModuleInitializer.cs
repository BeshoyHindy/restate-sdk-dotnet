using System.Runtime.CompilerServices;

namespace Restate.Sdk.Tests.Client;

/// <summary>
///     The referenced <c>Restate.Sdk</c> assembly is <c>IsAotCompatible</c>/<c>IsTrimmable</c>,
///     which flows the <c>System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault</c> feature
///     switch into the VSTest test host. <c>RestateClient</c> serializes ingress payloads with the
///     reflection-based JSON resolver, so we re-enable the STJ reflection fallback for the test
///     assembly via a <see cref="ModuleInitializerAttribute" /> that runs at assembly load —
///     strictly before any test method and before STJ first reads the switch. This only ADDS a
///     fallback (matching how the §2 E2E suite runs RestateClient in a reflection-enabled host), so
///     no existing test's behavior regresses.
/// </summary>
internal static class ReflectionJsonModuleInitializer
{
    [ModuleInitializer]
    internal static void Enable()
    {
        AppContext.SetSwitch(
            "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);
    }
}
