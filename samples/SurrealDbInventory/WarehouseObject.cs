using Restate.Sdk;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace SurrealDbInventory;

/// <summary>
///   Virtual Object managing inventory for a single warehouse. Each key has
///   exclusive access — no concurrent mutations to the same warehouse's stock.
///   SurrealDB v3 holds the persistent state; Restate's ctx.Run guarantees
///   each DB write is journaled and replayed at most once.
/// </summary>
[VirtualObject]
public sealed class WarehouseObject(ISurrealDbClient db)
{
  /// <summary>
  ///   Adds stock for a product. Read-then-upsert inside a single ctx.Run:
  ///   Restate journals the resulting quantity, so retries return the same
  ///   value without re-running the DB calls.
  /// </summary>
  [Handler]
  public async Task<StockLevel> AddStock(ObjectContext ctx, AddStockRequest request)
  {
    var warehouseId = ctx.Key;
    var stockId = RecordId.From("stock", $"{warehouseId}_{request.ProductName}");

    var newQuantity = await ctx.Run("upsert-stock", async () =>
    {
      var existing = await db.Select<StockEntry>(stockId);
      var total = (existing?.Quantity ?? 0) + request.Quantity;

      var updated = await db.Upsert(new StockEntry
      {
        Id = stockId,
        WarehouseId = warehouseId,
        ProductName = request.ProductName,
        Quantity = total,
      });
      return updated.Quantity;
    });

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

    var response = await db.Query(
      $"SELECT * FROM stock WHERE WarehouseId = {warehouseId}");
    response.EnsureAllOks();

    return response.GetValues<StockEntry>(0)
      .Select(s => new StockLevel(s.ProductName, s.Quantity))
      .ToArray();
  }
}
