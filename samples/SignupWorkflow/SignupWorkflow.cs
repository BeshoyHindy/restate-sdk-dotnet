using Restate.Sdk;

namespace SignupWorkflow;

/// <summary>
///     A Workflow for user signup with email verification.
///     Workflows have a special Run handler that executes exactly once per workflow ID.
///     Additional shared handlers can query or signal the workflow while it's running.
///     This demonstrates:
///     - Workflow lifecycle with state tracking
///     - Signals: durable values delivered to a running invocation (the user clicking a
///     verification link), awaited by name and resolved through the invocation handle
///     - Shared handlers for querying and signalling the workflow
///     - ctx.Run() for idempotent side effects (account creation, email sending)
/// </summary>
[Workflow]
public sealed class SignupWorkflow
{
    /// <summary>The name of the signal the Run handler awaits while the email is unverified.</summary>
    private const string EmailVerified = "email-verified";

    private static readonly StateKey<string> Status = new("status");
    private static readonly StateKey<string> AccountId = new("accountId");
    private static readonly StateKey<string> RunInvocationId = new("runInvocationId");

    /// <summary>
    ///     Main workflow logic. Executes exactly once per workflow ID (the user's email).
    ///     Creates an account, sends a verification email, waits for the user to click
    ///     the link, then activates the account.
    ///     The signal is a durable rendezvous point — the workflow suspends while waiting,
    ///     using zero compute. When the user clicks the verification link, the Verify handler
    ///     resolves the signal and the workflow resumes automatically.
    /// </summary>
    [Handler]
    public async Task<SignupResult> Run(WorkflowContext ctx, SignupRequest request)
    {
        // Step 1: Create the account
        ctx.Set(Status, "creating-account");

        var accountId = await ctx.Run("create-account",
            () => AccountService.Create(request.Email, request.Name));

        ctx.Set(AccountId, accountId);

        // Step 2: Send the verification email and await the signal the click resolves

        // Signals have no ingress route, so the resolver has to be a handler: this invocation's
        // id is published in state for Verify below to address. It is written before the status
        // flips, so anything that sees "awaiting-verification" can already read the id.
        ctx.Set(RunInvocationId, ctx.InvocationId);
        ctx.Set(Status, "awaiting-verification");

        // Awaited before the email goes out, so a verification that lands immediately is not missed.
        var verification = ctx.Signal<string>(EmailVerified);

        var workflowKey = ctx.Key;
        await ctx.Run("send-verification-email",
            () =>
            {
                EmailService.SendVerification(request.Email, workflowKey);
                return Task.CompletedTask;
            });

        // The workflow suspends here — no compute charges on FaaS.
        // When the user clicks the link, the frontend calls Verify, which resolves the signal.
        await verification.GetResult();

        // Step 3: Activate the account
        ctx.Set(Status, "activating");

        await ctx.Run("activate-account",
            () =>
            {
                AccountService.Activate(accountId);
                return Task.CompletedTask;
            });

        ctx.Set(Status, "completed");

        return new SignupResult(accountId, true);
    }

    /// <summary>
    ///     Resolve the signal the Run handler awaits, from the verification link the user clicked.
    ///     Shared handler — runs concurrently, addressing the Run invocation by the id it published.
    /// </summary>
    [SharedHandler]
    public async Task<string> Verify(SharedWorkflowContext ctx)
    {
        var invocationId = await ctx.Get(RunInvocationId)
                           ?? throw new TerminalException("Signup has not reached verification yet.", 404);

        await new InvocationHandle(invocationId).ResolveSignal(ctx, EmailVerified, "verified");

        return "verified";
    }

    /// <summary>
    ///     Query the current workflow status. Shared handler — runs concurrently,
    ///     does not block the Run handler.
    /// </summary>
    [SharedHandler]
    public async Task<string> GetStatus(SharedWorkflowContext ctx)
    {
        return await ctx.Get(Status) ?? "unknown";
    }
}
