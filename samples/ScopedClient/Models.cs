namespace ScopedClient;

/// <summary>A price request for one tenant: the tenant is both the caller and the limit key.</summary>
public record QuoteRequest(string Tenant, string Sku, int Quantity);

/// <summary>The price a quote settled on, in cents.</summary>
public record QuoteReply(string Sku, int Quantity, int AmountCents);
