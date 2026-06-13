# Restate.Sdk.E2E — testcontainer replay suite

End-to-end tests that drive the `samples/ReplayLab` services through a **real `restate-server`
container** (`docker.io/restatedev/restate:1.4`), forcing genuine suspend/resume cycles and
asserting the durable post-conditions plus the `ExecutionProbe` attempt/run counters that prove the
faulty replay paths (B1–B10) actually ran.

## How it works

- The ReplayLab services are hosted **in-process** (`ReplayLabHost.Build(0)`) on an OS-assigned
  port, so the tests can read `ExecutionProbe`/`AwakeableMailbox` directly.
- A `restate-server` container is started with a **5s inactivity timeout** — the suspension forcer.
  Every ReplayLab handler sleeps 8s somewhere, so the server closes its input and the SDK suspends.
- The container's worker reaches the in-process endpoint via `host.docker.internal` (mapped to
  `host-gateway`), and the deployment is registered through the admin API.

## Running locally

```bash
dotnet test test/Restate.Sdk.E2E/Restate.Sdk.E2E.csproj -c Release
```

Tests **skip cleanly** (via `[DockerFact]`) when no Docker daemon is reachable. In CI Docker is
always present and `e2e.yml` fails the build if the suite skipped, so the skip never hides a
regression.

## Docker vs Podman / remote daemons

`host.docker.internal` + `host-gateway` requires Docker >= 20.10 (the default on GitHub runners).
On Podman or a remote daemon you may need to point Testcontainers at the right socket and host:

```bash
export DOCKER_HOST=unix:///run/user/$(id -u)/podman/podman.sock
export TESTCONTAINERS_HOST_OVERRIDE=host.containers.internal   # Podman's host-gateway alias
```

This is intentionally documented rather than engineered around — the fixture targets the
standard Docker behavior used in CI.
