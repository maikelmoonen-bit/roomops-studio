import { useBridgeQuery } from "@/bridge/hooks";
import type { BookingMode } from "@/bridge/contracts.gen";

import { navigate } from "@/lib/router";
import { Card, CardHeader } from "@/components/ui/core";
import { EmptyState, PageHeader, RoomName } from "@/components/common/domain";
import { BookingModeBadge } from "@/features/rooms/rooms-page";
import { RoomMasterDetail } from "@/features/rooms/room-master-detail";
import { PolicyDesigner } from "./policy-designer";
import { t, tn } from "@/lib/i18n";

const order: (BookingMode | "NotRead")[] = ["Automatic", "Restricted", "DelegateApproval", "Hybrid", "Custom", "ManualAutoUpdate", "Reserved", "ProcessingDisabled", "NotRead"];

function Overview() {
  const rooms = useBridgeQuery("rooms.list", { refresh: false });
  const groups = order.map((mode) => ({ mode, rooms: (rooms.data?.rooms ?? []).filter((r) => (r.bookingMode ?? "NotRead") === mode) })).filter((g) => g.rooms.length > 0);
  return (
    <div className="space-y-3">
      <Card className="px-5 py-4">
        <p className="text-[13px] leading-relaxed text-ink-2">
          {t("Choose a room to open the policy designer. Every Set-CalendarProcessing parameter documented for Exchange Online is available, explained in plain language, validated as you edit, and testable with the scenario simulator before you preview the change.")}</p>
      </Card>
      {groups.map((g) => (
        <Card key={g.mode}>
          <CardHeader title={<span className="flex items-center gap-2">{g.mode === "NotRead" ? t("Booking policy not read yet") : <BookingModeBadge mode={g.mode} />}<span className="text-[12px] font-normal text-ink-3">{tn(g.rooms.length, "{n} room", "{n} rooms")}</span></span>} />
          <ul className="grid border-t border-line md:grid-cols-2">
            {g.rooms.map((r) => (
              <li key={r.id}>
                <button type="button" onClick={() => navigate(`/booking/${encodeURIComponent(r.id)}`)} className="flex w-full items-center justify-between gap-3 border-b border-line px-4 py-2.5 text-left hover:bg-hover">
                  <RoomName name={r.displayName} className="text-[12.5px] font-medium" />
                  <span className="shrink-0 text-[11.5px] tabular text-ink-3">{r.bookingWindowInDays != null ? `${r.bookingWindowInDays} d · ${tn(r.delegateCount ?? 0, "{n} delegate", "{n} delegates")}` : t("Open to read")}</span>
                </button>
              </li>
            ))}
          </ul>
        </Card>
      ))}
      {rooms.data && groups.length === 0 && <EmptyState title={t("No rooms")} />}
    </div>
  );
}

export default function BookingPoliciesPage({ roomId }: { roomId?: string }) {
  return (
    <div>
      <PageHeader title={t("Booking policies")} description={t("Design how each room accepts, forwards or declines meeting requests — with a plain-language summary that follows every change.")} />
      <RoomMasterDetail
        basePath="/booking"
        roomId={roomId}
        meta={(r) => (r.bookingModeLabel ? `${r.bookingModeLabel}${r.bookingWindowInDays != null ? ` · ${r.bookingWindowInDays} d` : ""}` : t("Booking policy not read"))}
        empty={<Overview />}
      >
        {(detail, refetch) => detail.booking ? <PolicyDesigner detail={detail} onApplied={refetch} /> : <Card><EmptyState title={t("The booking policy could not be read")} description={detail.loadErrors.join(" ")} /></Card>}
      </RoomMasterDetail>
    </div>
  );
}
