using Restate.Sdk;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace SurrealDbInventory;

/// <summary>
///   Orchestrates durable inventory transfers between warehouses by routing
///   each side of the transfer through the keyed WarehouseObject. The
///   VirtualObject's per-key exclusive concurrency makes the typed
///   Select/Upsert calls race-free without needing conditional SurrealQL.
///   Restate journals each ctx.Call + ctx.Run, so a crash mid-transfer
///   resumes from the last completed step — no duplicate writes.
/// </summary>
[Service]
public sealed class TransferService(ISurrealDbClient db)
{
  /// <summary>
  ///   Three durable steps:
  ///     1. Deduct from source via WarehouseObject (TerminalException on insufficient)
  ///     2. Add to destination via WarehouseObject
  ///     3. Append audit record
  ///   Each ctx.Call is journaled, so retries replay from the journal rather
  ///   than re-invoking handlers. The audit RecordId comes from ctx.Random
  ///   so it stays stable across replay.
  /// </summary>
  [Handler]
  public async Task<TransferResult> Transfer(Context ctx, TransferRequest request)
  {
    ctx.Console.Log(
      $"Starting transfer: {request.Quantity}x {request.ProductName} " +
      $"from {request.SourceWarehouse} to {request.DestinationWarehouse}");

    var stockRequest = new AddStockRequest(request.ProductName, request.Quantity);

    // Step 1: deduct from source — handled inside WarehouseObject so the
    // VirtualObject's exclusive lock per key prevents racing transfers.
    var sourceLevel = await ctx.Call<StockLevel>(
      "WarehouseObject", request.SourceWarehouse, "DeductStock", stockRequest);

    ctx.Console.Log(
      $"Deducted {request.Quantity} from source. Remaining: {sourceLevel.Quantity}");

    // Step 2: add to destination — same exclusivity guarantee, different key.
    var destinationLevel = await ctx.Call<StockLevel>(
      "WarehouseObject", request.DestinationWarehouse, "AddStock", stockRequest);

    ctx.Console.Log(
      $"Added {request.Quantity} to destination. Total: {destinationLevel.Quantity}");

    // Step 3: append audit row. Pre-generate the RecordId via ctx.Random so
    // it's stable across replay; db.Create is the typed insert path.
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

    return new TransferResult(
      transferId, "completed", sourceLevel.Quantity, destinationLevel.Quantity);
  }
}
