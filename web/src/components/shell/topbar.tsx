import { useQueryClient } from "@tanstack/react-query";
import { Bell, Check, ChevronDown, FlaskConical, Loader2, LogOut, Moon, PlugZap, RefreshCw, Search, Sun, SunMoon } from "lucide-react";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import type { ServiceConnection, ServiceKind } from "@/bridge/contracts.gen";
import { cn, formatRelative, initials } from "@/lib/format";
import { navigate } from "@/lib/router";
import { useNotifications, notifications } from "@/state/notifications";
import { usePlanFlow } from "@/state/plan-flow";
import { useSession } from "@/state/session";
import { Button } from "@/components/ui/button";
import { Dot, Kbd } from "@/components/ui/core";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger, Popover, PopoverContent, PopoverTrigger, Tooltip } from "@/components/ui/overlay";
import { languages, t, useLanguage, type Language } from "@/lib/i18n";

const serviceLabel: Record<ServiceKind, string> = { Exchange: "Exchange", Places: "Places", Graph: "Graph" };

function ConnectionPill({ service, connection }: { service: ServiceKind; connection?: ServiceConnection }) {
  const status = connection?.status ?? "NotConnected";
  const tone = status === "Connected" ? (connection?.verification === "Unverified" ? "warn" : "ok") : status === "Connecting" ? "accent" : status === "NotConnected" ? "neutral" : "bad";
  const detail =
    status === "Connected"
      ? `${connection?.account ?? t("Connected")} · ${connection?.verification === "Correlated" ? t("tenant confirmed by room correlation") : connection?.verification === "Unverified" ? t("tenant not verified") : t("tenant verified")}`
      : status === "Connecting"
        ? t("Waiting for sign-in")
        : status === "NotConnected"
          ? t("Not connected")
          : connection?.detail ?? status;
  return (
    <Tooltip content={<span><span className="font-medium">{serviceLabel[service]}</span> — {detail}</span>}>
      <button type="button" onClick={() => navigate("/connections")} className="flex h-7 items-center gap-1.5 rounded-sm px-2 text-[12px] font-medium text-ink-2 hover:bg-hover hover:text-ink">
        <Dot tone={tone} pulse={status === "Connecting"} />
        {serviceLabel[service]}
      </button>
    </Tooltip>
  );
}

/** EN | NL switch. The choice is stored in the settings, so the backend answers in the same language. */
function LanguageToggle() {
  const language = useLanguage();
  const { updateSettings } = useSession();
  const choose = (value: Language) => {
    if (value !== language) void updateSettings((s) => ({ ...s, appearance: { ...s.appearance, language: value } }));
  };
  return (
    <div role="radiogroup" aria-label={t("Language")} className="mr-1 flex h-7 items-center rounded-sm border border-line p-0.5" data-testid="language-toggle">
      {languages.map((l) => (
        <button
          key={l.value}
          type="button"
          role="radio"
          aria-checked={language === l.value}
          aria-label={l.label}
          onClick={() => choose(l.value)}
          className={cn("h-full rounded-[3px] px-1.5 text-[11px] font-semibold tracking-wide transition-colors", language === l.value ? "bg-[color-mix(in_srgb,var(--accent)_16%,transparent)] text-accent-text" : "text-ink-3 hover:text-ink")}
        >
          {l.short}
        </button>
      ))}
    </div>
  );
}

