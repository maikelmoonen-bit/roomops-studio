import { useState, type ReactNode } from "react";
import { AlertOctagon, AlertTriangle, CheckCircle2, CircleHelp, Copy, Check, Info, Lightbulb, PlugZap, ShieldAlert, Timer, WifiOff } from "lucide-react";
import type { FindingSeverity, SourceSystem } from "@/bridge/contracts.gen";
import { BridgeError } from "@/bridge/client";
import { cn, roomPlate } from "@/lib/format";
import { navigate } from "@/lib/router";
import { Badge } from "@/components/ui/core";
import { Button } from "@/components/ui/button";
import { Tooltip } from "@/components/ui/overlay";
import { t, tx } from "@/lib/i18n";

// ------------------------------------------------------------------------------------------------ Brand

export function LogoMark({ size = 28, className }: { size?: number; className?: string }) {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" className={className} aria-hidden>
      <rect width="32" height="32" rx="8" className="fill-[#16191e] dark:fill-[#222730]" />
      <rect x="7" y="7" width="8" height="8" rx="2" fill="none" stroke="#8b93a5" strokeWidth="1.6" />
      <rect x="17" y="7" width="8" height="8" rx="2" fill="#36b5f2" />
      <rect x="7" y="17" width="8" height="8" rx="2" fill="none" stroke="#8b93a5" strokeWidth="1.6" />
      <rect x="17" y="17" width="8" height="8" rx="2" fill="none" stroke="#8b93a5" strokeWidth="1.6" />
    </svg>
  );
}

// ------------------------------------------------------------------------------------------------ Room plate (signature)

/** Renders a room name in the style of architectural door signage: building code and room number on a plate. */
export function RoomName({ name, className, size = "md" }: { name: string; className?: string; size?: "md" | "lg" }) {
  const plate = roomPlate(name);
  if (!plate.code) return <span className={cn("truncate", className)}>{name}</span>;
  return (
    <span className={cn("flex min-w-0 items-center gap-2", className)}>
      <span
        className={cn(
          "inline-flex shrink-0 items-stretch overflow-hidden rounded-[4px] border border-line-strong font-display font-semibold leading-none tracking-wide",
          size === "lg" ? "h-8 text-[16px]" : "h-[20px] text-[12px]",
        )}
      >
        <span className={cn("flex items-center bg-ink text-bg", size === "lg" ? "px-2" : "px-1.5")}>{plate.code}</span>
        <span className={cn("flex items-center tabular text-ink", size === "lg" ? "px-2" : "px-1.5")}>{plate.number}</span>
      </span>
      <span className="truncate">{plate.name}</span>
    </span>
  );
}

// ------------------------------------------------------------------------------------------------ Source & severity

const sourceMeta: Record<SourceSystem, { label: string; short: string; className: string }> = {
  ExchangeOnline: { label: tx("Exchange Online"), short: "EXO", className: "text-[#4ea3e8] border-[#4ea3e8]/40" },
  MicrosoftPlaces: { label: tx("Microsoft Places"), short: "PLC", className: "text-[#3fb6a8] border-[#3fb6a8]/40" },
  MicrosoftGraph: { label: tx("Microsoft Graph"), short: "GRA", className: "text-[#c18bf0] border-[#c18bf0]/40" },
  RoomOpsLocal: { label: tx("RoomOps (local)"), short: "LOC", className: "text-ink-3 border-line-strong" },
};

export function SourceBadge({ source, full }: { source: SourceSystem; full?: boolean }) {
  const meta = sourceMeta[source];
  return (
    <Tooltip content={t("Authoritative source: {source}", { source: t(meta.label) })}>
      <span className={cn("inline-flex h-[18px] items-center rounded-[4px] border px-1.5 font-mono text-[10px] font-semibold tracking-wide", meta.className)}>{full ? t(meta.label) : meta.short}</span>
    </Tooltip>
  );
}

