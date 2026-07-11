# Native AOT

For ahead-of-time compiled deployments with minimal startup time and memory footprint, use
`BuildAot` with the source-generated registration:

```csharp
using Restate.Sdk.Hosting;

await RestateHost.CreateBuilder()
    .AddService<GreeterService>()
    .BuildAot()       // Slim Kestrel host, no reflection
    .RunAsync();
```

Publish as a self-contained NativeAOT binary:

```bash
dotnet publish -c Release -r linux-x64
```

The source generator emits `AddRestateGenerated()` which registers all service definitions
and JSON serializer contexts without reflection. See the
[NativeAotGreeter](https://github.com/BeshoyHindy/restate-sdk-dotnet/tree/main/samples/NativeAotGreeter)
sample for a complete working example.

> **Tip**: Set `<PublishAot>true</PublishAot>` in your `.csproj` to enable AOT compilation.
> The SDK's source generator handles all trimming and serialization concerns automatically.
