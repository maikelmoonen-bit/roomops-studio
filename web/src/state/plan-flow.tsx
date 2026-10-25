import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { toast } from "sonner";
import { ChevronRight, FileCode2, Loader2, ShieldCheck } from "lucide-react";
import { bridge, BridgeError } from "@/bridge/client";
import type { ChangeIntent, ExecutionState, PlanPreview } from "@/bridge/contracts.gen";
import { cn, formatDateTime } from "@/lib/format";
import { navigate } from "@/lib/router";
import { Button } from "@/components/ui/button";
import { Badge, Progress } from "@/components/ui/core";
import { Checkbox, Disclosure } from "@/components/ui/controls";
import { Dialog, DialogContent } from "@/components/ui/overlay";
import { CodeBlock } from "@/components/common/code-block";
import { ErrorState, Identifier, InlineNotice } from "@/components/common/domain";
import { ChangeTable, ExecutionStatusBadge, IssueList, kindIcon, PreviewStatusBadge, ProvisioningStages, TargetProgress, VerificationDetails } from "@/components/plan/plan-parts";
import { t, tn, tr, useLanguage } from "@/lib/i18n";

interface ReviewOptions {
  /** Called once the execution finishes (whatever the outcome). */
  onCompleted?: (state: ExecutionState) => void;
}

interface PlanFlowValue {
  /** Creates a preview for an intent and opens the review dialog. Nothing is executed until the administrator confirms. */
  review: (intent: ChangeIntent, options?: ReviewOptions) => void;
  reviewPreview: (preview: PlanPreview, options?: ReviewOptions) => void;
  reopen: (executionId: string) => void;
  running: ExecutionState[];
}

const PlanFlowContext = createContext<PlanFlowValue | null>(null);

export function usePlanFlow() {
  const value = useContext(PlanFlowContext);
  if (!value) throw new Error("usePlanFlow must be used inside PlanFlowProvider");
  return value;
}

type Phase =
  | { kind: "closed" }
  | { kind: "loading" }
  | { kind: "error"; error: unknown; intent: ChangeIntent | null }
  | { kind: "preview"; preview: PlanPreview }
  | { kind: "execution"; executionId: string; preview: PlanPreview | null };

