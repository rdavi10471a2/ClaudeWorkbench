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

app.listen(SIDECAR_PORT, () => {
  const banner: SidecarEvent = {
    type: "error",
    message: `sidecar listening on :${SIDECAR_PORT}, MCP -> ${WORKBENCH_MCP_URL}`,
  };
  // eslint-disable-next-line no-console
  console.log(banner.message);
});
