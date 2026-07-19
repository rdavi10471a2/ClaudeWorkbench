import express from "express";
import { randomUUID } from "node:crypto";
import { dirname } from "node:path";
import {
  query,
  type CanUseTool,
  type Options,
  type PermissionResult,
  type Query,
  type SDKMessage,
  type SDKUserMessage,
} from "@anthropic-ai/claude-agent-sdk";
import { EventBus } from "./bus.js";
import { OperatorGate, isGatedTool, baseName } from "./gate.js";
import type { SidecarEvent } from "./events.js";

// --- config -------------------------------------------------------------
const SIDECAR_PORT = Number(process.env.SIDECAR_PORT ?? 6110);
const WORKBENCH_MCP_URL =
  process.env.WORKBENCH_MCP_URL ?? "http://localhost:6100/mcp";
const MCP_SERVER_NAME = "claude-workbench";
const HOST_BASE = WORKBENCH_MCP_URL.replace(/\/mcp\/?$/, "");

// The agent's working directory is the watched solution's folder (read-only there:
// Read/Grep/Glob allowed, writes disallowed). Auto-derived from the host so it
// always tracks the configured watched solution; falls back to WORKBENCH_CWD.
let workspaceCwd: string | undefined = process.env.WORKBENCH_CWD;
// Operator-uploaded files live here (under the workspace runtime, NOT the watched
// tree). Granted to the agent as an additional read-only directory so it can Read
// attachments that sit outside cwd. Resolved from the host /health.
let uploadsDir: string | undefined;
// The watched solution the agent is governing, for the injected role card.
let watchedSolutionPath: string | undefined;

async function resolveWorkspaceCwd(): Promise<void> {
  try {
    const response = await fetch(`${HOST_BASE}/health`);
    if (response.ok) {
      const info = (await response.json()) as {
        watchedSolutionPath?: string;
        uploadsPath?: string;
      };
      if (info.watchedSolutionPath) {
        workspaceCwd = dirname(info.watchedSolutionPath);
        watchedSolutionPath = info.watchedSolutionPath;
      }
      uploadsDir = info.uploadsPath ?? undefined;
    }
  } catch {
    // keep the env/default cwd if the host is not reachable yet
  }
}

// The governed role card, injected as the system prompt so the agent understands
// its read-only + staging-workflow contract from turn one (instead of discovering
// it by hitting a deny). This is the programmatic skill-card seam: no CLAUDE.md is
// loaded (settingSources: []), so authored guidance is injected here.
function buildGovernanceCard(): string {
  const project = watchedSolutionPath ?? workspaceCwd ?? "(resolving)";
  return [
    "This session runs inside ClaudeWorkbench as a GOVERNED, READ-ONLY coding agent over a watched project. Follow these rules exactly.",
    "",
    `WATCHED PROJECT: ${project}`,
    "",
    "- You have NO Write, Edit, MultiEdit, NotebookEdit, or shell (Bash/PowerShell) tools, and you never will. Never claim you can write files to disk, and do not ask for those tools.",
    "- Inspect the workspace with Read, Grep, Glob and the claude-workbench MCP tools. Verify workspace facts with a tool before stating them — never answer from memory or infer from the tool list.",
    "- EVERY change to watched source goes through the AIMonitor staging workflow. The operator's Accept in the Merge Review dialog is the ONLY path that writes watched source; you cannot bypass it.",
    "- When asked to change code, call get_staging_guide, then:",
    "  1. start_monitor_session with filesPlanned listing every file you intend to change.",
    "  2. refresh_file (existing file) or new_file (future file) for each planned file.",
    "  3. Edit the monitor-owned Working candidate with the typed tools (submit_symbol, add_method, add_property, replace_span_in_file, replace_text_in_file, submit_file). For C# symbol edits, call get_source_map (selector mode) first.",
    "  4. stage_candidate_for_review for each file.",
    "  5. STOP and tell the operator it is staged for review. Do NOT call launch_staged_diff or record_diff_decision — the operator drives the merge in the UI.",
    "- The task board is OPTIONAL context, not a per-turn step. Work is free-flowing by default: do NOT tie a turn to a task automatically. ONLY when the operator's request clearly concerns a board task should you call get_current_task for that task's context and record progress with update_agent_notes. For ad-hoc requests, do not load or write task notes, and never fold an unrelated request into the Active task.",
    "- Ground truth lives behind tools, not memory: get_self_check, get_monitor_status, list_watched_projects, get_source_map.",
  ].join("\n");
}

