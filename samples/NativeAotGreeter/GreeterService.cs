using Restate.Sdk;

namespace NativeAotGreeter;

/// <summary>
///     A simple greeting service that demonstrates NativeAOT compatibility.
///     When published with <c>dotnet publish -c Release</c>, produces a
///     self-contained, ahead-of-time compiled executable with no JIT dependency.
/// </summary>
[Service]
public sealed class GreeterService
{
    /// <summary>
    ///     Greets a user with a durable, replay-safe side effect.
    ///     Works identically to the standard Greeter sample but runs without
    ///     reflection or dynamic code generation at runtime.
    /// </summary>
    [Handler]
    public async Task<GreetResponse> Greet(Context ctx, GreetRequest request)
    {
        var greeting = await ctx.Run(
            "build-greeting",
            () => $"Hello, {request.Name}! (served from NativeAOT)"
        );

        ctx.Console.Log($"Greeted {request.Name}");

        return new GreetResponse(greeting);
    }

    /// <summary>
    ///     Demonstrates retry policy with NativeAOT.
    ///     The side effect retries locally up to 3 times with exponential backoff
    ///     before propagating the failure to the Restate runtime.
    /// </summary>
    [Handler]
    public async Task<GreetResponse> GreetWithRetry(Context ctx, GreetRequest request)
    {
        var greeting = await ctx.Run(
            "build-greeting",
            () => $"Hello, {request.Name}! Attempt succeeded.",
            RetryPolicy.FixedAttempts(3)
        );

        return new GreetResponse(greeting);
    }
}
