// Neutral sidecar -> host event contract.
//
// Deliberately decoupled from any agent vendor's wire shapes (no thread/turn
// lifecycle, no tokenUsage %-of-window, no approval_mode). The sidecar maps
// whatever the Claude Agent SDK streams onto THESE types, and the Blazor host
// binds to these types only. That keeps host UI free of SDK-specific coupling.

export type GateDecision = "allow" | "deny";

export type SidecarEvent =
  | { type: "turn_started"; turnId: string }
  | { type: "thread_reset"; turnId: string }
  | { type: "user_prompt"; turnId: string; text: string }
  | { type: "assistant_text"; turnId: string; text: string }
  | {
      type: "tool_call_started";
      turnId: string;
      callId: string;
      tool: string;
      input: unknown;
    }
  | {
      type: "tool_call_finished";
      turnId: string;
      callId: string;
      tool: string;
      ok: boolean;
      summary?: string;
    }
  | {
      // A gated (mutating) tool call is paused, awaiting an operator decision.
      type: "gate_request";
      turnId: string;
      gateId: string;
      tool: string;
      input: unknown;
      filePath?: string;
    }
  | {
      type: "gate_resolved";
      turnId: string;
      gateId: string;
      decision: GateDecision;
      reason?: string;
    }
  | {
      type: "usage";
      turnId: string;
      inputTokens?: number;
      outputTokens?: number;
    }
  | { type: "turn_finished"; turnId: string; stopReason?: string }
  | { type: "error"; turnId?: string; message: string };

export type SidecarEventType = SidecarEvent["type"];
