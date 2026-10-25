import { useMemo, useState } from "react";
import { ChevronDown, ScanSearch, Search, ShieldCheck, Wand2 } from "lucide-react";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { Finding, FindingSeverity, RuleCategory } from "@/bridge/contracts.gen";
import { cn, formatRelative } from "@/lib/format";
import { navigate } from "@/lib/router";
import { usePlanFlow } from "@/state/plan-flow";
import { useSession } from "@/state/session";
import { Button } from "@/components/ui/button";
import { Badge, Card, Input, Progress, Skeleton } from "@/components/ui/core";
import { Checkbox, Segmented, Tabs, TabsContent, TabsList } from "@/components/ui/controls";
import { EmptyState, ErrorState, InlineNotice, PageHeader, SeverityBadge, severityLabel, severityOrder } from "@/components/common/domain";
import { FindingRow } from "./finding-row";
import { t, tn, tr } from "@/lib/i18n";

export default function ValidationPage() {
  const report = useBridgeQuery("validation.run", { refresh: false });
  const { scan } = useSession();
  const { review } = usePlanFlow();
  const [severity, setSeverity] = useState<FindingSeverity | "all">("all");
  const [category, setCategory] = useState<RuleCategory | "all">("all");
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [openRules, setOpenRules] = useState<Set<string>>(new Set());

  const findings = report.data?.findings ?? [];
  const filtered = useMemo(() => findings.filter((f) =>
    (severity === "all" || f.severity === severity) &&
    (category === "all" || f.category === category) &&
    (!search || `${f.title} ${f.targetName} ${f.explanation} ${f.ruleId}`.toLowerCase().includes(search.toLowerCase()))), [findings, severity, category, search]);
  const groups = useMemo(() => {
    const map = new Map<string, Finding[]>();
    filtered.forEach((f) => map.set(f.ruleId, [...(map.get(f.ruleId) ?? []), f]));
    return [...map.entries()].sort((a, b) => severityOrder.indexOf(a[1][0].severity) - severityOrder.indexOf(b[1][0].severity));
  }, [filtered]);
  const remediable = filtered.filter((f) => f.remediationId && f.severity !== "UnableToVerify");
  const selectedRemediable = remediable.filter((f) => selected.has(f.id));

  return (
    <div>
      <PageHeader
        title={t("Validation center")}
        description={t("Technical findings follow documented Exchange and Places behavior. Organizational baseline checks follow your own conventions and can be tuned in Settings.")}
        actions={
          <>
            <Button icon={<ScanSearch />} onClick={() => void bridge.call("scan.start", {}).then(() => toast.info(t("Configuration scan started")))} disabled={scan?.running}>{t("Run configuration scan")}</Button>
            <Button variant="primary" icon={<Wand2 />} disabled={selectedRemediable.length === 0} onClick={() => review({ kind: "remediate", findingIds: selectedRemediable.map((f) => f.id) }, { onCompleted: () => setSelected(new Set()) })}>
              {selectedRemediable.length > 0 ? t("Remediate {n} selected", { n: selectedRemediable.length }) : t("Remediate selected")}</Button>
          </>
        }
      />
      {scan?.running && (
        <InlineNotice tone="info" className="mb-4" icon={<ScanSearch />} title={t("Scanning {completed} of {total} rooms", { completed: scan.completed, total: scan.total })}>
          <Progress value={(scan.completed / Math.max(1, scan.total)) * 100} className="mt-1.5" />
        </InlineNotice>
      )}
      {report.isLoading && <Skeleton className="h-96" />}
      {report.error && <Card><ErrorState error={report.error} onRetry={() => void report.refetch()} /></Card>}
      {report.data && (
        <Tabs defaultValue="findings">
          <TabsList className="mb-4" tabs={[{ value: "findings", label: t("Findings"), count: findings.filter((f) => f.severity !== "UnableToVerify").length }, { value: "rules", label: t("Rules") }]} />
          <TabsContent value="findings">
            <div className="mb-3 grid grid-cols-2 gap-2 md:grid-cols-5">
              {severityOrder.map((s) => {
                const count = findings.filter((f) => f.severity === s).length;
                return (
                  <button key={s} type="button" onClick={() => setSeverity(severity === s ? "all" : s)} className={cn("rounded-lg border bg-surface px-3 py-2.5 text-left transition-colors", severity === s ? "border-accent" : "border-line hover:border-line-strong")}>
                    <SeverityBadge severity={s} />
                    <div className="t-metric mt-2 text-[24px]">{count}</div>
                  </button>
                );
              })}
            </div>
            <p className="mb-3 text-[12px] text-ink-3">{t("Evaluated {time} · booking configuration loaded for {n} of {total} rooms", { time: formatRelative(report.data.generatedAt), n: report.data.roomsWithBookingData, total: report.data.roomsTotal })}{report.data.placesAvailable ? "" : ` · ${t("Microsoft Places not connected, Places rules skipped")}`}</p>
            <div className="mb-3 flex flex-wrap items-center gap-2">
              <Input data-page-search className="w-72" leading={<Search />} placeholder={t("Search findings, rooms or rule ids")} value={search} onChange={(e) => setSearch(e.target.value)} />
              <Segmented value={category} onChange={setCategory} options={[{ value: "all", label: t("All") }, { value: "Technical", label: t("Technical") }, { value: "OrganizationalBaseline", label: t("Org baseline") }]} />
              {remediable.length > 0 && (
                <label className="ml-auto flex items-center gap-2 text-[12.5px] text-ink-2">
                  <Checkbox checked={selectedRemediable.length === remediable.length ? true : selectedRemediable.length ? "indeterminate" : false} onCheckedChange={(v) => setSelected(v ? new Set(remediable.map((f) => f.id)) : new Set())} />
                  {t("Select all {n} with automatic remediation", { n: remediable.length })}</label>
              )}
            </div>
            {groups.length === 0 ? (
              <Card><EmptyState icon={<ShieldCheck />} title={t("No findings match")} description={findings.length === 0 ? t("Every evaluated rule passed.") : t("Adjust the filters to see more findings.")} /></Card>
            ) : (
              <div className="space-y-2">
                {groups.map(([ruleId, items]) => {
                  const open = openRules.has(ruleId) || groups.length <= 3;
                  return (
                    <Card key={ruleId} className="overflow-hidden">
                      <button type="button" onClick={() => setOpenRules((s) => { const n = new Set(s); if (n.has(ruleId)) n.delete(ruleId); else n.add(ruleId); return n; })} className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-hover" aria-expanded={open}>
                        <SeverityBadge severity={items[0].severity} />
                        <span className="text-[13px] font-semibold">{items[0].title}</span>
                        <span className="t-mono text-[10.5px] text-ink-3">{ruleId}</span>
                        <Badge tone="outline">{items[0].category === "Technical" ? t("Technical") : t("Org baseline")}</Badge>
                        <span className="ml-auto text-[12px] text-ink-3">{items[0].targetKind === "RoomList" ? tn(items.length, "{n} room list", "{n} room lists") : tn(items.length, "{n} room", "{n} rooms")}</span>
                        <ChevronDown className={cn("size-4 text-ink-3 transition-transform", open && "rotate-180")} />
                      </button>
                      {open && (
                        <div className="divide-y divide-line border-t border-line">
                          {items.map((f) => (
                            <div key={f.id} className="flex items-start">
                              <div className="pl-4 pt-3.5">
                                <Checkbox checked={selected.has(f.id)} disabled={!f.remediationId || f.severity === "UnableToVerify"} onCheckedChange={(v) => setSelected((s) => { const n = new Set(s); if (v) n.add(f.id); else n.delete(f.id); return n; })} aria-label={t("Select finding for {name}", { name: f.targetName })} />
                              </div>
                              <div className="min-w-0 flex-1">
                                <FindingRow finding={f} showTarget onNavigate={() => navigate(f.targetKind === "Room" ? `/rooms/${encodeURIComponent(f.targetId)}` : "/room-lists")} />
                              </div>
                            </div>
                          ))}
                        </div>
                      )}
                    </Card>
                  );
                })}
              </div>
            )}
          </TabsContent>
          <TabsContent value="rules">
            <Card>
              <table className="w-full text-[12.5px]">
                <thead><tr className="border-b border-line text-left text-[11.5px] text-ink-3"><th className="px-4 py-2 font-medium">{t("Rule")}</th><th className="py-2 font-medium">{t("Category")}</th><th className="py-2 font-medium">{t("Severity")}</th><th className="px-4 py-2 font-medium">{t("Basis")}</th></tr></thead>
                <tbody>
                  {report.data.rules.map((r) => {
                    const setting = report.data!.baseline.rules[r.id];
                    const disabled = r.category === "OrganizationalBaseline" && setting?.enabled === false;
                    return (
                      <tr key={r.id} className={cn("border-b border-line align-top last:border-b-0", disabled && "opacity-50")}>
                        <td className="px-4 py-2.5"><div className="font-medium">{r.title}</div><div className="t-mono text-[10.5px] text-ink-3">{r.id}</div><div className="t-help mt-0.5">{r.description}</div></td>
                        <td className="py-2.5"><Badge tone="outline">{r.category === "Technical" ? t("Technical") : t("Org baseline")}</Badge></td>
                        <td className="py-2.5">{disabled ? <Badge tone="neutral">{t("Disabled")}</Badge> : <SeverityBadge severity={setting?.severity ?? r.defaultSeverity} />}</td>
                        <td className="px-4 py-2.5 text-ink-2">{r.basis}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </Card>
            <p className="mt-2 text-[12px] text-ink-3">{tr("Severities of technical rules reflect documented impact and can't be changed. Baseline rules are configured in {settings}.", { settings: <button type="button" className="text-accent-text hover:underline" onClick={() => navigate("/settings")}>{t("Settings")}</button> })} {t("{label} means the data needed to evaluate a rule hasn't been read.", { label: severityLabel("UnableToVerify") })}</p>
          </TabsContent>
        </Tabs>
      )}
    </div>
  );
}
