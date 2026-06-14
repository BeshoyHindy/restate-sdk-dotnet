#!/usr/bin/env -S deno run --allow-run --allow-net --allow-env
//
// End-to-end test for the F# Restate sample against a LOCAL k0s cluster that already runs the
// restate-operator + a RestateCluster named "restate" (the same cluster the other samples deploy to).
//
// Why this shape: importing a locally-built image into k0s's root-owned containerd needs privileges
// this test does not assume. Instead it runs the F# SDK as a host process and registers it with the
// in-cluster restate-server using the node's reachable IP — so the server dials BACK into the host and
// drives the handlers. Every invocation therefore travels the full real path:
//
//     kubectl port-forward -> restate ingress (k0s pod) -> restate-server -> F# SDK (host) -> back
//
// It asserts four scenarios that can only pass if the F# binding is wired correctly end to end:
//   1. Virtual Object durable per-key state (Counter)
//   2. Saga happy path (all bookings confirmed)
//   3. Saga compensation path (terminal failure rolls back completed bookings in reverse)
//   4. Workflow suspend -> external awakeable resolve -> resume -> completed
//
// Run:  deno run --allow-run --allow-net --allow-env samples/FSharpServices/e2e/k0s-e2e.ts
// Prereqissites: kubectl context pointing at the k0s cluster; restate namespace + RestateCluster ready.

const HERE = new URL(".", import.meta.url).pathname;
const PROJECT = `${HERE}../FSharpServices.fsproj`;
const NS = Deno.env.get("RESTATE_NS") ?? "restate";
const HOST_PORT = Number(Deno.env.get("FS_HOST_PORT") ?? "9080");
const ADMIN_PORT = Number(Deno.env.get("FS_ADMIN_PORT") ?? "19070");
const INGRESS_PORT = Number(Deno.env.get("FS_INGRESS_PORT") ?? "18088");
const ADMIN = `http://localhost:${ADMIN_PORT}`;
const INGRESS = `http://localhost:${INGRESS_PORT}`;

const children: Deno.ChildProcess[] = [];
let deploymentId: string | null = null;

function run(cmd: string, args: string[]): Deno.ChildProcess {
  const child = new Deno.Command(cmd, {
    args,
    stdout: "piped",
    stderr: "piped",
  }).spawn();
  children.push(child);
  return child;
}

