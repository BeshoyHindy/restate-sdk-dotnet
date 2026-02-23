using NativeAotGreeter;
using Restate.Sdk.Generated;
using Restate.Sdk.Hosting;

// NativeAOT-compatible Restate endpoint.
// Uses BuildAot() with source-generated AddRestateGenerated() to avoid reflection.
// Publish with: dotnet publish -c Release
await RestateHost
    .CreateBuilder()
    .WithPort(9085)
    .BuildAot(services => services.AddRestateGenerated(AppJsonContext.Default))
    .RunAsync();
