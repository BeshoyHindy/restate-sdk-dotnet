# Releasing

## Prerequisites

- `NUGET_API_KEY` repository secret configured
  (Settings > Secrets and variables > Actions > New repository secret)
- Get your API key from https://www.nuget.org/account/apikeys
  - Scopes: Push new packages and package versions
  - Glob pattern: `Restate.Sdk*`

## Release Steps

Releases are automated with [release-please](https://github.com/googleapis/release-please).

1. Merge PRs to `main` with conventional-commit titles (`feat:`, `fix:`, ...).
   The repo uses squash merges, so the PR title becomes the commit subject.
2. The `release-please.yml` workflow maintains a release PR that:
   - bumps `<Version>` in `Directory.Build.props` (the line is marked with
     `x-release-please-version`)
   - adds the pending changes to `CHANGELOG.md`
   - updates `.release-please-manifest.json`
3. Merge the release PR. release-please creates the `vX.Y.Z` tag and the
   tag triggers `publish.yml`.

> **Note:** tags created with the default `GITHUB_TOKEN` do not trigger other
> workflows. Configure a `RELEASE_PLEASE_TOKEN` PAT secret (contents: write,
> pull-requests: write) so the tag triggers `publish.yml`. Without it,
> re-push the tag from your machine after release-please creates it:
>
> ```bash
> git fetch --tags
> git push origin :refs/tags/vX.Y.Z
> git push origin vX.Y.Z
> ```

### Manual fallback

If release-please is unavailable:

1. Update the version in `Directory.Build.props` (keep the
   `<!-- x-release-please-version -->` marker on the line):

   ```xml
   <Version>0.2.0</Version> <!-- x-release-please-version -->
   ```

2. Update `CHANGELOG.md`: move items from `[Unreleased]` to a new version
   section. Update `.release-please-manifest.json` to the same version.

3. Commit and push:

   ```bash
   git commit -am "chore: release v0.2.0"
   git push
   ```

4. Tag and push:

   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```

### What publish.yml does

On a `v*` tag push, the `publish.yml` workflow will automatically:
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
