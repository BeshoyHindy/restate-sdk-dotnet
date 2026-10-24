namespace Restate.Sdk.Tests;

/// <summary>
///     Pins what the default <see cref="IContext" /> options overloads do for an external
///     implementation that has not overridden them: a scope or limit key is reported, and an
///     idempotency key on its own is forwarded.
/// </summary>
public class ContextOptionsDefaultsTests
{
    [Fact]
    public async Task TypedCall_IdempotencyKeyAlone_ForwardsToTheOptionsOverload()
    {
        var impl = new MinimalContext();
        IContext ctx = impl;

        var unkeyed = await ctx.Call<string, string>("Svc", "handler", "req", null,
            CallOptions.WithIdempotencyKey("idem-1"));
        var keyed = await ctx.Call<string, string>("Obj", "handler", "req", "key-1",
            CallOptions.WithIdempotencyKey("idem-2"));

        Assert.Equal("Svc/handler", unkeyed);
        Assert.Equal("Obj/key-1/handler", keyed);
        Assert.Equal(["idem-1", "idem-2"], impl.ForwardedIdempotencyKeys);
    }

    [Fact]
    public async Task TypedCall_DefaultOptions_ForwardsToTheOptionLessOverload()
    {
        var impl = new MinimalContext();
        IContext ctx = impl;

        var result = await ctx.Call<string, string>("Svc", "handler", "req", null, default);

        Assert.Equal("typed Svc/handler", result);
        Assert.Empty(impl.ForwardedIdempotencyKeys);
    }

