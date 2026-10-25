import type { ReactNode } from "react";
import { AlertOctagon, AlertTriangle, ArrowRight, Ban, Building2, Check, CheckCircle2, CircleDashed, Clock3, DoorOpen, Hourglass, Info, ListTree, Loader2, RefreshCcw, ShieldCheck, XCircle } from "lucide-react";
import type { ExecutionStage, PropertyChange, TargetExecutionState, TargetExecutionStatus, TargetKind, TargetPreview, ValidationIssue, VerificationResult } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { Badge } from "@/components/ui/core";
import { SourceBadge } from "@/components/common/domain";
import { t, tx } from "@/lib/i18n";

export const kindIcon: Record<TargetKind, ReactNode> = {
  Room: <DoorOpen />,
  RoomList: <ListTree />,
  Place: <Building2 />,
  Tenant: <ShieldCheck />,
};

export function ChangeTable({ changes }: { changes: PropertyChange[] }) {
  if (changes.length === 0) return null;
  return (
    <table className="w-full table-fixed text-[12.5px]">
      <thead>
        <tr className="text-left text-ink-3">
          <th className="w-[34%] py-1.5 pr-2 font-medium">{t("Setting")}</th>
          <th className="w-[33%] py-1.5 pr-2 font-medium">{t("Current")}</th>
          <th className="w-[33%] py-1.5 font-medium">{t("New")}</th>
        </tr>
      </thead>
      <tbody>
        {changes.map((c) => (
          <tr key={c.field + c.label} className="border-t border-line align-top">
            <td className="py-1.5 pr-2">
              <div className="flex items-center gap-1.5">
                <SourceBadge source={c.source} />
                <span className="truncate text-ink" title={c.label}>{c.label}</span>
              </div>
              <div className="t-mono mt-0.5 truncate pl-[46px] text-[10.5px] text-ink-3">{c.field.includes(":") ? c.field.split(":")[0] : c.field}</div>
            </td>
            <td className="break-words py-1.5 pr-2 text-ink-3 line-through decoration-ink-3/40">{c.before}</td>
            <td className="break-words py-1.5 text-ink">
              <span className="inline-flex items-start gap-1"><ArrowRight className="mt-[3px] size-3 shrink-0 text-accent" />{c.after}</span>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function IssueList({ issues }: { issues: ValidationIssue[] }) {
  if (issues.length === 0) return null;
  const order = { Error: 0, Warning: 1, Info: 2 } as const;
  return (
    <ul className="space-y-1">
      {[...issues].sort((a, b) => order[a.level] - order[b.level]).map((i, idx) => (
        <li key={idx} className="flex gap-2 text-[12.5px] leading-snug">
          {i.level === "Error" ? <AlertOctagon className="mt-[2px] size-3.5 shrink-0 text-bad" /> : i.level === "Warning" ? <AlertTriangle className="mt-[2px] size-3.5 shrink-0 text-warn-ink" /> : <Info className="mt-[2px] size-3.5 shrink-0 text-ink-3" />}
          <span className={i.level === "Error" ? "text-ink" : "text-ink-2"}>{i.message}</span>
        </li>
      ))}
    </ul>
  );
}

export function PreviewStatusBadge({ status }: { status: TargetPreview["status"] }) {
  if (status === "Changes") return <Badge tone="accent">{t("Changes")}</Badge>;
  if (status === "NoChange") return <Badge tone="neutral"><Check />{t("No change required")}</Badge>;
  return <Badge tone="bad"><Ban />{t("Blocked")}</Badge>;
}

const statusMeta: Record<TargetExecutionStatus, { label: string; tone: "ok" | "warn" | "bad" | "neutral" | "accent"; icon: ReactNode }> = {
  Pending: { label: tx("Pending"), tone: "neutral", icon: <CircleDashed /> },
  Running: { label: tx("Running"), tone: "accent", icon: <Loader2 className="animate-spin" /> },
  Succeeded: { label: tx("Verified"), tone: "ok", icon: <CheckCircle2 /> },
  PendingPropagation: { label: tx("Accepted · propagation pending"), tone: "warn", icon: <Hourglass /> },
  PendingProvisioning: { label: tx("Waiting for provisioning"), tone: "warn", icon: <Clock3 /> },
  VerificationMismatch: { label: tx("Verification mismatch"), tone: "bad", icon: <AlertTriangle /> },
  NoChange: { label: tx("No change required"), tone: "neutral", icon: <Check /> },
  Skipped: { label: tx("Skipped"), tone: "neutral", icon: <Ban /> },
  Stale: { label: tx("Stale — not applied"), tone: "warn", icon: <RefreshCcw /> },
  Failed: { label: tx("Failed"), tone: "bad", icon: <XCircle /> },
  Canceled: { label: tx("Canceled"), tone: "neutral", icon: <Ban /> },
};

export function ExecutionStatusBadge({ status, verification }: { status: TargetExecutionStatus; verification?: VerificationResult | null }) {
  const meta = statusMeta[status];
  const label = status === "Succeeded" && verification?.outcome === "NotApplicable" ? t("Completed") : t(meta.label);
  return <Badge tone={meta.tone}>{meta.icon}{label}</Badge>;
}

const stageLabel: Record<ExecutionStage, string> = {
  Pending: tx("Waiting"),
  Revalidating: tx("Rereading current configuration"),
  Applying: tx("Applying"),
  WaitingForProvisioning: tx("Waiting for provisioning"),
  Verifying: tx("Reading back result"),
  Completed: tx("Completed"),
};

export function TargetProgress({ target }: { target: TargetExecutionState }) {
  const done = target.steps.filter((s) => s.status !== "Pending" && s.status !== "Running" && s.status !== "NotRun").length;
  return (
    <div className="flex items-center gap-2 text-[12px] text-ink-3">
      {target.stage !== "Completed" && target.status === "Running" && <Loader2 className="size-3 animate-spin text-accent" />}
      <span>{t(stageLabel[target.stage])}</span>
      {target.steps.length > 0 && <span className="tabular">· {t("step {current} of {total}", { current: Math.min(done + (target.stage === "Applying" ? 1 : 0), target.steps.length), total: target.steps.length })}</span>}
    </div>
  );
}

/** Provisioning stages for newly created objects. Each stage is derived from observed execution facts, never assumed. */
export function ProvisioningStages({ target }: { target: TargetExecutionState }) {
  const firstOk = target.steps[0]?.status === "Succeeded";
  const created = target.verification?.fields.some((f) => f.field === "Exists" && f.matches) || target.steps.slice(1).some((s) => s.status === "Succeeded");
  const pending = target.status === "PendingPropagation" || target.status === "PendingProvisioning";
  const verified = target.status === "Succeeded" && target.verification?.outcome === "Verified";
  const failed = target.status === "Failed" || target.status === "VerificationMismatch";
  const stages = [
    { label: t("Requested"), state: "done" },
    { label: t("Accepted by Microsoft 365"), state: firstOk ? "done" : failed ? "failed" : "active" },
    { label: t("Object created"), state: created ? "done" : firstOk && !failed ? "active" : "idle" },
    { label: t("Directory synchronization"), state: verified ? "done" : pending ? "pending" : created ? "active" : "idle" },
    { label: t("Fully verified"), state: verified ? "done" : "idle" },
  ] as const;
  return (
    <ol className="flex flex-wrap items-center gap-x-1 gap-y-2" aria-label={t("Provisioning progress")}>
      {stages.map((s, i) => (
        <li key={s.label} className="flex items-center gap-1">
          <span
            className={cn(
              "inline-flex h-6 items-center gap-1.5 rounded-full border px-2.5 text-[11.5px] font-medium",
              s.state === "done" && "border-[color-mix(in_srgb,var(--ok)_40%,transparent)] text-ok",
              s.state === "active" && "border-accent text-accent-text",
              s.state === "pending" && "border-[color-mix(in_srgb,var(--warn)_45%,transparent)] text-warn-ink",
              s.state === "failed" && "border-[color-mix(in_srgb,var(--bad)_45%,transparent)] text-bad",
              s.state === "idle" && "border-line text-ink-3",
            )}
          >
            {s.state === "done" ? <Check className="size-3" /> : s.state === "active" ? <Loader2 className="size-3 animate-spin" /> : s.state === "pending" ? <Hourglass className="size-3" /> : <CircleDashed className="size-3" />}
            {s.state === "pending" ? t("{stage} pending", { stage: s.label }) : s.label}
          </span>
          {i < stages.length - 1 && <span className="h-px w-3 bg-line-strong" />}
        </li>
      ))}
    </ol>
  );
}

export function VerificationDetails({ verification }: { verification: VerificationResult }) {
  return (
    <div className="mt-2 rounded-md border border-line bg-sunken/50 px-3 py-2">
      <p className="text-[12px] text-ink-2">{verification.message}</p>
      {verification.fields.length > 0 && (
        <table className="mt-1.5 w-full table-fixed text-[11.5px]">
          <tbody>
            {verification.fields.map((f) => (
              <tr key={f.field} className="border-t border-line first:border-t-0">
                <td className="w-[40%] truncate py-1 pr-2 text-ink-2">{f.label}</td>
                <td className="truncate py-1 pr-2 text-ink-3" title={t("Expected {value}", { value: f.expected })}>{f.expected}</td>
                <td className={cn("truncate py-1 pr-2", f.matches ? "text-ink" : "text-bad")} title={t("Read back {value}", { value: f.actual })}>{f.actual}</td>
                <td className="w-5 py-1">{f.matches ? <Check className="size-3.5 text-ok" /> : <XCircle className="size-3.5 text-bad" />}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