const severityMeta: Record<FindingSeverity, { label: string; tone: "bad" | "warn" | "info" | "neutral" | "outline"; icon: ReactNode }> = {
  Critical: { label: tx("Critical"), tone: "bad", icon: <AlertOctagon /> },
  Warning: { label: tx("Warning"), tone: "warn", icon: <AlertTriangle /> },
  Recommendation: { label: tx("Recommendation"), tone: "info", icon: <Lightbulb /> },
  Informational: { label: tx("Informational"), tone: "neutral", icon: <Info /> },
  UnableToVerify: { label: tx("Unable to verify"), tone: "outline", icon: <CircleHelp /> },
};

export function SeverityBadge({ severity }: { severity: FindingSeverity }) {
  const meta = severityMeta[severity];
  return (
    <Badge tone={meta.tone}>
      {meta.icon}
      {t(meta.label)}
    </Badge>
  );
}

export const severityOrder: FindingSeverity[] = ["Critical", "Warning", "Recommendation", "Informational", "UnableToVerify"];
export const severityLabel = (s: FindingSeverity) => t(severityMeta[s].label);

// ------------------------------------------------------------------------------------------------ Identifiers

export function Identifier({ value, className, copy = true }: { value: string | null | undefined; className?: string; copy?: boolean }) {
  const [copied, setCopied] = useState(false);
  if (!value) return <span className="text-ink-3">—</span>;
  return (
    <span className={cn("group inline-flex min-w-0 items-center gap-1", className)}>
      <span className="t-mono truncate text-ink-2" title={value}>{value}</span>
      {copy && (
        <button
          type="button"
          aria-label={t("Copy")}
          className="shrink-0 rounded-[3px] p-0.5 text-ink-3 opacity-0 transition-opacity hover:text-ink group-hover:opacity-100 focus-visible:opacity-100"
          onClick={(e) => {
            e.stopPropagation();
            void navigator.clipboard?.writeText(value).then(() => {
              setCopied(true);
              window.setTimeout(() => setCopied(false), 1200);
            });
          }}
        >
          {copied ? <Check className="size-3 text-ok" /> : <Copy className="size-3" />}
        </button>
      )}
    </span>
  );
}

// ------------------------------------------------------------------------------------------------ Page header