void resolveWorkspaceCwd();

// --- minimal content-block shapes we read off SDK messages --------------
interface TextBlock {
  type: "text";
  text: string;
}
interface ToolUseBlock {
  type: "tool_use";
  id: string;
  name: string;
  input: unknown;
}
interface ToolResultBlock {
  type: "tool_result";
  tool_use_id: string;
  is_error?: boolean;
}
type ContentBlock =
  | TextBlock
  | ToolUseBlock
  | ToolResultBlock
  | { type: string; [key: string]: unknown };

function filePathOf(input: unknown): string | undefined {
  if (input && typeof input === "object") {
    const record = input as Record<string, unknown>;
    for (const key of ["path", "sourceFilePath", "filePath"]) {
      const value = record[key];
      if (typeof value === "string" && value.length > 0) {
        return value;
      }
    }
  }
  return undefined;
}

// --- wiring -------------------------------------------------------------
const bus = new EventBus();
const gate = new OperatorGate();
let activeTurn: string | null = null;
// Merge-review outcomes (build + index results) posted by the host after an accept,
// prepended to the agent's next prompt so it learns whether its edit compiled.
let pendingReviewOutcome = "";
// Current thread's SDK session id, captured from the message stream and passed as
// `resume` on the next turn so the agent remembers the conversation. Null = fresh thread.
let currentSessionId: string | null = null;
// AskUserQuestion elicitations awaiting operator answers (mirrors the gate registry).
const elicitations = new Map<string, { resolve: (answers: Record<string, unknown>) => void; questions: unknown }>();

// The human accept/reject gate. Read-only discovery tools auto-allow so the
// operator is interrupted only for changes that can reach watched source or the
// review queue; mutations pause here until the host resolves the gate.
// Deny-by-default tool surface, driven by the operator's per-turn tool policy.
// ALWAYS_ALLOWED_NATIVE is needed for the agent to function (ToolSearch loads the
// MCP tool schemas). READ_TOOLS gate on allowNativeReads. BLOCKABLE_TOOLS are the
// writers/shells hard-removed unless the operator opts them in.
const ALWAYS_ALLOWED_NATIVE = ["ToolSearch", "TodoWrite"];
const READ_TOOLS = ["Read", "Grep", "Glob"];
const BLOCKABLE_TOOLS = ["Write", "Edit", "MultiEdit", "NotebookEdit", "Bash", "PowerShell"];
const WORKBENCH_TOOL_PREFIX = `mcp__${MCP_SERVER_NAME}__`;

interface ToolPolicy {
  allowNativeReads: boolean;
  strictMcpConfig: boolean;
  enabledTools: string[];
  autoApprove: boolean;
  model: string;
  effort: string;
}

const DEFAULT_TOOL_POLICY: ToolPolicy = {
  allowNativeReads: true,
  strictMcpConfig: true,
  enabledTools: [],
  autoApprove: false,
  model: "",
  effort: "",
};

const EFFORT_LEVELS = new Set(["low", "medium", "high", "xhigh", "max"]);

// Recomputed at the start of each turn from that turn's policy; canUseTool reads it.
let activeAllowedNative = new Set<string>([...ALWAYS_ALLOWED_NATIVE, ...READ_TOOLS]);
// When on, claude-workbench mutations auto-allow instead of pausing at the operator
// gate. Watched source is still only written by the operator's merge-review Accept.
let activeAutoApprove = false;
// The long-lived streaming query for the current thread + its input stream.
let activeQuery: Query | null = null;
let activeInput: InputStream | null = null;

