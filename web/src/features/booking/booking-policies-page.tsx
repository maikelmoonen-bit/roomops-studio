import { useMemo } from "react";
import type { ColumnDef } from "@tanstack/react-table";
import type { RoomSummary } from "@/bridge/contracts.gen";
import { Card } from "@/components/ui/core";
import { EmptyState, PageHeader } from "@/components/common/domain";
import { BookingModeBadge, HealthCell } from "@/features/rooms/rooms-page";
import { FlagCell, RoomOverviewTable, roomColumn } from "@/features/rooms/room-grid";
import { RoomMasterDetail } from "@/features/rooms/room-master-detail";
import { PolicyDesigner } from "./policy-designer";
import { t } from "@/lib/i18n";

function durationText(minutes: number | null) {
  if (minutes == null) return "—";
  if (minutes === 0) return "∞";
  return minutes % 60 === 0 ? `${minutes / 60} h` : `${minutes} m`;
}

function Overview({ rooms, total }: { rooms: RoomSummary[]; total: number }) {
  const columns = useMemo<ColumnDef<RoomSummary>[]>(() => [
    roomColumn,
    { id: "bookingMode", size: 220, accessorFn: (r) => r.bookingModeLabel ?? "~", header: () => t("Booking mode"), cell: ({ row }) => <BookingModeBadge mode={row.original.bookingMode} label={row.original.bookingModeLabel} /> },
    { id: "window", size: 130, accessorFn: (r) => r.bookingWindowInDays ?? -1, header: () => t("Booking window"), cell: ({ row }) => <span className="tabular text-ink-2">{row.original.bookingWindowInDays == null ? "—" : `${row.original.bookingWindowInDays} d`}</span> },
    { id: "maxDuration", size: 120, accessorFn: (r) => r.maximumDurationInMinutes ?? -1, header: () => t("Max. duration"), cell: ({ row }) => <span className="tabular text-ink-2">{durationText(row.original.maximumDurationInMinutes)}</span> },
    { id: "recurring", size: 110, accessorFn: (r) => Number(r.allowRecurringMeetings ?? -1), header: () => t("Recurring"), cell: ({ row }) => <FlagCell value={row.original.allowRecurringMeetings} /> },
    { id: "workHoursOnly", size: 200, accessorFn: (r) => Number(r.scheduleOnlyDuringWorkHours ?? -1), header: () => t("Working hours only"), cell: ({ row }) => <FlagCell value={row.original.scheduleOnlyDuringWorkHours} /> },
    { id: "delegates", size: 100, accessorFn: (r) => r.delegateCount ?? -1, header: () => t("Delegates"), cell: ({ row }) => row.original.delegateCount == null ? <span className="text-ink-3">—</span> : <span className="tabular">{row.original.delegateCount}</span> },
    { id: "health", size: 150, accessorFn: (r) => r.findings.critical * 1000 + r.findings.warning * 10 + r.findings.recommendation, header: () => t("Validation"), cell: ({ row }) => <HealthCell room={row.original} /> },
  ], []);
  return <RoomOverviewTable rooms={rooms} total={total} columns={columns} basePath="/booking" />;
}

export default function BookingPoliciesPage({ roomId }: { roomId?: string }) {
  return (
    <div>
      <PageHeader title={t("Booking policies")} description={t("Design how each room accepts, forwards or declines meeting requests — with a plain-language summary that follows every change.")} />
      <RoomMasterDetail basePath="/booking" roomId={roomId} overview={(rooms, total) => <Overview rooms={rooms} total={total} />}>
        {(detail, refetch) => detail.booking ? <PolicyDesigner detail={detail} onApplied={refetch} /> : <Card><EmptyState title={t("The booking policy could not be read")} description={detail.loadErrors.join(" ")} /></Card>}
      </RoomMasterDetail>
    </div>
  );
}
