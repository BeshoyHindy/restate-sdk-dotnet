# Changelog

All notable changes to the Restate .NET SDK will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-07-11

### Added

- Service protocol V7 support, negotiated per request from the `Content-Type`; V5 remains the
  minimum. Unsupported versions are rejected with 415, and the response content-type now echoes
  the negotiated version instead of a hardcoded v6.
- Suspension: invocations blocked on durable results (sleeps, awakeables, calls, promises) emit
  a `SuspensionMessage` and release the connection when the input stream closes, instead of
  holding the HTTP/2 stream open. Restate re-invokes with the longer journal on completion.
- Request identity verification: configure `publickeyv1_...` keys via `WithIdentityKeys(...)`
  (host builder, `RestateOptions`, or the Lambda handler) to require a valid `x-restate-jwt-v1`
  Ed25519 signature on `/invoke` and `/discover`; unsigned or invalid requests get 401.
  Implemented with a vendored, verify-only Ed25519 — no new dependencies, Native AOT-safe.
- Eager state: state known from the invocation start message is journaled with eager-state
  commands instead of lazy server round-trips.
- Discovery manifest v4 negotiation.
- AOT-safe ingress client: `JsonTypeInfo<T>` overloads across `RestateClient` so Native AOT
  applications can call Restate services without reflection.
- `Any` and `AllSettled` combinators on `Context`, with `SettledResult<T>`
  (adopted from a community contribution by @stevefan1999-personal).
- New package `Restate.Sdk.Testing.Containers`: a testcontainers harness
  (`RestateTestHarness`, `RestateBuilder`/`RestateContainer`) for integration-testing services
  against a real Restate server in Docker.
- Observability: a `Restate.Sdk` meter (invocation counter, duration histogram,
  replayed-commands histogram), enriched invocation spans, opt-in per-operation activities via
  `RestateTelemetryOptions` on all hosting paths, and a replay-aware `ctx.Logger`.
- Public API surface tracked with `Microsoft.CodeAnalysis.PublicApiAnalyzers` across all
  shipped packages.

### Fixed

- Journals containing notifications (produced by suspension/resume) now replay correctly: the
  replay boundary counted only commands, and replayed results were read from command payloads
  instead of completion notifications.
- Replayed `ctx.CancelInvocation` entries no longer fail the invocation on resume.
- `Run` closures whose completion was never journaled are re-executed on replay instead of
  waiting forever.
- Awakeable signal indices no longer collide with the protocol's reserved built-in range, so a
  runtime cancellation can no longer resolve a user awakeable.
- Pending durable waits are failed when the connection dies abnormally (server restart,
  malformed frame), so handlers unwind instead of leaking parked invocations.
- `Any`/`AllSettled`/`WaitAll` propagate suspension instead of recording phantom failures, and
  treat canceled futures as failures rather than hanging.
- Cancelled invocation attempts end with a terminal error frame, keeping Lambda responses
  protocol-valid.
- Partial-state invocations no longer return wrong `Get` results after a `Set` on another key.
- The ingress client no longer throws on first use when reflection serialization is active.
- Identity keys are validated eagerly at configuration time, and configuring keys after Lambda
  handler construction now throws instead of being silently ignored.
- NativeAotCounter and NativeAotSaga sample ports no longer collide with Saga and FanOut.

### Changed

- Invocation hot path allocates less: cached loggers and handler display names, no tracing
  setup when no listener is attached, single-pass JWT decoding.
- Dependency floor: Amazon.Lambda.Core 3.1.1, Amazon.Lambda.APIGatewayEvents 3.0.0,
  Google.Protobuf 3.35, Grpc.Tools 2.81; CI runs against restate-server 1.7.

## [0.1.0-alpha.5] - 2026-02-23

### Changed

- `BuildAwakeableId` rewritten with `System.Buffers.Text.Base64Url` — eliminates 3-4 intermediate string allocations per awakeable
- Serialization hot path (`Run`, `SetState`, `Call`) uses `ArrayPool<byte>` rentals via `CopyToPooled` instead of `.ToArray()` — reduces GC pressure for typical 50-500 byte payloads
- Replay journal entries use `RawMessage.DetachPayload()` ownership transfer — eliminates 1 `byte[]` copy per replayed entry
- `CompletionManager` uses `CompletionSlot` discriminated union struct instead of `ConcurrentDictionary<int, object>` — eliminates boxing of `CompletionResult` on early completions
- `ProtocolReader` single-segment fast paths for header parsing and payload copy — avoids `stackalloc` + `ReadOnlySequence.CopyTo` overhead on the common Kestrel path
- `NegotiateVersion` uses explicit version substring checks instead of loop iteration

### Fixed

- NativeAotCounter sample port 9088 → 9086 to match CI integration test expectations
- NativeAotSaga sample port 9089 → 9087 to match CI integration test expectations

## [0.1.0-alpha.4] - 2026-02-23

### Added

