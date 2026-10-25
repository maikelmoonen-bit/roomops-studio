import { useMemo } from "react";
import type { ColumnDef } from "@tanstack/react-table";
import { CircleHelp } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { RoomSummary } from "@/bridge/contracts.gen";
import { Badge, Card } from "@/components/ui/core";
import { Tooltip } from "@/components/ui/overlay";
import { EmptyState, PageHeader } from "@/components/common/domain";
import { FlagCell, RoomOverviewTable, roomColumn } from "@/features/rooms/room-grid";
import { RoomMasterDetail } from "@/features/rooms/room-master-detail";
import { WorkingHoursEditor } from "./working-hours-editor";
import { describeDays } from "./work-days";
import { t } from "@/lib/i18n";

function TimeZoneCell({ zone, expected }: { zone: string | null; expected: string[] }) {
  if (!zone) return <span className="text-ink-3">—</span>;
  return (
    <span className="flex min-w-0 items-center gap-1.5">
      <span className="truncate">{zone}</span>
      {expected.length > 0 && !expected.includes(zone) && <Badge tone="warn">{t("Not in your baseline")}</Badge>}
      {zone === "Pacific Standard Time" && (
        <Tooltip content={t("Default for new room mailboxes — confirm it matches the location.")}><span className="shrink-0 text-ink-3"><CircleHelp className="size-3.5" /></span></Tooltip>
      )}
    </span>
  );
}

function Overview({ rooms, total }: { rooms: RoomSummary[]; total: number }) {
  const settings = useBridgeQuery("settings.get", {});
  const expected = useMemo(() => settings.data?.validation.expectedTimeZones ?? [], [settings.data]);
  const columns = useMemo<ColumnDef<RoomSummary>[]>(() => [
    roomColumn,
    { id: "timeZone", size: 280, accessorFn: (r) => r.timeZone ?? "~", header: () => t("Time zone"), cell: ({ row }) => <TimeZoneCell zone={row.original.timeZone} expected={expected} /> },
    { id: "workDays", size: 200, accessorFn: (r) => (r.workingHours ? describeDays(r.workingHours.days) : "~"), header: () => t("Work days"), cell: ({ row }) => row.original.workingHours ? describeDays(row.original.workingHours.days) : <span className="text-ink-3">—</span> },
    { id: "hours", size: 130, accessorFn: (r) => r.workingHours?.start ?? "~", header: () => t("Hours"), cell: ({ row }) => row.original.workingHours ? <span className="tabular">{row.original.workingHours.start.slice(0, 5)}–{row.original.workingHours.end.slice(0, 5)}</span> : <span className="text-ink-3">—</span> },
    { id: "workHoursOnly", size: 200, accessorFn: (r) => Number(r.scheduleOnlyDuringWorkHours ?? -1), header: () => t("Working hours only"), cell: ({ row }) => <FlagCell value={row.original.scheduleOnlyDuringWorkHours} /> },
    {
      id: "location",
      size: 220,
      accessorFn: (r) => r.locationPath ?? "",
      header: () => t("Location"),
      cell: ({ row }) => row.original.building ? (
        <div className="min-w-0 leading-tight">
          <div className="truncate">{row.original.building}</div>
          <div className="truncate text-[11.5px] text-ink-3">{[row.original.floor, row.original.section].filter(Boolean).join(" · ") || t("No floor")}</div>
        </div>
      ) : <span className="text-ink-3">—</span>,
    },
  ], [expected]);
  return <RoomOverviewTable rooms={rooms} total={total} columns={columns} basePath="/working-hours" />;
}

export default function WorkingHoursPage({ roomId }: { roomId?: string }) {
  return (
    <div>
      <PageHeader title={t("Working hours")} description={t("Work days, hours and time zone of each room mailbox (Set-MailboxCalendarConfiguration). They limit bookings only when the policy schedules only during working hours.")} />
      <RoomMasterDetail basePath="/working-hours" roomId={roomId} overview={(rooms, total) => <Overview rooms={rooms} total={total} />}>
        {(detail, refetch) => detail.workingHours
          ? <WorkingHoursEditor roomId={detail.mailbox.id} roomName={detail.mailbox.displayName} current={detail.workingHours} scheduleOnlyDuringWorkHours={detail.booking?.scheduleOnlyDuringWorkHours ?? null} onApplied={refetch} />
          : <Card><EmptyState title={t("Working hours could not be read")} description={detail.loadErrors.join(" ")} /></Card>}
      </RoomMasterDetail>
    </div>
  );
}
