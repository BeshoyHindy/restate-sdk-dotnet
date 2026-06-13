#!/usr/bin/env -S deno run --allow-read
// Coverage gate (plan 07 §1.1(6)). Reads a MERGED Cobertura report, aggregates line/branch
// coverage per namespace-prefix rule from eng/coverage-thresholds.json (longest-prefix match
// per class), prints a per-rule table plus the worst offenders, and fails the process if any
// rule is below threshold OR the --audit-internal class audit finds a hand-written type missing
// from the report (the anti-gaming check: an attribute/filter change that silently deletes
// hand-written code from the accounting would otherwise make the thresholds trivially green).
//
// Deno only (repo policy bans Python). Parsing via npm:fast-xml-parser@4 — Cobertura is small,
// flat XML; no other deps.
//
// The gate's input is ALWAYS the reportgenerator-MERGED report (plan 07 §1.1/§1.4), never the
// raw per-run coverage.cobertura.xml files — multiple test partitions each emit their own raw
// file and only the merge sums them. Canonical COLLECT + MERGE + GATE sequence:
//
//   rm -rf TestResults artifacts/coverage
//   dotnet test test/Restate.Sdk.Tests/Restate.Sdk.Tests.csproj -c Release \
//     --collect:"XPlat Code Coverage" --settings eng/coverage.runsettings \
//     --results-directory TestResults
//   dotnet reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" \
//     -targetdir:artifacts/coverage/report -reporttypes:Cobertura
//   deno run --allow-read eng/coverage-gate.ts \
//     artifacts/coverage/report/Cobertura.xml --audit-internal src/Restate.Sdk
//
// Run (the gate itself takes the MERGED file as <cobertura.xml>):
//   deno run --allow-read eng/coverage-gate.ts <merged-cobertura.xml> [--phase-in] [--audit-internal <srcDir>]

import { XMLParser } from "npm:fast-xml-parser@4";

// ---- Threshold config (single source of truth) --------------------------------------------

interface Rule {
  prefix: string;
  line: number;
  branch: number;
}

// A single genuinely-unreachable source line that is excluded from the line accounting (NOT a
// threshold knob). Each entry pinpoints exactly ONE line in ONE file with a written reason, so the
// 100% line target keeps full force on every OTHER (reachable) line of the class. This is strictly
// more precise than lowering a threshold, which would mask regressions class-wide. Used ONLY for
// in-method defensive lines that cannot carry a [ExcludeFromCodeCoverage] member attribute (an
// attribute can only target whole members) — see plan 07 §1.3(3) and its "Coverage exclusions"
// appendix. Whole-member dead code uses the source attribute instead; this list never grows to hide
// a testable gap.
interface UnreachableLine {
  file: string; // filename SUFFIX as it appears in the cobertura <class filename=...> attribute
  line: number;
  reason: string;
}

interface ThresholdConfig {
  rules: Rule[];
  phaseInOverrides?: { rules: Rule[] };
  unreachableLines?: UnreachableLine[];
}

// ---- Cobertura model ----------------------------------------------------------------------

interface ClassCoverage {
  name: string; // fully-qualified type name from the <class name=...> attribute
  linesCovered: number;
  linesValid: number;
  branchesCovered: number;
  branchesValid: number;
  uncoveredLines: number[];
}

interface RuleTotals {
  rule: Rule;
  linesCovered: number;
  linesValid: number;
  branchesCovered: number;
  branchesValid: number;
  classes: ClassCoverage[];
}

const THRESHOLDS_PATH = new URL("./coverage-thresholds.json", import.meta.url);

function loadThresholds(): ThresholdConfig {
  return JSON.parse(Deno.readTextFileSync(THRESHOLDS_PATH));
}

// fast-xml-parser collapses a single-element array into the bare object; this normalizes both
// shapes (and the empty/undefined case) into a plain array so the rest of the code is uniform.
function asArray<T>(value: T | T[] | undefined): T[] {
  if (value === undefined || value === null) return [];
  return Array.isArray(value) ? value : [value];
}

// Builds a fast lookup of allowlisted (file-suffix, line) pairs marked genuinely unreachable.
function buildUnreachableIndex(entries: UnreachableLine[] | undefined): {
  has: (filename: string, line: number) => boolean;
} {
  const list = entries ?? [];
  return {
    has(filename: string, line: number): boolean {
      return list.some((entry) => filename.endsWith(entry.file) && entry.line === line);
    },
  };
}

