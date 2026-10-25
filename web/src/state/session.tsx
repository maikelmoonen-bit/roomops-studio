import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import type { AppInfo, AppSettings, ScanProgress, SessionSnapshot, SignInPrompt, ThemePreference } from "@/bridge/contracts.gen";
import { setLanguage, t } from "@/lib/i18n";
import { notifications } from "./notifications";

interface SessionContextValue {
  session: SessionSnapshot | null;
  appInfo: AppInfo | null;
  settings: AppSettings | null;
  bridgeConnected: boolean;
  resolvedTheme: "dark" | "light";
  signInPrompt: SignInPrompt | null;
  scan: ScanProgress | null;
  updateSettings: (update: (s: AppSettings) => AppSettings) => Promise<void>;
  setTheme: (theme: ThemePreference) => void;
  isDemo: boolean;
  isLive: boolean;
}

const SessionContext = createContext<SessionContextValue | null>(null);

export function useSession() {
  const value = useContext(SessionContext);
  if (!value) throw new Error("useSession must be used inside SessionProvider");
  return value;
}

const prefersDark = () => window.matchMedia("(prefers-color-scheme: dark)").matches;

export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [session, setSession] = useState<SessionSnapshot | null>(null);
  const [appInfo, setAppInfo] = useState<AppInfo | null>(null);
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [bridgeConnected, setBridgeConnected] = useState(bridge.kind === "webview2");
  const [systemDark, setSystemDark] = useState(prefersDark());
  const [signInPrompt, setSignInPrompt] = useState<SignInPrompt | null>(null);
  const [scan, setScan] = useState<ScanProgress | null>(null);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        const [s, info, prefs] = await Promise.all([bridge.call("session.get", {}), bridge.call("app.info", {}), bridge.call("settings.get", {})]);
        if (cancelled) return;
        setSession(s);
        setAppInfo(info);
        setSettings(prefs);
        setBridgeConnected(true);
        void bridge.call("app.ready", {});
      } catch {
        if (!cancelled) window.setTimeout(load, 1000);
      }
    };
    void load();
    const offStatus = bridge.onStatus((connected) => {
      setBridgeConnected(connected);
      if (connected) void bridge.call("session.get", {}).then(setSession);
    });
    return () => {
      cancelled = true;
      offStatus();
    };
  }, []);

  // Session changes: a new epoch means a different tenant/session, so every cached query is discarded.
  useEffect(
    () =>
      bridge.on("session.changed", (next) => {
        setSession((previous) => {
          if (!previous || previous.epoch !== next.epoch) {
            queryClient.clear();
            setScan(null);
          }
          for (const service of next.services) {
            const before = previous?.services.find((s) => s.service === service.service);
            if (before?.status !== service.status && (service.status === "Expired" || service.status === "Failed")) {
              notifications.push({ title: service.status === "Expired" ? t("{service} connection expired", { service: service.service }) : t("{service} connection failed", { service: service.service }), description: service.detail ?? undefined, tone: "bad", href: "/connections" });
            }
          }
          return next;
        });
        setSignInPrompt(null);
      }),
    [queryClient],
  );

  useEffect(() => bridge.on("signIn.prompt", setSignInPrompt), []);

  useEffect(
    () =>
      bridge.on("execution.completed", (state) => {
        void queryClient.invalidateQueries();
        const failed = state.counts.failed;
        notifications.push({
          title: state.title,
          description: state.counts.skipped
            ? t("{completed} completed, {unchanged} unchanged, {failed} failed, {skipped} skipped.", { completed: state.counts.succeeded, unchanged: state.counts.noChange, failed, skipped: state.counts.skipped })
            : t("{completed} completed, {unchanged} unchanged, {failed} failed.", { completed: state.counts.succeeded, unchanged: state.counts.noChange, failed }),
          tone: failed > 0 ? "warn" : "ok",
          href: "/history",
        });
      }),
    [queryClient],
  );

  useEffect(
    () =>
      bridge.on("scan.progress", (progress) => {
        setScan(progress);
        if (!progress.running) {
          void queryClient.invalidateQueries();
          notifications.push({ title: t("Configuration scan finished"), description: progress.failed
            ? t("{completed} of {total} rooms read, {failed} could not be read.", { completed: progress.completed, total: progress.total, failed: progress.failed })
            : t("{completed} of {total} rooms read.", { completed: progress.completed, total: progress.total }), tone: progress.failed ? "warn" : "ok", href: "/validation" });
          toast.success(t("Configuration scan finished"), { description: t("{completed} of {total} rooms read.", { completed: progress.completed, total: progress.total }) });
        }
      }),
    [queryClient],
  );

  useEffect(() => bridge.on("inventory.changed", () => void queryClient.invalidateQueries()), [queryClient]);

  useEffect(() => {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const listener = () => setSystemDark(media.matches);
    media.addEventListener("change", listener);
    return () => media.removeEventListener("change", listener);
  }, []);

  const theme = settings?.appearance.theme ?? "System";
  const resolvedTheme: "dark" | "light" = theme === "System" ? (systemDark ? "dark" : "light") : theme === "Dark" ? "dark" : "light";

  useEffect(() => {
    const root = document.documentElement;
    root.classList.toggle("dark", resolvedTheme === "dark");
    root.dataset.density = settings?.appearance.density === "Compact" ? "compact" : "comfortable";
    root.dataset.motion = settings?.appearance.motion === "Reduced" ? "reduced" : "auto";
    setLanguage(settings?.appearance.language ?? "en");
    void bridge.call("app.setTheme", { dark: resolvedTheme === "dark" }).catch(() => undefined);
  }, [resolvedTheme, settings?.appearance.density, settings?.appearance.motion, settings?.appearance.language]);

  const updateSettings = useCallback(async (update: (s: AppSettings) => AppSettings) => {
    const current = settings ?? (await bridge.call("settings.get", {}));
    const optimistic = update(current);
    setSettings(optimistic);
    try {
      setSettings(await bridge.call("settings.update", optimistic));
      // The backend writes explanations, findings and previews in the stored language; refetch them after a switch.
      if (optimistic.appearance.language !== current.appearance.language) void queryClient.invalidateQueries();
    } catch (error) {
      setSettings(current);
      toast.error(t("Settings were not saved"), { description: error instanceof Error ? error.message : undefined });
    }
  }, [settings, queryClient]);

  const value = useMemo<SessionContextValue>(
    () => ({
      session,
      appInfo,
      settings,
      bridgeConnected,
      resolvedTheme,
      signInPrompt,
      scan,
      updateSettings,
      setTheme: (theme) => void updateSettings((s) => ({ ...s, appearance: { ...s.appearance, theme: theme } })),
      isDemo: session?.mode === "Demo",
      isLive: session?.mode === "Live",
    }),
    [session, appInfo, settings, bridgeConnected, resolvedTheme, signInPrompt, scan, updateSettings],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}
