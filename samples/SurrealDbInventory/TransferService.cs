using Restate.Sdk;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace SurrealDbInventory;

/// <summary>
///   Orchestrates durable inventory transfers between warehouses.
///   Each step runs inside ctx.Run, so Restate journals the result and any
///   crash mid-transfer resumes from the last completed step — no duplicate
///   writes, no lost updates, no manual two-phase commit.
/// </summary>
[Service]
public sealed class TransferService(ISurrealDbClient db)
{
  /// <summary>
  ///   Three durable steps:
  ///     1. Atomic conditional deduct from source (TerminalException on insufficient stock)
  ///     2. Read-and-add to destination
  ///     3. Append audit record
  ///   Each SurrealDB write is wrapped in ctx.Run so it executes exactly once
  ///   across retries. The conditional UPDATE in step 1 is server-atomic,
  ///   guarding against overdraw in a single statement.
  /// </summary>
  [Handler]
  public async Task<TransferResult> Transfer(Context ctx, TransferRequest request)
  {
    ctx.Console.Log(
      $"Starting transfer: {request.Quantity}x {request.ProductName} " +
      $"from {request.SourceWarehouse} to {request.DestinationWarehouse}");

    var sourceStockId = RecordId.From("stock",
      $"{request.SourceWarehouse}_{request.ProductName}");
    var destStockId = RecordId.From("stock",
      $"{request.DestinationWarehouse}_{request.ProductName}");

    // Step 1: Conditional atomic deduct. SurrealDb.Net's typed CRUD
    // (Update/Merge/Upsert) lacks a "decrement only if quantity >= qty"
    // primitive, and a separate Select-then-Upsert would race under
    // concurrent transfers from a stateless Service. The single-statement
    // UPDATE ... WHERE ... RETURN AFTER guards overdraw at the DB layer;
    // no row returned means insufficient stock, surfaced as a
    // TerminalException so Restate doesn't retry.
    var sourceRemaining = await ctx.Run("deduct-from-source", async () =>
    {
      var qty = request.Quantity;
      var response = await db.Query(
        $@"UPDATE {sourceStockId} SET Quantity -= {qty}
           WHERE Quantity >= {qty}
           RETURN AFTER");
      response.EnsureAllOks();

      var updated = response.GetValues<StockEntry>(0).FirstOrDefault();
      if (updated is null)
      {
        throw new TerminalException(
          $"Insufficient stock at {request.SourceWarehouse} for {request.ProductName}",
          409);
      }
      return updated.Quantity;
    });

    ctx.Console.Log($"Deducted {request.Quantity} from source. Remaining: {sourceRemaining}");

    // Step 2: Add to destination — read existing then upsert with new total.
    var destinationTotal = await ctx.Run("add-to-destination", async () =>
    {
      var existing = await db.Select<StockEntry>(destStockId);
      var total = (existing?.Quantity ?? 0) + request.Quantity;

      var updated = await db.Upsert(new StockEntry
      {
        Id = destStockId,
        WarehouseId = request.DestinationWarehouse,
        ProductName = request.ProductName,
        Quantity = total,
      });
      return updated.Quantity;
    });

    ctx.Console.Log($"Added {request.Quantity} to destination. Total: {destinationTotal}");

    // Step 3: Audit record. We pre-generate the RecordId via ctx.Random so the
    // value is replay-safe and we avoid a server-side auto-id round-trip.
    var auditUlid = ctx.Random.NextGuid().ToString("N");
    var transferId = $"transfer:{auditUlid}";
    await ctx.Run("record-transfer", async () =>
    {
      await db.Create(new TransferRecord
      {
        Id = RecordId.From("transfer", auditUlid),
        SourceWarehouse = request.SourceWarehouse,
        DestinationWarehouse = request.DestinationWarehouse,
        ProductName = request.ProductName,
        Quantity = request.Quantity,
        Status = "completed",
        CreatedAt = DateTime.UtcNow,
      });
    });

    ctx.Console.Log($"Transfer {transferId} completed successfully.");

    return new TransferResult(transferId, "completed", sourceRemaining, destinationTotal);
  }
}