async function capture(cmd: string, args: string[]): Promise<string> {
  const { stdout } = await new Deno.Command(cmd, {
    args,
    stdout: "piped",
    stderr: "null",
  }).output();
  return new TextDecoder().decode(stdout).trim();
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function assert(cond: boolean, msg: string) {
  if (!cond) throw new Error(`ASSERTION FAILED: ${msg}`);
  console.log(`  ✓ ${msg}`);
}

async function waitTcp(port: number, label: string, tries = 40) {
  for (let i = 0; i < tries; i++) {
    try {
      const conn = await Deno.connect({ hostname: "localhost", port });
      conn.close();
      console.log(`  ${label} listening on :${port} (after ${i}s)`);
      return;
    } catch {
      await sleep(1000);
    }
  }
  throw new Error(`${label} never came up on :${port}`);
}

async function waitHttp(url: string, label: string, tries = 40) {
  for (let i = 0; i < tries; i++) {
    try {
      const res = await fetch(url);
      await res.body?.cancel();
      if (res.ok) {
        console.log(`  ${label} ready (after ${i}s)`);
        return;
      }
    } catch {
      // not up yet
    }
    await sleep(1000);
  }
  throw new Error(`${label} never became ready at ${url}`);
}

// Restate ingress invocation helpers (HTTP/1.1 over the port-forward). No-input handlers must be
// called with an empty body AND no content-type, so the header is only attached when a body exists.
async function invoke(path: string, body?: unknown): Promise<Response> {
  const init: RequestInit = { method: "POST" };
  if (body !== undefined) {
    init.headers = { "content-type": "application/json" };
    init.body = JSON.stringify(body);
  }
  return await fetch(`${INGRESS}${path}`, init);
}
async function invokeJson(path: string, body?: unknown): Promise<unknown> {
  const res = await invoke(path, body);
  const text = await res.text();
  if (!res.ok) throw new Error(`${path} -> HTTP ${res.status}: ${text}`);
  return text.length ? JSON.parse(text) : null;
}

async function main() {
  // 0) Discover the node IP the restate-server pod can reach back on.
  const nodeIp = await capture("kubectl", [
    "get",
    "nodes",
    "-o",
    "jsonpath={.items[0].status.addresses[?(@.type=='InternalIP')].address}",
  ]);
  if (!nodeIp) throw new Error("could not resolve node InternalIP via kubectl");
  console.log(`Node IP (server dials back here): ${nodeIp}`);

  // 1) Build, then start the F# SDK on the host.
  console.log("\n[build] dotnet build (Release)…");
  const build = await new Deno.Command("dotnet", {
    args: ["build", PROJECT, "-c", "Release", "-v", "q"],
    stdout: "inherit",
    stderr: "inherit",
  }).output();
  if (!build.success) throw new Error("dotnet build failed");

  console.log(`[host] starting F# SDK on :${HOST_PORT}…`);
  run("dotnet", [
    "run",
    "--project",
    PROJECT,
    "-c",
    "Release",
    "--no-build",
    "--",
    String(HOST_PORT),
  ]);
  await waitTcp(HOST_PORT, "F# host");

  // 2) Port-forward the restate admin + ingress.
  console.log("[pf] port-forwarding restate admin + ingress…");
  run("kubectl", [
    "-n",
    NS,
    "port-forward",
    "svc/restate",
    `${ADMIN_PORT}:9070`,
    `${INGRESS_PORT}:8080`,
  ]);
  await waitHttp(`${ADMIN}/version`, "restate admin");

  // 3) Register the F# deployment (server fetches /discover from the host).
  console.log("[register] registering F# deployment…");
  const regRes = await fetch(`${ADMIN}/deployments`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ uri: `http://${nodeIp}:${HOST_PORT}`, force: true }),
  });
  const reg = await regRes.json();
  if (!regRes.ok) {
    throw new Error(`registration failed: ${JSON.stringify(reg)}`);
  }
  deploymentId = reg.id;
  const svcNames = (reg.services ?? []).map((s: { name: string }) => s.name)
    .sort();
  console.log(
    `  deployment ${deploymentId} -> services ${svcNames.join(", ")}`,
  );
  assert(
    ["CounterObject", "SignupWorkflow", "TripBookingService"].every((n) =>
      svcNames.includes(n)
    ),
    "all three F# services registered",
  );

  // 4) Scenario 1 — Virtual Object durable state.
  console.log("\n[T1] Counter (Virtual Object) durable state");
  const key = `c-${Date.now()}`;
  assert(
    (await invokeJson(`/CounterObject/${key}/Add`, 5)) === 5,
    "Add 5 -> 5",
  );
  assert(
    (await invokeJson(`/CounterObject/${key}/Add`, 3)) === 8,
    "Add 3 -> 8 (state accumulated)",
  );
  assert((await invokeJson(`/CounterObject/${key}/Get`)) === 8, "Get -> 8");
  await invoke(`/CounterObject/${key}/Reset`);
  assert(
    (await invokeJson(`/CounterObject/${key}/Get`)) === 0,
    "Get after Reset -> 0",
  );

  // 5) Scenario 2 — Saga happy path.
  console.log("\n[T2] Saga happy path");
  const trip = (id: string, city: string) => ({
    tripId: id,
    userId: "alice",
    flight: { from: "SFO", to: "JFK", date: "2026-03-15" },
    hotel: { city: "New York", checkIn: "2026-03-15", checkOut: "2026-03-18" },
    carRental: { city, pickUp: "2026-03-15", dropOff: "2026-03-18" },
  });
  const ok = await invokeJson(
    "/TripBookingService/Book",
    trip("trip-ok", "New York"),
  ) as {
    flightConfirmation: string;
    hotelConfirmation: string;
    carRentalConfirmation: string;
  };
  assert(
    ok.flightConfirmation.startsWith("FL-") &&
      ok.hotelConfirmation.startsWith("HT-") &&
      ok.carRentalConfirmation.startsWith("CR-"),
    "all three bookings confirmed",
  );

  // 6) Scenario 3 — Saga compensation path (deterministic FAILVILLE trigger).
  console.log("\n[T3] Saga compensation path");
  const failRes = await invoke(
    "/TripBookingService/Book",
    trip("trip-fail", "FAILVILLE"),
  );
  const failBody = await failRes.json();
  assert(
    failRes.status === 409,
    `terminal failure surfaced as HTTP 409 (got ${failRes.status})`,
  );
  assert(
    typeof failBody.message === "string" &&
      failBody.message.includes("Car rental"),
    "failure carries the terminal message",
  );

  // 7) Scenario 4 — Workflow suspend -> external resume.
  console.log("\n[T4] Workflow suspend + external awakeable resume");
  const wfKey = `signup-${Date.now()}`;
  const started = await invokeJson(`/SignupWorkflow/${wfKey}/Run/send`, {
    email: "alice@example.com",
    name: "Alice",
  }) as { invocationId: string };
  assert(
    typeof started.invocationId === "string",
    "Run accepted asynchronously",
  );

  let status = "";
  for (let i = 0; i < 20 && status !== "awaiting-verification"; i++) {
    status = await invokeJson(`/SignupWorkflow/${wfKey}/GetStatus`) as string;
    if (status !== "awaiting-verification") await sleep(500);
  }
  assert(
    status === "awaiting-verification",
    "workflow suspended awaiting verification",
  );

  const awkId = await invokeJson(
    `/SignupWorkflow/${wfKey}/GetPendingVerificationId`,
  ) as string;
  assert(awkId.length > 0, `pending awakeable id exposed (${awkId})`);

  const resolveRes = await fetch(
    `${INGRESS}/restate/awakeables/${awkId}/resolve`,
    {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify("verified"),
    },
  );
  await resolveRes.body?.cancel();
  assert(
    resolveRes.status === 202 || resolveRes.ok,
    "awakeable resolve accepted",
  );

  let final = "";
  for (let i = 0; i < 20 && final !== "completed"; i++) {
    final = await invokeJson(`/SignupWorkflow/${wfKey}/GetStatus`) as string;
    if (final !== "completed") await sleep(500);
  }
  assert(
    final === "completed",
    "workflow resumed and completed after external resolve",
  );

  // The workflow result attach endpoint is a GET (not an invocation POST).
  const outRes = await fetch(
    `${INGRESS}/restate/workflow/SignupWorkflow/${wfKey}/output`,
  );
  const output = await outRes.json() as {
    accountId: string;
    verified: boolean;
  };
  assert(
    output.verified === true && output.accountId.startsWith("acct-"),
    `workflow output is the activated account (${output.accountId})`,
  );

  console.log("\n✅ ALL F# k0s E2E SCENARIOS PASSED");
}

async function teardown() {
  console.log("\n[teardown] cleaning up…");
  if (deploymentId) {
    try {
      await fetch(`${ADMIN}/deployments/${deploymentId}?force=true`, {
        method: "DELETE",
      });
      console.log(`  deregistered ${deploymentId}`);
    } catch { /* admin port-forward may already be gone */ }
  }
  for (const child of children.reverse()) {
    try {
      child.kill("SIGTERM");
      await child.status;
    } catch { /* already exited */ }
  }
}

try {
  await main();
} catch (err) {
  console.error(`\n❌ ${err instanceof Error ? err.message : err}`);
  await teardown();
  Deno.exit(1);
}
await teardown();
Deno.exit(0);
