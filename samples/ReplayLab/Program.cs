using ReplayLab;

// ReplayLab: every service here is bait for a specific suspend/resume replay bug (B1–B10).
// Run standalone, register at http://localhost:9090, and drive a scenario, e.g.:
//   restate invocations invoke RunSleepRunService Execute --body '{"ProbeId":"demo"}'
//   restate invocations invoke ProbeService Get --body '{"ProbeId":"demo"}'   # read the probe counters
// The E2E suite hosts the same services in-process (see ReplayLabHost.Build) behind a real
// restate-server container so it can also read ExecutionProbe directly.
await ReplayLabHost.Build(9090).RunAsync();