export function TopBar({ onOpenPalette }: { onOpenPalette: () => void }) {
  const { session, settings, setTheme, resolvedTheme, isDemo } = useSession();
  const queryClient = useQueryClient();
  const { running, reopen } = usePlanFlow();
  const items = useNotifications();
  const unread = items.filter((i) => !i.read).length;
  const hasSession = session != null && session.mode !== "None";
  const known = settings?.knownTenants ?? [];

  const refresh = async () => {
    try {
      await bridge.call("rooms.list", { refresh: true });
      await queryClient.invalidateQueries();
      toast.success(t("Inventory refreshed"), { description: t("Rooms, room lists and Places data were reloaded.") });
    } catch (error) {
      toast.error(t("Refresh failed"), { description: error instanceof Error ? error.message : undefined });
    }
  };

  return (
    <header className="flex h-[52px] shrink-0 items-center gap-3 border-b border-line bg-bg px-4">
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <button type="button" className={cn("flex h-8 min-w-0 items-center gap-2 rounded-sm border px-2.5 text-left hover:bg-hover", isDemo ? "border-[color-mix(in_srgb,var(--demo)_45%,transparent)]" : "border-line-strong")} data-testid="tenant-switcher">
            <span className={cn("flex size-5 items-center justify-center rounded-[4px] text-[10px] font-bold", isDemo ? "bg-demo text-white" : hasSession ? "bg-accent text-accent-ink" : "bg-hover text-ink-3")}>
              {isDemo ? <FlaskConical className="size-3" /> : hasSession ? initials(session?.tenantName) : <PlugZap className="size-3" />}
            </span>
            <span className="min-w-0 leading-tight">
              <span className="block max-w-[220px] truncate text-[12.5px] font-semibold">{hasSession ? session?.tenantName ?? session?.tenantId : t("No tenant connected")}</span>
              <span className="block text-[10.5px] text-ink-3">{isDemo ? t("Demo tenant · synthetic data") : hasSession ? t("Live Microsoft 365 tenant") : t("Connect or start demo mode")}</span>
            </span>
            <ChevronDown className="size-3.5 text-ink-3" />
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-72">
          <DropdownMenuLabel>{t("Active session")}</DropdownMenuLabel>
          <div className="px-2 pb-2 text-[12px] text-ink-2">
            {hasSession ? (
              <>
                <div className="font-medium text-ink">{session?.tenantName}</div>
                <div className="t-mono text-[10.5px] text-ink-3">{session?.tenantId}</div>
                <div className="mt-1">{session?.adminAccount}</div>
              </>
            ) : t("No session")}
          </div>
          {known.length > 0 && (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuLabel>{t("Switch tenant")}</DropdownMenuLabel>
              {known.map((tenant) => (
                <DropdownMenuItem key={tenant.tenantId} onSelect={() => navigate(`/connections?account=${encodeURIComponent(tenant.lastAccount ?? "")}`)}>
                  <span className="min-w-0 flex-1 truncate">{tenant.displayName ?? tenant.tenantId}</span>
                  {tenant.tenantId === session?.tenantId && <Check />}
                </DropdownMenuItem>
              ))}
            </>
          )}
          <DropdownMenuSeparator />
          <DropdownMenuItem onSelect={() => navigate("/connections")}><PlugZap />{t("Connect to a tenant…")}</DropdownMenuItem>
          {!isDemo && <DropdownMenuItem onSelect={() => void bridge.call("session.startDemo", {}).then(() => navigate("/"))}><FlaskConical />{t("Explore demo mode")}</DropdownMenuItem>}
          {hasSession && <DropdownMenuItem danger onSelect={() => void bridge.call("session.end", {}).then(() => navigate("/"))}><LogOut />{isDemo ? t("Exit demo mode") : t("Disconnect and clear session")}</DropdownMenuItem>}
        </DropdownMenuContent>
      </DropdownMenu>

      <button type="button" onClick={onOpenPalette} className="mx-auto flex h-8 w-full max-w-[420px] items-center gap-2 rounded-sm border border-line bg-surface px-2.5 text-[12.5px] text-ink-3 hover:border-line-strong" data-testid="command-trigger">
        <Search className="size-3.5" />
        <span className="flex-1 text-left">{t("Search rooms, pages and actions")}</span>
        <Kbd>Ctrl</Kbd><Kbd>K</Kbd>
      </button>

      <div className="flex items-center gap-1">
        {running.length > 0 && (
          <button type="button" onClick={() => reopen(running[0].executionId)} className="flex h-7 items-center gap-1.5 rounded-full border border-[color-mix(in_srgb,var(--accent)_40%,transparent)] px-2.5 text-[11.5px] font-medium text-accent-text">
            <Loader2 className="size-3 animate-spin" />
            {t("{n} running", { n: running.length })}</button>
        )}
        {hasSession && (
          <div className="flex items-center rounded-sm border border-line px-0.5" aria-label={t("Service connections")}>
            {(["Exchange", "Places", "Graph"] as const).map((s) => <ConnectionPill key={s} service={s} connection={session?.services.find((c) => c.service === s)} />)}
          </div>
        )}
        <Tooltip content={t("Refresh inventory (Ctrl+Shift+R)")}>
          <Button variant="ghost" size="icon" onClick={() => void refresh()} disabled={!hasSession} aria-label={t("Refresh")}><RefreshCw /></Button>
        </Tooltip>
        <LanguageToggle />
        <DropdownMenu>
          <Tooltip content={t("Theme")}>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" aria-label={t("Theme")}>{resolvedTheme === "dark" ? <Moon /> : <Sun />}</Button>
            </DropdownMenuTrigger>
          </Tooltip>
          <DropdownMenuContent>
            <DropdownMenuItem onSelect={() => setTheme("Dark")}><Moon />{t("Dark")}{settings?.appearance.theme === "Dark" && <Check className="ml-auto" />}</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => setTheme("Light")}><Sun />{t("Light")}{settings?.appearance.theme === "Light" && <Check className="ml-auto" />}</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => setTheme("System")}><SunMoon />{t("Match system")}{settings?.appearance.theme === "System" && <Check className="ml-auto" />}</DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
        <Popover onOpenChange={(open) => !open && notifications.markAllRead()}>
          <PopoverTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={unread ? t("Notifications ({n} unread)", { n: unread }) : t("Notifications")} className="relative">
              <Bell />
              {unread > 0 && <span className="absolute right-1.5 top-1.5 size-2 rounded-full bg-accent ring-2 ring-bg" />}
            </Button>
          </PopoverTrigger>
          <PopoverContent align="end" className="w-[360px] p-0">
            <div className="flex items-center justify-between border-b border-line px-3 py-2">
              <span className="text-[13px] font-semibold">{t("Notifications")}</span>
              {items.length > 0 && <button type="button" className="text-[11.5px] text-ink-3 hover:text-ink" onClick={() => notifications.clear()}>{t("Clear")}</button>}
            </div>
            <div className="scroll-area max-h-[420px]">
              {items.length === 0 ? (
                <p className="px-4 py-8 text-center text-[12.5px] text-ink-3">{t("Completed operations, scans and connection changes from this session appear here.")}</p>
              ) : (
                items.map((n) => (
                  <button key={n.id} type="button" onClick={() => n.href && navigate(n.href)} className="flex w-full gap-2.5 border-b border-line px-3 py-2.5 text-left last:border-b-0 hover:bg-hover">
                    <Dot tone={n.tone === "info" ? "accent" : n.tone} className="mt-1.5" />
                    <span className="min-w-0 flex-1">
                      <span className="block text-[12.5px] font-medium text-ink">{n.title}</span>
                      {n.description && <span className="mt-0.5 block text-[12px] text-ink-2">{n.description}</span>}
                      <span className="mt-0.5 block text-[11px] text-ink-3">{formatRelative(new Date(n.at).toISOString())}</span>
                    </span>
                  </button>
                ))
              )}
            </div>
          </PopoverContent>
        </Popover>
        {hasSession && (
          <Tooltip content={session?.adminAccount ?? ""}>
            <span className="ml-1 flex size-7 items-center justify-center rounded-full bg-raised text-[11px] font-semibold text-ink-2 ring-1 ring-line-strong">{initials(session?.adminAccount)}</span>
          </Tooltip>
        )}
      </div>
    </header>
  );
}