// Async-iterable input backed by a queue we push operator turns into. Ending it
// completes the query (New Thread). This is what makes it streaming-input mode,
// which is the only mode that exposes the Query control handle (interrupt /
// getContextUsage / getUsage).
class InputStream {
  private readonly queue: SDKUserMessage[] = [];
  private waiter: ((r: IteratorResult<SDKUserMessage>) => void) | null = null;
  private ended = false;

  push(text: string): void {
    const msg = {
      type: "user",
      message: { role: "user", content: text },
      parent_tool_use_id: null,
    } as unknown as SDKUserMessage;
    if (this.waiter) {
      const w = this.waiter;
      this.waiter = null;
      w({ value: msg, done: false });
    } else {
      this.queue.push(msg);
    }
  }

  end(): void {
    this.ended = true;
    if (this.waiter) {
      const w = this.waiter;
      this.waiter = null;
      w({ value: undefined as unknown as SDKUserMessage, done: true });
    }
  }

  async *stream(): AsyncGenerator<SDKUserMessage> {
    while (true) {
      const next = this.queue.shift();
      if (next !== undefined) {
        yield next;
        continue;
      }
      if (this.ended) {
        return;
      }
      const r = await new Promise<IteratorResult<SDKUserMessage>>((res) => {
        this.waiter = res;
      });
      if (r.done) {
        return;
      }
      yield r.value;
    }
  }
}

const canUseTool: CanUseTool = async (toolName, input, { signal }) => {
  const turnId = activeTurn ?? "unknown";

  // AskUserQuestion is the agent asking the operator a clarifying question. Route it
  // to the elicitation dialog and return the operator's answers as updatedInput
  // (per the Agent SDK contract: { questions, answers }).
  if (toolName === "AskUserQuestion") {
    const elicitationId = randomUUID();
    const questions = (input as { questions?: unknown }).questions ?? [];
    const answers = await new Promise<Record<string, unknown>>((resolve) => {
      elicitations.set(elicitationId, { resolve, questions });
      bus.emit({ type: "elicitation_request", turnId, elicitationId, questions });
      const onAbort = () => {
        if (elicitations.delete(elicitationId)) {
          resolve({});
        }
      };
      signal.addEventListener("abort", onAbort, { once: true });
    });
    bus.emit({ type: "elicitation_resolved", turnId, elicitationId });
    return { behavior: "allow", updatedInput: { ...(input as object), questions, answers } };
  }

  // Non-workbench tools: allow the safe read-only set, deny everything else.
  if (!toolName.startsWith(WORKBENCH_TOOL_PREFIX)) {
    if (activeAllowedNative.has(toolName)) {
      return { behavior: "allow", updatedInput: input };
    }
    return {
      behavior: "deny",
      message: `'${toolName}' is not permitted in the governed workbench. Read the workspace with Read/Grep/Glob and make changes through the claude-workbench tools (submit_file -> stage -> operator review).`,
    };
  }

  // claude-workbench read-only tools: allow. Mutations: pause at the operator gate,
  // unless auto-approve is on for this thread (candidate mutations proceed without a
  // per-call prompt; the merge-review Accept still gates the write to watched source).
  if (!isGatedTool(toolName)) {
    return { behavior: "allow", updatedInput: input };
  }

  if (activeAutoApprove) {
    return { behavior: "allow", updatedInput: input };
  }

  const { gateId, decided } = gate.request(
    baseName(toolName),
    input,
    filePathOf(input),
  );
  bus.emit({
    type: "gate_request",
    turnId,
    gateId,
    tool: baseName(toolName),
    input,
    filePath: filePathOf(input),
  });

  const onAbort = () => gate.resolve(gateId, "deny", "aborted");
  signal.addEventListener("abort", onAbort, { once: true });
  const resolution = await decided;
  signal.removeEventListener("abort", onAbort);

  bus.emit({
    type: "gate_resolved",
    turnId,
    gateId,
    decision: resolution.decision,
    reason: resolution.reason,
  });

  const result: PermissionResult =
    resolution.decision === "allow"
      ? { behavior: "allow", updatedInput: input }
      : { behavior: "deny", message: resolution.reason ?? "Operator rejected" };
  return result;
};

