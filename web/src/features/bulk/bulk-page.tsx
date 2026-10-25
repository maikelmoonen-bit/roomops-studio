import { useState } from "react";
import { AlertOctagon, ArrowRight, CheckCircle2, Download, FileSpreadsheet, FileUp, RotateCcw, Upload, Wand2 } from "lucide-react";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { BulkParseResult } from "@/bridge/contracts.gen";
import { cn, formatRelative } from "@/lib/format";
import { openText, saveText, toCsv } from "@/lib/files";
import { navigate } from "@/lib/router";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Card, CardHeader, Progress } from "@/components/ui/core";
import { Combobox } from "@/components/ui/controls";
import { EmptyState, InlineNotice, PageHeader } from "@/components/common/domain";
import { t, tn, tr } from "@/lib/i18n";

const SAMPLE = [
  "Room;BookingWindowInDays;MaximumDurationInMinutes;AllowRecurringMeetings;Capacity;Places.Tags;RoomList",
  "ehv-1.02@contoso-demo.example;60;120;yes;2;Focus;",
  "ehv-1.07@contoso-demo.example;60;90;yes;;Huddle|Display;",
  "ams-4.05@contoso-demo.example;30;90;no;4;;Amsterdam Zuidas",
].join("\n");

export default function BulkPage() {
  const { reviewPreview, reopen } = usePlanFlow();
  const executions = useBridgeQuery("executions.list", {}, { refetchInterval: 4000 });
  const [file, setFile] = useState<{ name: string; content: string } | null>(null);
  const [parsed, setParsed] = useState<BulkParseResult | null>(null);
  const [mapping, setMapping] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);

  const parse = async (content: string, name: string, map: Record<string, string> | null) => {
    setBusy(true);
    try {
      const result = await bridge.call("bulk.parse", { format: name.toLowerCase().endsWith(".json") ? "json" : "csv", content, mapping: map });
      setParsed(result);
      setMapping(result.mapping);
    } catch (error) {
      toast.error(t("The file could not be read"), { description: error instanceof Error ? error.message : undefined });
    } finally {
      setBusy(false);
    }
  };

  const choose = async () => {
    const f = await openText(["csv", "json", "txt"]);
    if (!f) return;
    setFile(f);
    await parse(f.content, f.name, null);
  };

  const preview = async () => {
    if (!parsed?.intent) return;
    setBusy(true);
    try {
      // The parse result carries the concrete intent type; the plan endpoint needs the polymorphic discriminator.
      reviewPreview(await bridge.call("plans.create", { ...parsed.intent, kind: "bulkImport" }));
    } catch (error) {
      toast.error(t("The preview could not be created"), { description: error instanceof Error ? error.message : undefined });
    } finally {
      setBusy(false);
    }
  };

  const exportFailures = async (executionId: string) => {
    const state = await bridge.call("executions.get", { executionId });
    const rows = state.targets.filter((tg) => ["Failed", "Stale", "Skipped", "Canceled", "VerificationMismatch"].includes(tg.status)).map((tg) => [tg.targetName, tg.operationLabel, tg.status, tg.message ?? "", tg.retryable ? "yes" : "no"]);
    if (await saveText(`roomops-failures-${executionId.slice(0, 8)}.csv`, "text/csv", toCsv([t("Target"), t("Operation"), t("Status"), t("Message"), t("Retryable")], rows))) toast.success(tn(rows.length, "Exported {n} item", "Exported {n} items"));
  };

  const errorRows = parsed?.rows.filter((r) => r.issues.some((i) => i.level === "Error")).length ?? 0;
  const fieldOptions = (parsed?.fields ?? []).map((f) => ({ value: f.key, label: f.label, group: f.group, hint: f.key.startsWith("Places.") ? f.key.slice(7) : f.key }));

  return (
    <div>
      <PageHeader
        title={t("Bulk operations")}
        description={t("Import a CSV or JSON file to change many rooms at once. Every row is validated and planned against the room's current configuration; nothing changes until you preview and apply.")}
        actions={<Button icon={<Download />} onClick={() => void saveText("roomops-bulk-sample.csv", "text/csv", SAMPLE)}>{t("Download sample file")}</Button>}
      />
      <div className="mb-4 grid gap-3 md:grid-cols-3">
        {[["1", t("Choose a file"), t("CSV (comma or semicolon) or JSON")], ["2", t("Map and validate"), t("Columns, types, duplicates and room resolution")], ["3", t("Preview and apply"), t("Per-object changes, throttled execution, verification")]].map(([n, title, text]) => (
          <div key={n} className="flex items-center gap-3 rounded-lg border border-line bg-surface px-4 py-3">
            <span className="flex size-7 shrink-0 items-center justify-center rounded-full border border-line-strong font-display text-[14px] font-semibold">{n}</span>
            <span><span className="block text-[13px] font-semibold">{title}</span><span className="block text-[11.5px] text-ink-3">{text}</span></span>
          </div>
        ))}
      </div>

      {!parsed ? (
        <Card>
          <EmptyState icon={<FileSpreadsheet />} title={t("Import a configuration file")} description={t("One row per room. Identify rooms by address, alias or id; leave a cell empty to keep the current value.")} action={<Button variant="primary" icon={<FileUp />} loading={busy} onClick={() => void choose()}>{t("Choose file")}</Button>} />
        </Card>
      ) : (
        <div className="space-y-3">
          <Card>
            <CardHeader title={file?.name ?? t("Imported file")} description={`${tn(parsed.rows.length, "{n} row", "{n} rows")} · ${t("{n} ready", { n: parsed.validRows })} · ${t("{n} with errors", { n: errorRows })}`} actions={<><Button size="sm" variant="ghost" icon={<RotateCcw />} onClick={() => { setParsed(null); setFile(null); }}>{t("Start over")}</Button><Button size="sm" icon={<Upload />} onClick={() => void choose()}>{t("Choose another file")}</Button></>} />
            {parsed.errors.length > 0 && <div className="space-y-2 border-t border-line p-4">{parsed.errors.map((e) => <InlineNotice key={e} tone="bad">{e}</InlineNotice>)}</div>}
            <div className="border-t border-line p-4">
              <div className="t-eyebrow mb-2">{t("Column mapping")}</div>
              <div className="grid gap-2 md:grid-cols-2 xl:grid-cols-3">
                {parsed.columns.map((c) => (
                  <div key={c.name} className="flex items-center gap-2 rounded-md border border-line px-2.5 py-2">
                    <div className="min-w-0 flex-1">
                      <div className="t-mono truncate text-[12px] text-ink">{c.name}</div>
                      <div className="truncate text-[11px] text-ink-3">{c.samples.filter(Boolean).slice(0, 2).join(" · ") || t("(empty)")}</div>
                    </div>
                    <ArrowRight className="size-3.5 shrink-0 text-ink-3" />
                    <div className="w-44 shrink-0">
                      <Combobox value={mapping[c.name] ?? null} onChange={(v) => setMapping((m) => ({ ...m, [c.name]: v }))} options={[{ value: "", label: t("Ignore column") }, ...fieldOptions]} placeholder={t("Ignore")} />
                    </div>
                  </div>
                ))}
              </div>
              <div className="mt-3 flex justify-end">
                <Button size="sm" loading={busy} onClick={() => void parse(file!.content, file!.name, Object.fromEntries(Object.entries(mapping).filter(([, v]) => v)))}>{t("Apply mapping and revalidate")}</Button>
              </div>
            </div>
          </Card>

          <Card className="overflow-hidden">
            <CardHeader title={t("Rows")} description={t("Rows with errors are excluded from the plan. Fix them in the file and import it again.")} />
            <div className="scroll-area max-h-[420px] border-t border-line">
              <table className="w-full text-[12.5px]">
                <thead className="sticky top-0 bg-surface"><tr className="border-b border-line text-left text-[11.5px] text-ink-3"><th className="px-4 py-2 font-medium">{t("Row")}</th><th className="py-2 font-medium">{t("Room")}</th><th className="py-2 font-medium">{t("Values")}</th><th className="px-4 py-2 font-medium">{t("Validation")}</th></tr></thead>
                <tbody>
                  {parsed.rows.map((r) => {
                    const errors = r.issues.filter((i) => i.level === "Error");
                    return (
                      <tr key={r.rowNumber} className={cn("border-b border-line align-top last:border-b-0", errors.length && "bg-[color-mix(in_srgb,var(--bad)_5%,transparent)]")}>
                        <td className="px-4 py-2 tabular text-ink-3">{r.rowNumber}</td>
                        <td className="py-2"><div className="font-medium">{r.resolvedRoomName ?? r.roomIdentity}</div>{r.resolvedRoomName && <div className="t-mono text-[10.5px] text-ink-3">{r.roomIdentity}</div>}</td>
                        <td className="py-2"><div className="flex flex-wrap gap-1">{Object.entries(r.values).filter(([k, v]) => k !== "Room" && v).map(([k, v]) => <Badge key={k} tone="outline" className="gap-1"><span className="text-ink-3">{k.replace("Places.", "")}</span>{v}</Badge>)}</div></td>
                        <td className="px-4 py-2">
                          {r.issues.length === 0 ? <span className="inline-flex items-center gap-1 text-ok"><CheckCircle2 className="size-3.5" />{t("Ready")}</span> : (
                            <ul className="space-y-0.5">{r.issues.map((i, n) => <li key={n} className={cn("flex gap-1", i.level === "Error" ? "text-bad" : "text-warn-ink")}><AlertOctagon className="mt-0.5 size-3 shrink-0" />{i.message}</li>)}</ul>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <div className="flex items-center justify-between border-t border-line px-4 py-3">
              <span className="text-[12px] text-ink-3">{t("The preview shows each room's current value next to the new one. Values that already match are reported as “No change required”.")}</span>
              <Button variant="primary" icon={<Wand2 />} disabled={!parsed.intent} loading={busy} onClick={() => void preview()}>{tn(parsed.validRows, "Preview {n} row", "Preview {n} rows")}</Button>
            </div>
          </Card>
        </div>
      )}

      <Card className="mt-4">
        <CardHeader title={t("Executions in this session")} description={t("Bulk and single changes, with failure reports and retry of unresolved items after revalidation.")} />
        <ul className="divide-y divide-line border-t border-line">
          {(executions.data ?? []).length === 0 && <li className="px-4 py-4 text-[12.5px] text-ink-3">{t("No executions yet.")}</li>}
          {(executions.data ?? []).map((e) => (
            <li key={e.executionId} className="grid grid-cols-[1fr_220px_auto] items-center gap-4 px-4 py-3">
              <div className="min-w-0">
                <div className="truncate text-[13px] font-medium">{e.title}</div>
                <div className="text-[11.5px] text-ink-3">{formatRelative(e.startedAt)} · {[t("{n} applied", { n: e.counts.succeeded }), t("{n} unchanged", { n: e.counts.noChange }), t("{n} failed", { n: e.counts.failed }), t("{n} skipped", { n: e.counts.skipped }), t("{n} pending", { n: e.counts.pending }), ...(e.counts.retryable ? [t("{n} retryable", { n: e.counts.retryable })] : [])].join(" · ")}</div>
              </div>
              <Progress value={(e.counts.completed / Math.max(1, e.counts.total)) * 100} tone={e.counts.failed ? "warn" : e.running ? "accent" : "ok"} />
              <div className="flex gap-1.5">
                <Button size="sm" variant="ghost" onClick={() => reopen(e.executionId)}>{t("Details")}</Button>
                {!e.running && e.counts.failed + e.counts.skipped > 0 && <Button size="sm" variant="ghost" icon={<Download />} onClick={() => void exportFailures(e.executionId)}>{t("Failures")}</Button>}
              </div>
            </li>
          ))}
        </ul>
      </Card>
      <p className="mt-3 text-[12px] text-ink-3">{tr("For quick changes to a selection of rooms, select them on the {rooms} page.", { rooms: <button type="button" className="text-accent-text hover:underline" onClick={() => navigate("/rooms")}>{t("Rooms")}</button> })}</p>
    </div>
  );
}
