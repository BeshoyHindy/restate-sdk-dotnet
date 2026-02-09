# Design Decision Log

This document records key design decisions for the Restate .NET SDK, including context,
alternatives considered, and rationale.

## 1. Abstract Classes over Interfaces for Handler Parameters

**Decision:** Handlers accept abstract class parameters (`Context`, `ObjectContext`, etc.) rather than interfaces.

**Context:** The Java and Go SDKs use interfaces for their context types. We initially considered the same approach for .NET.

**Rationale:**
- Abstract classes allow adding new methods without breaking existing implementations (interfaces require default interface methods, which have limitations in .NET)
- The `HttpContext` pattern from ASP.NET Core uses abstract classes for the same reason
- Mock contexts can subclass the abstract types directly
- Interfaces are still extracted (`IContext`, `IObjectContext`, etc.) for utility methods, type constraints, and documentation

**Alternatives:**
- Pure interfaces: rejected because adding methods would break user implementations
- Default interface methods: considered, but limited tooling support and cannot hold state

## 2. Interface Hierarchy (Additive, Non-Breaking)

**Decision:** Extract interfaces mirroring the abstract class hierarchy: `IContext` > `ISharedObjectContext` > `IObjectContext` / `ISharedWorkflowContext` > `IWorkflowContext`.

**Context:** Interfaces are valuable for utility methods (`void DoWork(IContext ctx)`), generic constraints, and API documentation, even though handlers use abstract class parameters.

**Rationale:**
- Enables Moq/NSubstitute mocking for helper methods and utilities
- Provides clean API documentation surface
- `IWorkflowContext` extends both `IObjectContext` and `ISharedWorkflowContext` via multiple interface inheritance
- Interfaces expose only durable execution primitives; implementation details (DurableRandom, DurableConsole, typed client methods) stay on the abstract class

## 3. BaseContext Visibility: `internal` Instead of `protected`

**Decision:** Changed `SharedObjectContext.BaseContext` from `protected Context?` to `internal Context`.

**Context:** The keyed context classes (ObjectContext, WorkflowContext, etc.) delegate all base operations to an inner `Context` instance. This property was originally `protected`, leaking implementation detail to external subclassers.

**Rationale:**
- `internal` hides the delegation pattern from the public API
- Mock contexts in `Restate.Sdk.Testing` still access it via `InternalsVisibleTo`
- Eliminates the nullable reference pattern (`BaseContext!`) by using `= null!` initializer
- Simpler than a `ContextOperations` helper class, which wouldn't reduce override declarations

## 4. Composition for Mock Context Deduplication

**Decision:** Use `MockContextHelper` (composition) rather than a shared base class for mock context deduplication.

**Context:** Four keyed mock contexts (MockObjectContext, MockSharedObjectContext, MockWorkflowContext, MockSharedWorkflowContext) had ~320 lines of duplicated delegation code.

**Rationale:**
- Mock contexts already inherit from abstract context classes (e.g., `MockObjectContext : ObjectContext`), so C# single inheritance prevents a shared mock base class
- `MockContextHelper` is an internal composition class that holds the inner `MockContext` and exposes shared setup methods
- Each keyed mock delegates to `_helper` for calls/sends/sleeps and to specialized stores for state/promises
- Reduced ~320 lines of duplication while maintaining the same public API

## 5. HandlerAttributeBase Extraction

**Decision:** Extract common properties from `[Handler]` and `[SharedHandler]` into abstract `HandlerAttributeBase`.

**Context:** Both handler attributes had 6 identical properties (Name, InactivityTimeout, AbortTimeout, IdempotencyRetention, JournalRetention, IngressPrivate).

**Rationale:**
- Eliminates property duplication — new handler options only need to be added once
- Source generator is unaffected: it matches concrete type names (`HandlerAttribute`, `SharedHandlerAttribute`) and reads properties via `NamedArguments`, which works with inherited properties
- Named `HandlerAttributeBase` (not `HandlerAttributeAttribute`) because it's a base for attributes, not an attribute itself

## 6. RunOptions Removal

**Decision:** Delete the `RunOptions` struct and remove the parameter from `Run()` overloads.

**Context:** `RunOptions` was an empty `readonly record struct` with zero properties. Investigation confirmed that `DefaultContext.Run()` completely ignores the options parameter — `InvocationStateMachine.WriteRunCommand()` accepts only a name string.

**Rationale:**
- Dead code in a pre-1.0 alpha — removing is better than keeping a misleading empty type
- If retry/retention options are needed later, they can be re-added when protocol support is wired through
- Clean API is more important than speculative future compatibility

## 7. Source Generator Hardening

**Decision:** Add `#pragma warning disable` to generated files, null checks in deserializers, and compile-time TimeSpan validation (RESTATE009).

**Rationale:**
- `#pragma warning disable` prevents user analyzer configurations from producing spurious warnings on generated code
- Null checks in deserializer lambdas provide clear error messages instead of NullReferenceExceptions
- RESTATE009 catches invalid TimeSpan strings (e.g., `[Handler(InactivityTimeout = "invalid")]`) at compile time rather than runtime

## 8. Deterministic Time in Mock Contexts

**Decision:** `MockContext.Now()` returns a configurable `CurrentTime` property (default: 2024-01-01T00:00:00Z) instead of `DateTimeOffset.UtcNow`.

**Rationale:**
- Tests that depend on time should be deterministic
- Users can set `ctx.CurrentTime = ...` to simulate specific timestamps
- Default is a fixed date to make test output predictable

## 9. .NET 10.0 Target Framework

**Decision:** Target `net10.0` exclusively (no multi-targeting).

**Context:** The SDK uses C# 14 features and .NET 10 APIs. Multi-targeting older frameworks would require conditional compilation and feature polyfills.

**Rationale:**
- Restate is a modern infrastructure platform — users are expected to use current .NET versions
- Single target simplifies the build, eliminates `#if` directives, and allows using the latest APIs
- .NET 10 is the current LTS release

## 10. Typed Client Registration for Testing

**Decision:** Add `RegisterClient<TClient>()` to mock contexts instead of auto-generating mock clients.

**Rationale:**
- Source-generated typed clients depend on internal context wiring that doesn't exist in mocks
- `RegisterClient<TClient>()` lets users provide hand-crafted or Moq-based client instances
- Without registration, client methods throw `NotSupportedException` with a helpful message
- Simple and explicit — no magic or auto-generation needed

## Comparison with Official SDKs

| Feature | Java | TypeScript | Go | Rust | Python | **.NET (this)** |
|---------|------|------------|-----|------|--------|-----------------|
| Context types | Interfaces | Classes | Interfaces | Traits | Classes | **Abstract classes + interfaces** |
| Code generation | Annotation processor | None | codegen CLI | Proc macros | None | **Roslyn source generator** |
| Testing | TestRestateRuntime | Mock utilities | Minimal | Minimal | Minimal | **Mock context classes** |
| Handler registration | Annotation scan | Manual | Manual | Attribute macros | Decorators | **Source generator + attributes** |
| State typing | StateKey | String keys | String keys | String keys | String keys | **StateKey\<T\>** |
| Protocol version | v5-v6 | v5-v6 | v5-v6 | v5-v6 | v5-v6 | **v5-v6** |
