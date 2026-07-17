import type { Response } from "express";
import type { SidecarEvent } from "./events.js";

// Fan-out of SidecarEvents to any number of Server-Sent-Events subscribers
// (the Blazor host is one). Keeps a bounded history so a late-connecting UI
// replays recent context instead of starting blank.
export class EventBus {
  private readonly clients = new Set<Response>();
  private readonly history: SidecarEvent[] = [];
  private readonly maxHistory = 1000;

  addClient(res: Response): void {
    res.setHeader("Content-Type", "text/event-stream");
    res.setHeader("Cache-Control", "no-cache");
    res.setHeader("Connection", "keep-alive");
    res.flushHeaders?.();
    for (const event of this.history) {
      this.write(res, event);
    }
    this.clients.add(res);
    res.on("close", () => this.clients.delete(res));
  }

  emit(event: SidecarEvent): void {
    this.history.push(event);
    if (this.history.length > this.maxHistory) {
      this.history.shift();
    }
    for (const res of this.clients) {
      this.write(res, event);
    }
  }

  private write(res: Response, event: SidecarEvent): void {
    res.write(`data: ${JSON.stringify(event)}\n\n`);
  }
}
