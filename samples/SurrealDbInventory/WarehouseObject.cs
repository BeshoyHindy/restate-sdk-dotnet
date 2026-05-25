using Restate.Sdk;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace SurrealDbInventory;

/// <summary>
///   Virtual Object managing inventory for a single warehouse. Each key has
///   exclusive access — no two writers run concurrently for the same warehouse,
///   so Select-then-Upsert against SurrealDB stays race-free without needing
///   conditional SQL or distributed locks. SurrealDB v3 holds the persistent
///   state; Restate's ctx.Run journals each DB write so retries don't re-apply.
/// </summary>
[VirtualObject]
public sealed class WarehouseObject(ISurrealDbClient db)
{
    /// <summary>
    ///   Adds stock for a product. Read-then-upsert inside one ctx.Run: safe
    ///   because the VirtualObject serializes exclusive handlers per key.
    /// </summary>
    [Handler]
    public async Task<StockLevel> AddStock(ObjectContext ctx, AddStockRequest request)
    {
        var newQuantity = await ApplyDelta(ctx, request.ProductName, request.Quantity);
        return new StockLevel(request.ProductName, newQuantity);
    }

    /// <summary>
    ///   Atomic deduct-if-sufficient for a product, throwing TerminalException
    ///   when the warehouse can't cover the requested quantity. Called by
    ///   TransferService.Transfer; surfaced as a handler so cross-warehouse
    ///   transfers serialize through the VirtualObject's per-key exclusivity.
    /// </summary>
    [Handler]
    public async Task<StockLevel> DeductStock(ObjectContext ctx, AddStockRequest request)
    {
        var newQuantity = await ApplyDelta(ctx, request.ProductName, -request.Quantity);
        return new StockLevel(request.ProductName, newQuantity);
    }

    /// <summary>
    ///   Returns all stock levels for this warehouse. Shared handler — multiple
    ///   reads run concurrently with each other (and never alongside an exclusive
    ///   handler for the same key).
    /// </summary>
    [SharedHandler]
    public async Task<StockLevel[]> GetStock(SharedObjectContext ctx)
    {
        var warehouseId = ctx.Key;
        var entries = await db.Select<StockEntry>("stock");

        return entries
          .Where(s => s.WarehouseId == warehouseId)
          .Select(s => new StockLevel(s.ProductName, s.Quantity))
          .ToArray();
    }

    private async Task<int> ApplyDelta(ObjectContext ctx, string productName, int delta)
    {
        var warehouseId = ctx.Key;
        var stockId = RecordId.From("stock", $"{warehouseId}_{productName}");

        return await ctx.Run("apply-delta", async () =>
        {
            var existing = await db.Select<StockEntry>(stockId);
            var current = existing?.Quantity ?? 0;
            var total = current + delta;

            if (total < 0)
            {
                throw new TerminalException(
              $"Insufficient stock at {warehouseId} for {productName}: have {current}, need {-delta}",
              409);
            }

            var updated = await db.Upsert(new StockEntry
            {
                Id = stockId,
                WarehouseId = warehouseId,
                ProductName = productName,
                Quantity = total,
            });
            return updated.Quantity;
        });
    }
}
