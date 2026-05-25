using SurrealDb.Net.Models;

namespace SurrealDbInventory;

/// <summary>Stock level for a specific product in a specific warehouse.</summary>
public sealed class StockEntry : Record
{
    public string WarehouseId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
}

/// <summary>Audit record of an inventory transfer between warehouses.</summary>
public sealed class TransferRecord : Record
{
    public string SourceWarehouse { get; set; } = "";
    public string DestinationWarehouse { get; set; } = "";
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public record AddStockRequest(string ProductName, int Quantity);

public record StockLevel(string ProductName, int Quantity);

public record TransferRequest(
  string SourceWarehouse,
  string DestinationWarehouse,
  string ProductName,
  int Quantity);

public record TransferResult(
  string TransferId,
  string Status,
  int SourceRemaining,
  int DestinationTotal);
