import { useSyncExternalStore, type AnchorHTMLAttributes, type MouseEvent } from "react";

/** Minimal hash router: the app is served from a local virtual host, so hash routes need no server cooperation. */
export interface Route {
  path: string;
  segments: string[];
  query: URLSearchParams;
}

function read(): Route {
  const raw = window.location.hash.replace(/^#/, "") || "/";
  const [path, search] = raw.split("?");
  const clean = path.length > 1 ? path.replace(/\/+$/, "") : path;
  return { path: clean, segments: clean.split("/").filter(Boolean).map(decodeURIComponent), query: new URLSearchParams(search ?? "") };
}

let current = read();
const listeners = new Set<() => void>();
window.addEventListener("hashchange", () => {
  current = read();
  listeners.forEach((l) => l());
});

export function useRoute(): Route {
  return useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => current,
  );
}

export function navigate(to: string, options?: { replace?: boolean }) {
  const target = `#${to.startsWith("/") ? to : `/${to}`}`;
  if (options?.replace) {
    window.history.replaceState(null, "", target);
    current = read();
    listeners.forEach((l) => l());
  } else {
    window.location.hash = target;
  }
}

export function setQuery(key: string, value: string | null) {
  const route = read();
  const query = new URLSearchParams(route.query);
  if (value === null) query.delete(key);
  else query.set(key, value);
  const qs = query.toString();
  navigate(`${route.path}${qs ? `?${qs}` : ""}`, { replace: true });
}

export function Link({ to, onClick, ...props }: AnchorHTMLAttributes<HTMLAnchorElement> & { to: string }) {
  return (
    <a
      href={`#${to}`}
      onClick={(e: MouseEvent<HTMLAnchorElement>) => {
        onClick?.(e);
      }}
      {...props}
    />
  );
}
