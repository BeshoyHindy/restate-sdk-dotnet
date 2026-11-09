using System.Text.Json;
using System.Text.Json.Serialization;

namespace Restate.Sdk.TestServices;

// Metadata is accepted but cannot be propagated: TerminalException has no metadata yet.
public sealed record FailureToPropagate(
    [property: JsonPropertyName("errorMessage")] string ErrorMessage,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata);

[VirtualObject(Name = "Failing")]
public sealed class Failing
{
    private static int _failures;
    private static int _eventualSuccessSideEffects;
    private static int _eventualFailureSideEffects;

    [Handler(Name = "terminallyFailingCall")]
    public Task TerminallyFailingCall(ObjectContext ctx, FailureToPropagate failure)
    {
        throw new TerminalException(failure.ErrorMessage);
    }

    [Handler(Name = "callTerminallyFailingCall")]
    public async Task<string> CallTerminallyFailingCall(ObjectContext ctx, FailureToPropagate failure)
    {
        await ctx.Call<FailureToPropagate, JsonElement?>("Failing", "terminallyFailingCall", failure,
            "random-583e1bf2");
        throw new InvalidOperationException("Should not reach here");
    }

    [Handler(Name = "failingCallWithEventualSuccess")]
    public Task<int> FailingCallWithEventualSuccess(ObjectContext ctx)
    {
        var attempt = Interlocked.Increment(ref _failures);
        if (attempt >= 4)
        {
            Interlocked.Exchange(ref _failures, 0);
            return Task.FromResult(4);
        }

        throw new InvalidOperationException($"Failed at attempt: {attempt}");
    }

    [Handler(Name = "terminallyFailingSideEffect")]
    public async Task TerminallyFailingSideEffect(ObjectContext ctx, FailureToPropagate failure)
    {
        await ctx.Run("sideEffect", (Func<string>)(() => throw new TerminalException(failure.ErrorMessage)));
        throw new InvalidOperationException("Should not reach here");
    }

    [Handler(Name = "sideEffectSucceedsAfterGivenAttempts")]
    public async Task<int> SideEffectSucceedsAfterGivenAttempts(ObjectContext ctx, int minimumAttempts)
    {
        var policy = new RetryPolicy
        {
            MaxAttempts = minimumAttempts + 1,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            ExponentiationFactor = 1.0,
        };

        return await ctx.Run("sideEffect", () =>
        {
            var attempt = Interlocked.Increment(ref _eventualSuccessSideEffects);
            if (attempt >= minimumAttempts)
                return attempt;
            throw new InvalidOperationException($"Failed at attempt: {attempt}");
        }, policy);
    }

    [Handler(Name = "sideEffectFailsAfterGivenAttempts")]
    public async Task<int> SideEffectFailsAfterGivenAttempts(ObjectContext ctx, int retryPolicyMaxRetryCount)
    {
        var policy = new RetryPolicy
        {
            MaxAttempts = retryPolicyMaxRetryCount,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            ExponentiationFactor = 1.0,
        };

        try
        {
            await ctx.Run("sideEffect", (Func<int>)(() =>
            {
                var attempt = Interlocked.Increment(ref _eventualFailureSideEffects);
                throw new InvalidOperationException($"Failed at attempt: {attempt}");
            }), policy);
        }
        catch (TerminalException)
        {
            return Volatile.Read(ref _eventualFailureSideEffects);
        }

        throw new InvalidOperationException("Side effect did not fail.");
    }
}
