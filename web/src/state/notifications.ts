import { useSyncExternalStore } from "react";

export interface AppNotification {
  id: string;
  title: string;
  description?: string;
  tone: "ok" | "warn" | "bad" | "info";
  at: number;
  href?: string;
  read: boolean;
}

/** In-memory notification center: only events that actually happened in this application session. */
let items: AppNotification[] = [];
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());

export const notifications = {
  push(n: Omit<AppNotification, "id" | "at" | "read">) {
    items = [{ ...n, id: crypto.randomUUID(), at: Date.now(), read: false }, ...items].slice(0, 100);
    emit();
  },
  markAllRead() {
    items = items.map((i) => ({ ...i, read: true }));
    emit();
  },
  clear() {
    items = [];
    emit();
  },
};

export function useNotifications() {
  return useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => items,
  );
}
