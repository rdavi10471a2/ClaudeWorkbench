import type { Response } from "express";
import type { SidecarEvent } from "./events.js";

// Fan-out of SidecarEvents to any number of Server-Sent-Events subscribers
// (the Blazor host is one). Keeps a bounded history so a late-connecting UI
// replays recent context instead of starting blank.
type StampedEvent = SidecarEvent & { ts: number };

export class EventBus {
  private readonly clients = new Set<Response>();
  private readonly history: StampedEvent[] = [];
  private readonly maxHistory = 1000;

  addClient(res: Response): void {
    res.setHeader("Content-Type", "text/event-stream");
    res.setHeader("Cache-Control", "no-cache");
    res.setHeader("Connection", "keep-alive");
    res.flushHeaders?.();
    for (const event of this.history) {
      // Do NOT replay gate lifecycle from history: a late-joining/reconnecting UI
      // must take its pending gates from the live registry (GET /gates), not from
      // stale history, or it shows gates whose promises are already gone.
      if (event.type === "gate_request" || event.type === "gate_resolved") {
        continue;
      }
      this.write(res, event);
    }
    this.clients.add(res);
    res.on("close", () => this.clients.delete(res));
  }

  emit(event: SidecarEvent): void {
    const stamped: StampedEvent = { ...event, ts: Date.now() } as StampedEvent;
    this.history.push(stamped);
    if (this.history.length > this.maxHistory) {
      this.history.shift();
    }
    for (const res of this.clients) {
      this.write(res, stamped);
    }
  }

  private write(res: Response, event: StampedEvent): void {
    res.write(`data: ${JSON.stringify(event)}\n\n`);
  }
}
