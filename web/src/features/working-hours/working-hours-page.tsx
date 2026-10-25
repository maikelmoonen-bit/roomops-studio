import { useBridgeQuery } from "@/bridge/hooks";
import type { RoomSummary } from "@/bridge/contracts.gen";
import { navigate } from "@/lib/router";
import { Badge, Card, CardHeader } from "@/components/ui/core";
import { EmptyState, PageHeader, RoomName } from "@/components/common/domain";
import { RoomMasterDetail } from "@/features/rooms/room-master-detail";
import { WorkingHoursEditor } from "./working-hours-editor";
import { describeDays } from "./work-days";
import { t, tn } from "@/lib/i18n";

const NOT_READ = "~not-read";

function Overview({ rooms }: { rooms: RoomSummary[] }) {
  const settings = useBridgeQuery("settings.get", {});
  const expected = settings.data?.validation.expectedTimeZones ?? [];
  const byZone = new Map<string, RoomSummary[]>();
  rooms.forEach((r) => {
    const zone = r.timeZone ?? NOT_READ;
    byZone.set(zone, [...(byZone.get(zone) ?? []), r]);
  });
  // Largest time zone first; rooms whose working hours haven't been read come last.
  const zones = [...byZone.entries()].sort((a, b) => Number(a[0] === NOT_READ) - Number(b[0] === NOT_READ) || b[1].length - a[1].length);
  return (
    <div className="space-y-3">
      <Card className="px-5 py-4">
        <p className="text-[13px] leading-relaxed text-ink-2">
          {t("Choose a room to change its work days, hours and time zone.")}{" "}
          {expected.length ? t("Expected by your baseline: {zones}.", { zones: expected.join(", ") }) : t("Set the expected time zones in Settings to flag rooms that differ.")}</p>
      </Card>
      {zones.map(([zone, list]) => {
        const notRead = zone === NOT_READ;
        const unexpected = !notRead && expected.length > 0 && !expected.includes(zone);
        return (
          <Card key={zone}>
            <CardHeader
              title={
                <span className="flex items-center gap-2">
                  {notRead ? t("Working hours not read yet") : zone}
                  <span className="text-[12px] font-normal text-ink-3">{tn(list.length, "{n} room", "{n} rooms")}</span>
                  {unexpected && <Badge tone="warn">{t("Not in your baseline")}</Badge>}
                </span>
              }
              description={zone === "Pacific Standard Time" ? t("Default for new room mailboxes — confirm it matches the location.") : undefined}
            />
            <ul className="grid border-t border-line md:grid-cols-2">
              {list.map((r) => (
                <li key={r.id}>
                  <button type="button" onClick={() => navigate(`/working-hours/${encodeURIComponent(r.id)}`)} className="flex w-full items-center justify-between gap-3 border-b border-line px-4 py-2.5 text-left hover:bg-hover">
                    <RoomName name={r.displayName} className="text-[12.5px] font-medium" />
                    <span className="shrink-0 text-[11.5px] tabular text-ink-3">
                      {r.workingHours ? `${r.workingHours.start.slice(0, 5)}–${r.workingHours.end.slice(0, 5)} · ${describeDays(r.workingHours.days)}` : t("Open to read")}</span>
                  </button>
                </li>
              ))}
            </ul>
          </Card>
        );
      })}
    </div>
  );
}

export default function WorkingHoursPage({ roomId }: { roomId?: string }) {
  return (
    <div>
      <PageHeader title={t("Working hours")} description={t("Work days, hours and time zone of each room mailbox (Set-MailboxCalendarConfiguration). They limit bookings only when the policy schedules only during working hours.")} />
      <RoomMasterDetail basePath="/working-hours" roomId={roomId} overview={(rooms) => <Overview rooms={rooms} />}>
        {(detail, refetch) => detail.workingHours
          ? <WorkingHoursEditor roomId={detail.mailbox.id} roomName={detail.mailbox.displayName} current={detail.workingHours} scheduleOnlyDuringWorkHours={detail.booking?.scheduleOnlyDuringWorkHours ?? null} onApplied={refetch} />
          : <Card><EmptyState title={t("Working hours could not be read")} description={detail.loadErrors.join(" ")} /></Card>}
      </RoomMasterDetail>
    </div>
  );
}