export function PageHeader({ title, description, actions, eyebrow, children }: { title: ReactNode; description?: ReactNode; actions?: ReactNode; eyebrow?: ReactNode; children?: ReactNode }) {
  return (
    <header className="mb-5 flex flex-wrap items-end justify-between gap-x-6 gap-y-3">
      <div className="min-w-0 max-w-3xl">
        {eyebrow && <div className="t-eyebrow mb-1.5">{eyebrow}</div>}
        <h1 className="t-page text-ink">{title}</h1>
        {description && <p className="mt-1.5 text-[13px] leading-relaxed text-ink-2">{description}</p>}
        {children}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}

// ------------------------------------------------------------------------------------------------ Empty and error states

export function EmptyState({ icon, title, description, action, className }: { icon?: ReactNode; title: ReactNode; description?: ReactNode; action?: ReactNode; className?: string }) {
  return (
    <div className={cn("flex flex-col items-center justify-center px-6 py-14 text-center", className)}>
      {icon && <div className="mb-3 flex size-10 items-center justify-center rounded-lg border border-line bg-sunken text-ink-3 [&_svg]:size-5">{icon}</div>}
      <div className="text-[14px] font-semibold text-ink">{title}</div>
      {description && <p className="mt-1 max-w-md text-[12.5px] leading-relaxed text-ink-3">{description}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  );
}

const errorGuidance: Partial<Record<string, { icon: ReactNode; title: string; hint: string }>> = {
  NotConnected: { icon: <PlugZap />, title: tx("Not connected"), hint: tx("Connect the required Microsoft 365 service on the Connections page.") },
  Authentication: { icon: <ShieldAlert />, title: tx("Session expired or sign-in required"), hint: tx("Reconnect the service on the Connections page. Multi-factor and Conditional Access prompts appear in the sign-in window.") },
  PermissionDenied: { icon: <ShieldAlert />, title: tx("Permission denied"), hint: tx("Your role doesn't allow this operation. Exchange Administrator, Places Administrator or a custom role with the required permissions is needed.") },
  Throttled: { icon: <Timer />, title: tx("Throttled by Microsoft 365"), hint: tx("The service limited the request rate. Wait a moment and try again.") },
  Transient: { icon: <WifiOff />, title: tx("Temporary failure"), hint: tx("The request didn't complete. Check connectivity and try again.") },
  TenantMismatch: { icon: <ShieldAlert />, title: tx("Tenant changed"), hint: tx("The active tenant is different from the one this action was prepared for. Nothing was changed.") },
};

export function ErrorState({ error, onRetry, compact }: { error: unknown; onRetry?: () => void; compact?: boolean }) {
  const kind = error instanceof BridgeError ? error.kind : "Internal";
  const message = error instanceof Error ? error.message : t("An unexpected error occurred.");
  const guidance = errorGuidance[kind];
  return (
    <div className={cn("flex flex-col items-center text-center", compact ? "py-6" : "py-14")} role="alert">
      <div className="mb-3 flex size-10 items-center justify-center rounded-lg border border-[color-mix(in_srgb,var(--bad)_35%,transparent)] bg-[color-mix(in_srgb,var(--bad)_8%,transparent)] text-bad [&_svg]:size-5">
        {guidance?.icon ?? <AlertOctagon />}
      </div>
      <div className="text-[14px] font-semibold">{guidance ? t(guidance.title) : t("Something went wrong")}</div>
      <p className="mt-1 max-w-lg text-[12.5px] leading-relaxed text-ink-2">{message}</p>
      {guidance && <p className="t-help mt-1 max-w-lg">{t(guidance.hint)}</p>}
      <div className="mt-4 flex gap-2">
        {kind === "NotConnected" || kind === "Authentication" ? (
          <Button variant="primary" onClick={() => navigate("/connections")}>{t("Open connections")}</Button>
        ) : null}
        {onRetry && <Button onClick={onRetry}>{t("Try again")}</Button>}
      </div>
    </div>
  );
}

export function InlineNotice({ tone = "info", title, children, icon, action, className }: { tone?: "info" | "warn" | "bad" | "ok" | "demo"; title?: ReactNode; children?: ReactNode; icon?: ReactNode; action?: ReactNode; className?: string }) {
  const color = { info: "var(--accent)", warn: "var(--warn)", bad: "var(--bad)", ok: "var(--ok)", demo: "var(--demo)" }[tone];
  const fallbackIcon = { info: <Info />, warn: <AlertTriangle />, bad: <AlertOctagon />, ok: <CheckCircle2 />, demo: <Info /> }[tone];
  return (
    <div className={cn("flex gap-3 rounded-md border px-3 py-2.5 text-[12.5px] leading-relaxed", className)} style={{ borderColor: `color-mix(in srgb, ${color} 32%, transparent)`, background: `color-mix(in srgb, ${color} 7%, transparent)` }}>
      <span className="mt-[1px] shrink-0 [&_svg]:size-4" style={{ color }}>{icon ?? fallbackIcon}</span>
      <div className="min-w-0 flex-1 text-ink-2">
        {title && <div className="font-medium text-ink">{title}</div>}
        {children}
      </div>
      {action && <div className="shrink-0 self-center">{action}</div>}
    </div>
  );
}

/** Explicit "not available" value — never a fabricated number. */
export function Unavailable({ reason }: { reason: string }) {
  return (
    <Tooltip content={reason}>
      <span className="inline-flex items-center gap-1 text-[12px] text-ink-3"><CircleHelp className="size-3.5" />{t("Unavailable")}</span>
    </Tooltip>
  );
}
