import express from "express";
import { randomUUID } from "node:crypto";
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
const canUseTool: CanUseTool = async (toolName, input, { signal }) => {
  const turnId = activeTurn ?? "unknown";
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

async function runTurn(prompt: string, turnId: string): Promise<void> {
  const options: Options = {
    mcpServers: {
      [MCP_SERVER_NAME]: { type: "http", url: WORKBENCH_MCP_URL },
    },
    canUseTool,
    permissionMode: "default",
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
  const turnId = randomUUID();
  activeTurn = turnId;
  void runTurn(prompt, turnId);
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
