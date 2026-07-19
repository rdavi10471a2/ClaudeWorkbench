// End-to-end flow smoke for the governed edit loop, driven entirely in TypeScript
// against the live sidecar + host. It proves the paths that carry the safety
// guarantee, each with a real Claude turn:
//
//   1. accept      prompt (auto-approve) -> stage -> operator Accept -> written + GATE-2 build passes
//   2. reject      prompt (auto-approve) -> stage -> operator Reject -> watched source UNCHANGED
//   3. multi-file  prompt -> stage two files in one session -> accept both -> both written, terminal build passes
//
// Accept/Reject are `EngineReviewWorkflow` (H1/H2) reached via the host's test-only
// /review endpoints, the SAME methods the Merge Review dialog buttons call. This is
// an INTEGRATION smoke: needs the full stack on the CalculatorSample fixture and
// makes real Claude turns. Every test restores the fixture on the way out.
//
// Turn completion is detected by POLLING (GET /health activeTurn) and staged work is
// read from GET /review/pending — NOT from the SSE /events stream. With the host, the
// smoke, and possibly a browser all subscribed to /events, the sidecar's unguarded
// fan-out (a throwing subscriber tears down the stream) made SSE-based turn detection
// flaky. Polling is independent of the event fan-out and is bulletproof here.
//
// Run: see sidecar/src/smoke/README.md   (npm run smoke)

import test, { before } from "node:test";
import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { dirname, join, basename } from "node:path";

const HOST = process.env.WORKBENCH_HOST ?? "http://127.0.0.1:6100";
const SIDECAR = process.env.SIDECAR_BASE ?? "http://127.0.0.1:6110";
const TURN_TIMEOUT_MS = Number(process.env.SMOKE_TURN_TIMEOUT_MS ?? 300_000);

// The fixture files the smoke may touch. Snapshotted before each test, restored after.
const FIXTURE_FILES = ["Calculator.cs", "AdvancedCalculations.cs", "Program.cs"];

interface PendingItem {
  stagedRecordId: string;
  relativePath: string;
  sessionId: string;
  createdAtUtc: string;
}
interface AcceptResult {
  accepted: boolean;
  message: string;
  agentSummary?: string | null;
}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

async function getJson<T>(url: string): Promise<T> {
  const res = await fetch(url);
  assert.ok(res.ok, `GET ${url} -> ${res.status}`);
  return (await res.json()) as T;
}

async function postJson<T>(url: string, body: unknown): Promise<{ status: number; body: T }> {
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const parsed = (await res.json().catch(() => ({}))) as T;
  return { status: res.status, body: parsed };
}

async function pending(): Promise<PendingItem[]> {
  return getJson<PendingItem[]>(`${HOST}/review/pending`);
}

// Submit a prompt (auto-approve) and wait for the turn to leave the sidecar's active
// slot — success OR error both clear activeTurn. Returns the records that became
// pending DURING this turn (new since submission), newest first, so a test only ever
// acts on its own staged edits regardless of anything left over from a prior run.
async function runTurnAndCollectStaged(prompt: string): Promise<PendingItem[]> {
  const priorIds = new Set((await pending()).map((item) => item.stagedRecordId));

  const submit = await postJson<{ turnId?: string; error?: string }>(`${SIDECAR}/prompt`, {
    prompt,
    toolPolicy: { autoApprove: true },
  });
  assert.equal(submit.status, 202, `POST /prompt -> ${submit.status} (${JSON.stringify(submit.body)})`);
  const turnId = submit.body.turnId;
  assert.ok(turnId, "expected a turnId");

  // Poll activeTurn until this turn is no longer the active one.
  const deadline = Date.now() + TURN_TIMEOUT_MS;
  for (;;) {
    const health = await getJson<{ activeTurn: string | null }>(`${SIDECAR}/health`);
    if (health.activeTurn !== turnId) break;
    assert.ok(Date.now() < deadline, `turn ${turnId} did not finish within ${TURN_TIMEOUT_MS}ms`);
    await sleep(2_000);
  }

  return (await pending())
    .filter((item) => !priorIds.has(item.stagedRecordId))
    .sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
}

// Snapshot the fixture, run the body, and ALWAYS restore every file + reset the
// thread — so each test is independent and the fixture is left pristine.
async function withFixture(body: (fixtureDir: string) => Promise<void>): Promise<void> {
  const health = await getJson<{ watchedSolutionPath?: string }>(`${HOST}/health`);
  const solutionPath = health.watchedSolutionPath ?? "";
  assert.ok(
    /CalculatorSample\.slnx$/i.test(solutionPath),
    `Refusing to run: host is watching '${solutionPath}', not the CalculatorSample fixture.`,
  );
  const fixtureDir = dirname(solutionPath);

  const snapshot = new Map<string, string>();
  for (const name of FIXTURE_FILES) {
    snapshot.set(name, await readFile(join(fixtureDir, name), "utf8"));
  }
  try {
    await body(fixtureDir);
  } finally {
    for (const [name, content] of snapshot) {
      await writeFile(join(fixtureDir, name), content, "utf8");
    }
    // Give each test a clean thread. This exercises the sidecar's new-thread lifecycle
    // between turns — which used to race turn-2's startup (index.ts read-loop finally
    // clobbering the new turn's state) until that finally was guarded on `activeQuery`.
    await fetch(`${SIDECAR}/new-thread`, { method: "POST" }).catch(() => {});
  }
}

