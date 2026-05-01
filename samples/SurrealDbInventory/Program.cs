using Restate.Sdk.Hosting;
using SurrealDb.Net;
using SurrealDbInventory;

// Durable Inventory Transfer System
// Restate (durable execution) + SurrealDB v3 (persistent storage).
// Restate journals each ctx.Run side effect, so DB writes execute exactly once
// across crashes. SurrealDB v3 over WebSocket gives us multi-statement
// transactions, sessions, and live queries on the same connection.
//
// Start SurrealDB v3:
//   docker run -d -p 8000:8000 surrealdb/surrealdb:v3 \
//     start --user root --pass root surrealkv:///data/dev.skv
//
// Register endpoint with Restate runtime:
//   curl -X POST http://localhost:9070/deployments \
//     -H 'content-type: application/json' \
//     -d '{"uri":"http://localhost:9088"}'
//
// Add stock:
//   curl -X POST http://localhost:8080/WarehouseObject/warehouse-east/AddStock \
//     -H 'content-type: application/json' \
//     -d '{"productName":"widget","quantity":100}'
//
// Transfer:
//   curl -X POST http://localhost:8080/TransferService/Transfer \
//     -H 'content-type: application/json' \
//     -d '{"sourceWarehouse":"warehouse-east","destinationWarehouse":"warehouse-west","productName":"widget","quantity":25}'
//
// Query stock:
//   curl -X POST http://localhost:8080/WarehouseObject/warehouse-east/GetStock

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureRestate(port: 9088);

// HTTP scheme — single shared client is concurrent-safe across overlapping
// Restate invocations. ws:// would enable v3 transactions and live queries,
// but it serializes all calls on one connection which deadlocks under load.
var endpoint = Environment.GetEnvironmentVariable("SURREALDB_ENDPOINT")
  ?? "http://127.0.0.1:8000";
var ns = Environment.GetEnvironmentVariable("SURREALDB_NS") ?? "test";
var database = Environment.GetEnvironmentVariable("SURREALDB_DB") ?? "test";
var user = Environment.GetEnvironmentVariable("SURREALDB_USER") ?? "root";
var pass = Environment.GetEnvironmentVariable("SURREALDB_PASS") ?? "root";

// Singleton lifetime keeps the HttpClient alive. NamingPolicy=CamelCase maps
// C# PascalCase to SurrealDB camelCase columns. Auth and Use happen at
// startup so we can DEFINE the namespace before switching into it.
var connectionString =
  $"Endpoint={endpoint};Username={user};Password={pass}";

builder.Services.AddSurreal(connectionString, ServiceLifetime.Singleton);

builder.Services.AddRestate(opts =>
{
  opts.Bind<WarehouseObject>();
  opts.Bind<TransferService>();
});

var app = builder.Build();

// SurrealDB v3 strict mode requires explicit NAMESPACE/DATABASE/TABLE
// definitions. Run the bootstrap once at startup so handlers see a ready
// schema (stock + transfer tables, and an index for warehouseId lookups).
var db = app.Services.GetRequiredService<ISurrealDbClient>();
var bootstrap = await db.RawQuery($$"""
  DEFINE NAMESPACE IF NOT EXISTS {{ns}};
  USE NAMESPACE {{ns}};
  DEFINE DATABASE IF NOT EXISTS {{database}};
  USE DATABASE {{database}};
  DEFINE TABLE IF NOT EXISTS stock SCHEMALESS;
  DEFINE TABLE IF NOT EXISTS transfer SCHEMALESS;
  DEFINE INDEX IF NOT EXISTS stock_warehouse ON stock FIELDS WarehouseId;
""");
bootstrap.EnsureAllOks();
await db.Use(ns, database);

app.MapRestate();
await app.RunAsync();
