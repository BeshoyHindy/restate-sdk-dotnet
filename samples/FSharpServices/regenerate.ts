#!/usr/bin/env -S deno run --allow-run --allow-env --allow-read
//
// Regenerates samples/FSharpServices/Services.Generated.fs from Services.fs with the
// Restate.Sdk.FSharp.Myriad generator — the F# analog of the C# Roslyn source generator.
//
//   deno run --allow-run --allow-env --allow-read samples/FSharpServices/regenerate.ts
//
// Notes on the (Myriad 0.8.3) toolchain:
//   * the `myriad` CLI is a net6.0 tool, so it is launched with DOTNET_ROLL_FORWARD=LatestMajor on a
//     net10-only machine;
//   * the plugin pins FSharp.Core 6.0.x so its Fantomas.Core/FCS 6.1.1 calls line up with the tool's
//     own copies at load time (0.8.5+ would make this automatic via PreferSharedTypes);
//   * the plugin must be passed as an ABSOLUTE path.

const repoRoot = new URL("../../", import.meta.url).pathname;
const plugin =
  `${repoRoot}src/Restate.Sdk.FSharp.Myriad/bin/Release/net10.0/Restate.Sdk.FSharp.Myriad.dll`;
const input = `${repoRoot}samples/FSharpServices/Services.fs`;
const output = `${repoRoot}samples/FSharpServices/Services.Generated.fs`;

async function run(
  cmd: string,
  args: string[],
  env: Record<string, string> = {},
) {
  console.log(`$ ${cmd} ${args.join(" ")}`);
  const proc = new Deno.Command(cmd, {
    args,
    cwd: repoRoot,
    env: { ...Deno.env.toObject(), ...env },
    stdout: "inherit",
    stderr: "inherit",
  });
  const { success } = await proc.output();
  if (!success) throw new Error(`${cmd} ${args.join(" ")} failed`);
}

await run("dotnet", ["tool", "restore"]);
await run("dotnet", [
  "build",
  "src/Restate.Sdk.FSharp.Myriad/Restate.Sdk.FSharp.Myriad.fsproj",
  "-c",
  "Release",
  "-v",
  "q",
]);
await run(
  "dotnet",
  ["myriad", "--inputfile", input, "--outputfile", output, "--plugin", plugin],
  { DOTNET_ROLL_FORWARD: "LatestMajor" },
);

console.log(`\n✅ regenerated ${output}`);
