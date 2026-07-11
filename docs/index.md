# Restate .NET SDK

Community .NET SDK for [Restate](https://restate.dev/) — a system for building resilient
applications using *distributed durable async/await*.

> **Pre-release** — under active development. APIs may change between releases.
> This is a community-driven project, not an official Restate SDK.
> For official SDKs, see [github.com/restatedev](https://github.com/restatedev).

## Packages

| Package | Description |
|---------|-------------|
| `Restate.Sdk` | Core SDK with bundled source generator |
| `Restate.Sdk.Testing` | Mock contexts for unit testing |
| `Restate.Sdk.Lambda` | AWS Lambda adapter |

## Contents

- [Getting Started](guide/getting-started.md) — install, quick start, hosting, error handling
- [Service Types](guide/service-types.md) — Service, Virtual Object, Workflow
- [Durable Building Blocks](guide/durable-building-blocks.md) — Run, Sleep, state, awakeables, calls
- [Testing](guide/testing.md) — mock contexts for unit tests
- [AWS Lambda](guide/lambda.md) — deploy handlers as Lambda functions
- [Native AOT](guide/native-aot.md) — ahead-of-time compiled deployments
- [API Reference](api/Restate.Sdk.yml) — generated from XML documentation

## Links

- [GitHub repository](https://github.com/BeshoyHindy/restate-sdk-dotnet)
- [Restate documentation](https://docs.restate.dev)
- [Samples](https://github.com/BeshoyHindy/restate-sdk-dotnet/tree/main/samples)
