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
const string Namespace = "inventory";
const string Database = "warehouse";

var endpoint = Environment.GetEnvironmentVariable("SURREALDB_ENDPOINT")
  ?? "http://127.0.0.1:8000";
var user = Environment.GetEnvironmentVariable("SURREALDB_USER") ?? "root";
var pass = Environment.GetEnvironmentVariable("SURREALDB_PASS") ?? "root";

// Singleton lifetime keeps the HttpClient alive across overlapping
// invocations. Auth happens automatically; Use() runs after DEFINE so the
// namespace/database exist before we switch into them.
builder.Services.AddSurreal(
  $"Endpoint={endpoint};Username={user};Password={pass}",
  ServiceLifetime.Singleton);

builder.Services.AddRestate(opts =>
{
  opts.Bind<WarehouseObject>();
  opts.Bind<TransferService>();
});

var app = builder.Build();

// SurrealDB v3 strict mode rejects SELECT against undefined tables and
// won't auto-create namespaces, so DEFINE everything once at startup.
// DDL statements take literal identifiers (not parameters), which is why
// the namespace/database names are baked in as constants above rather than
// flowed through env vars.
var db = app.Services.GetRequiredService<ISurrealDbClient>();
var bootstrap = await db.Query($"""
  DEFINE NAMESPACE IF NOT EXISTS {Namespace};
  USE NAMESPACE {Namespace};
  DEFINE DATABASE IF NOT EXISTS {Database};
  USE DATABASE {Database};
  DEFINE TABLE IF NOT EXISTS stock SCHEMALESS;
  DEFINE TABLE IF NOT EXISTS transfer SCHEMALESS;
  DEFINE INDEX IF NOT EXISTS stock_warehouse ON stock FIELDS WarehouseId;
""");
bootstrap.EnsureAllOks();
await db.Use(Namespace, Database);

app.MapRestate();
await app.RunAsync();
