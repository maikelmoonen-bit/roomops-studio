import { ArrowUpRight, CalendarClock, ChevronRight, DoorOpen, ListTree, Plus, ScanSearch, ShieldAlert } from "lucide-react";
import { Bar, BarChart, Cell, ResponsiveContainer, Tooltip as ChartTooltip, XAxis, YAxis } from "recharts";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { AuditRecord, CountItem, DashboardSummary, FindingCounts, Metric } from "@/bridge/contracts.gen";
import { cn, formatNumber, formatRelative } from "@/lib/format";
import { Link, navigate } from "@/lib/router";
import { useSession } from "@/state/session";
import { Button } from "@/components/ui/button";
import { Badge, Card, CardHeader, Progress, Skeleton } from "@/components/ui/core";
import { Tooltip } from "@/components/ui/overlay";
import { EmptyState, ErrorState, InlineNotice, PageHeader, RoomName, severityLabel, severityOrder, Unavailable } from "@/components/common/domain";
import { ExecutionStatusBadge } from "@/components/plan/plan-parts";
import { t, tn } from "@/lib/i18n";

function MetricTile({ label, metric, sub, href, icon }: { label: string; metric: Metric; sub?: string; href?: string; icon: React.ReactNode }) {
  const body = (
    <Card className={cn("flex h-full flex-col justify-between gap-3 px-4 py-3.5", href && "transition-colors hover:border-line-strong")}>
      <div className="flex items-center justify-between text-ink-3">
        <span className="text-[12px] font-medium text-ink-2">{label}</span>
        <span className="[&_svg]:size-4">{icon}</span>
      </div>
      <div>
        {metric.value == null ? <Unavailable reason={metric.unavailableReason ?? t("Not available")} /> : <div className="t-metric text-ink">{formatNumber(metric.value)}</div>}
        {sub && <div className="mt-1.5 text-[11.5px] text-ink-3">{sub}</div>}
      </div>
    </Card>
  );
  return href ? <Link to={href} className="block">{body}</Link> : body;
}

function ReadinessRow({ label, detail, metric, total, href }: { label: string; detail: string; metric: Metric; total: number; href: string }) {
  const value = metric.value;
  const share = value == null || total === 0 ? 0 : (value / total) * 100;
  return (
    <Link to={href} className="group grid grid-cols-[1fr_120px_56px_16px] items-center gap-4 px-4 py-2.5 hover:bg-hover">
      <span className="min-w-0">
        <span className="block text-[13px] font-medium text-ink">{label}</span>
        <span className="block truncate text-[11.5px] text-ink-3">{metric.value != null && metric.unavailableReason ? metric.unavailableReason : detail}</span>
      </span>
      {value == null ? <span /> : <Progress value={share} tone={value === 0 ? "ok" : "accent"} />}
      <span className="text-right">
        {value == null ? <Unavailable reason={metric.unavailableReason ?? t("Not available")} /> : <span className={cn("t-metric text-[20px]", value === 0 ? "text-ink-3" : "text-ink")}>{value}</span>}
      </span>
      <ChevronRight className="size-4 text-ink-3 opacity-0 transition-opacity group-hover:opacity-100" />
    </Link>
  );
}

function BarList({ items, total, emptyText, hrefFor }: { items: CountItem[]; total: number; emptyText: string; hrefFor?: (item: CountItem) => string }) {
  if (items.length === 0) return <p className="px-4 py-6 text-center text-[12.5px] text-ink-3">{emptyText}</p>;
  const max = Math.max(...items.map((i) => i.count), 1);
  return (
    <ul className="space-y-1 px-2 pb-3">
      {items.map((item) => {
        const row = (
          <div className="group relative flex h-8 items-center gap-3 rounded-sm px-2 hover:bg-hover">
            <div className="absolute inset-y-1.5 left-2 rounded-r-[4px] bg-[color-mix(in_srgb,var(--accent)_22%,transparent)] transition-[width] duration-500 ease-snap" style={{ width: `calc(${(item.count / max) * 100}% - 16px)` }} />
            <span className="relative min-w-0 flex-1 truncate text-[12.5px] text-ink">{item.label}</span>
            <span className="relative tabular text-[12.5px] font-medium text-ink">{item.count}</span>
            <span className="relative w-10 text-right tabular text-[11.5px] text-ink-3">{total ? Math.round((item.count / total) * 100) : 0}%</span>
          </div>
        );
        return <li key={item.key}>{hrefFor ? <Link to={hrefFor(item)}>{row}</Link> : row}</li>;
      })}
    </ul>
  );
}

