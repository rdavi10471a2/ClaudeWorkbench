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
import { readFile, writeFile, mkdir, rm, readdir, cp } from "node:fs/promises";
import { dirname, join, basename } from "node:path";
import type { SidecarEvent } from "../events.js";

const HOST = process.env.WORKBENCH_HOST ?? "http://127.0.0.1:6100";
const SIDECAR = process.env.SIDECAR_BASE ?? "http://127.0.0.1:6110";
const TURN_TIMEOUT_MS = Number(process.env.SMOKE_TURN_TIMEOUT_MS ?? 300_000);

// Per-case evidence for offline analysis: the full agent chat (every assistant message + tool
// call, in order) and the raw event stream. Written under SMOKE_EVIDENCE_DIR (default
// sidecar/smoke-evidence, gitignored) as <case>.chat.txt + <case>.events.jsonl per turn.
const EVIDENCE_DIR = process.env.SMOKE_EVIDENCE_DIR ?? join(process.cwd(), "smoke-evidence");

// The pristine solution each test is reset FROM. Set it (setup copies CalculatorSample to a temp
// golden), and every test resets its watched copy by wiping and re-copying from here -- a
// completely fresh solution per case, no per-file snapshot/restore. When unset, the reset is a
// best-effort no-op and multi-accept cases will bleed into each other, so the harness warns.
const GOLDEN_DIR = process.env.SMOKE_GOLDEN_DIR ?? "";

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

// Capture the sidecar event bus for the duration of a turn: the agent's assistant text, every
// tool call, gate activity, and errors — the "full chat". Best-effort: a dropped stream loses
// capture, not the test (completion is detected by polling /health, not this). Runs concurrently
// with the turn and is aborted once the turn clears.
async function captureEvents(signal: AbortSignal): Promise<SidecarEvent[]> {
  const events: SidecarEvent[] = [];
  try {
    const res = await fetch(`${SIDECAR}/events`, { signal });
    if (!res.body) return events;
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const parts = buffer.split("\n");
      buffer = parts.pop() ?? "";
      for (const line of parts) {
        if (!line.startsWith("data:")) continue;
        const payload = line.slice(5).trim();
        if (!payload) continue;
        try {
          events.push(JSON.parse(payload) as SidecarEvent);
        } catch {
          // ignore a malformed frame
        }
      }
    }
  } catch {
    // aborted (turn done) or the stream dropped — keep whatever we captured
  }
  return events;
}

function summarizeInput(input: unknown): string {
  try {
    const s = JSON.stringify(input);
    return s.length > 200 ? `${s.slice(0, 197)}...` : s;
  } catch {
    return "";
  }
}

// Render captured events as a readable transcript: the prompt, each assistant message, and each
// tool call in order — the human-facing evidence for offline analysis.
function renderChat(caseName: string, prompt: string, events: SidecarEvent[]): string {
  const lines: string[] = [`# ${caseName}`, "", "## Prompt", prompt, "", "## Transcript", ""];
  for (const evt of events) {
    switch (evt.type) {
      case "user_prompt":
        lines.push(`USER: ${evt.text}`);
        break;
      case "assistant_text":
        if (evt.text.trim()) lines.push(`ASSISTANT: ${evt.text}`);
        break;
      case "tool_call_started":
        lines.push(`  -> ${evt.tool}(${summarizeInput(evt.input)})`);
        break;
      case "tool_call_finished":
        lines.push(`     ${evt.ok ? "ok" : "ERR"}${evt.summary ? `: ${evt.summary}` : ""}`);
        break;
      case "gate_request":
        lines.push(`  [gate] ${evt.tool} ${evt.filePath ?? ""}`.trimEnd());
        break;
      case "error":
        lines.push(`ERROR: ${evt.message}`);
        break;
      default:
        break;
    }
  }
  return `${lines.join("\n")}\n`;
}

// Write the case's evidence: the readable chat and the raw event stream.
async function saveEvidence(caseName: string, prompt: string, events: SidecarEvent[]): Promise<void> {
  try {
    await mkdir(EVIDENCE_DIR, { recursive: true });
    await writeFile(join(EVIDENCE_DIR, `${caseName}.chat.txt`), renderChat(caseName, prompt, events), "utf8");
    await writeFile(
      join(EVIDENCE_DIR, `${caseName}.events.jsonl`),
      `${events.map((e) => JSON.stringify(e)).join("\n")}\n`,
      "utf8",
    );
    // eslint-disable-next-line no-console
    console.log(`[smoke] evidence -> ${join(EVIDENCE_DIR, `${caseName}.chat.txt`)} (${events.length} events)`);
  } catch (err) {
    // eslint-disable-next-line no-console
    console.warn(`[smoke] could not write evidence for ${caseName}: ${(err as Error).message}`);
  }
}

