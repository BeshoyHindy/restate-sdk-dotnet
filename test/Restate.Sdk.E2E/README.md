# Restate.Sdk.E2E — testcontainer replay suite

End-to-end tests that drive the `samples/ReplayLab` services through a **real `restate-server`
container** (`docker.io/restatedev/restate:1.4`), forcing genuine suspend/resume cycles and
asserting the durable post-conditions plus the `ExecutionProbe` attempt/run counters that prove the
faulty replay paths (B1–B10) actually ran.

Two further scenarios (`NewFeaturesE2eTests`) exercise the SDK's newer features against the same
real server:

- **E9 — implicit child cancellation.** A parent spawns request/response child Calls and parks on
  their results; the test cancels the parent through the **admin** API
  (`PATCH /invocations/{id}/cancel` — the ingress port only exposes `/output` and `/attach`) WHILE the
  parent is still parked on its first attempt (within the 5s inactivity window), so the inbound CANCEL
  is processed against the live, fully-tracked Processing state — both children tracked AND their
  invocation-ids already resolved, the precondition for the SDK's child-cancel fan-out (it cancels
  only already-resolved children on the unwinding terminal path). The SDK then emits one cancel
  SendSignal per child and the server routes it. The discriminator is read on the child side via
  `ChildCancelProbe`: both children reach `cancelled:{i}`, never `completed:{i}`. A regression that
  dropped the child-cancel would leave the children sleeping (10 min) and the test times out.
- **E10 — named (string-keyed) signals.** A handler parks on `ctx.NamedSignal<string>("decision")`
  and resumes with the sender-supplied value only when another invocation sends a matching named
  signal via `ctx.SendSignal`. A regressed feature never completes the await, so the bounded
  scenario timeout turns the hang into a fast failure.

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
