using Restate.Sdk.Hosting;

// User signup workflow with email verification.
// Register at http://localhost:9084 then:
//   restate invocations invoke SignupWorkflow/alice@example.com Run \
//     --body '{"email": "alice@example.com", "name": "Alice"}'
//   restate invocations invoke SignupWorkflow/alice@example.com GetStatus
//   restate invocations invoke SignupWorkflow/alice@example.com Verify
await RestateHost.CreateBuilder()
    .AddWorkflow<SignupWorkflow.SignupWorkflow>()
    .WithPort(9084)
    .Build()
    .RunAsync();