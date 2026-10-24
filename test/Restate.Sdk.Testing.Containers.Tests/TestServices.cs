using System.Collections.Concurrent;

namespace Restate.Sdk.Testing.Containers.Tests;

public sealed record GreetRequest(string Name);

public sealed record GreetReply(string Message);

/// <summary>A trivial stateless service used to prove ingress round-trips through the harness.</summary>
[Service]
public sealed class HarnessGreeterService
{
    [Handler]
    public Task<GreetReply> Greet(Context ctx, GreetRequest request)
    {
        return Task.FromResult(new GreetReply($"Hello, {request.Name}!"));
    }
}

/// <summary>A virtual object used to prove state persists across calls to the same key.</summary>
[VirtualObject]
public sealed class HarnessCounterObject
{
    private static readonly StateKey<int> Count = new("count");

    [Handler]
    public async Task<int> Add(ObjectContext ctx, int delta)
    {
        var next = await ctx.Get(Count) + delta;
        ctx.Set(Count, next);
        return next;
    }

    [SharedHandler]
    public async Task<int> Get(SharedObjectContext ctx)
    {
        return await ctx.Get(Count);
    }
}

/// <summary>Request for the blocking handler, carrying the token it reports under.</summary>
public sealed record BlockRequest(string Token);

/// <summary>
///     A service that blocks on a long durable sleep, so an invocation of it stays in flight
///     while a test addresses it by invocation id. It reports that it has started — through
///     <see cref="Started" />, which works because the harness runs the SDK endpoint in the test
///     process — before sleeping, so a test never addresses an invocation the server has not begun.
/// </summary>
[Service]
public sealed class HarnessBlockingService
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new(StringComparer.Ordinal);

    /// <summary>Completes once the handler invoked with <paramref name="token" /> has started.</summary>
    public static Task Started(string token)
    {
        return Gate(token).Task;
    }

    [Handler]
    public async Task<string> Block(Context ctx, BlockRequest request)
    {
        Gate(request.Token).TrySetResult();
        await ctx.Sleep(TimeSpan.FromMinutes(10));
        return "woken";
    }

    private static TaskCompletionSource Gate(string token)
    {
        return Gates.GetOrAdd(token,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}

/// <summary>
///     Targets and callers for the flow-control scenarios: <see cref="Echo" /> is the target of
///     scoped invocations from the ingress, and the other handlers call it with a scope — and with
///     a scope plus limit key — from inside a handler, which is the protocol V7 path rather than
///     the scoped ingress URL.
/// </summary>
[Service]
public sealed class HarnessScopedService
{
    /// <summary>The concurrency scope the harness routes its flow-control scenarios through.</summary>
    public const string Scope = "harness-scope";

    /// <summary>The limit key that narrows <see cref="Scope" /> in those scenarios.</summary>
    public const string LimitKey = "harness-limit-key";

    [Handler]
    public Task<string> Echo(Context ctx, string input)
    {
        return Task.FromResult($"echo:{input}");
    }

    [Handler]
    public async Task<string> CallScoped(Context ctx, string input)
    {
        return await ctx.Call<string, string>(
            nameof(HarnessScopedService), nameof(Echo), input, null, CallOptions.WithScope(Scope));
    }

    [Handler]
    public async Task<string> CallScopedWithLimitKey(Context ctx, string input)
    {
        return await ctx.Call<string, string>(
            nameof(HarnessScopedService), nameof(Echo), input, null, CallOptions.WithScope(Scope, LimitKey));
    }
}

/// <summary>Request for a handler that parks on a signal, carrying the token it reports under.</summary>
public sealed record SignalStartRequest(string Token);

/// <summary>Addresses a named signal on another invocation.</summary>
public sealed record ResolveSignalRequest(string InvocationId, string Name, string Value);

/// <summary>Addresses an unnamed signal on another invocation by its index.</summary>
public sealed record ResolveSignalByIndexRequest(string InvocationId, int Index, string Value);

/// <summary>Rejects a named signal on another invocation.</summary>
public sealed record RejectSignalRequest(string InvocationId, string Name, string Reason);

/// <summary>
///     Handlers that park on a signal. Each reports that its invocation has started — through
///     <see cref="Started" />, which works because the harness runs the SDK endpoint in the test
///     process — before awaiting, so a test never resolves a signal on an invocation the server
///     has not begun: restate-server drops those unless preflight signal recording is enabled.
/// </summary>
[Service]
public sealed class HarnessSignalService
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> SignalIndices = new(StringComparer.Ordinal);

    /// <summary>Completes once the handler invoked with <paramref name="token" /> has started.</summary>
    public static Task Started(string token)
    {
        return Gate(token).Task;
    }

    /// <summary>
    ///     The index the unnamed signal of the invocation reported under <paramref name="token" />
    ///     allocated, read from the handle <see cref="AwaitUnnamed" /> received. It is published
    ///     before <see cref="Started" /> completes, so a test that waited for the start can read it.
    /// </summary>
    public static int UnnamedSignalIndex(string token)
    {
        return SignalIndices[token];
    }

    private static TaskCompletionSource Gate(string token)
    {
        return Gates.GetOrAdd(token,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    [Handler]
    public async Task<string> AwaitNamed(Context ctx, SignalStartRequest request)
    {
        var signal = ctx.Signal<string>("go");
        Gate(request.Token).TrySetResult();
        return await signal.GetResult();
    }

    [Handler]
    public async Task<string> AwaitUnnamed(Context ctx, SignalStartRequest request)
    {
        var signal = ctx.Signal<string>();
        SignalIndices[request.Token] = signal.Index;
        Gate(request.Token).TrySetResult();
        return await signal.GetResult();
    }
}

/// <summary>Resolves and rejects signals on another invocation, addressed by its handle.</summary>
[Service]
public sealed class HarnessSignalResolverService
{
    [Handler]
    public async Task<string> Resolve(Context ctx, ResolveSignalRequest request)
    {
        await new InvocationHandle(request.InvocationId).ResolveSignal(ctx, request.Name, request.Value);
        return "resolved";
    }

    [Handler]
    public async Task<string> ResolveByIndex(Context ctx, ResolveSignalByIndexRequest request)
    {
        await new InvocationHandle(request.InvocationId).ResolveSignal(ctx, request.Index, request.Value);
        return "resolved";
    }

    [Handler]
    public async Task<string> Reject(Context ctx, RejectSignalRequest request)
    {
        await new InvocationHandle(request.InvocationId).RejectSignal(ctx, request.Name, request.Reason);
        return "rejected";
    }
}

/// <summary>
///     A workflow whose run handler parks on a durable promise, so a test can look its invocation
///     up by name and key while it is still in flight, attach to it, and then release it.
/// </summary>
[Workflow]
public sealed class HarnessLookupWorkflow
{
    /// <summary>The promise the run handler awaits and <see cref="Release" /> resolves.</summary>
    public const string ReleasePromise = "release";

    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new(StringComparer.Ordinal);

    /// <summary>Completes once the run handler invoked with <paramref name="token" /> has started.</summary>
    public static Task Started(string token)
    {
        return Gate(token).Task;
    }

    private static TaskCompletionSource Gate(string token)
    {
        return Gates.GetOrAdd(token,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    [Handler]
    public async Task<string> Run(WorkflowContext ctx, SignalStartRequest request)
    {
        var release = ctx.Promise<string>(ReleasePromise);
        Gate(request.Token).TrySetResult();
        return await release;
    }

    [SharedHandler]
    public Task<string> Release(SharedWorkflowContext ctx, string value)
    {
        ctx.ResolvePromise(ReleasePromise, value);
        return Task.FromResult("accepted");
    }
}
