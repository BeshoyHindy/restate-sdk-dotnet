using Restate.Sdk;
using Restate.Sdk.Client;

namespace ScopedClient;

/// <summary>
///     Drives the registered <see cref="PricingService" /> from outside the Restate runtime.
///
///     Key patterns demonstrated:
///     - <c>RestateClient.WithScope</c>: bind the scope once instead of passing it on every call.
///     - A per-call limit key narrows the bound scope, with no per-call scope beside it.
///     - <c>RestateIngressException</c>: what a non-success ingress response carries — the HTTP
///     status, the Restate error source and code, and the server's message.
/// </summary>
internal static class IngressDemo
{
    public static async Task Run(string ingressAddress)
    {
        using var client = new RestateClient(ingressAddress);

        // Every call, send and lookup this client makes runs in the pricing scope.
        var pricing = client.WithScope(PricingService.Scope).Service(nameof(PricingService));

        var quote = await pricing.Call<QuoteReply>(nameof(PricingService.Quote),
            new QuoteRequest("tenant-a", "widget", 2));
        Console.WriteLine($"scoped call: {quote.Sku} x{quote.Quantity} = {quote.AmountCents}c");

        // The bound scope is the scope a limit key needs, so the limit key stands on its own here.
        var limited = await pricing.Call<QuoteReply>(nameof(PricingService.Quote),
            new QuoteRequest("tenant-b", "widget", 4),
            new CallOptions { LimitKey = "tenant-b" });
        Console.WriteLine($"limited call: {limited.Sku} x{limited.Quantity} = {limited.AmountCents}c");

        // A failure the ingress produces itself: there is no handler by that name.
        await ReportFailure("unknown handler",
            () => pricing.Call<QuoteReply>("NoSuchHandler", new QuoteRequest("tenant-a", "widget", 1)));

        // A failure that came back from the handler: the quantity check throws a TerminalException.
        await ReportFailure("terminal failure in the handler",
            () => pricing.Call<QuoteReply>(nameof(PricingService.Quote),
                new QuoteRequest("tenant-a", "widget", 0)));
    }

    /// <summary>
    ///     Runs an ingress call that is expected to fail and prints everything the typed exception
    ///     reports. <see cref="RestateIngressException.ErrorSource" /> and
    ///     <see cref="RestateIngressException.ErrorCode" /> are null against restate-server before
    ///     1.7.4, which does not report them.
    /// </summary>
    private static async Task ReportFailure(string description, Func<Task<QuoteReply>> call)
    {
        try
        {
            await call();
            Console.WriteLine($"{description}: unexpectedly succeeded");
        }
        catch (RestateIngressException ex)
        {
            Console.WriteLine($"{description}: status={(int?)ex.StatusCode}, " +
                              $"source={ex.ErrorSource ?? "<none>"}, code={ex.ErrorCode ?? "<none>"}");
            Console.WriteLine($"  message: {ex.Message}");
        }
    }
}