function parseCobertura(
  xmlText: string,
  unreachable: { has: (filename: string, line: number) => boolean },
): ClassCoverage[] {
  const parser = new XMLParser({
    ignoreAttributes: false,
    attributeNamePrefix: "@_",
  });
  const doc = parser.parse(xmlText);

  // Cobertura shape: coverage > packages > package > classes > class > lines > line.
  const packages = asArray(doc?.coverage?.packages?.package);
  const classes: ClassCoverage[] = [];

  for (const pkg of packages) {
    for (const cls of asArray(pkg?.classes?.class)) {
      const filename = String(cls["@_filename"] ?? "");
      const lines = asArray(cls?.lines?.line);
      let linesCovered = 0;
      let linesValid = 0;
      let branchesCovered = 0;
      let branchesValid = 0;
      const uncoveredLines: number[] = [];

      for (const line of lines) {
        const hits = Number(line["@_hits"] ?? 0);
        const lineNumber = Number(line["@_number"] ?? 0);
        // A genuinely-unreachable allowlisted line is removed from BOTH numerator and denominator:
        // it cannot be covered (no reachable trigger) and must not count against the reachable
        // surface. Every other line still gates at 100.
        if (unreachable.has(filename, lineNumber)) continue;
        linesValid++;
        if (hits > 0) {
          linesCovered++;
        } else {
          uncoveredLines.push(lineNumber);
        }
        // Branch coverage is encoded per-line as condition-coverage="NN% (covered/valid)".
        // coverlet emits @_branch="True" (capital T); compare case-insensitively so the branch
        // gate actually counts branches instead of vacuously passing on 0/0.
        if (String(line["@_branch"]).toLowerCase() === "true") {
          const cc: string = String(line["@_condition-coverage"] ?? "");
          const match = cc.match(/\((\d+)\/(\d+)\)/);
          if (match) {
            branchesCovered += Number(match[1]);
            branchesValid += Number(match[2]);
          }
        }
      }

      classes.push({
        name: String(cls["@_name"] ?? ""),
        linesCovered,
        linesValid,
        branchesCovered,
        branchesValid,
        uncoveredLines,
      });
    }
  }

  return classes;
}

// Longest-prefix match: a class is attributed to the most specific rule whose prefix it starts
// with. Classes matching no rule fall to the catch-all (shortest) rule — which the config
// guarantees is "Restate.Sdk".
function ruleFor(className: string, rules: Rule[]): Rule {
  let best: Rule | undefined;
  for (const rule of rules) {
    if (
      className === rule.prefix ||
      className.startsWith(rule.prefix + ".")
    ) {
      if (best === undefined || rule.prefix.length > best.prefix.length) {
        best = rule;
      }
    }
  }
  // The config always carries a "Restate.Sdk" catch-all, so this fallback is belt-and-braces.
  return best ?? rules.reduce((a, b) => (a.prefix.length <= b.prefix.length ? a : b));
}

function aggregate(classes: ClassCoverage[], rules: Rule[]): RuleTotals[] {
  const byPrefix = new Map<string, RuleTotals>();
  for (const rule of rules) {
    byPrefix.set(rule.prefix, {
      rule,
      linesCovered: 0,
      linesValid: 0,
      branchesCovered: 0,
      branchesValid: 0,
      classes: [],
    });
  }

  for (const cls of classes) {
    const rule = ruleFor(cls.name, rules);
    const totals = byPrefix.get(rule.prefix)!;
    totals.linesCovered += cls.linesCovered;
    totals.linesValid += cls.linesValid;
    totals.branchesCovered += cls.branchesCovered;
    totals.branchesValid += cls.branchesValid;
    totals.classes.push(cls);
  }

  return rules.map((rule) => byPrefix.get(rule.prefix)!);
}

function pct(covered: number, valid: number): number {
  // An empty denominator (no lines/branches in a namespace) is vacuously 100% — there is
  // nothing left uncovered, so it cannot fail a threshold.
  if (valid === 0) return 100;
  return (covered / valid) * 100;
}

function fmtPct(value: number): string {
  return value.toFixed(2).padStart(7);
}

// Apply phase-in overrides ON TOP of the base rules: an override for a prefix replaces that
// prefix's thresholds; non-overridden prefixes keep their base values.
function applyOverrides(base: Rule[], overrides: Rule[]): Rule[] {
  const overrideByPrefix = new Map(overrides.map((rule) => [rule.prefix, rule]));
  return base.map((rule) => overrideByPrefix.get(rule.prefix) ?? rule);
}

// ---- Class audit (anti-gaming) ------------------------------------------------------------

