// The WebSocket connection to SpiceForgeServer (WebSocketApi.md, Messages): requests matched to responses by id, and
// events handed to listeners.

import type { ApiError } from "./protocol";

export class RequestError extends Error {
  constructor(readonly op: string, readonly error: ApiError) {
    super(`${op}: ${error.message}`);
  }
}

type Listener = (data: unknown) => void;

export class ApiClient {
  private socket: WebSocket | null = null;
  private nextId = 0;
  private readonly waiting = new Map<number, { op: string; resolve: (r: unknown) => void; reject: (e: Error) => void }>();
  private readonly listeners = new Map<string, Set<Listener>>();

  /** Called when the connection closes, with the reason if there was one. */
  onClose: (reason: string) => void = () => {};

  /** Opens the socket to the server this page was served from. */
  connect(token: string): Promise<void> {
    const url = `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/api?token=${encodeURIComponent(token)}`;
    return new Promise((resolve, reject) => {
      const socket = new WebSocket(url);
      this.socket = socket;
      socket.onopen = () => resolve();
      socket.onerror = () => reject(new Error("Couldn't connect to the server."));
      socket.onclose = (e) => {
        for (const w of this.waiting.values()) w.reject(new Error("The connection closed."));
        this.waiting.clear();
        this.onClose(e.reason || (e.code === 1006 ? "The connection was lost." : `Closed (${e.code}).`));
      };
      socket.onmessage = (e) => this.receive(e.data);
    });
  }

  /** Sends a request and resolves with its result, or rejects with a RequestError. */
  request<T>(op: string, args?: Record<string, unknown>): Promise<T> {
    const socket = this.socket;
    if (!socket || socket.readyState !== WebSocket.OPEN) return Promise.reject(new Error("Not connected."));
    const id = ++this.nextId;
    return new Promise<T>((resolve, reject) => {
      this.waiting.set(id, { op, resolve: resolve as (r: unknown) => void, reject });
      socket.send(JSON.stringify({ op, id, args: args ?? {} }));
    });
  }

  /** Listens for an event; returns a function that stops listening. */
  on(event: string, listener: Listener): () => void {
    let set = this.listeners.get(event);
    if (!set) this.listeners.set(event, (set = new Set()));
    set.add(listener);
    return () => set!.delete(listener);
  }

  private receive(data: unknown) {
    if (typeof data !== "string") return; // binary frames (scope samples) aren't used yet
    const message = JSON.parse(data) as { re?: number; result?: unknown; error?: ApiError; event?: string; data?: unknown };
    if (message.re !== undefined) {
      const w = this.waiting.get(message.re);
      if (!w) return;
      this.waiting.delete(message.re);
      if (message.error) w.reject(new RequestError(w.op, message.error));
      else w.resolve(message.result);
    } else if (message.event) {
      for (const listener of this.listeners.get(message.event) ?? []) listener(message.data);
    }
  }
}