// Lazily create the long-lived streaming query for the current thread. Options are
// set ONCE here (per session): cwd, tool surface, resume. A workspace switch or a
// tool-policy change is applied by starting a new thread. Only the message content
// and the review-outcome prepend are per-turn (see submitTurn).
async function ensureSession(policy: ToolPolicy): Promise<void> {
  if (activeQuery) {
    return;
  }
  await resolveWorkspaceCwd();

  const enabled = new Set(policy.enabledTools);
  activeAllowedNative = new Set<string>([
    ...ALWAYS_ALLOWED_NATIVE,
    ...(policy.allowNativeReads ? READ_TOOLS : []),
    ...policy.enabledTools,
  ]);
  const disallowed = BLOCKABLE_TOOLS.filter((tool) => !enabled.has(tool));
  if (!policy.allowNativeReads) {
    disallowed.push(...READ_TOOLS);
  }

  const input = new InputStream();
  activeInput = input;

  const options: Options = {
    mcpServers: {
      [MCP_SERVER_NAME]: { type: "http", url: WORKBENCH_MCP_URL },
    },
    canUseTool,
    permissionMode: "default",
    // Governed role card injected up front (default claude_code prompt + our rules).
    systemPrompt: { type: "preset", preset: "claude_code", append: buildGovernanceCard() },
    // Operator-selected model + reasoning effort (empty => inherit the default).
    ...(policy.model ? { model: policy.model } : {}),
    ...(EFFORT_LEVELS.has(policy.effort) ? { effort: policy.effort as Options["effort"] } : {}),
    cwd: workspaceCwd,
    // Operator uploads sit outside cwd; grant read there so the agent can Read them.
    ...(uploadsDir ? { additionalDirectories: [uploadsDir] } : {}),
    disallowedTools: disallowed,
    strictMcpConfig: policy.strictMcpConfig,
    // SDK isolation mode: load NO filesystem settings (no personal ~/.claude leak,
    // no reads/writes of .claude/* in the watched tree, no CLAUDE.md injection).
    settingSources: [],
    // Resume the thread's session (restore after a process restart). Within a live
    // process the session persists in the query handle itself.
    ...(currentSessionId ? { resume: currentSessionId } : {}),
  };

  const q = query({ prompt: input.stream(), options }) as unknown as Query;
  activeQuery = q;

  // Background read loop: drain the query's output for the life of the thread.
  void (async () => {
    try {
      for await (const message of q as AsyncIterable<SDKMessage>) {
        handleMessage(message);
      }
    } catch (error) {
      const detail = error instanceof Error ? error.message : String(error);
      if (!/abort|interrupt/i.test(detail)) {
        bus.emit({ type: "error", message: detail });
      }
    } finally {
      activeQuery = null;
      activeInput = null;
      activeTurn = null;
    }
  })();
}

// Push one operator turn into the live session's input stream.
async function submitTurn(prompt: string, turnId: string, policy: ToolPolicy): Promise<void> {
  await ensureSession(policy);
  activeAutoApprove = policy.autoApprove;
  activeTurn = turnId;
  bus.emit({ type: "turn_started", turnId });
  bus.emit({ type: "user_prompt", turnId, text: prompt });

  let text = prompt;
  if (pendingReviewOutcome) {
    text = `Context — outcome of your previously accepted edit(s):\n${pendingReviewOutcome}\n\n---\n\n${prompt}`;
    pendingReviewOutcome = "";
  }
  activeInput?.push(text);
}

