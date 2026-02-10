# Releasing

## Prerequisites

- `NUGET_API_KEY` repository secret configured
  (Settings > Secrets and variables > Actions > New repository secret)
- Get your API key from https://www.nuget.org/account/apikeys
  - Scopes: Push new packages and package versions
  - Glob pattern: `Restate.Sdk*`

## Release Steps

1. Update the version in `Directory.Build.props`:

   ```xml
   <Version>0.2.0</Version>
   ```

2. Update `CHANGELOG.md`: move items from `[Unreleased]` to a new version section.

3. Commit and push:

   ```bash
   git commit -am "Release v0.2.0"
   git push
   ```

4. Tag and push:

   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```

5. The `publish.yml` workflow will automatically:
   - Validate the tag version matches `Directory.Build.props`
   - Build, test, and pack
   - Verify the source generator is bundled in the package
   - Push packages to NuGet.org (Restate.Sdk, Restate.Sdk.Testing, Restate.Sdk.Lambda)
   - Create a GitHub Release with artifacts

## Published Packages

| Package | Description |
|---------|-------------|
| `Restate.Sdk` | Core SDK with bundled source generator |
| `Restate.Sdk.Testing` | Mock contexts for unit testing |
| `Restate.Sdk.Lambda` | AWS Lambda adapter |

## Package ID Reservation

Reserve package names on NuGet.org to prevent squatting:

1. Go to https://www.nuget.org/account/manage
2. Search for your packages and ensure you own the IDs
3. Consider enabling package ID prefix reservation if available

## Versioning

This project follows [Semantic Versioning](https://semver.org/):

- **Pre-release:** `0.x.y-alpha.z` -- APIs may change between releases
- **Stable:** `1.0.0+` -- backwards-compatible API guaranteed within major versions
