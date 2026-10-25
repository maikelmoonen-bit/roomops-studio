import { useState } from "react";
import { Download, FileText, Play, RefreshCcw, RotateCcw, Search } from "lucide-react";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { AuditOutcome, AuditRecord, TargetExecutionStatus } from "@/bridge/contracts.gen";
import { formatDateTime, formatRelative } from "@/lib/format";
import { saveText } from "@/lib/files";
import { navigate } from "@/lib/router";
import { usePlanFlow } from "@/state/plan-flow";
import { useSession } from "@/state/session";
import { Button } from "@/components/ui/button";
import { Badge, Card, Input, Skeleton } from "@/components/ui/core";
import { Select } from "@/components/ui/controls";
import { Dialog, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger, SheetContent } from "@/components/ui/overlay";
import { CodeBlock } from "@/components/common/code-block";
import { EmptyState, ErrorState, Identifier, InlineNotice, PageHeader, RoomName } from "@/components/common/domain";
import { ChangeTable, ExecutionStatusBadge, VerificationDetails } from "@/components/plan/plan-parts";
import { t, tn } from "@/lib/i18n";

export function outcomeStatus(r: AuditRecord): TargetExecutionStatus {
  switch (r.outcome) {
    case "Succeeded": return r.verification?.outcome === "PendingPropagation" || r.verification?.outcome === "NotVerified" ? "PendingPropagation" : r.verification?.outcome === "Mismatch" ? "VerificationMismatch" : "Succeeded";
    case "PendingProvisioning": return "PendingProvisioning";
    case "NoChange": return "NoChange";
    case "Skipped": return "Skipped";
    case "Stale": return "Stale";
    default: return "Failed";
  }
}

const outcomes: (AuditOutcome | "all")[] = ["all", "Succeeded", "Failed", "PartiallyApplied", "PendingProvisioning", "Stale", "NoChange"];

