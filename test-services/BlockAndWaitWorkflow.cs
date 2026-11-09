namespace Restate.Sdk.TestServices;

[Workflow(Name = "BlockAndWaitWorkflow")]
public sealed class BlockAndWaitWorkflow
{
    private const string Promise = "durable-promise";
    private static readonly StateKey<string> MyState = new("my-state");

    [Handler(Name = "run")]
    public async Task<string> Run(WorkflowContext ctx, string input)
    {
        ctx.Set(MyState, input);
        var output = await ctx.Promise<string>(Promise);

        if (await ctx.PeekPromise<string>(Promise) is null)
            throw new TerminalException("Durable promise should be completed");

        return output;
    }

    [SharedHandler(Name = "unblock")]
    public Task Unblock(SharedWorkflowContext ctx, string output)
    {
        ctx.ResolvePromise(Promise, output);
        return Task.CompletedTask;
    }

    [SharedHandler(Name = "getState")]
    public async Task<string?> GetState(SharedWorkflowContext ctx)
    {
        return await ctx.Get(MyState);
    }
}
