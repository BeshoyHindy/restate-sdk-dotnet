using Restate.Sdk;

namespace ScopedClient;

/// <summary>
///     Pricing behind a server-side concurrency scope, so one noisy tenant cannot exhaust the
///     capacity the others share.
///
///     Key patterns demonstrated:
///     - <c>CallOptions.WithScope(scope, limitKey)</c>: run a call in a scope, narrowed to the
///     invocations of that scope sharing a limit key — here, one tenant.
///     - <c>SendOptions.WithScope(scope)</c>: the same for a fire-and-forget send.
///     - Both carry the scope as protocol V7 fields on the call command, so they need a server
///     that negotiates V7 (restate-server 1.8 does by default). The generated typed clients take
///     the same options.
/// </summary>
[Service]
public sealed class PricingService
{
    /// <summary>The concurrency scope every quote in this sample runs in.</summary>
    public const string Scope = "pricing";

    /// <summary>The unit price every sku in this sample carries, in cents.</summary>
    private const int UnitPriceCents = 250;

    /// <summary>
    ///     Prices a request. This is the target of the scoped call below, and of the scoped
    ///     invocations the ingress client makes.
    /// </summary>
    [Handler]
    public async Task<QuoteReply> Quote(Context ctx, QuoteRequest request)
    {
        // A terminal failure is never retried, and the ingress reports it as having come from the
        // invocation rather than from the ingress itself — see RestateIngressException.ErrorSource.
        if (request.Quantity <= 0)
            throw new TerminalException("Quantity must be positive.", 400);

        var amount = await ctx.Run("price", () => request.Quantity * UnitPriceCents);
        return new QuoteReply(request.Sku, request.Quantity, amount);
    }

    /// <summary>
    ///     Prices a request through a scoped call instead of inline, and files the result with a
    ///     scoped send. The scope bounds how many quotes run at once; the limit key narrows that
    ///     bound to the calling tenant.
    /// </summary>
    [Handler]
    public async Task<QuoteReply> QuoteInScope(Context ctx, QuoteRequest request)
    {
        var quote = await ctx.Call<QuoteRequest, QuoteReply>(nameof(PricingService), nameof(Quote),
            request, null, CallOptions.WithScope(Scope, request.Tenant));

        await ctx.Send(nameof(PricingService), nameof(Audit), quote, SendOptions.WithScope(Scope));

        return quote;
    }

    /// <summary>Records a quote. The target of the scoped send above.</summary>
    [Handler]
    public Task Audit(Context ctx, QuoteReply quote)
    {
        ctx.Console.Log($"Quoted {quote.Sku} x{quote.Quantity} at {quote.AmountCents}c");
        return Task.CompletedTask;
    }
}
