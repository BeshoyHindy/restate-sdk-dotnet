# Service Types

Restate supports three service types, each with different consistency and state guarantees.

## Stateless Service

No state. Multiple invocations run concurrently.

```csharp
[Service]
public class EmailService
{
    [Handler]
    public async Task<bool> SendEmail(Context ctx, EmailRequest request)
    {
        return await ctx.Run("send-email", async () =>
        {
            await emailClient.SendAsync(request.To, request.Subject, request.Body);
            return true;
        });
    }
}
```

## Virtual Object

Keyed entities with exclusive state access. Only one `[Handler]` runs at a time per key.
`[SharedHandler]` methods can run concurrently with read-only state access.

```csharp
[VirtualObject]
public class Counter
{
    private static readonly StateKey<int> Count = new("count");

    [Handler]
    public async Task<int> Add(ObjectContext ctx, int delta)
    {
        var current = await ctx.Get(Count);
        var next = current + delta;
        ctx.Set(Count, next);
        return next;
    }

    [SharedHandler]
    public async Task<int> Get(SharedObjectContext ctx)
        => await ctx.Get(Count);

    [Handler]
    public Task Reset(ObjectContext ctx)
    {
        ctx.ClearAll();
        return Task.CompletedTask;
    }
}
```

## Workflow

Long-running durable workflows with state and awakeables for external signaling. The `Run` handler
executes exactly once per workflow ID. Workflow promises (`ctx.Promise<T>()`) are also available
for signaling between handlers.

```csharp
[Workflow]
public class SignupWorkflow
{
    private static readonly StateKey<string> Status = new("status");

    [Handler]
    public async Task<bool> Run(WorkflowContext ctx, SignupRequest request)
    {
        ctx.Set(Status, "creating-account");

        var accountId = await ctx.Run("create-account",
            () => AccountService.Create(request.Email, request.Name));

        ctx.Set(Status, "awaiting-verification");

        // Awakeable: a durable promise resolved by an external system.
        // Pass awakeable.Id to the external system; await awakeable.Value to block.
        var awakeable = ctx.Awakeable<string>();

        await ctx.Run("send-verification-email",
            () =>
            {
                EmailService.SendVerification(request.Email, awakeable.Id);
                return Task.CompletedTask;
            });

        // Workflow suspends here until the external system resolves the awakeable
        await awakeable.Value;

        ctx.Set(Status, "completed");
        return true;
    }

    [SharedHandler]
    public async Task<string> GetStatus(SharedWorkflowContext ctx)
        => await ctx.Get(Status) ?? "unknown";
}
```

## Context Interfaces

Context interfaces (`IContext`, `IObjectContext`, etc.) are available for utility methods,
type constraints, and generic programming:

```csharp
// Utility method accepting any context type
public static async Task<string> FormatTimestamp(IContext ctx)
{
    var now = await ctx.Now();
    return now.ToString("O");
}
```