export default function HistoryPage({ auditId }: { auditId?: string }) {
  const { isDemo } = useSession();
  const [search, setSearch] = useState("");
  const [outcome, setOutcome] = useState<AuditOutcome | "all">("all");
  const query = { search: search || null, outcome: outcome === "all" ? null : outcome, limit: 300 };
  const page = useBridgeQuery("history.list", query);

  const exportAs = async (format: "html" | "json") => {
    try {
      const file = await bridge.call("history.export", { format, query, ids: null });
      if (await saveText(file.fileName, file.mimeType, file.content)) toast.success(t("Exported {file}", { file: file.fileName }));
    } catch (error) {
      toast.error(t("Export failed"), { description: error instanceof Error ? error.message : undefined });
    }
  };

  return (
    <div>
      <PageHeader
        title={t("Change history")}
        description={t("Every change applied through RoomOps Studio in this tenant, with its before-state, executed commands and verification result.")}
        actions={
          <DropdownMenu>
            <DropdownMenuTrigger asChild><Button icon={<Download />}>{t("Export")}</Button></DropdownMenuTrigger>
            <DropdownMenuContent>
              <DropdownMenuItem onSelect={() => void exportAs("html")}><FileText />{t("Change report (HTML)")}</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => void exportAs("json")}><Download />{t("Records (JSON)")}</DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        }
      />
      <InlineNotice tone={isDemo ? "demo" : "info"} className="mb-4">
        {isDemo ? `${t("Demo history: sample records for the synthetic tenant, discarded when the demo ends.")} ` : ""}
        {t("This is an application-local log stored on this device. It is not the Microsoft 365 unified audit log, not centralized and not tamper-proof.")}</InlineNotice>
      <div className="mb-3 flex flex-wrap gap-2">
        <Input data-page-search className="w-80" leading={<Search />} placeholder={t("Search target, operation or administrator")} value={search} onChange={(e) => setSearch(e.target.value)} />
        <Select value={outcome} onChange={setOutcome} className="w-56" aria-label={t("Outcome")} options={outcomes.map((o) => ({ value: o, label: o === "all" ? t("All outcomes") : o.replace(/([a-z])([A-Z])/g, "$1 $2") }))} />
      </div>
      {page.isLoading && <Skeleton className="h-96" />}
      {page.error && <Card><ErrorState error={page.error} onRetry={() => void page.refetch()} /></Card>}
      {page.data && (
        <Card className="overflow-hidden">
          {page.data.records.length === 0 ? <EmptyState title={t("No changes recorded")} description={t("Changes you apply through RoomOps appear here.")} /> : (
            <table className="w-full text-[12.5px]">
              <thead><tr className="border-b border-line text-left text-[11.5px] text-ink-3"><th className="px-4 py-2 font-medium">{t("When")}</th><th className="py-2 font-medium">{t("Change")}</th><th className="py-2 font-medium">{t("Target")}</th><th className="py-2 font-medium">{t("Administrator")}</th><th className="px-4 py-2 text-right font-medium">{t("Result")}</th></tr></thead>
              <tbody>
                {page.data.records.map((r) => (
                  <tr key={r.id} className="cursor-default border-b border-line last:border-b-0 hover:bg-hover" onClick={() => navigate(`/history/${r.id}`)}>
                    <td className="whitespace-nowrap px-4 py-2.5 text-ink-2" title={formatDateTime(r.timestamp)}>{formatRelative(r.timestamp)}</td>
                    <td className="py-2.5"><div className="font-medium">{r.operationLabel}</div><div className="max-w-[360px] truncate text-[11.5px] text-ink-3">{r.changes.slice(0, 2).map((c) => `${c.label}: ${c.after}`).join(" · ")}{r.changes.length > 2 ? ` · +${r.changes.length - 2}` : ""}</div></td>
                    <td className="max-w-[260px] py-2.5"><RoomName name={r.targetName} /></td>
                    <td className="py-2.5 text-ink-2">{r.actor}</td>
                    <td className="px-4 py-2.5 text-right"><ExecutionStatusBadge status={outcomeStatus(r)} verification={r.verification} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <div className="border-t border-line px-4 py-2 text-[12px] text-ink-3">{tn(page.data.total, "{n} record", "{n} records")}</div>
        </Card>
      )}
      <Dialog open={!!auditId} onOpenChange={(o) => !o && navigate("/history")}>
        {auditId && <RecordSheet id={auditId} />}
      </Dialog>
    </div>
  );
}

function RecordSheet({ id }: { id: string }) {
  const record = useBridgeQuery("history.get", { id });
  const { review } = usePlanFlow();
  const [busy, setBusy] = useState(false);
  const r = record.data;
  const reverify = async () => {
    setBusy(true);
    try {
      await bridge.call("history.reverify", { id });
      await record.refetch();
      toast.success(t("Re-verified against the current configuration"));
    } catch (error) {
      toast.error(t("Re-verification failed"), { description: error instanceof Error ? error.message : undefined });
    } finally {
      setBusy(false);
    }
  };
  return (
    <SheetContent title={r ? `${r.operationLabel} — ${r.targetName}` : t("Change")} description={r ? `${formatDateTime(r.timestamp)} · ${r.actor}` : undefined} width={680}
      footer={r && (
        <>
          {(r.operationType === "booking.update" || r.operationType.startsWith("places.")) && r.outcome === "Succeeded" && <Button icon={<RefreshCcw />} loading={busy} onClick={() => void reverify()}>{t("Verify again")}</Button>}
          {r.pendingCommands.length > 0 && <Button icon={<Play />} onClick={() => review({ kind: "resume", auditId: r.id })}>{t("Resume pending steps")}</Button>}
          {r.revertAvailable && <Button variant="primary" icon={<RotateCcw />} onClick={() => review({ kind: "revert", auditId: r.id })}>{t("Restore previous values")}</Button>}
        </>
      )}>
      {record.isLoading && <Skeleton className="h-64" />}
      {record.error && <ErrorState error={record.error} compact />}
      {r && (
        <div className="space-y-4">
          <div className="flex flex-wrap items-center gap-2">
            <ExecutionStatusBadge status={outcomeStatus(r)} verification={r.verification} />
            {r.isDemo && <Badge tone="demo">{t("Demo")}</Badge>}
            <Identifier value={r.targetId} />
          </div>
          {r.error && <InlineNotice tone="bad" title={t("Error")}>{r.error}</InlineNotice>}
          {r.changes.length > 0 && <section><div className="t-eyebrow mb-1.5">{t("Requested changes")}</div><ChangeTable changes={r.changes} /></section>}
          {r.verification && <section><div className="t-eyebrow mb-1.5">{t("Verification")}{r.verifiedAt ? ` · ${formatDateTime(r.verifiedAt)}` : ""}</div><VerificationDetails verification={r.verification} /></section>}
          {r.steps.length > 0 && (
            <section>
              <div className="t-eyebrow mb-1.5">{t("Executed commands")}</div>
              <CodeBlock code={r.steps.map((s) => `# ${s.description} — ${s.outcome}${s.attempts > 1 ? ` ${t("after {n} attempts", { n: s.attempts })}` : ""}${s.error ? ` — ${s.error}` : ""}\n${s.script}`).join("\n")} maxHeight={260} />
            </section>
          )}
          {r.pendingCommands.length > 0 && <InlineNotice tone="warn" title={tn(r.pendingCommands.length, "{n} step is waiting for provisioning", "{n} steps are waiting for provisioning")}>{t("They were saved because the object was still being provisioned. Resuming re-validates them against the command allowlist.")}</InlineNotice>}
          {!r.revertAvailable && r.outcome === "Succeeded" && <p className="t-help">{t("No automatic restore is offered for this change. Created objects are never deleted automatically, because they may already hold data or dependencies.")}</p>}
        </div>
      )}
    </SheetContent>
  );
}