export function PlanFlowProvider({ children }: { children: ReactNode }) {
  // The review and execution dialogs live above the routed content; subscribing keeps them in the current language.
  useLanguage();
  const [phase, setPhase] = useState<Phase>({ kind: "closed" });
  const [executions, setExecutions] = useState<Record<string, ExecutionState>>({});
  const options = useRef<ReviewOptions | undefined>(undefined);
  const completedCallbacks = useRef<Record<string, ReviewOptions["onCompleted"]>>({});

  const load = useCallback(async (intent: ChangeIntent) => {
    setPhase({ kind: "loading" });
    try {
      setPhase({ kind: "preview", preview: await bridge.call("plans.create", intent) });
    } catch (error) {
      setPhase({ kind: "error", error, intent });
    }
  }, []);

  const refresh = useCallback(async (executionId: string) => {
    try {
      const state = await bridge.call("executions.get", { executionId });
      setExecutions((all) => ({ ...all, [executionId]: state }));
    } catch {
      // Execution may have been pruned; keep the last known state.
    }
  }, []);

  useEffect(() => {
    const timers = new Map<string, number>();
    const offProgress = bridge.on("execution.progress", (e) => {
      if (timers.has(e.executionId)) return;
      timers.set(e.executionId, window.setTimeout(() => { timers.delete(e.executionId); void refresh(e.executionId); }, 120));
    });
    const offCompleted = bridge.on("execution.completed", (state) => {
      setExecutions((all) => ({ ...all, [state.executionId]: state }));
      completedCallbacks.current[state.executionId]?.(state);
      delete completedCallbacks.current[state.executionId];
      const issues = state.counts.failed + state.counts.skipped;
      if (issues === 0) toast.success(state.title, { description: t("{applied} applied, {unchanged} unchanged.", { applied: state.counts.succeeded, unchanged: state.counts.noChange }) });
      else toast.warning(state.title, { description: t("{failed} failed, {skipped} skipped. Review the results.", { failed: state.counts.failed, skipped: state.counts.skipped }) });
    });
    return () => {
      offProgress();
      offCompleted();
      timers.forEach((tg) => window.clearTimeout(tg));
    };
  }, [refresh]);

  const execute = useCallback(async (preview: PlanPreview, acknowledgeRemovals: boolean) => {
    try {
      const state = await bridge.call("plans.execute", { planId: preview.planId, confirmTenantId: preview.tenantId, acknowledgeRemovals });
      if (options.current?.onCompleted) completedCallbacks.current[state.executionId] = options.current.onCompleted;
      setExecutions((all) => ({ ...all, [state.executionId]: state }));
      setPhase({ kind: "execution", executionId: state.executionId, preview });
    } catch (error) {
      setPhase({ kind: "error", error, intent: null });
    }
  }, []);

  const retry = useCallback(async (executionId: string) => {
    setPhase({ kind: "loading" });
    try {
      setPhase({ kind: "preview", preview: await bridge.call("plans.retry", { executionId }) });
    } catch (error) {
      setPhase({ kind: "error", error, intent: null });
    }
  }, []);

  const value = useMemo<PlanFlowValue>(
    () => ({
      review: (intent, o) => {
        options.current = o;
        void load(intent);
      },
      reviewPreview: (preview, o) => {
        options.current = o;
        setPhase({ kind: "preview", preview });
      },
      reopen: (executionId) => setPhase({ kind: "execution", executionId, preview: null }),
      running: Object.values(executions).filter((e) => e.running),
    }),
    [load, executions],
  );

  const execution = phase.kind === "execution" ? executions[phase.executionId] : undefined;

  return (
    <PlanFlowContext.Provider value={value}>
      {children}
      <Dialog open={phase.kind !== "closed"} onOpenChange={(open) => !open && setPhase({ kind: "closed" })}>
        {phase.kind === "loading" && (
          <DialogContent title={t("Preparing preview")} description={t("Reading the current configuration and comparing it with the requested state.")} wide>
            <div className="flex flex-col items-center gap-3 py-12 text-ink-3">
              <Loader2 className="size-6 animate-spin text-accent" />
              <span className="text-[12.5px]">{t("Loading current configuration · validating · calculating differences")}</span>
            </div>
          </DialogContent>
        )}
        {phase.kind === "error" && (
          <DialogContent title={t("The preview could not be created")} wide footer={<Button onClick={() => setPhase({ kind: "closed" })}>{t("Close")}</Button>}>
            <ErrorState error={phase.error} compact onRetry={phase.intent ? () => void load(phase.intent!) : undefined} />
          </DialogContent>
        )}
        {phase.kind === "preview" && <PreviewView preview={phase.preview} onCancel={() => setPhase({ kind: "closed" })} onExecute={(ack) => void execute(phase.preview, ack)} />}
        {phase.kind === "execution" && execution && (
          <ExecutionView state={execution} onClose={() => setPhase({ kind: "closed" })} onRetry={() => void retry(execution.executionId)} />
        )}
      </Dialog>
    </PlanFlowContext.Provider>
  );
}