function handleMessage(message: SDKMessage): void {
  // Every SDK message carries the session id; capture it so the thread can resume.
  const sessionId = (message as { session_id?: string }).session_id;
  if (typeof sessionId === "string" && sessionId.length > 0) {
    currentSessionId = sessionId;
  }

  const turnId = activeTurn ?? "";

  switch (message.type) {
    case "assistant": {
      const content = message.message.content as ContentBlock[];
      const usage = message.message.usage;
      for (const block of content) {
        if (block.type === "text") {
          bus.emit({ type: "assistant_text", turnId, text: (block as TextBlock).text });
        } else if (block.type === "tool_use") {
          const toolBlock = block as ToolUseBlock;
          bus.emit({
            type: "tool_call_started",
            turnId,
            callId: toolBlock.id,
            tool: baseName(toolBlock.name),
            input: toolBlock.input,
          });
        }
      }
      if (usage) {
        bus.emit({
          type: "usage",
          turnId,
          inputTokens: usage.input_tokens,
          outputTokens: usage.output_tokens,
        });
      }
      break;
    }
    case "user": {
      const content = message.message.content;
      if (Array.isArray(content)) {
        for (const block of content as ContentBlock[]) {
          if (block.type === "tool_result") {
            const resultBlock = block as ToolResultBlock;
            bus.emit({
              type: "tool_call_finished",
              turnId,
              callId: resultBlock.tool_use_id,
              tool: "",
              ok: resultBlock.is_error !== true,
            });
          }
        }
      }
      break;
    }
    case "result": {
      if (message.subtype === "success") {
        bus.emit({
          type: "usage",
          turnId,
          inputTokens: message.usage.input_tokens,
          outputTokens: message.usage.output_tokens,
        });
      }
      bus.emit({
        type: "turn_finished",
        turnId,
        stopReason: message.subtype,
      });
      activeTurn = null;
      break;
    }
    default:
      break;
  }
}

// --- HTTP surface for the Blazor host -----------------------------------
const app = express();
app.use(express.json({ limit: "2mb" }));

// H3: the control surface is localhost-only. We bind to loopback (app.listen below) AND
// reject any request whose Host header isn't localhost, plus any browser request carrying
// a non-local Origin (DNS-rebinding defense). The Blazor host talks to us over 127.0.0.1
// and sends no Origin, so it is unaffected.
app.use((req, res, next) => {
  const host = (req.headers.host ?? "").split(":")[0];
  if (host !== "localhost" && host !== "127.0.0.1" && host !== "[::1]" && host !== "::1") {
    res.status(403).json({ error: "forbidden host" });
    return;
  }
  const origin = req.headers.origin;
  if (origin && !/^https?:\/\/(localhost|127\.0\.0\.1|\[::1\])(:\d+)?$/.test(origin)) {
    res.status(403).json({ error: "forbidden origin" });
    return;
  }
  next();
});

app.get("/health", (_req, res) => {
  res.json({
    status: "ok",
    mcpServer: MCP_SERVER_NAME,
    mcpUrl: WORKBENCH_MCP_URL,
    activeTurn,
    pendingGates: gate.list().length,
  });
});

// Live token/context + subscription usage, read straight off the Query handle.
// Both methods are experimental in the SDK (guarded); null until a thread exists.
app.get("/usage", async (_req, res) => {
  const q = activeQuery as unknown as {
    getContextUsage?: () => Promise<unknown>;
    usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET?: () => Promise<unknown>;
  } | null;
  if (!q) {
    res.json({ context: null, subscription: null });
    return;
  }
  let context: unknown = null;
  let subscription: unknown = null;
  try {
    if (typeof q.getContextUsage === "function") {
      context = await q.getContextUsage();
    }
  } catch {
    context = null;
  }
  try {
    if (typeof q.usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET === "function") {
      subscription = await q.usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET();
    }
  } catch {
    subscription = null;
  }
  res.json({ context, subscription });
});

app.get("/events", (_req, res) => {
  bus.addClient(res);
});

