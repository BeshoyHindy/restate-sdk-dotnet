# Changelog

All notable changes to the Restate .NET SDK will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.1](https://github.com/BeshoyHindy/restate-sdk-dotnet/compare/v0.4.0...v0.4.1) (2026-10-24)


### Bug Fixes

* pull the Restate image from docker.restate.dev ([#135](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/135)) ([7bfc3f3](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/7bfc3f33ac6752bf8f1c08b3cafb23d054cd9edf))

## [0.4.0](https://github.com/BeshoyHindy/restate-sdk-dotnet/compare/v0.3.1...v0.4.0) (2026-10-24)


### Features

* add workflow lookup and a scope-bound client to RestateClient ([#128](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/128)) ([1b79e77](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/1b79e77f2eac9ebc33599ff72998b73dacc36f38))
* add optional entry names to state operations ([#130](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/130)) ([6c52b19](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/6c52b19e40a8331cbe89171c15d2386d84c40de2))
* cancel invocations through the admin API ([#127](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/127)) ([9a9ef23](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/9a9ef23e8fe7ac6b14707bbb258430c028e386b3))
* unnamed signals return a handle carrying their index ([#129](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/129)) ([3164af4](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/3164af415936b1ca2dbfd2514a7a0b53870b36f7))


### Bug Fixes

* **generator:** report IRunContext handler parameters as their own error ([#126](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/126)) ([e549b88](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/e549b885fddcb60d0befc28175359e1362de1cc6))
* report the invocation id on call futures ([#125](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/125)) ([2cc87c6](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/2cc87c64e9add1a2cfc9df5bac3c111f735000cb))
* validate and record flow-control options in MockContext ([#124](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/124)) ([dd5f400](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/dd5f40083c93753d8d249fa0891e1e1561b0a6fa))

## [0.3.1](https://github.com/BeshoyHindy/restate-sdk-dotnet/compare/v0.3.0...v0.3.1) (2026-10-24)


### Bug Fixes

* compare command name, target and key when replaying ([#117](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/117)) ([1e9f5e6](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/1e9f5e699fe22b65635bba0c1c41a85db32b52eb))
* honour journaled completion order when combinators replay ([#116](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/116)) ([12c64e9](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/12c64e9dcf001720ef2751cbcb0a10a3d1bfe150))
* pin restate-server 1.8 and derive the harness image from docker-compose.yml ([#118](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/118)) ([a2ff7f2](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/a2ff7f201110c4ca1eaff98fa1221e7f0eb970f5))
* reject scope and limit key below protocol v7 ([#114](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/114)) ([e930c2a](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/e930c2a825fa957c1ecaa91c1f941e8b462f15b1))
* report journal mismatch as retryable error 570 ([#115](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/115)) ([1c754df](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/1c754df2f671d4321270c557d5119c3fcdf52370))
* seed durable random from the invocation id on protocol v5 ([#113](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/113)) ([9185888](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/91858889b0e4ce7eda004fe5e054c2e24270562f))
* use the canonical ingress routes and escape path segments in the ingress client ([#112](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/112)) ([d3ef311](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/d3ef311530c951ff7038326ae04d37157fdadff0))

## [0.3.0](https://github.com/BeshoyHindy/restate-sdk-dotnet/compare/v0.2.1...v0.3.0) (2026-09-15)


### Features

* awaitable signals on the handler context ([#97](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/97)) ([a9d0895](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/a9d08955960e12972321ff74c33365b1f83467f5))
* resolve and reject signals through the invocation handle ([#98](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/98)) ([b9ccc0e](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/b9ccc0e4e0e48e8e98d27a3486727e654fc79c89))
* scope and limit key for calls, sends, and the ingress client ([#96](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/96)) ([2ce90b1](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/2ce90b1031e6096c1d60f0d7c306523ef7bca309))
* typed ingress exception carrying the Restate error source and code ([#95](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/95)) ([bf2cfe5](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/bf2cfe5a520c5624fd9d68cbae905c78b06c07d9))


### Bug Fixes

* fail with a journal mismatch error instead of hanging on non-deterministic replay ([#93](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/93)) ([20beb57](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/20beb57dc32b062b5d15604f2e8046421c37af24))
* **generator:** recognise interface context types and case-insensitive run handler ([#87](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/87)) ([e8e1e29](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/e8e1e293a9100bfd5b8562db340bd7b412e9376e))
* **generator:** reject context parameters the invoker cannot supply ([#100](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/100)) ([78e5d5b](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/78e5d5b666193b4a89f8a5692bfda6c4852f739e))
* honour requested manifest version when Accept is */* ([#86](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/86)) ([8d130e1](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/8d130e187baa107889b6ab13af2ea57ebaf28e20))
* honour RetryPolicy when a Run is re-executed after replay ([#83](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/83)) ([d01e9c1](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/d01e9c14ea2768ed246782da11d1b3df02370895))
* include PathBase in request identity audience ([#85](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/85)) ([3620b18](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/3620b18af0cbc471a1af749e5af051f76da9ad32))
* make lazy durable futures safe to await more than once ([#94](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/94)) ([b448355](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/b448355e711a7b6401f11d61816162310f4a5dbb))
* propose failure for the replayed run when retries are exhausted ([#99](https://github.com/BeshoyHindy/restate-sdk-dotnet/issues/99)) ([00c01cb](https://github.com/BeshoyHindy/restate-sdk-dotnet/commit/00c01cbe1c0aa4b43147bbaa8944a24b2bb82ed7))

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
