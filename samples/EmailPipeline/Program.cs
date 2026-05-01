using EmailPipeline;
using Restate.Sdk.Hosting;

// Email sending pipeline with retry, awakeable delivery tracking, batch fan-out,
// saga compensation, and delayed follow-ups.
//
// Register at http://localhost:9088 then test:
//
// Single email with delivery tracking:
//   restate invocations invoke EmailPipelineService SendEmail --body '{
//     "to": "alice@example.com",
//     "subject": "Welcome!",
//     "htmlBody": "<h1>Welcome aboard!</h1>"
//   }'
//
// Batch send (parallel fan-out):
//   restate invocations invoke EmailPipelineService SendBatch --body '{
//     "batchId": "batch-1",
//     "emails": [
//       {"to": "alice@example.com", "subject": "Hello", "htmlBody": "<p>Hi Alice</p>"},
//       {"to": "bob@example.com", "subject": "Hello", "htmlBody": "<p>Hi Bob</p>"},
//       {"to": "carol@example.com", "subject": "Hello", "htmlBody": "<p>Hi Carol</p>"}
//     ]
//   }'
//
// Campaign send (sequential with saga compensation + delayed follow-up):
//   restate invocations invoke EmailPipelineService SendCampaign --body '{
//     "batchId": "campaign-2026-q1",
//     "emails": [
//       {"to": "alice@example.com", "subject": "Spring Sale!", "htmlBody": "<p>30% off!</p>"},
//       {"to": "bob@example.com", "subject": "Spring Sale!", "htmlBody": "<p>30% off!</p>"}
//     ]
//   }'
await RestateHost.CreateBuilder()
    .AddService<EmailPipelineService>()
    .WithPort(9088)
    .Build()
    .RunAsync();
