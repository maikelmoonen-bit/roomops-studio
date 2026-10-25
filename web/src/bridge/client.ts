import type { BridgeEventTopic, BridgeEvents, BridgeMethodName, BridgeMethods, DomainError, ErrorKind } from "./contracts.gen";
import { t } from "@/lib/i18n";

/**
 * Typed client for the RoomOps bridge. In the desktop app messages travel through WebView2's postMessage channel; in the
 * development host they use a token-protected loopback WebSocket. The frontend can only call registered methods.
 */
export class BridgeError extends Error {
  readonly code: string;
  readonly kind: ErrorKind;
  readonly detail: string | null;

  constructor(error: DomainError) {
    super(error.message);
    this.name = "BridgeError";
    this.code = error.code;
    this.kind = error.kind;
    this.detail = error.detail ?? null;
  }
}

type Request<M extends BridgeMethodName> = BridgeMethods[M]["request"];
type Response<M extends BridgeMethodName> = BridgeMethods[M]["response"];
type Handler<T extends BridgeEventTopic> = (payload: BridgeEvents[T]) => void;

interface Transport {
  readonly kind: "webview2" | "websocket";
  send(text: string): void;
  onMessage(listener: (text: string) => void): void;
  onStatus(listener: (connected: boolean) => void): void;
}

interface WebView2 {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
}

function webView(): WebView2 | undefined {
  return (window as unknown as { chrome?: { webview?: WebView2 } }).chrome?.webview;
}

function devToken(): string | null {
  const url = new URL(window.location.href);
  const fromUrl = url.searchParams.get("devtoken");
  if (fromUrl) {
    try {
      sessionStorage.setItem("roomops.devtoken", fromUrl);
    } catch {
      // Storage may be unavailable; the token stays in memory for this page.
    }
    url.searchParams.delete("devtoken");
    window.history.replaceState(null, "", url.toString());
    return fromUrl;
  }
  try {
    return sessionStorage.getItem("roomops.devtoken");
  } catch {
    return null;
  }
}

function webViewTransport(view: WebView2): Transport {
  return {
    kind: "webview2",
    send: (text) => view.postMessage(text),
    onMessage: (listener) => view.addEventListener("message", (e) => listener(typeof e.data === "string" ? e.data : JSON.stringify(e.data))),
    onStatus: (listener) => listener(true),
  };
}

function socketTransport(): Transport {
  const token = devToken() ?? "";
  const queue: string[] = [];
  const listeners: ((text: string) => void)[] = [];
  const statusListeners: ((connected: boolean) => void)[] = [];
  let socket: WebSocket | null = null;
  let attempt = 0;

  const connect = () => {
    const protocol = window.location.protocol === "https:" ? "wss" : "ws";
    socket = new WebSocket(`${protocol}://${window.location.host}/bridge?token=${encodeURIComponent(token)}`);
    socket.onopen = () => {
      attempt = 0;
      statusListeners.forEach((l) => l(true));
      while (queue.length > 0) socket?.send(queue.shift()!);
    };
    socket.onmessage = (e) => listeners.forEach((l) => l(String(e.data)));
    socket.onclose = () => {
      statusListeners.forEach((l) => l(false));
      attempt += 1;
      window.setTimeout(connect, Math.min(500 * 2 ** attempt, 8000));
    };
  };
  connect();

  return {
    kind: "websocket",
    send: (text) => (socket?.readyState === WebSocket.OPEN ? socket.send(text) : queue.push(text)),
    onMessage: (listener) => listeners.push(listener),
    onStatus: (listener) => statusListeners.push(listener),
  };
}

interface Pending {
  resolve: (value: unknown) => void;
  reject: (error: unknown) => void;
  timer: number;
}

const DEFAULT_TIMEOUT = 120_000;
const LONG_RUNNING: Partial<Record<BridgeMethodName, number>> = {
  "connections.connect": 11 * 60_000,
  "plans.create": 10 * 60_000,
  "plans.retry": 10 * 60_000,
  "bulk.parse": 10 * 60_000,
  "rooms.list": 10 * 60_000,
  "validation.run": 10 * 60_000,
};

class BridgeClient {
  private transport: Transport | null = null;
  private readonly pending = new Map<string, Pending>();
  private readonly handlers = new Map<string, Set<(payload: unknown) => void>>();
  private readonly statusHandlers = new Set<(connected: boolean) => void>();
  private nextId = 0;
  connected = false;

  private ensure(): Transport {
    if (this.transport) return this.transport;
    const view = webView();
    const transport = view ? webViewTransport(view) : socketTransport();
    transport.onMessage((text) => this.receive(text));
    transport.onStatus((connected) => {
      this.connected = connected;
      this.statusHandlers.forEach((h) => h(connected));
    });
    this.transport = transport;
    return transport;
  }

  get kind() {
    return this.ensure().kind;
  }

  private receive(text: string) {
    let message: { type: string; id?: string; ok?: boolean; result?: unknown; error?: DomainError; topic?: string; payload?: unknown };
    try {
      message = JSON.parse(text);
    } catch {
      return;
    }
    if (message.type === "event" && message.topic) {
      this.handlers.get(message.topic)?.forEach((h) => h(message.payload));
      return;
    }
    if (message.type === "response" && message.id) {
      const pending = this.pending.get(message.id);
      if (!pending) return;
      this.pending.delete(message.id);
      window.clearTimeout(pending.timer);
      if (message.ok) pending.resolve(message.result);
      else pending.reject(new BridgeError(message.error ?? { code: "unknown", message: t("Unknown error"), kind: "Internal", detail: null }));
    }
  }

  call<M extends BridgeMethodName>(method: M, params: Request<M>, timeoutMs?: number): Promise<Response<M>> {
    const transport = this.ensure();
    const id = `${Date.now().toString(36)}-${(this.nextId++).toString(36)}`;
    return new Promise<Response<M>>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(id);
        reject(new BridgeError({ code: "bridge.timeout", message: t("The request '{method}' did not complete in time.", { method }), kind: "Transient", detail: null }));
      }, timeoutMs ?? LONG_RUNNING[method] ?? DEFAULT_TIMEOUT);
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject, timer });
      transport.send(JSON.stringify({ type: "request", id, method, params }));
    });
  }

  on<T extends BridgeEventTopic>(topic: T, handler: Handler<T>): () => void {
    this.ensure();
    const set = this.handlers.get(topic) ?? new Set();
    set.add(handler as (payload: unknown) => void);
    this.handlers.set(topic, set);
    return () => set.delete(handler as (payload: unknown) => void);
  }

  onStatus(handler: (connected: boolean) => void): () => void {
    this.ensure();
    this.statusHandlers.add(handler);
    return () => this.statusHandlers.delete(handler);
  }
}

export const bridge = new BridgeClient();
