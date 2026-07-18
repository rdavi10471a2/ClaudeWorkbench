import express from "express";
import { randomUUID } from "node:crypto";
import { dirname } from "node:path";
import {
  query,
  type CanUseTool,
  type Options,
  type PermissionResult,
  type SDKMessage,
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

async function resolveWorkspaceCwd(): Promise<void> {
  try {
    const response = await fetch(`${HOST_BASE}/health`);
    if (response.ok) {
      const info = (await response.json()) as { watchedSolutionPath?: string };
      if (info.watchedSolutionPath) {
        workspaceCwd = dirname(info.watchedSolutionPath);
      }
    }
  } catch {
    // keep the env/default cwd if the host is not reachable yet
  }
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
}

const DEFAULT_TOOL_POLICY: ToolPolicy = {
  allowNativeReads: true,
  strictMcpConfig: true,
  enabledTools: [],
};

// Recomputed at the start of each turn from that turn's policy; canUseTool reads it.
let activeAllowedNative = new Set<string>([...ALWAYS_ALLOWED_NATIVE, ...READ_TOOLS]);

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

  // claude-workbench read-only tools: allow. Mutations: pause at the operator gate.
  if (!isGatedTool(toolName)) {
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

async function runTurn(prompt: string, turnId: string, policy: ToolPolicy): Promise<void> {
  // Re-resolve CWD each turn so it tracks runtime workspace switches, not just startup.
  await resolveWorkspaceCwd();

  // Show the operator's own submission in the transcript (before any prompt prefixing).
  bus.emit({ type: "user_prompt", turnId, text: prompt });

  // Fold in any review outcomes from accepts since the last turn.
  if (pendingReviewOutcome) {
    prompt = `Context — outcome of your previously accepted edit(s):\n${pendingReviewOutcome}\n\n---\n\n${prompt}`;
    pendingReviewOutcome = "";
  }

  // Compute this turn's tool surface from the operator's policy.
  const enabled = new Set(policy.enabledTools);
  activeAllowedNative = new Set<string>([
    ...ALWAYS_ALLOWED_NATIVE,
    ...(policy.allowNativeReads ? READ_TOOLS : []),
    ...policy.enabledTools,
  ]);
  // Hard-remove the writers/shells unless the operator opted them in; also remove
  // the read tools when native reads are off (force MCP-only access).
  const disallowed = BLOCKABLE_TOOLS.filter((tool) => !enabled.has(tool));
  if (!policy.allowNativeReads) {
    disallowed.push(...READ_TOOLS);
  }

  const options: Options = {
    mcpServers: {
      [MCP_SERVER_NAME]: { type: "http", url: WORKBENCH_MCP_URL },
    },
    canUseTool,
    permissionMode: "default",
    cwd: workspaceCwd,
    disallowedTools: disallowed,
    strictMcpConfig: policy.strictMcpConfig,
    // SDK isolation mode: load NO filesystem settings. Without this the SDK
    // defaults to user+project+local sources, which (a) leaks the operator's
    // personal ~/.claude/settings.json allow-rules into the governed agent,
    // potentially pre-authorizing tools we mean to gate, and (b) resolves
    // project/local settings from cwd = the watched solution, reading/writing
    // .claude/* inside a read-only tree. Governance here is purely programmatic
    // (canUseTool + disallowedTools, recomputed each turn from agent-settings.json),
    // so no file config should apply. [] also disables CLAUDE.md loading, which
    // matches the "no turn-start policy / no monitor CLAUDE.md injection" rule.
    settingSources: [],
    // Continuity: resume the current thread's session so the agent remembers prior
    // turns. Cleared by /new-thread to start a fresh conversation.
    ...(currentSessionId ? { resume: currentSessionId } : {}),
    // No systemPrompt: governance is the gate + on-demand skills, not turn-start
    // policy prose. Do not inject a monitor-style CLAUDE.md here.
  };

  bus.emit({ type: "turn_started", turnId });
  try {
    for await (const message of query({ prompt, options }) as AsyncIterable<SDKMessage>) {
      handleMessage(message, turnId);
    }
  } catch (error) {
    bus.emit({
      type: "error",
      turnId,
      message: error instanceof Error ? error.message : String(error),
    });
  } finally {
    activeTurn = null;
  }
}

function handleMessage(message: SDKMessage, turnId: string): void {
  // Every SDK message carries the session id; capture it so the next turn resumes.
  const sessionId = (message as { session_id?: string }).session_id;
  if (typeof sessionId === "string" && sessionId.length > 0) {
    currentSessionId = sessionId;
  }

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
      break;
    }
    default:
      break;
  }
}

// --- HTTP surface for the Blazor host -----------------------------------
const app = express();
app.use(express.json({ limit: "2mb" }));

app.get("/health", (_req, res) => {
  res.json({
    status: "ok",
    mcpServer: MCP_SERVER_NAME,
    mcpUrl: WORKBENCH_MCP_URL,
    activeTurn,
    pendingGates: gate.list().length,
  });
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
    enabledTools: Array.isArray(raw.enabledTools) ? raw.enabledTools.map(String) : [],
  };
  const turnId = randomUUID();
  activeTurn = turnId;
  void runTurn(prompt, turnId, policy);
  res.status(202).json({ turnId });
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
  currentSessionId = null;
  pendingReviewOutcome = "";
  elicitations.clear();
  bus.clear();
  bus.emit({ type: "thread_reset", turnId: "thread" });
  res.json({ ok: true });
});

app.listen(SIDECAR_PORT, () => {
  const banner: SidecarEvent = {
    type: "error",
    message: `sidecar listening on :${SIDECAR_PORT}, MCP -> ${WORKBENCH_MCP_URL}`,
  };
  // eslint-disable-next-line no-console
  console.log(banner.message);
});