// Submit a prompt (auto-approve), capture the full chat as evidence, and wait for the turn to
// leave the sidecar's active slot — success OR error both clear activeTurn. Returns the records
// that became pending DURING this turn (new since submission), newest first.
async function runTurnAndCollectStaged(caseName: string, prompt: string): Promise<PendingItem[]> {
  const priorIds = new Set((await pending()).map((item) => item.stagedRecordId));

  const capture = new AbortController();
  const eventsPromise = captureEvents(capture.signal);

  const submit = await postJson<{ turnId?: string; error?: string }>(`${SIDECAR}/prompt`, {
    prompt,
    toolPolicy: { autoApprove: true },
  });
  assert.equal(submit.status, 202, `POST /prompt -> ${submit.status} (${JSON.stringify(submit.body)})`);
  const turnId = submit.body.turnId;
  assert.ok(turnId, "expected a turnId");

  const deadline = Date.now() + TURN_TIMEOUT_MS;
  for (;;) {
    const health = await getJson<{ activeTurn: string | null }>(`${SIDECAR}/health`);
    if (health.activeTurn !== turnId) break;
    assert.ok(Date.now() < deadline, `turn ${turnId} did not finish within ${TURN_TIMEOUT_MS}ms`);
    await sleep(2_000);
  }

  capture.abort();
  const events = (await eventsPromise).filter((e) => e.turnId === turnId || e.type === "error");
  await saveEvidence(caseName, prompt, events);

  return (await pending())
    .filter((item) => !priorIds.has(item.stagedRecordId))
    .sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
}

// Run the body against the watched fixture, then ALWAYS reset it to a completely fresh copy of
// the golden and re-index — so every case starts from pristine source no matter what the last
// case wrote or created. The watched fixture is a throwaway temp copy; the golden and the repo
// sample are never mutated.
async function withFixture(body: (fixtureDir: string) => Promise<void>): Promise<void> {
  const health = await getJson<{ watchedSolutionPath?: string }>(`${HOST}/health`);
  const solutionPath = health.watchedSolutionPath ?? "";
  assert.ok(
    /CalculatorSample\.slnx$/i.test(solutionPath),
    `Refusing to run: host is watching '${solutionPath}', not the CalculatorSample fixture.`,
  );
  const fixtureDir = dirname(solutionPath);
  try {
    await body(fixtureDir);
  } finally {
    await resetFixture(fixtureDir);
    // A clean thread between cases (also exercises the sidecar new-thread lifecycle).
    await fetch(`${SIDECAR}/new-thread`, { method: "POST" }).catch(() => {});
  }
}

