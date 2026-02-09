using Restate.Sdk.Endpoint;

namespace Restate.Sdk.Tests.Endpoint;

public class HandlerInvokerTests
{
    [Fact]
    public async Task Invoker_TaskOfT_ReturnsResult()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        var handler = def.Handlers.First(h => h.Name == "Greet");

        var instance = new GreeterService();
        var ctx = new FakeContext();

        var result = await handler.Invoker(instance, ctx, "World", CancellationToken.None);

        Assert.Equal("Hello World!", result);
    }

    [Fact]
    public async Task Invoker_Task_ReturnsNull()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(GreeterService))!;
        var handler = def.Handlers.First(h => h.Name == "SayHello");

        var instance = new GreeterService();
        var ctx = new FakeContext();

        var result = await handler.Invoker(instance, ctx, null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Invoker_Void_ReturnsNull()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(VoidHandlerService))!;
        var handler = def.Handlers.First(h => h.Name == "DoNothing");

        var instance = new VoidHandlerService();
        var ctx = new FakeContext();

        var result = await handler.Invoker(instance, ctx, null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Invoker_ValueTaskOfT_ReturnsResult()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(ValueTaskService))!;
        var handler = def.Handlers.First(h => h.Name == "GetNumber");

        var instance = new ValueTaskService();
        var ctx = new FakeContext();

        var result = await handler.Invoker(instance, ctx, null, CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Invoker_ValueTask_ReturnsNull()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(ValueTaskService))!;
        var handler = def.Handlers.First(h => h.Name == "DoWork");

        var instance = new ValueTaskService();
        var ctx = new FakeContext();

        var result = await handler.Invoker(instance, ctx, null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Invoker_WithInputType_PassesCorrectly()
    {
        var def = ServiceDefinitionRegistry.TryGet(typeof(NamedService))!;
        var handler = def.Handlers.First(h => h.Name == "CustomHandler");

        var instance = new NamedService();
        var ctx = new FakeContext();

        var result = await handler.Invoker(instance, ctx, 5, CancellationToken.None);

        Assert.Equal(10, result);
    }

    private class FakeContext : WorkflowContext
    {
        // Context abstract members
        public override DurableRandom Random => throw new NotImplementedException();

        public override DurableConsole Console => new(() => false);
        public override IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string>();
        public override string InvocationId => "test-inv-1";
        public override CancellationToken Aborted => CancellationToken.None;

        // SharedObjectContext abstract members
        public override string Key => "test-key";

        public override ValueTask<DateTimeOffset> Now()
        {
            return new ValueTask<DateTimeOffset>(DateTimeOffset.UtcNow);
        }

        public override ValueTask<T> Run<T>(string name, Func<Task<T>> action)
        {
            throw new NotImplementedException();
        }

        public override ValueTask Run(string name, Func<Task> action)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<T> Run<T>(string name, Func<T> action)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action)
        {
            throw new NotImplementedException();
        }

        public override ValueTask Run(string name, Func<IRunContext, Task> action)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<TResponse> Call<TResponse>(string service, string key, string handler,
            object? request = null)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
            TimeSpan? delay = null, string? idempotencyKey = null)
        {
            return new ValueTask<InvocationHandle>(new InvocationHandle(""));
        }

        public override ValueTask<InvocationHandle> Send(string service, string key, string handler,
            object? request = null, TimeSpan? delay = null, string? idempotencyKey = null)
        {
            return new ValueTask<InvocationHandle>(new InvocationHandle(""));
        }

        public override TClient ServiceClient<TClient>()
        {
            throw new NotImplementedException();
        }

        public override TClient ObjectClient<TClient>(string key)
        {
            throw new NotImplementedException();
        }

        public override TClient WorkflowClient<TClient>(string key)
        {
            throw new NotImplementedException();
        }

        public override TClient ServiceSendClient<TClient>(SendOptions? options = null)
        {
            throw new NotImplementedException();
        }

        public override TClient ObjectSendClient<TClient>(string key, SendOptions? options = null)
        {
            throw new NotImplementedException();
        }

        public override TClient WorkflowSendClient<TClient>(string key, SendOptions? options = null)
        {
            throw new NotImplementedException();
        }

        public override ValueTask Sleep(TimeSpan duration)
        {
            throw new NotImplementedException();
        }

        public override Awakeable<T> Awakeable<T>(ISerde<T>? serde = null)
        {
            throw new NotImplementedException();
        }

        public override void ResolveAwakeable<T>(string id, T payload, ISerde<T>? serde = null)
        {
        }

        public override void RejectAwakeable(string id, string reason)
        {
        }

        public override IDurableFuture<T> RunAsync<T>(string name, Func<Task<T>> action)
        {
            throw new NotImplementedException();
        }

        public override IDurableFuture Timer(TimeSpan duration)
        {
            throw new NotImplementedException();
        }

        public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler,
            object? request = null)
        {
            throw new NotImplementedException();
        }

        public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
            object? request = null)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<T[]> All<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<T> Race<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
        {
            throw new NotImplementedException();
        }

        public override IAsyncEnumerable<(IDurableFuture future, Exception? error)> WaitAll(
            params IDurableFuture[] futures)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler, TRequest request,
            string? key = null)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<InvocationHandle> Send<TRequest>(string service, string handler, TRequest request,
            string? key = null, SendOptions? options = null)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<T> Attach<T>(string invocationId)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<T?> GetOutput<T>(string invocationId) where T : default
        {
            return new ValueTask<T?>(default(T));
        }

        public override ValueTask<T?> Get<T>(StateKey<T> key) where T : default
        {
            return new ValueTask<T?>(default(T));
        }

        public override ValueTask<string[]> StateKeys()
        {
            return new ValueTask<string[]>(Array.Empty<string>());
        }

        // ObjectContext abstract members
        public override void Set<T>(StateKey<T> key, T value)
        {
        }

        public override void Clear(string key)
        {
        }

        public override void ClearAll()
        {
        }

        // WorkflowContext abstract members
        public override ValueTask<T> Promise<T>(string name)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<T?> PeekPromise<T>(string name) where T : default
        {
            return new ValueTask<T?>(default(T));
        }

        public override void ResolvePromise<T>(string name, T payload)
        {
        }

        public override void RejectPromise(string name, string reason)
        {
        }
    }
}