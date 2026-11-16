# Test services for the Restate SDK test suite

Implements the service contracts of the Restate SDK conformance suite,
[`sdk-tests`](https://github.com/restatedev/e2e/tree/main/sdk-tests) in `restatedev/e2e`, which
the official SDKs run in CI. The suite starts this image once per test class and passes the
services that class needs in `SERVICES`; `PORT` and `E2E_REQUEST_SIGNING` are honoured too.

## Running locally

Requires Docker (or Podman) and a JVM >= 21.

1. Build the image from the repository root:

   ```bash
   docker build . -f test-services/Dockerfile -t localhost/restate-test-services-dotnet
   ```

2. Download `sdk-tests.jar` from the
   [e2e releases](https://github.com/restatedev/e2e/releases) and run it:

   ```bash
   java -jar sdk-tests.jar run --restate-container-image=ghcr.io/restatedev/restate:main --service-container-image=localhost/restate-test-services-dotnet --exclusions-file=test-services/exclusions.yaml
   ```

   Add `--test-suite=default --test-name=dev.restate.sdktesting.tests.State` to run a single
   class. With Podman, add `--sequential` and point `DOCKER_HOST` at the Podman socket.

   Each run writes a report directory with, per test class, the service and runtime logs and
   dumps of the runtime's invocation and journal tables.

## Debugging a single test

Run the service from your IDE (it listens on `PORT`, default 9080), then:

```bash
java -jar sdk-tests.jar debug --restate-container-image=ghcr.io/restatedev/restate:main --test-suite=default --test-name=dev.restate.sdktesting.tests.State default-service=9080
```

## Exclusions

`exclusions.yaml` lists the tests that exercise features the SDK does not have yet. Remove an
entry once the feature lands.