- Retry policy support for `ctx.Run()` side effects with configurable exponential backoff (`RetryPolicy`)
- Idempotency key support via `CallOptions` for service-to-service calls
- `CancelInvocation(string invocationId)` API for cancelling running invocations
- Native AOT support: `BuildAot()`, `AddRestateAot()`, `RegistrationEmitter` source generator
- 5 new samples: FanOut, Saga, NativeAotGreeter, NativeAotCounter, NativeAotSaga
- Mock context support for RetryPolicy, CallOptions, and CancelInvocation in `Restate.Sdk.Testing`
- Interface conformance tests for new API surface

### Fixed

- `ProtocolReader` double-complete on dispose — idempotent guard prevents crash (contributed by @stevefan1999-personal)
- `ProtocolWriter` double-complete on dispose — same idempotent guard pattern
- Journal index misalignment after retry exhaustion when handler continues (saga compensation pattern)
- Sample port conflicts — all 9 samples now have unique ports (9080–9089)
- Solution file updated to include all new sample projects

## [0.1.0-alpha.3] - 2026-02-12

### Fixed

- Lambda handler API: use generic `Bind<T>()` instead of `Action<Type>` for type-safe handler registration

## [0.1.0-alpha.2] - 2026-02-11

### Fixed

- NuGet packages missing README on nuget.org — `PackageReadmeFile` condition blocked by MSBuild import order, and `PackagePath` used backslash instead of forward slash

## [0.1.0-alpha.1] - 2026-02-11

### Added

- Core SDK (`Restate.Sdk`) with full context hierarchy: `Context`, `ObjectContext`, `SharedObjectContext`, `WorkflowContext`, `SharedWorkflowContext`
- Context interfaces: `IContext`, `ISharedObjectContext`, `IObjectContext`, `ISharedWorkflowContext`, `IWorkflowContext` for utility methods, type constraints, and documentation
- Service types: `[Service]`, `[VirtualObject]`, `[Workflow]`
- Handler attributes: `[Handler]`, `[SharedHandler]` with handler-level configuration via `HandlerAttributeBase`
- Durable execution primitives: `Run`, `RunAsync`, `Sleep`, `After`
- Service-to-service communication: `Call`, `CallFuture`, `Send` with typed clients
- State management: `Get`, `Set`, `Clear`, `ClearAll`, `StateKeys` via `StateKey<T>`
- Awakeable support: `Awakeable`, `ResolveAwakeable`, `RejectAwakeable`
- Workflow promises: `Promise`, `PeekPromise`, `ResolvePromise`, `RejectPromise`
- Invocation tracking: `Attach`, `GetInvocationOutput`
- Durable future combinators: `IDurableFuture<T>`, `All`, `Race`, `WaitAll`
- Deterministic utilities: `DurableRandom`, `Now()`
- Replay-aware logging: `DurableConsole` with `ReplayAwareInterpolatedStringHandler`
- Ingress client (`RestateClient`) for external service invocation
- Source generator (`Restate.Sdk.Generators`) bundled into Restate.Sdk NuGet package (no separate install needed)
- Source generator diagnostics: RESTATE001-009, including compile-time TimeSpan validation (RESTATE009)
- Source generator hardening: `#pragma warning disable` on generated code, null checks in deserializers
- Testing package (`Restate.Sdk.Testing`) with `MockContext`, `MockObjectContext`, `MockWorkflowContext`, `MockSharedObjectContext`, `MockSharedWorkflowContext`
- Testing features: deterministic `CurrentTime`, `SetupCallFailure()`, `RegisterClient<T>()`, call/send/sleep recording
- AWS Lambda adapter (`Restate.Sdk.Lambda`) with `RestateLambdaHandler`
- ASP.NET Core integration: `AddRestate()` / `MapRestate()` DI extensions
- Quick-start host: `RestateHost.CreateBuilder()`
- Native AOT and trimming compatibility (`IsAotCompatible`, `IsTrimmable`)
- Per-package NuGet descriptions for Testing and Lambda packages
- Pack verification in CI pipeline (validates generator bundling on every PR)
- Version validation in publish workflow (tag must match Directory.Build.props)
- GitHub Release creation on tag push
- Release process guide (RELEASING.md)
- 4 sample applications: Greeter, Counter, TicketReservation, SignupWorkflow
- BenchmarkDotNet microbenchmarks for protocol layer and serialization
- Community files: CONTRIBUTING.md, DECISION_LOG.md, issue templates, CodeQL, Dependabot

### Fixed

- Protocol layer hang caused by incorrect `PipeReader.AdvanceTo(consumed, buffer.End)` — fixed to `AdvanceTo(consumed)`
- `ProposeRunCompletion` encoding: raw bytes instead of nested Value
- `CallCompletion` / `CallInvocationIdCompletion` IDs were swapped (0x800D/0x800E)
- PipeReader cleanup race condition in InvocationHandler (await incoming task before completing reader)

### Removed

- `RunOptions` struct (was empty, never used by the protocol layer)