app.post("/prompt", (req, res) => {
  if (activeTurn) {
    res.status(409).json({ error: "A turn is already active.", activeTurn });
    return;
  }
  const prompt = (req.body?.prompt ?? "").toString();
  if (!prompt.trim()) {
    res.status(400).json({ error: "prompt is required." });
    return;
  }
  const raw = (req.body?.toolPolicy ?? {}) as Partial<ToolPolicy>;
  const policy: ToolPolicy = {
    allowNativeReads: raw.allowNativeReads !== false,
    strictMcpConfig: raw.strictMcpConfig !== false,
    // H3: an operator may only re-enable tools from the blockable set (the writers/shells).
    // Arbitrary names (Agent, WebFetch, Workflow, anything unknown) can NOT be spliced into
    // the allow-set, so a /prompt body cannot widen the deny-by-default surface beyond the
    // intended opt-ins.
    enabledTools: Array.isArray(raw.enabledTools)
      ? raw.enabledTools.map(String).filter((tool) => BLOCKABLE_TOOLS.includes(tool))
      : [],
    autoApprove: raw.autoApprove === true,
    model: typeof raw.model === "string" ? raw.model : "",
    effort: typeof raw.effort === "string" ? raw.effort : "",
  };
  const turnId = randomUUID();
  activeTurn = turnId;
  void submitTurn(prompt, turnId, policy);
  res.status(202).json({ turnId });
});

app.post("/stop", (_req, res) => {
  if (activeQuery && activeTurn) {
    void activeQuery.interrupt();
    res.json({ stopped: true });
    return;
  }
  res.json({ stopped: false });
});

app.get("/gates", (_req, res) => {
  res.json(gate.list());
});

app.post("/gates/:id", (req, res) => {
  const decision = req.body?.decision;
  if (decision !== "allow" && decision !== "deny") {
    res.status(400).json({ error: "decision must be 'allow' or 'deny'." });
    return;
  }
  const ok = gate.resolve(req.params.id, decision, req.body?.reason);
  res.status(ok ? 200 : 404).json({ resolved: ok });
});

app.get("/elicitations", (_req, res) => {
  res.json(
    [...elicitations.entries()].map(([elicitationId, entry]) => ({
      elicitationId,
      questions: entry.questions,
    })),
  );
});

app.post("/elicitations/:id", (req, res) => {
  const entry = elicitations.get(req.params.id);
  if (!entry) {
    res.status(404).json({ resolved: false });
    return;
  }
  elicitations.delete(req.params.id);
  entry.resolve((req.body?.answers as Record<string, unknown>) ?? {});
  res.json({ resolved: true });
});

app.post("/review-outcome", (req, res) => {
  const summary = typeof req.body?.summary === "string" ? req.body.summary.trim() : "";
  if (summary.length > 0) {
    pendingReviewOutcome = pendingReviewOutcome
      ? `${pendingReviewOutcome}\n${summary}`
      : summary;
    // Surface it in the transcript for the operator too.
    bus.emit({ type: "assistant_text", turnId: activeTurn ?? "review", text: `[merge review] ${summary}` });
  }
  res.json({ ok: true });
});

app.post("/new-thread", (_req, res) => {
  if (activeTurn) {
    res.status(409).json({ error: "Cannot start a new thread while a turn is active." });
    return;
  }
  activeInput?.end();
  activeQuery = null;
  activeInput = null;
  currentSessionId = null;
  pendingReviewOutcome = "";
  elicitations.clear();
  bus.clear();
  bus.emit({ type: "thread_reset", turnId: "thread" });
  res.json({ ok: true });
});

app.listen(SIDECAR_PORT, "127.0.0.1", () => {
  const banner: SidecarEvent = {
    type: "error",
    message: `sidecar listening on :${SIDECAR_PORT}, MCP -> ${WORKBENCH_MCP_URL}`,
  };
  // eslint-disable-next-line no-console
  console.log(banner.message);
});
