using NativeAotSaga;
using Restate.Sdk.Generated;
using Restate.Sdk.Hosting;

// NativeAOT-compatible Restate endpoint demonstrating the Saga pattern.
// Publish with: dotnet publish -c Release
await RestateHost
    .CreateBuilder()
    .WithPort(9089)
    .BuildAot(services => services.AddRestateGenerated(AppJsonContext.Default))
    .RunAsync();
