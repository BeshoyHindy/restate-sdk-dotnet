# Contributing to Restate .NET SDK

Thank you for your interest in contributing! This is a community-driven project and all contributions are welcome.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later
- [Restate Server](https://restate.dev/get-restate/) (for end-to-end testing)

## Building

```bash
dotnet build
```

## Running Tests

```bash
# All tests
dotnet test

# Specific test project
dotnet test test/Restate.Sdk.Tests
dotnet test test/Restate.Sdk.Generators.Tests

# Specific test
dotnet test --filter "FullyQualifiedName~ProtocolIntegrationTests"
```

## Code Formatting

The CI pipeline enforces consistent formatting. Check locally:

```bash
dotnet format --verify-no-changes
```

Fix formatting issues:

```bash
dotnet format
```

## Project Structure

```
src/
├── Restate.Sdk/              Core SDK (context hierarchy, protocol, hosting)
├── Restate.Sdk.Generators/   Roslyn source generator (netstandard2.0)
├── Restate.Sdk.Testing/      Mock contexts for unit testing
└── Restate.Sdk.Lambda/       AWS Lambda adapter
test/
├── Restate.Sdk.Tests/        Core SDK tests
├── Restate.Sdk.Generators.Tests/  Generator tests
└── Restate.Sdk.Benchmarks/   BenchmarkDotNet microbenchmarks
samples/                       Working sample applications
```

## Branching and Releases

The repository uses trunk-based development (GitHub Flow):

- `main` is protected; every change lands through a pull request that passes the
  required checks (`Build & Test`, `Format Check`, CodeQL).
- PRs are squash-merged, so the PR title becomes the commit subject. Titles must
  follow [Conventional Commits](https://www.conventionalcommits.org/):
  `feat:`, `fix:`, `docs:`, `perf:`, `refactor:`, `test:`, `build:`, `ci:`, `chore:`.
  A lint check on the PR title enforces this.
- Releases are cut by release-please: it collects merged commits into a release PR
  that updates `CHANGELOG.md` and the version in `Directory.Build.props`; merging that
  PR tags the release and CI publishes the packages to NuGet. See `RELEASING.md`.

## Pull Request Process

1. Fork the repository and create a feature branch from `main`
2. Make your changes with tests
3. Run `dotnet build && dotnet test` to verify
4. Run `dotnet format` to fix formatting
5. Submit a PR with a conventional-commit title and a clear description of the change

## Coding Standards

- Follow existing code patterns and naming conventions
- Add XML doc comments for public API surface
- Use `file` access modifier for test-only types
- Keep the public API minimal — use `internal` by default
- Source generator changes require `dotnet clean` + rebuild (stale artifacts)

## Reporting Issues

- Use [GitHub Issues](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues) for bugs and feature requests
- Include Restate server version, .NET SDK version, and reproduction steps for bugs
