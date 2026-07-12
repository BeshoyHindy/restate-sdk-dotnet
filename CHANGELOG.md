# Changelog

All notable changes to the Restate .NET SDK will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0](https://github.com/BeshoyHindy/restate-sdk-dotnet/compare/v0.2.1...v0.3.0) (2026-07-12)


### Features

* Add multiple new features and samples ([38f7366](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/38f73663a570b83a331df4c83d5699ef32922d02))
* add NativeAotCounter sample and simplify AOT registration API ([a4cb19a](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/a4cb19a00067105f0621d30969b3c6246fdace3b))
* add NativeAotSaga sample demonstrating compensating transactions ([6a000ee](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/6a000eed8dcb0c4f849e54a52038455ee81dc630))
* AOT-safe ingress client and Any/AllSettled combinators ([#53](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/53)) ([dc8329f](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/dc8329f5e1df34b33e21215f9b5469017763e176))
* enable manual triggering of CI workflow with workflow_dispatch ([2deafc0](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/2deafc0e83d547e3fe1f7c563af60c1b5eec47ff))
* enhance CI workflow with integration tests and update .NET version ([4390280](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/4390280b86db78cf8c40329fefbd51d3f507ed33))
* Implement source-generated JSON serializer context and update DI registration methods ([ea4f080](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/ea4f0808f38cfa5af4816e3be066595e7349d0fe))
* metrics, span enrichment, and replay-aware context logger ([#55](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/55)) ([26623a1](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/26623a137acd71bcc8e20858612e985fdeb77e75))
* request identity verification (x-restate-signature v1) ([#54](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/54)) ([9dafff8](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/9dafff8fe036406280696752bce47d1a92a73532))
* Restate testcontainers integration harness ([#56](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/56)) ([7efac8c](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/7efac8c4243a9010d9a7fbd33257e864d35c03e3))
* service protocol v7, suspension, and replay-correctness fixes ([#58](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/58)) ([dee9fbc](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/dee9fbc5fae7454bc5ed64b018690044697fea55))


### Bug Fixes

* **client:** use Restate ingress send endpoint ([#64](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/64)) ([bcc8bda](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/bcc8bdab853c22b77811552a3ea7a645340a233f))
* cover invocation-abort cancellation in Any and AllSettled ([#59](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/59)) ([b80029a](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/b80029acfa28d773be46841a297a780d61cd8763))
* cross-cutting audit fixes and public API tracking ([#62](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/62)) ([86ee23a](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/86ee23a605d693cfc2a50d8c396157ffafcefda3))
* resolve JsonSerializer and AOT DI registration failures on .NET 10 ([c6d7d63](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/c6d7d63d8a45a83e081abe290982f316a2e39f59))

## [Unreleased]

## [0.2.1] - 2026-07-12

### Fixed

- `RestateClient.Send` now uses the ingress `/send` endpoint (including delayed sends) instead of
  an unsupported `x-restate-mode` header, validates returned invocation IDs, and supports explicit
  reflection JSON configuration through `RestateClientOptions`.

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
