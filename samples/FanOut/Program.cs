using FanOut;
using Restate.Sdk.Hosting;

// Parallel fan-out: process items concurrently with durable futures.
// Demonstrates All (wait for all), Race (first to complete), and timeout patterns.
//
// Test:
//   restate invocations invoke FanOutService ProcessAll --body '{
//     "batchId": "batch-1",
//     "items": ["alpha", "bravo", "charlie", "delta", "echo"]
//   }'
//
//   restate invocations invoke FanOutService ProcessFirst --body '{
//     "batchId": "batch-2",
//     "items": ["fast", "medium", "slow"]
//   }'
await RestateHost.CreateBuilder()
    .WithPort(9087)
    .AddService<FanOutService>()
    .Build()
    .RunAsync();
