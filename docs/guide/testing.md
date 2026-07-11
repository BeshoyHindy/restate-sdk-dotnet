# Testing

The `Restate.Sdk.Testing` package provides mock contexts for unit testing handlers without
a running Restate server:

```csharp
using Restate.Sdk.Testing;

var ctx = new MockContext();
var service = new GreeterService();
var result = await service.Greet(ctx, "Alice");
Assert.Equal("Hello, Alice!", result);
```

## Mock Contexts

Mock contexts are available for every context type:

| Mock Class | For |
|------------|-----|
| `MockContext` | Stateless services |
| `MockObjectContext` | Virtual object exclusive handlers |
| `MockSharedObjectContext` | Virtual object shared handlers |
| `MockWorkflowContext` | Workflow run handlers |
| `MockSharedWorkflowContext` | Workflow shared handlers |

## Features

```csharp
// Deterministic time
var ctx = new MockContext();
ctx.CurrentTime = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
var now = await ctx.Now(); // returns the configured time

// Setup call results
ctx.SetupCall<string>("GreeterService", "Greet", "Hello!");

// Setup call failures
ctx.SetupCallFailure("GreeterService", "Greet", new TerminalException("fail", 500));

// Register typed clients
ctx.RegisterClient<IGreeterServiceClient>(myMockClient);

// Verify recorded calls, sends, and sleeps
Assert.Single(ctx.Calls);
Assert.Equal("GreeterService", ctx.Calls[0].Service);

// Verify idempotency keys on recorded calls
Assert.Equal("my-key", ctx.Calls[0].IdempotencyKey);

// Verify cancellations
Assert.Single(ctx.Cancellations);
Assert.Equal("inv-123", ctx.Cancellations[0]);
```