function TenantStrip({ tenantId, tenantName, isDemo, createdAt }: { tenantId: string; tenantName: string | null; isDemo: boolean; createdAt?: string }) {
  return (
    <div className={cn("mb-4 flex flex-wrap items-center gap-x-4 gap-y-1 rounded-md border px-3 py-2", isDemo ? "demo-stripes border-[color-mix(in_srgb,var(--demo)_40%,transparent)]" : "border-[color-mix(in_srgb,var(--accent)_35%,transparent)] bg-[color-mix(in_srgb,var(--accent)_6%,transparent)]")}>
      <ShieldCheck className={cn("size-4", isDemo ? "text-demo" : "text-accent")} />
      <div className="flex items-center gap-2">
        <span className="t-eyebrow">{t("Target tenant")}</span>
        <span className="font-semibold text-ink">{tenantName ?? tenantId}</span>
        {isDemo ? <Badge tone="demo">{t("DEMO · no Microsoft 365 changes")}</Badge> : <Badge tone="accent">{t("LIVE")}</Badge>}
      </div>
      <Identifier value={tenantId} />
      {createdAt && <span className="ml-auto text-[11.5px] text-ink-3">{t("Preview created {time}", { time: formatDateTime(createdAt) })}</span>}
    </div>
  );
}

function PreviewView({ preview, onCancel, onExecute }: { preview: PlanPreview; onCancel: () => void; onExecute: (acknowledgeRemovals: boolean) => void }) {
  const [acknowledged, setAcknowledged] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const executable = preview.totals.withChanges > 0;
  const blockedAll = preview.totals.blocked > 0 && preview.totals.withChanges === 0;
  const canApply = executable && (!preview.requiresRemovalAcknowledgement || acknowledged) && !submitting;
  const ordered = [...preview.targets].sort((a, b) => ({ Blocked: 0, Changes: 1, NoChange: 2 })[a.status] - ({ Blocked: 0, Changes: 1, NoChange: 2 })[b.status]);
  const visible = ordered.slice(0, 120);

  return (
    <DialogContent
      wide="xl"
      title={preview.title}
      description={t("Review exactly what will change. Nothing is sent to Microsoft 365 until you apply.")}
      onInteractOutside={(e) => e.preventDefault()}
      footer={
        <>
          <span className="mr-auto text-[12px] text-ink-3">
            {executable ? t("{commands} on {objects}", { commands: tn(preview.totals.operations, "{n} command", "{n} commands"), objects: tn(preview.totals.withChanges, "{n} object", "{n} objects") }) : blockedAll ? t("Resolve the blocking issues to continue.") : t("Everything already matches — nothing to apply.")}
          </span>
          <Button variant="ghost" onClick={onCancel}>{t("Cancel")}</Button>
          <Button
            variant="primary"
            disabled={!canApply}
            loading={submitting}
            onClick={() => {
              setSubmitting(true);
              onExecute(acknowledged);
            }}
          >
            {t("Apply configuration")}</Button>
        </>
      }
    >
      <TenantStrip tenantId={preview.tenantId} tenantName={preview.tenantName} isDemo={preview.isDemo} createdAt={preview.createdAt} />
      <div className="mb-4 flex flex-wrap gap-2">
        <Badge tone="outline">{tn(preview.totals.targets, "{n} object", "{n} objects")}</Badge>
        {preview.totals.withChanges > 0 && <Badge tone="accent">{t("{n} with changes", { n: preview.totals.withChanges })}</Badge>}
        {preview.totals.noChange > 0 && <Badge tone="neutral">{t("{n} no change required", { n: preview.totals.noChange })}</Badge>}
        {preview.totals.blocked > 0 && <Badge tone="bad">{t("{n} blocked", { n: preview.totals.blocked })}</Badge>}
        {preview.totals.removals > 0 && <Badge tone="warn">{tn(preview.totals.removals, "{n} removal", "{n} removals")}</Badge>}
        {preview.servicesUsed.map((s) => <Badge key={s} tone="outline">{s === "Exchange" ? t("Exchange Online") : s === "Places" ? t("Microsoft Places") : t("Microsoft Graph")}</Badge>)}
      </div>
      {preview.notes.length > 0 && (
        <InlineNotice tone="info" className="mb-4">
          <ul className="space-y-0.5">{preview.notes.map((n) => <li key={n}>{n}</li>)}</ul>
        </InlineNotice>
      )}

      <div className="space-y-2.5">
        {visible.map((tg, i) => (
          <section key={`${tg.targetId}-${tg.operationLabel}-${i}`} className={cn("rounded-lg border bg-surface", tg.status === "Blocked" ? "border-[color-mix(in_srgb,var(--bad)_40%,transparent)]" : "border-line")}>
            <header className="flex items-center gap-2.5 px-3.5 py-2.5">
              <span className="text-ink-3 [&_svg]:size-4">{kindIcon[tg.kind]}</span>
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[13px] font-semibold">{tg.targetName}</span>
                <span className="block text-[11.5px] text-ink-3">{tg.operationLabel}{tg.revertAvailable ? ` · ${t("reversible")}` : ""}</span>
              </span>
              <PreviewStatusBadge status={tg.status} />
            </header>
            {(tg.changes.length > 0 || tg.issues.length > 0 || tg.notes.length > 0) && (
              <div className="space-y-2.5 border-t border-line px-3.5 py-2.5">
                {tg.issues.length > 0 && <IssueList issues={tg.issues} />}
                <ChangeTable changes={tg.changes} />
                {tg.notes.length > 0 && <ul className="space-y-0.5 text-[12px] text-ink-3">{tg.notes.map((n) => <li key={n}>· {n}</li>)}</ul>}
              </div>
            )}
          </section>
        ))}
        {ordered.length > visible.length && <p className="text-center text-[12px] text-ink-3">{t("{n} more objects are included in this plan and the script below.", { n: ordered.length - visible.length })}</p>}
      </div>

      <div className="mt-4">
        <Disclosure title={<span className="flex items-center gap-2"><FileCode2 className="size-4 text-ink-3" />{t("Technical Execution Preview")}</span>} description={t("The exact commands RoomOps will run, bound by parameter name. Copy them to run manually if you prefer.")}>
          <div className="p-2">
            <CodeBlock code={preview.script} maxHeight={320} />
            <ul className="mt-2 space-y-0.5 text-[11.5px] text-ink-3">{preview.scriptNotes.map((n) => <li key={n}>· {n}</li>)}</ul>
          </div>
        </Disclosure>
      </div>

      {preview.requiresRemovalAcknowledgement && (
        <label className="mt-4 flex items-start gap-2.5 rounded-md border border-[color-mix(in_srgb,var(--warn)_45%,transparent)] bg-[color-mix(in_srgb,var(--warn)_7%,transparent)] px-3 py-2.5">
          <Checkbox checked={acknowledged} onCheckedChange={setAcknowledged} aria-label={t("Acknowledge removals")} />
          <span className="text-[12.5px] leading-snug">
            <span className="font-medium text-ink">{tn(preview.totals.removals, "I understand that {n} membership or permission will be removed in {tenant}.", "I understand that {n} memberships or permissions will be removed in {tenant}.", { tenant: preview.tenantName ?? preview.tenantId })}</span>
            <span className="t-help block">{t("Removals are reversible from Change History, but people may lose access in the meantime.")}</span>
          </span>
        </label>
      )}
    </DialogContent>
  );
}