// Enumerate every top-level type declaration under <srcDir>, then FAIL unless each appears in
// the report (as a <class> name or as the declaring-type prefix of a nested-class entry).
// Catches any filter/attribute change that silently removes hand-written code from coverage.
function enumerateDeclaredTypes(srcDir: string): Map<string, string> {
  // Matches `... class|struct|record [class|struct] Name` at any indent, capturing the keyword
  // and the Name. record-struct / record-class variants are handled by the optional second
  // keyword group. `enum` and `interface` are intentionally EXCLUDED: they carry no executable
  // IL, so coverlet never emits a <class> entry for them — auditing them would be a guaranteed
  // false positive (the anti-gaming check only protects executable hand-written code).
  const typeDecl =
    /^\s*(?:public|internal|private|protected|sealed|abstract|static|partial|file|readonly|ref|unsafe|\s)*\b(class|struct|record)(?:\s+(?:class|struct))?\s+([A-Za-z_][A-Za-z0-9_]*)/;

  const declared = new Map<string, string>(); // typeName -> file (first sighting)
  for (const entry of walkCsFiles(srcDir)) {
    const text = Deno.readTextFileSync(entry);
    for (const rawLine of text.split("\n")) {
      const line = rawLine.replace(/\/\/.*$/, "");
      const match = line.match(typeDecl);
      if (match) {
        const name = match[2];
        if (!declared.has(name)) declared.set(name, entry);
      }
    }
  }
  return declared;
}

function* walkCsFiles(dir: string): Generator<string> {
  for (const entry of Deno.readDirSync(dir)) {
    const path = `${dir}/${entry.name}`;
    if (entry.isDirectory) {
      // obj/ and bin/ carry generated + compiled artifacts, never hand-written source.
      if (entry.name === "obj" || entry.name === "bin") continue;
      yield* walkCsFiles(path);
    } else if (entry.isFile && entry.name.endsWith(".cs")) {
      yield path;
    }
  }
}

// Types the audit must NOT flag as "silently deleted hand-written code". Two sanctioned reasons,
// each with a written justification per plan 07 §1.3 so the allowlist stays auditable:
//
//   (a) GENERATED / FILTER-EXCLUDED — stripped in eng/coverage.runsettings on purpose.
//       `Log` hosts only [LoggerMessage] partial methods whose bodies are source-generated;
//       the hand-written half is bodiless, so the type is filtered out of the report.
//       `DiscoveryJsonContext` is a `partial class : JsonSerializerContext;` whose entire body
//       (GetJsonTypeInfo / per-type converters / property names) is emitted by the
//       System.Text.Json source generator into obj/**/*.g.cs — none of it hand-written; it is
//       filtered out by the same <Exclude> rule so its hundreds of generated lines cannot tank
//       the Internal namespace.
//
//   (b) NO-EXECUTABLE-CODE — coverlet emits NO <class> entry because the type carries zero
//       IL-bearing members, so its absence is correct, not a stripped-coverage gap:
//         - HandlerAttribute / SharedHandlerAttribute: `sealed class X : HandlerAttributeBase;`
//           — empty body, no ctor/field/method of their own. Every real property lives on the
//           abstract base HandlerAttributeBase, which DOES appear in the report.
//         - ObjectContext / WorkflowContext / SharedWorkflowContext: abstract classes whose
//           every member is an `abstract` declaration (no body). All concrete behavior lives on
//           the sibling SharedObjectContext (present in the report) and the Default* contexts
//           under Internal.Context (present in the report).
//       These are facade/marker types in the public API surface; there is literally nothing to
//       instrument, so they can never appear no matter how the handlers are exercised.
//         - SendResponse: `private sealed record SendResponse(string InvocationId);` nested in
//           RestateClient. A positional record with NO hand-written body — every member (primary
//           ctor, the InvocationId property, Equals/GetHashCode/ToString/Deconstruct/EqualityContract)
//           is compiler-synthesized and carries [CompilerGenerated], so coverlet folds it and emits
//           no distinct <class> entry even though SendAsync deserializes into it (the ingress
//           invocation-id round-trip IS exercised by RestateClientTests). Its absence is correct,
//           not a stripped-coverage gap; the executable RestateClient/ServiceHandle code that USES
//           it is present and covered.
const SANCTIONED_EXCLUSIONS = new Set<string>([
  "Log",
  "DiscoveryJsonContext",
  "HandlerAttribute",
  "SharedHandlerAttribute",
  "ObjectContext",
  "WorkflowContext",
  "SharedWorkflowContext",
  "SendResponse",
]);

