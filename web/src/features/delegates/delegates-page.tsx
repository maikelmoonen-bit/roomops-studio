import { useMemo } from "react";
import type { ColumnDef } from "@tanstack/react-table";
import type { RoomSummary } from "@/bridge/contracts.gen";
import { Badge } from "@/components/ui/core";
import { PageHeader } from "@/components/common/domain";
import { BookingModeBadge, HealthCell } from "@/features/rooms/rooms-page";
import { FlagCell, RoomOverviewTable, roomColumn } from "@/features/rooms/room-grid";
import { RoomMasterDetail } from "@/features/rooms/room-master-detail";
import { DelegateManager } from "./delegate-manager";
import { t } from "@/lib/i18n";

function DelegatesCell({ room }: { room: RoomSummary }) {
  if (room.delegateCount == null) return <span className="text-ink-3">—</span>;
  if (room.delegateNames.length === 0) return <span className="text-ink-3">{t("None")}</span>;
  return (
    <div className="flex min-w-0 gap-1 overflow-hidden">
      {room.delegateNames.slice(0, 2).map((name) => <Badge key={name} tone="outline" className="max-w-[160px] truncate">{name}</Badge>)}
      {room.delegateNames.length > 2 && <Badge tone="neutral">+{room.delegateNames.length - 2}</Badge>}
    </div>
  );
}

function Overview({ rooms, total }: { rooms: RoomSummary[]; total: number }) {
  const columns = useMemo<ColumnDef<RoomSummary>[]>(() => [
    roomColumn,
    { id: "bookingMode", size: 220, accessorFn: (r) => r.bookingModeLabel ?? "~", header: () => t("Booking mode"), cell: ({ row }) => <BookingModeBadge mode={row.original.bookingMode} label={row.original.bookingModeLabel} /> },
    { id: "delegates", size: 360, accessorFn: (r) => r.delegateCount ?? -1, header: () => t("Booking delegates"), cell: ({ row }) => <DelegatesCell room={row.original} /> },
    { id: "forward", size: 170, accessorFn: (r) => Number(r.forwardRequestsToDelegates ?? -1), header: () => t("Forward requests"), cell: ({ row }) => <FlagCell value={row.original.forwardRequestsToDelegates} /> },
    { id: "health", size: 150, accessorFn: (r) => r.findings.critical * 1000 + r.findings.warning * 10 + r.findings.recommendation, header: () => t("Validation"), cell: ({ row }) => <HealthCell room={row.original} /> },
  ], []);
  return <RoomOverviewTable rooms={rooms} total={total} columns={columns} basePath="/delegates" />;
}

export default function DelegatesPage({ roomId }: { roomId?: string }) {
  return (
    <div>
      <PageHeader title={t("Resource delegates")} description={t("Booking delegates approve requests. Calendar folder permissions, Full Access, Send As and Send on Behalf are separate mechanisms and are never granted as a side effect.")} />
      <RoomMasterDetail basePath="/delegates" roomId={roomId} overview={(rooms, total) => <Overview rooms={rooms} total={total} />}>
        {(detail) => <DelegateManager roomId={detail.mailbox.id} />}
      </RoomMasterDetail>
    </div>
  );
}
