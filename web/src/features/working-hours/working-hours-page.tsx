import { useBridgeQuery } from "@/bridge/hooks";

import { navigate } from "@/lib/router";
import { Badge, Card, CardHeader } from "@/components/ui/core";
import { EmptyState, PageHeader, RoomName } from "@/components/common/domain";
import { RoomMasterDetail } from "@/features/rooms/room-master-detail";
import { WorkingHoursEditor } from "./working-hours-editor";
import { t, tn } from "@/lib/i18n";

const NOT_READ = "~not-read";

function Overview() {
  const rooms = useBridgeQuery("rooms.list", { refresh: false });
  const settings = useBridgeQuery("settings.get", {});
  const expected = settings.data?.validation.expectedTimeZones ?? [];
  const byZone = new Map<string, string[]>();
  (rooms.data?.rooms ?? []).forEach((r) => {
    const zone = r.timeZone ?? NOT_READ;
    byZone.set(zone, [...(byZone.get(zone) ?? []), r.id]);
  });
  const zones = [...byZone.entries()].sort((a, b) => b[1].length - a[1].length);
  return (
    <Card>
      <CardHeader title={t("Time zones in use")} description={expected.length ? t("Expected by your baseline: {zones}.", { zones: expected.join(", ") }) : t("Set the expected time zones in Settings to flag rooms that differ.")} />
      <ul className="divide-y divide-line border-t border-line">
        {zones.map(([zone, ids]) => (
          <li key={zone} className="px-4 py-3">
            <div className="mb-1.5 flex items-center gap-2">
              <span className="text-[13px] font-medium">{zone === NOT_READ ? t("Not read yet") : zone}</span>
              <Badge tone={zone === NOT_READ ? "neutral" : expected.length && !expected.includes(zone) ? "warn" : "outline"}>{tn(ids.length, "{n} room", "{n} rooms")}</Badge>
              {zone === "Pacific Standard Time" && <span className="text-[11.5px] text-ink-3">{t("Default for new room mailboxes — confirm it matches the location.")}</span>}
            </div>
            <div className="flex flex-wrap gap-1.5">
              {ids.map((id) => {
                const room = rooms.data!.rooms.find((r) => r.id === id)!;
                return <button key={id} type="button" onClick={() => navigate(`/working-hours/${encodeURIComponent(id)}`)} className="rounded-sm border border-line px-2 py-1 text-[12px] hover:border-line-strong"><RoomName name={room.displayName} /></button>;
              })}
            </div>
          </li>
        ))}
        {rooms.data && zones.length === 0 && <EmptyState title={t("No rooms")} />}
      </ul>
    </Card>
  );
}

export default function WorkingHoursPage({ roomId }: { roomId?: string }) {
  return (
    <div>
      <PageHeader title={t("Working hours")} description={t("Work days, hours and time zone of each room mailbox (Set-MailboxCalendarConfiguration). They limit bookings only when the policy schedules only during working hours.")} />
      <RoomMasterDetail basePath="/working-hours" roomId={roomId} meta={(r) => r.timeZone ?? t("Not read")} empty={<Overview />}>
        {(detail, refetch) => detail.workingHours
          ? <WorkingHoursEditor roomId={detail.mailbox.id} roomName={detail.mailbox.displayName} current={detail.workingHours} scheduleOnlyDuringWorkHours={detail.booking?.scheduleOnlyDuringWorkHours ?? null} onApplied={refetch} />
          : <Card><EmptyState title={t("Working hours could not be read")} description={detail.loadErrors.join(" ")} /></Card>}
      </RoomMasterDetail>
    </div>
  );
}