// Force an index rebuild before any turn. A config-launched fixture starts with a
// COLD index (startup only provisions the skeleton), which makes start_monitor_session
// flaky — it can't prove the single owning project until the solution is indexed.
before(async () => {
  const guard = await getJson<{ watchedSolutionPath?: string }>(`${HOST}/health`);
  assert.ok(
    /CalculatorSample\.slnx$/i.test(guard.watchedSolutionPath ?? ""),
    `Refusing to run: host is watching '${guard.watchedSolutionPath}', not the CalculatorSample fixture.`,
  );
  const warm = await postJson<{ warmed?: boolean }>(`${HOST}/review/warmup`, {});
  assert.equal(warm.status, 200, `index warmup (POST /review/warmup) failed: ${warm.status}`);
  assert.ok(warm.body.warmed, "index warmup did not report warmed");
}, { timeout: 180_000 });

test("accept: prompt -> stage -> operator accept -> written + build passes", { timeout: TURN_TIMEOUT_MS + 60_000 }, async () => {
  await withFixture(async (fixtureDir) => {
    const staged = await runTurnAndCollectStaged(
      "Add a public method `Modulo(double a, double b)` to the Calculator class in Calculator.cs " +
        "that returns a % b. Change ONLY Calculator.cs. Stage it for review and then stop.",
    );
    const record = staged.find((item) => basename(item.relativePath).toLowerCase() === "calculator.cs");
    assert.ok(record, `agent never staged Calculator.cs (staged: ${JSON.stringify(staged)})`);

    const { body } = await postJson<AcceptResult>(`${HOST}/review/accept`, { stagedRecordId: record.stagedRecordId });
    assert.ok(body.accepted, `accept was rejected: ${body.message}`);
    assert.match(body.agentSummary ?? "", /Build passed/i, `expected a passing build, got: ${body.agentSummary}`);

    const written = await readFile(join(fixtureDir, "Calculator.cs"), "utf8");
    assert.match(written, /Modulo/, "accepted change is not on disk");
  });
});

test("reject: prompt -> stage -> operator reject -> watched source unchanged", { timeout: TURN_TIMEOUT_MS + 60_000 }, async () => {
  await withFixture(async (fixtureDir) => {
    const before = await readFile(join(fixtureDir, "Calculator.cs"), "utf8");
    const staged = await runTurnAndCollectStaged(
      "Add a public method `Triple(double x)` to the Calculator class in Calculator.cs that returns x * 3. " +
        "Change ONLY Calculator.cs. Stage it for review and then stop.",
    );
    const record = staged.find((item) => basename(item.relativePath).toLowerCase() === "calculator.cs");
    assert.ok(record, `agent never staged Calculator.cs (staged: ${JSON.stringify(staged)})`);

    const { body } = await postJson<{ rejected: boolean; message: string }>(`${HOST}/review/reject`, {
      stagedRecordId: record.stagedRecordId,
    });
    assert.ok(body.rejected, `reject failed: ${body.message}`);

    // The core reject guarantee: nothing was written to watched source.
    const after = await readFile(join(fixtureDir, "Calculator.cs"), "utf8");
    assert.equal(after, before, "reject must NOT modify watched source, but Calculator.cs changed");
    assert.doesNotMatch(after, /Triple/, "rejected method leaked into Calculator.cs");

    // And the rejected record is no longer pending.
    const stillPending = (await pending()).some((item) => item.stagedRecordId === record.stagedRecordId);
    assert.equal(stillPending, false, "rejected record is still pending");
  });
});

test("multi-file: stage two files in one session -> accept both -> both written, terminal build passes", { timeout: TURN_TIMEOUT_MS + 90_000 }, async () => {
  await withFixture(async (fixtureDir) => {
    const staged = await runTurnAndCollectStaged(
      "Make two changes and stage BOTH files in a single monitor session: " +
        "(1) add a public method `Negate(double x)` returning -x to the Calculator class in Calculator.cs, and " +
        "(2) add a public method `Cube(double x)` returning x * x * x to the AdvancedCalculations class in AdvancedCalculations.cs. " +
        "Stage both for review and then stop.",
    );
    const paths = staged.map((r) => basename(r.relativePath).toLowerCase());
    assert.ok(paths.includes("calculator.cs"), `Calculator.cs not staged (staged: ${JSON.stringify(staged)})`);
    assert.ok(paths.includes("advancedcalculations.cs"), `AdvancedCalculations.cs not staged (staged: ${JSON.stringify(staged)})`);

    // Accept every record in the session; the engine builds once on the terminal accept.
    let summaries = "";
    for (const record of [...staged].reverse()) {
      const { body } = await postJson<AcceptResult>(`${HOST}/review/accept`, { stagedRecordId: record.stagedRecordId });
      assert.ok(body.accepted, `accept failed for ${record.relativePath}: ${body.message}`);
      summaries += ` ${body.agentSummary ?? ""}`;
    }
    // The terminal accept ran the real GATE-2 build over the combined session overlay.
    assert.match(summaries, /Build passed/i, `expected a passing terminal build, got: ${summaries.trim()}`);

    const calc = await readFile(join(fixtureDir, "Calculator.cs"), "utf8");
    const adv = await readFile(join(fixtureDir, "AdvancedCalculations.cs"), "utf8");
    assert.match(calc, /Negate/, "Calculator.cs change not on disk");
    assert.match(adv, /Cube/, "AdvancedCalculations.cs change not on disk");
  });
});