// Wipe the watched fixture's contents and re-copy the golden — a completely fresh solution, no
// per-file snapshot. Keeps the watched DIRECTORY itself (the host's file watcher holds it) and
// skips build output, then re-warms the index so start_monitor_session can resolve owners.
async function resetFixture(fixtureDir: string): Promise<void> {
  if (!GOLDEN_DIR) {
    return;
  }
  for (const entry of await readdir(fixtureDir)) {
    if (entry === "bin" || entry === "obj") continue;
    await rm(join(fixtureDir, entry), { recursive: true, force: true });
  }
  await cp(GOLDEN_DIR, fixtureDir, { recursive: true });
  await fetch(`${HOST}/review/warmup`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" }).catch(() => {});
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
      "accept",
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
      "reject",
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
      "multi-file",
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

test("multi-file reject: two files in one session -> reject one -> whole session voided, NOTHING written", { timeout: TURN_TIMEOUT_MS + 90_000 }, async () => {
  await withFixture(async (fixtureDir) => {
    const calcBefore = await readFile(join(fixtureDir, "Calculator.cs"), "utf8");
    const advBefore = await readFile(join(fixtureDir, "AdvancedCalculations.cs"), "utf8");

    const staged = await runTurnAndCollectStaged(
      "multi-reject",
      "Make two changes and stage BOTH files in a single monitor session: " +
        "(1) add a public method `Half(double x)` returning x / 2 to the Calculator class in Calculator.cs, and " +
        "(2) add a public method `Quadruple(double x)` returning x * 4 to the AdvancedCalculations class in AdvancedCalculations.cs. " +
        "Stage both for review and then stop.",
    );
    const paths = staged.map((r) => basename(r.relativePath).toLowerCase());
    assert.ok(paths.includes("calculator.cs"), `Calculator.cs not staged (staged: ${JSON.stringify(staged)})`);
    assert.ok(paths.includes("advancedcalculations.cs"), `AdvancedCalculations.cs not staged (staged: ${JSON.stringify(staged)})`);
    // One run must be one session, so a single reject can void the whole set.
    assert.equal(new Set(staged.map((r) => r.sessionId)).size, 1, `expected ONE session, got: ${JSON.stringify(staged.map((r) => r.sessionId))}`);

    // Reject ONE file. ADR-0005: a single reject voids the ENTIRE session.
    const target = staged.find((r) => basename(r.relativePath).toLowerCase() === "calculator.cs")!;
    const { body } = await postJson<{ rejected: boolean; message: string }>(`${HOST}/review/reject`, {
      stagedRecordId: target.stagedRecordId,
    });
    assert.ok(body.rejected, `reject failed: ${body.message}`);

    // The core guarantee: NOTHING was written — not the rejected file, nor the other file in the
    // voided session (which was never even accepted here).
    assert.equal(await readFile(join(fixtureDir, "Calculator.cs"), "utf8"), calcBefore, "reject must not write watched source (Calculator.cs changed)");
    assert.equal(await readFile(join(fixtureDir, "AdvancedCalculations.cs"), "utf8"), advBefore, "a rejected session must write NOTHING, but AdvancedCalculations.cs changed");

    // Every record from the voided session is gone from the pending queue.
    const stagedIds = new Set(staged.map((r) => r.stagedRecordId));
    const stillPending = (await pending()).filter((item) => stagedIds.has(item.stagedRecordId));
    assert.equal(stillPending.length, 0, `voided session's records still pending: ${JSON.stringify(stillPending)}`);
  });
});

test("multi-file build error: change that breaks a caller -> accept refused, watched source unchanged", { timeout: TURN_TIMEOUT_MS + 90_000 }, async () => {
  await withFixture(async (fixtureDir) => {
    const calcBefore = await readFile(join(fixtureDir, "Calculator.cs"), "utf8");
    const advBefore = await readFile(join(fixtureDir, "AdvancedCalculations.cs"), "utf8");

    // Rename Calculator.Multiply -> Mul and update Program, but DELIBERATELY leave
    // AdvancedCalculations.Power (which calls Multiply) untouched. The fixture is designed for
    // exactly this "break the caller in the other file" case, so the combined overlay/GATE-2 build
    // cannot compile. The instruction to stage despite the error is what makes this a build-gate
    // test and not an agent-judgement test.
    const staged = await runTurnAndCollectStaged(
      "build-error",
      "Change ONLY Calculator.cs and Program.cs. Do NOT modify AdvancedCalculations.cs, and do NOT add a Multiply method back, EVEN IF validation reports errors in AdvancedCalculations.cs — the operator will decide. " +
        "In Calculator.cs, rename the `Multiply` method to `Mul`. In Program.cs, change the multiply demo line to call `calculator.Mul(6, 7)`. " +
        "Stage both files for review and then stop.",
    );
    assert.ok(staged.length >= 1, `agent staged nothing (staged: ${JSON.stringify(staged)})`);
    assert.equal(new Set(staged.map((r) => r.sessionId)).size, 1, `expected ONE session, got: ${JSON.stringify(staged.map((r) => r.sessionId))}`);

    // Accept the whole session. The terminal accept runs the authoritative GATE-2 build over the
    // combined overlay, which FAILS (AdvancedCalculations.Power calls the now-gone Multiply), so
    // the accept is REFUSED and nothing is written. (Accept With Validation Override is the only
    // way past it, and this test never takes it.)
    let refused = false;
    let messages = "";
    for (const record of [...staged].reverse()) {
      const { body } = await postJson<AcceptResult>(`${HOST}/review/accept`, { stagedRecordId: record.stagedRecordId });
      messages += ` ${body.message}`;
      if (!body.accepted) {
        refused = true;
      }
    }
    assert.ok(refused, `expected the build gate to REFUSE the broken change, but every accept succeeded: ${messages.trim()}`);
    assert.match(messages, /build failed|not accepted|validation override/i, `expected a build-failure message, got: ${messages.trim()}`);

    // Failed build => watched source is untouched (H2: validate before write).
    assert.equal(await readFile(join(fixtureDir, "Calculator.cs"), "utf8"), calcBefore, "a failed build must not write Calculator.cs");
    assert.equal(await readFile(join(fixtureDir, "AdvancedCalculations.cs"), "utf8"), advBefore, "a failed build must not write AdvancedCalculations.cs");
  });
});