    [Fact]
    public async Task TypedCall_ScopeOrLimitKey_Throws()
    {
        var impl = new MinimalContext();
        IContext ctx = impl;

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await ctx.Call<string, string>("Svc", "handler", "req", null, CallOptions.WithScope("orders")));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await ctx.Call<string, string>("Svc", "handler", "req", null,
                new CallOptions { LimitKey = "tenant-1" }));
    }

    [Fact]
    public async Task CallFuture_IdempotencyKeyAlone_ForwardsToTheOptionLessOverload()
    {
        var impl = new MinimalContext();
        IContext ctx = impl;

        var unkeyed = ctx.CallFuture<string>(service: "Svc", handler: "handler", request: null,
            options: CallOptions.WithIdempotencyKey("idem-1"));
        var keyed = ctx.CallFuture<string>(service: "Obj", key: "key-1", handler: "handler", request: null,
            options: CallOptions.WithIdempotencyKey("idem-2"));

        Assert.Equal("future Svc/handler", await unkeyed.GetResult());
        Assert.Equal("future Obj/key-1/handler", await keyed.GetResult());
    }

    [Fact]
    public void CallFuture_ScopeOrLimitKey_Throws()
    {
        var impl = new MinimalContext();
        IContext ctx = impl;

        var limitKeyOnly = new CallOptions { LimitKey = "tenant-1" };

        Assert.Throws<NotSupportedException>(() => ctx.CallFuture<string>(
            service: "Svc", handler: "handler", request: null, options: CallOptions.WithScope("orders")));
        Assert.Throws<NotSupportedException>(() => ctx.CallFuture<string>(
            service: "Obj", key: "key-1", handler: "handler", request: null,
            options: CallOptions.WithScope("orders", "tenant-1")));
        Assert.Throws<NotSupportedException>(() => ctx.CallFuture<string>(
            service: "Svc", handler: "handler", request: null, options: limitKeyOnly));
        Assert.Throws<NotSupportedException>(() => ctx.CallFuture<string>(
            service: "Obj", key: "key-1", handler: "handler", request: null, options: limitKeyOnly));
    }

    [Fact]
    public async Task Send_IdempotencyKeyAndDelay_AreForwarded()
    {
        var impl = new MinimalContext();
        IContext ctx = impl;

        await ctx.Send("Svc", "handler", null, new SendOptions
        {
            Delay = TimeSpan.FromMinutes(5),
            IdempotencyKey = "idem-send"
        });

        Assert.Equal([("idem-send", TimeSpan.FromMinutes(5))], impl.ForwardedSends);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await ctx.Send("Svc", "handler", null, SendOptions.WithScope("orders")));
    }

    /// <summary>
    ///     An external implementation of <see cref="IContext" /> that implements only the members
    ///     without a default, so the defaults under test are the ones the interface supplies.
    /// </summary>
    private sealed class MinimalContext : IContext
    {
        public List<string?> ForwardedIdempotencyKeys { get; } = [];
        public List<(string? IdempotencyKey, TimeSpan? Delay)> ForwardedSends { get; } = [];

        public string InvocationId => "inv-1";
        public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string>();
        public CancellationToken Aborted => CancellationToken.None;

        public ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null)
        {
            return Result<TResponse>($"{service}/{handler}");
        }

        public ValueTask<TResponse> Call<TResponse>(string service, string key, string handler,
            object? request = null)
        {
            return Result<TResponse>($"{service}/{key}/{handler}");
        }

        public ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request,
            CallOptions options)
        {
            ForwardedIdempotencyKeys.Add(options.IdempotencyKey);
            return Result<TResponse>($"{service}/{handler}");
        }

        public ValueTask<TResponse> Call<TResponse>(string service, string key, string handler, object? request,
            CallOptions options)
        {
            ForwardedIdempotencyKeys.Add(options.IdempotencyKey);
            return Result<TResponse>($"{service}/{key}/{handler}");
        }

        public ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler, TRequest request,
            string? key = null)
        {
            return Result<TResponse>($"typed {service}/{handler}");
        }

        public ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
            TimeSpan? delay = null, string? idempotencyKey = null)
        {
            ForwardedSends.Add((idempotencyKey, delay));
            return new ValueTask<InvocationHandle>(new InvocationHandle("inv-send"));
        }

        public ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request = null,
            TimeSpan? delay = null, string? idempotencyKey = null)
        {
            ForwardedSends.Add((idempotencyKey, delay));
            return new ValueTask<InvocationHandle>(new InvocationHandle("inv-send"));
        }

        public ValueTask<InvocationHandle> Send<TRequest>(string service, string handler, TRequest request,
            string? key = null, SendOptions? options = null)
        {
            ForwardedSends.Add((options?.IdempotencyKey, options?.Delay));
            return new ValueTask<InvocationHandle>(new InvocationHandle("inv-send"));
        }

        public IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler,
            object? request = null)
        {
            return new StubFuture<TResponse>($"future {service}/{handler}");
        }

        public IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
            object? request = null)
        {
            return new StubFuture<TResponse>($"future {service}/{key}/{handler}");
        }

        public ValueTask<T> Run<T>(string name, Func<Task<T>> action) => throw new NotImplementedException();
        public ValueTask Run(string name, Func<Task> action) => throw new NotImplementedException();
        public ValueTask<T> Run<T>(string name, Func<T> action) => throw new NotImplementedException();

        public ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action) =>
            throw new NotImplementedException();

        public ValueTask Run(string name, Func<IRunContext, Task> action) => throw new NotImplementedException();

        public ValueTask<T> Run<T>(string name, Func<Task<T>> action, RetryPolicy retryPolicy) =>
            throw new NotImplementedException();

        public ValueTask Run(string name, Func<Task> action, RetryPolicy retryPolicy) =>
            throw new NotImplementedException();

        public ValueTask<T> Run<T>(string name, Func<T> action, RetryPolicy retryPolicy) =>
            throw new NotImplementedException();

        public ValueTask CancelInvocation(string invocationId) => throw new NotImplementedException();
        public ValueTask Sleep(TimeSpan duration) => throw new NotImplementedException();
        public Awakeable<T> Awakeable<T>(ISerde<T>? serde = null) => throw new NotImplementedException();

        public void ResolveAwakeable<T>(string id, T payload, ISerde<T>? serde = null) =>
            throw new NotImplementedException();

        public void RejectAwakeable(string id, string reason) => throw new NotImplementedException();
        public ValueTask<DateTimeOffset> Now() => throw new NotImplementedException();
        public ValueTask<T> Attach<T>(string invocationId) => throw new NotImplementedException();
        public ValueTask<T?> GetOutput<T>(string invocationId) => throw new NotImplementedException();
        public IDurableFuture<T> RunAsync<T>(string name, Func<Task<T>> action) => throw new NotImplementedException();
        public IDurableFuture Timer(TimeSpan duration) => throw new NotImplementedException();

        private static ValueTask<TResponse> Result<TResponse>(string value)
        {
            return new ValueTask<TResponse>((TResponse)(object)value);
        }

        private sealed class StubFuture<T>(string value) : IDurableFuture<T>
        {
            public ValueTask<T> GetResult() => new((T)(object)value);
            ValueTask<object?> IDurableFuture.GetResult() => new(value);
            public string? InvocationId => null;
        }
    }
}