function CapacityChart({ items }: { items: CountItem[] }) {
  const data = items.map((i) => ({ ...i, fill: i.key === "unknown" ? "var(--line-strong)" : "var(--accent)" }));
  return (
    <div className="h-[188px] px-2 pb-2" role="img" aria-label={t("Capacity distribution: {items}", { items: items.map((i) => t("{seats} seats: {n}", { seats: i.label, n: i.count })).join(", ") })}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} margin={{ top: 16, right: 8, bottom: 0, left: -24 }} barCategoryGap={6}>
          <XAxis dataKey="label" tickLine={false} axisLine={{ stroke: "var(--line)" }} tick={{ fill: "var(--ink-3)", fontSize: 11 }} />
          <YAxis allowDecimals={false} tickLine={false} axisLine={false} tick={{ fill: "var(--ink-3)", fontSize: 11 }} width={40} />
          <ChartTooltip
            cursor={{ fill: "var(--hover)" }}
            content={({ active, payload }) =>
              active && payload?.[0] ? (
                <div className="rounded-sm border border-line bg-raised px-2.5 py-1.5 text-[12px] shadow-pop">
                  <div className="font-medium text-ink">{(payload[0].payload as CountItem).key === "unknown" ? t("Capacity not set") : t("{seats} seats", { seats: (payload[0].payload as CountItem).label })}</div>
                  <div className="text-ink-2">{tn(Number(payload[0].value), "{n} room", "{n} rooms")}</div>
                </div>
              ) : null
            }
          />
          <Bar dataKey="count" radius={[4, 4, 0, 0]} isAnimationActive={false} label={{ position: "top", fill: "var(--ink-2)", fontSize: 11 }}>
            {data.map((d) => <Cell key={d.key} fill={d.fill} />)}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

function SeverityBar({ counts }: { counts: FindingCounts }) {
  const parts = severityOrder.map((s) => ({ key: s, value: counts[(s.charAt(0).toLowerCase() + s.slice(1)) as keyof FindingCounts] as number }));
  const total = parts.reduce((a, p) => a + p.value, 0);
  const color: Record<string, string> = { Critical: "var(--bad)", Warning: "var(--warn)", Recommendation: "var(--info)", Informational: "var(--ink-3)", UnableToVerify: "transparent" };
  return (
    <div className="px-4 pb-4">
      <div className="flex h-2.5 gap-[2px] overflow-hidden rounded-full">
        {total === 0 ? <div className="h-full w-full bg-[color-mix(in_srgb,var(--ok)_45%,transparent)]" /> : parts.filter((p) => p.value > 0).map((p) => (
          <Tooltip key={p.key} content={`${severityLabel(p.key)}: ${p.value}`}>
            <div className="h-full" style={{ flexGrow: p.value, background: color[p.key], border: p.key === "UnableToVerify" ? "1px dashed var(--line-strong)" : undefined }} />
          </Tooltip>
        ))}
      </div>
      <ul className="mt-3 grid grid-cols-2 gap-x-4 gap-y-1.5 text-[12px]">
        {parts.map((p) => (
          <li key={p.key} className="flex items-center gap-2">
            <span className="size-2 rounded-[2px]" style={{ background: color[p.key], border: p.key === "UnableToVerify" ? "1px dashed var(--ink-3)" : undefined }} />
            <span className="text-ink-2">{severityLabel(p.key)}</span>
            <span className="ml-auto tabular font-medium text-ink">{p.value}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

function ActivityList({ records }: { records: AuditRecord[] }) {
  if (records.length === 0) {
    return <EmptyState className="py-8" title={t("No changes yet")} description={t("Changes you apply through RoomOps Studio are recorded here with their verification result.")} />;
  }
  return (
    <ul className="divide-y divide-line">
      {records.map((r) => (
        <li key={r.id}>
          <Link to={`/history/${r.id}`} className="flex items-center gap-3 px-4 py-2.5 hover:bg-hover">
            <CalendarClock className="size-4 shrink-0 text-ink-3" />
            <span className="min-w-0 flex-1">
              <span className="block truncate text-[12.5px] text-ink"><span className="font-medium">{r.operationLabel}</span> · <RoomName name={r.targetName} className="inline-flex align-middle" /></span>
              <span className="block truncate text-[11.5px] text-ink-3">{r.actor} · {formatRelative(r.timestamp)}</span>
            </span>
            <ExecutionStatusBadge
              status={r.outcome === "Succeeded" ? (r.verification?.outcome === "PendingPropagation" ? "PendingPropagation" : "Succeeded") : r.outcome === "PendingProvisioning" ? "PendingProvisioning" : r.outcome === "NoChange" ? "NoChange" : r.outcome === "Stale" ? "Stale" : r.outcome === "Skipped" ? "Skipped" : "Failed"}
              verification={r.verification}
            />
          </Link>
        </li>
      ))}
    </ul>
  );
}

function ScanStrip({ data }: { data: DashboardSummary }) {
  const { scan } = useSession();
  const live = scan ?? data.scan;
  const total = data.totalRooms.value ?? 0;
  if (live?.running) {
    return (
      <InlineNotice tone="info" className="mb-4" icon={<ScanSearch />} title={t("Reading booking configuration — {completed} of {total} rooms", { completed: live.completed, total: live.total })}>
        <Progress value={(live.completed / Math.max(live.total, 1)) * 100} className="mt-2" />
        {live.currentRoom && <span className="mt-1 block text-[11.5px] text-ink-3">{t("Current:")}{" "}{live.currentRoom}</span>}
      </InlineNotice>
    );
  }
  if (data.scannedRooms >= total) return null;
  return (
    <InlineNotice
      tone="warn"
      className="mb-4"
      title={t("Booking configuration has been read for {n} of {total} rooms", { n: data.scannedRooms, total })}
      action={<Button size="sm" variant="primary" icon={<ScanSearch />} onClick={() => void bridge.call("scan.start", {}).then(() => toast.info(t("Configuration scan started")))}>{t("Run configuration scan")}</Button>}
    >
      {t("Booking-dependent metrics and rules are calculated only from rooms whose calendar processing has been read. The scan reads each room in sequence and changes nothing.")}</InlineNotice>
  );
}

export default function DashboardPage() {
  const { session } = useSession();
  const summary = useBridgeQuery("dashboard.get", {});
  const data = summary.data;

  return (
    <div>
      <PageHeader
        eyebrow={t("Workspace control center")}
        title={session?.tenantName ?? t("Dashboard")}
        description={data ? `${tn(data.totalRooms.value ?? 0, "{n} room mailbox", "{n} room mailboxes")}, ${tn(data.totalRoomLists.value ?? 0, "{n} room list", "{n} room lists")}${data.buildings.value != null ? `, ${tn(data.buildings.value, "{n} building", "{n} buildings")}` : ""} · ${t("{n} seats in total", { n: formatNumber(data.totalCapacity.value) })}` : t("Loading the room inventory…")}
        actions={
          <>
            <Button icon={<ScanSearch />} onClick={() => void bridge.call("scan.start", {}).then(() => toast.info(t("Configuration scan started"), { description: t("Booking policies and working hours are read for every room.") }))}>{t("Run scan")}</Button>
            <Button variant="primary" icon={<Plus />} onClick={() => navigate("/rooms/new")}>{t("Create room")}</Button>
          </>
        }
      />

      {summary.isLoading && (
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">{Array.from({ length: 5 }, (_, i) => <Skeleton key={i} className="h-[104px]" />)}</div>
          <div className="grid gap-3 lg:grid-cols-[1.4fr_1fr]"><Skeleton className="h-72" /><Skeleton className="h-72" /></div>
        </div>
      )}
      {summary.error && <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />}

      {data && (
        <>
          <ScanStrip data={data} />
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
            <MetricTile label={t("Room mailboxes")} icon={<DoorOpen />} metric={data.totalRooms} sub={t("{n} seats configured", { n: formatNumber(data.totalCapacity.value) })} href="/rooms" />
            <MetricTile label={t("Room lists")} icon={<ListTree />} metric={data.totalRoomLists} sub={t("{n} rooms in no list", { n: data.withoutRoomList.value ?? 0 })} href="/room-lists" />
            <MetricTile label={t("Buildings")} icon={<DoorOpen />} metric={data.buildings} sub={data.floors.value != null ? tn(data.floors.value, "{n} floor", "{n} floors") : t("Microsoft Places not connected")} href="/buildings" />
            <MetricTile label={t("Critical findings")} icon={<ShieldAlert />} metric={{ value: data.findings.critical, unavailableReason: null }} sub={t("{warnings} warnings · {unverified} unable to verify", { warnings: data.findings.warning, unverified: data.findings.unableToVerify })} href="/validation" />
            <MetricTile label={t("Changes · 7 days")} icon={<CalendarClock />} metric={{ value: data.changesLast7Days, unavailableReason: null }} sub={t("Recorded by RoomOps Studio")} href="/history" />
          </div>

          <div className="mt-3 grid gap-3 lg:grid-cols-[1.35fr_1fr]">
            <Card>
              <CardHeader title={t("Configuration readiness")} description={t("Organizational best-practice checks across all rooms. Select a row to see the rooms.")} />
              <div className="divide-y divide-line border-t border-line">
                <ReadinessRow label={t("Capacity missing")} detail={t("Rooms without ResourceCapacity can't be filtered by size.")} metric={data.missingCapacity} total={data.totalRooms.value ?? 0} href="/rooms?filter=missingCapacity" />
                <ReadinessRow label={t("Not in a room list")} detail={t("Not discoverable through Outlook Room Finder browsing.")} metric={data.withoutRoomList} total={data.totalRooms.value ?? 0} href="/rooms?filter=noRoomList" />
                <ReadinessRow label={t("No Places location")} detail={t("No parent floor or section in Microsoft Places.")} metric={data.missingPlacesLocation} total={data.totalRooms.value ?? 0} href="/rooms?filter=noLocation" />
                <ReadinessRow label={t("Delegate approval")} detail={t("Requests are routed to booking delegates.")} metric={data.manualApproval} total={data.totalRooms.value ?? 0} href="/rooms?filter=delegateApproval" />
                <ReadinessRow label={t("Unusual booking configuration")} detail={t("Processing disabled, AutoUpdate, Reserved or custom combinations.")} metric={data.unusualBooking} total={data.totalRooms.value ?? 0} href="/rooms?filter=unusualBooking" />
              </div>
            </Card>
            <Card className="flex flex-col">
              <CardHeader title={t("Validation findings")} description={t("Technical findings and organizational baseline checks.")} actions={<Button size="sm" variant="ghost" onClick={() => navigate("/validation")}>{t("Open")}<ArrowUpRight /></Button>} />
              <SeverityBar counts={data.findings} />
              <div className="border-t border-line px-4 py-2.5">
                <div className="t-eyebrow mb-1.5">{t("Most frequent")}</div>
                {data.topRules.length === 0 ? <p className="text-[12.5px] text-ink-3">{t("No findings.")}</p> : (
                  <ul className="space-y-1">
                    {data.topRules.map((r) => (
                      <li key={r.key} className="flex items-center gap-2 text-[12.5px]">
                        <span className="t-mono w-[76px] shrink-0 text-[11px] text-ink-3">{r.key}</span>
                        <span className="min-w-0 flex-1 truncate">{r.label}</span>
                        <span className="tabular font-medium">{r.count}</span>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            </Card>
          </div>

          <div className="mt-3 grid gap-3 lg:grid-cols-3">
            <Card>
              <CardHeader title={t("Booking modes")} description={data.scannedRooms ? tn(data.scannedRooms, "Derived from {n} scanned room.", "Derived from {n} scanned rooms.") : t("Requires a configuration scan.")} />
              {data.bookingModes.length === 0 ? <EmptyState className="py-8" title={t("No booking data yet")} description={t("Run a configuration scan to classify each room's booking policy.")} /> : <BarList items={data.bookingModes} total={data.scannedRooms} emptyText="" hrefFor={(i) => `/rooms?mode=${i.key}`} />}
            </Card>
            <Card>
              <CardHeader title={t("Capacity distribution")} description={t("Seats per room, from Exchange ResourceCapacity.")} />
              <CapacityChart items={data.capacityBuckets} />
            </Card>
            <Card>
              <CardHeader title={t("Rooms by building")} description={t("From the Microsoft Places hierarchy.")} />
              {data.roomsByBuilding.length === 0 ? <EmptyState className="py-8" title={t("Places not connected")} description={t("Connect Microsoft Places to see how rooms are distributed across buildings.")} /> : <BarList items={data.roomsByBuilding} total={data.totalRooms.value ?? 0} emptyText="" />}
            </Card>
          </div>

          <Card className="mt-3">
            <CardHeader title={t("Recent activity")} description={t("Changes applied through RoomOps Studio in this tenant. Not the Microsoft 365 unified audit log.")} actions={<Button size="sm" variant="ghost" onClick={() => navigate("/history")}>{t("All changes")}<ArrowUpRight /></Button>} />
            <div className="border-t border-line"><ActivityList records={data.recentActivity} /></div>
          </Card>
          <p className="mt-3 text-right text-[11px] text-ink-3"><Badge tone="outline">{t("Updated")}{" "}{formatRelative(data.generatedAt)}</Badge></p>
        </>
      )}
    </div>
  );
}