function ExecutionView({ state, onClose, onRetry }: { state: ExecutionState; onClose: () => void; onRetry: () => void }) {
  const percent = state.counts.total === 0 ? 100 : (state.counts.completed / state.counts.total) * 100;
  const hasRetryable = !state.running && state.targets.some((tg) => ["Failed", "Stale", "Canceled", "VerificationMismatch"].includes(tg.status));
  const headline = state.running
    ? state.cancelRequested ? t("Stopping after the current item…") : t("Applying configuration")
    : state.counts.failed > 0 ? t("Completed with failures") : state.targets.some((tg) => tg.status === "PendingPropagation" || tg.status === "PendingProvisioning") ? t("Accepted — some changes are still propagating") : t("Applied and verified");
  const single = state.targets.length === 1 ? state.targets[0] : null;

  return (
    <DialogContent
      wide="xl"
      title={state.title}
      description={headline}
      onInteractOutside={(e) => state.running && e.preventDefault()}
      footer={
        <>
          {state.running ? (
            <>
              <span className="mr-auto text-[12px] text-ink-3">{t("You can close this window; the operation continues and appears in notifications.")}</span>
              <Button variant="ghost" disabled={state.cancelRequested} onClick={() => void bridge.call("executions.cancel", { executionId: state.executionId })}>{t("Stop after current item")}</Button>
              <Button onClick={onClose}>{t("Run in background")}</Button>
            </>
          ) : (
            <>
              <Button variant="ghost" className="mr-auto" onClick={() => { onClose(); navigate("/history"); }}>{t("View change history")}<ChevronRight /></Button>
              {hasRetryable && <Button onClick={onRetry}>{t("Retry unresolved items")}</Button>}
              <Button variant="primary" onClick={onClose}>{t("Done")}</Button>
            </>
          )}
        </>
      }
    >
      <TenantStrip tenantId={state.tenantId} tenantName={state.tenantName} isDemo={state.isDemo} />
      <div className="mb-4 rounded-lg border border-line bg-sunken/40 px-4 py-3">
        <div className="mb-2 flex items-center justify-between text-[12.5px]">
          <span className="font-medium">{t("{completed} of {total} complete", { completed: state.counts.completed, total: state.counts.total })}</span>
          <span className="flex gap-3 tabular text-ink-3">
            <span>{tr("{n} applied", { n: <span className="text-ok">{state.counts.succeeded}</span> })}</span>
            <span>{t("{n} unchanged", { n: state.counts.noChange })}</span>
            <span>{tr("{n} failed", { n: <span className={state.counts.failed ? "text-bad" : undefined}>{state.counts.failed}</span> })}</span>
            <span>{t("{n} skipped", { n: state.counts.skipped })}</span>
            <span>{t("{n} pending", { n: state.counts.pending })}</span>
            {state.counts.retryable > 0 && <span className="text-warn-ink">{t("{n} retryable", { n: state.counts.retryable })}</span>}
          </span>
        </div>
        <Progress value={percent} tone={state.counts.failed ? "warn" : state.running ? "accent" : "ok"} />
      </div>

      {single && /create/i.test(single.operationLabel) && (
        <div className="mb-4"><ProvisioningStages target={single} /></div>
      )}

      <div className="space-y-2">
        {state.targets.slice(0, 200).map((tg, i) => (
          <section key={`${tg.targetId}-${i}`} className="rounded-lg border border-line bg-surface px-3.5 py-2.5">
            <div className="flex items-center gap-3">
              <div className="min-w-0 flex-1">
                <div className="truncate text-[13px] font-semibold">{tg.targetName}</div>
                <div className="truncate text-[11.5px] text-ink-3">{tg.operationLabel}</div>
              </div>
              {tg.stage !== "Completed" && tg.status !== "Pending" ? <TargetProgress target={tg} /> : <ExecutionStatusBadge status={tg.status} verification={tg.verification} />}
            </div>
            {tg.message && tg.stage === "Completed" && !tg.verification && <p className={cn("mt-1.5 text-[12px]", tg.status === "Failed" ? "text-bad" : "text-ink-2")}>{tg.message}</p>}
            {tg.verification && <VerificationDetails verification={tg.verification} />}
            {tg.steps.some((s) => s.error) && (
              <ul className="mt-1.5 space-y-0.5">
                {tg.steps.filter((s) => s.error).map((s, j) => <li key={j} className="t-mono text-[11px] text-ink-3">{s.description}: {s.error}</li>)}
              </ul>
            )}
          </section>
        ))}
      </div>
    </DialogContent>
  );
}

export function describeError(error: unknown): string {
  return error instanceof BridgeError || error instanceof Error ? error.message : t("Unexpected error");
}