// A declared type is "present" in the report if some class entry's simple type name (last
// dotted segment, before any '/' nested-type separator) equals it, OR a class entry is nested
// inside it (name contains the type as a segment). Generated/excluded members legitimately
// vanish; this audit only protects HAND-WRITTEN core types, so the caller scopes <srcDir>.
function auditClasses(
  declared: Map<string, string>,
  classes: ClassCoverage[],
): string[] {
  const reportTypeNames = new Set<string>();
  for (const cls of classes) {
    // Cobertura names nested types Outer.Inner or Outer/Inner depending on emitter; split on both.
    // Generic types carry a CLR backtick-arity suffix (LazyRunFuture`1); strip it so the bare
    // source name (LazyRunFuture) matches.
    for (const segment of cls.name.split(/[./]/)) {
      reportTypeNames.add(segment.replace(/`\d+$/, ""));
    }
  }

  const missing: string[] = [];
  for (const [name, file] of declared) {
    if (SANCTIONED_EXCLUSIONS.has(name)) continue;
    if (!reportTypeNames.has(name)) {
      missing.push(`${name}  (declared in ${file})`);
    }
  }
  return missing;
}

// ---- Main ----------------------------------------------------------------------------------

function parseArgs(args: string[]): {
  reportPath?: string;
  phaseIn: boolean;
  auditDir?: string;
} {
  let reportPath: string | undefined;
  let phaseIn = false;
  let auditDir: string | undefined;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === "--phase-in") {
      phaseIn = true;
    } else if (arg === "--audit-internal") {
      auditDir = args[++i];
    } else if (!arg.startsWith("--")) {
      reportPath = arg;
    }
  }
  return { reportPath, phaseIn, auditDir };
}

function main(): number {
  const { reportPath, phaseIn, auditDir } = parseArgs(Deno.args);
  if (!reportPath) {
    console.error(
      "usage: coverage-gate.ts <cobertura.xml> [--phase-in] [--audit-internal <srcDir>]",
    );
    return 1;
  }

  const config = loadThresholds();
  let rules = config.rules;
  if (phaseIn && config.phaseInOverrides) {
    rules = applyOverrides(rules, config.phaseInOverrides.rules);
  }

  const xmlText = Deno.readTextFileSync(reportPath);
  const unreachable = buildUnreachableIndex(config.unreachableLines);
  const classes = parseCobertura(xmlText, unreachable);
  const totals = aggregate(classes, rules);

  // ---- Per-rule table ----
  console.log("\nCoverage gate — per-namespace thresholds" + (phaseIn ? " (phase-in)" : ""));
  console.log(
    "  prefix                                       lines%   (cov/val)   thr   branch%  (cov/val)   thr",
  );
  let failed = false;
  for (const total of totals) {
    const lp = pct(total.linesCovered, total.linesValid);
    const bp = pct(total.branchesCovered, total.branchesValid);
    const lineOk = lp + 1e-9 >= total.rule.line;
    const branchOk = bp + 1e-9 >= total.rule.branch;
    const ok = lineOk && branchOk;
    if (!ok) failed = true;
    const flag = ok ? "PASS" : "FAIL";
    console.log(
      `  ${total.rule.prefix.padEnd(44)} ${fmtPct(lp)} ` +
        `(${total.linesCovered}/${total.linesValid})`.padEnd(12) +
        ` ${String(total.rule.line).padStart(3)}  ${fmtPct(bp)} ` +
        `(${total.branchesCovered}/${total.branchesValid})`.padEnd(12) +
        ` ${String(total.rule.branch).padStart(3)}  ${flag}`,
    );

    // For a failing rule, surface the 10 worst classes (by uncovered line count) with line nos.
    if (!ok) {
      const worst = [...total.classes]
        .filter((c) => c.linesValid - c.linesCovered > 0 || c.branchesValid - c.branchesCovered > 0)
        .sort((a, b) =>
          (b.linesValid - b.linesCovered) - (a.linesValid - a.linesCovered)
        )
        .slice(0, 10);
      for (const cls of worst) {
        const missLines = cls.uncoveredLines.slice(0, 25).join(",");
        console.log(
          `      - ${cls.name}: lines ${cls.linesCovered}/${cls.linesValid}` +
            `, branches ${cls.branchesCovered}/${cls.branchesValid}` +
            (missLines ? `  uncovered: ${missLines}` : ""),
        );
      }
    }
  }

  // ---- Genuinely-unreachable line accounting (auditable, not a threshold knob) ----
  const unreachableList = config.unreachableLines ?? [];
  if (unreachableList.length > 0) {
    console.log(
      `\nUnreachable-line allowlist: ${unreachableList.length} defensive line(s) excluded ` +
        "from the line accounting (plan 07 §1.3 appendix):",
    );
    for (const entry of unreachableList) {
      console.log(`      - ${entry.file}:${entry.line} — ${entry.reason}`);
    }
  }

  // ---- Class audit ----
  if (auditDir) {
    const declared = enumerateDeclaredTypes(auditDir);
    const missing = auditClasses(declared, classes);
    console.log(
      `\nClass audit (${auditDir}): ${declared.size} hand-written types declared, ` +
        `${missing.length} missing from report.`,
    );
    if (missing.length > 0) {
      failed = true;
      console.log(
        "  FAIL — these hand-written types are absent from the coverage accounting " +
          "(an attribute/filter likely stripped them):",
      );
      for (const item of missing) console.log(`      - ${item}`);
    } else {
      console.log("  PASS — every hand-written type is accounted for.");
    }
  }

  console.log(failed ? "\nGATE FAILED\n" : "\nGATE PASSED\n");
  return failed ? 1 : 0;
}

Deno.exit(main());
