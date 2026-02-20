using NativeAotCounter;
using Restate.Sdk.Generated;
using Restate.Sdk.Hosting;

// NativeAOT-compatible Restate endpoint with a Virtual Object.
// Publish with: dotnet publish -c Release
await RestateHost
    .CreateBuilder()
    .WithPort(9086)
    .BuildAot(services => services.AddRestateGenerated(AppJsonContext.Default))
    .RunAsync();
