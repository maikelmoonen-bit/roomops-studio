import { useEffect, useMemo, useRef, useState } from "react";
import { AlertTriangle, Globe2, Info, RotateCcw, Wand2 } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { WorkingHoursConfig } from "@/bridge/contracts.gen";
import { cn, formatDateTime, formatOffset } from "@/lib/format";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Card, Field, Input } from "@/components/ui/core";
import { Combobox } from "@/components/ui/controls";
import { InlineNotice } from "@/components/common/domain";
import { DAYS, describeDays, formatDays, parseDays, shortDay } from "./work-days";
import { locale, t, tr } from "@/lib/i18n";

const toMinutes = (value: string) => {
  const [h, m] = value.split(":").map(Number);
  return h * 60 + (m || 0);
};
const toTime = (minutes: number) => `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}:00`;

/**
 * Visual weekly editor for Set-MailboxCalendarConfiguration. Exchange stores one start/end time shared by every work day,
 * so dragging the range on any day moves it for all selected days — the editor never suggests per-day ranges.
 */
export function WorkingHoursEditor({ roomId, roomName, current, scheduleOnlyDuringWorkHours, onApplied }: { roomId: string; roomName: string; current: WorkingHoursConfig; scheduleOnlyDuringWorkHours: boolean | null; onApplied?: () => void }) {
  const zones = useBridgeQuery("timeZones.list", {}, { staleTime: Infinity });
  const { review } = usePlanFlow();
  const [days, setDays] = useState(() => parseDays(current.days));
  const [start, setStart] = useState(() => toMinutes(current.start));
  const [end, setEnd] = useState(() => toMinutes(current.end));
  const [zone, setZone] = useState(current.timeZoneId);
  const track = useRef<HTMLDivElement>(null);
  const drag = useRef<"start" | "end" | "move" | null>(null);
  const dragOrigin = useRef({ x: 0, start: 0, end: 0 });

  useEffect(() => {
    setDays(parseDays(current.days));
    setStart(toMinutes(current.start));
    setEnd(toMinutes(current.end));
    setZone(current.timeZoneId);
  }, [current]);

  const zoneInfo = zones.data?.find((z) => z.id === zone);
  const changed = formatDays(days) !== formatDays(parseDays(current.days)) || start !== toMinutes(current.start) || end !== toMinutes(current.end) || zone !== current.timeZoneId;
  const issues = useMemo(() => {
    const list: { level: "error" | "warning" | "info"; text: string }[] = [];
    if (end <= start) list.push({ level: "error", text: t("The end time must be later than the start time. Exchange applies one time range to every work day; overnight ranges can't be represented.") });
    if (days.size === 0) list.push({ level: scheduleOnlyDuringWorkHours ? "error" : "warning", text: scheduleOnlyDuringWorkHours ? t("This room only accepts bookings during working hours. With no work days, every request would be declined.") : t("No work days are selected.") });
    if (end - start > 0 && end - start < 240) list.push({ level: "warning", text: t("Working hours span only {n} hours.", { n: ((end - start) / 60).toLocaleString(locale(), { maximumFractionDigits: 1 }) }) });
    if (zone === "Pacific Standard Time") list.push({ level: "info", text: t("Pacific Standard Time is the default for new room mailboxes. Confirm it matches the room's location.") });
    return list;
  }, [start, end, days, zone, scheduleOnlyDuringWorkHours]);

  const minutesAt = (clientX: number) => {
    const rect = track.current!.getBoundingClientRect();
    return Math.round((Math.min(Math.max(clientX - rect.left, 0), rect.width) / rect.width) * 1440 / 15) * 15;
  };

  useEffect(() => {
    const move = (e: PointerEvent) => {
      if (!drag.current || !track.current) return;
      const at = minutesAt(e.clientX);
      if (drag.current === "start") setStart(Math.min(at, end - 15));
      else if (drag.current === "end") setEnd(Math.max(at, start + 15));
      else {
        const rect = track.current.getBoundingClientRect();
        const delta = Math.round(((e.clientX - dragOrigin.current.x) / rect.width) * 1440 / 15) * 15;
        const length = dragOrigin.current.end - dragOrigin.current.start;
        const s = Math.min(Math.max(dragOrigin.current.start + delta, 0), 1440 - length);
        setStart(s);
        setEnd(s + length);
      }
    };
    const up = () => (drag.current = null);
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
    return () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
  });

  const apply = () =>
    review({ kind: "updateWorkingHours", roomIds: [roomId], workingHours: { days: formatDays(days), start: toTime(start), end: toTime(end), timeZoneId: zone } }, { onCompleted: onApplied });

  const pct = (m: number) => `${(m / 1440) * 100}%`;

  return (
    <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_320px]">
      <Card className="p-4">
        <div className="mb-3 flex items-center justify-between">
          <div>
            <h3 className="t-section">{t("Weekly working hours")} · {roomName}</h3>
            <p className="t-help mt-0.5">{t("Select the work days, then drag the range. Exchange stores one range for all work days.")}</p>
          </div>
          <span className="t-metric text-[22px] tabular">{toTime(start).slice(0, 5)}–{toTime(end).slice(0, 5)}</span>
        </div>
        <div className="ml-[104px] flex justify-between pb-1 text-[10.5px] tabular text-ink-3">
          {[0, 3, 6, 9, 12, 15, 18, 21, 24].map((h) => <span key={h} className="w-0 text-center">{String(h).padStart(2, "0")}</span>)}
        </div>
        <div className="space-y-1.5">
          {DAYS.map((day, index) => {
            const on = days.has(day);
            return (
              <div key={day} className="flex items-center gap-3">
                <button
                  type="button"
                  aria-pressed={on}
                  onClick={() => setDays((d) => { const next = new Set(d); if (next.has(day)) next.delete(day); else next.add(day); return next; })}
                  className={cn("flex h-9 w-[92px] shrink-0 items-center justify-between rounded-sm border px-2.5 text-[12.5px] font-medium transition-colors", on ? "border-accent bg-[color-mix(in_srgb,var(--accent)_10%,transparent)] text-ink" : "border-line text-ink-3 hover:text-ink")}
                >
                  {shortDay(day)}
                  <span className="text-[10.5px] font-normal">{on ? t("Workday") : t("Closed")}</span>
                </button>
                <div ref={index === 0 ? track : undefined} className="blueprint relative h-9 flex-1 rounded-sm border border-line bg-sunken/40" style={{ backgroundSize: `${100 / 24}% 100%` }}>
                  {on && (
                    <div
                      className="absolute inset-y-1 cursor-grab rounded-[5px] bg-[color-mix(in_srgb,var(--accent)_32%,transparent)] ring-1 ring-accent active:cursor-grabbing"
                      style={{ left: pct(start), width: `calc(${pct(end)} - ${pct(start)})` }}
                      onPointerDown={(e) => { drag.current = "move"; dragOrigin.current = { x: e.clientX, start, end }; }}
                      role="presentation"
                    >
                      <span className="absolute inset-y-0 -left-1 w-2.5 cursor-ew-resize rounded-l-[5px] bg-accent" onPointerDown={(e) => { e.stopPropagation(); drag.current = "start"; }} />
                      <span className="absolute inset-y-0 -right-1 w-2.5 cursor-ew-resize rounded-r-[5px] bg-accent" onPointerDown={(e) => { e.stopPropagation(); drag.current = "end"; }} />
                    </div>
                  )}
                </div>
              </div>
            );
          })}
        </div>
        <div className="mt-4 grid gap-3 md:grid-cols-[140px_140px_1fr]">
          <Field label={t("Start")} property="WorkingHoursStartTime">{(id) => <Input id={id} type="time" step={900} value={toTime(start).slice(0, 5)} onChange={(e) => e.target.value && setStart(toMinutes(e.target.value))} />}</Field>
          <Field label={t("End")} property="WorkingHoursEndTime">{(id) => <Input id={id} type="time" step={900} value={toTime(end === 1440 ? 1439 : end).slice(0, 5)} onChange={(e) => e.target.value && setEnd(toMinutes(e.target.value))} />}</Field>
          <Field label={t("Time zone")} property="WorkingHoursTimeZone">
            {(id) => (
              <Combobox
                id={id}
                value={zone}
                onChange={setZone}
                options={(zones.data ?? []).map((z) => ({ value: z.id, label: z.id, hint: formatOffset(z.currentOffsetMinutes), keywords: [z.displayName] }))}
                searchPlaceholder={t("Search time zones")}
              />
            )}
          </Field>
        </div>
        <div className="mt-4 space-y-2">
          {issues.map((i) => <InlineNotice key={i.text} tone={i.level === "error" ? "bad" : i.level === "warning" ? "warn" : "info"}>{i.text}</InlineNotice>)}
        </div>
        <div className="mt-4 flex justify-end gap-2 border-t border-line pt-3">
          <Button variant="ghost" icon={<RotateCcw />} disabled={!changed} onClick={() => { setDays(parseDays(current.days)); setStart(toMinutes(current.start)); setEnd(toMinutes(current.end)); setZone(current.timeZoneId); }}>{t("Reset")}</Button>
          <Button variant="primary" icon={<Wand2 />} disabled={!changed || issues.some((i) => i.level === "error")} onClick={apply}>{t("Preview changes")}</Button>
        </div>
      </Card>

      <div className="space-y-3">
        <Card className="p-4">
          <div className="t-eyebrow mb-2">{t("Current effective setting")}</div>
          <dl className="grid grid-cols-[96px_1fr] gap-y-1.5 text-[12.5px]">
            <dt className="text-ink-3">{t("Work days")}</dt><dd>{describeDays(current.days)}</dd>
            <dt className="text-ink-3">{t("Hours")}</dt><dd className="tabular">{current.start.slice(0, 5)}–{current.end.slice(0, 5)}</dd>
            <dt className="text-ink-3">{t("Time zone")}</dt><dd>{current.timeZoneId}</dd>
          </dl>
        </Card>
        {zoneInfo && (
          <Card className="p-4">
            <div className="t-eyebrow mb-2 flex items-center gap-1.5"><Globe2 className="size-3" />{zoneInfo.id}</div>
            <p className="text-[12.5px] text-ink-2">{zoneInfo.displayName}</p>
            <dl className="mt-2 grid grid-cols-[110px_1fr] gap-y-1 text-[12px]">
              <dt className="text-ink-3">{t("Standard offset")}</dt><dd className="tabular">{formatOffset(zoneInfo.baseOffsetMinutes)}</dd>
              <dt className="text-ink-3">{t("Current offset")}</dt><dd className="tabular">{formatOffset(zoneInfo.currentOffsetMinutes)}</dd>
              <dt className="text-ink-3">{t("Daylight saving")}</dt><dd>{zoneInfo.supportsDaylightSaving ? t("Observed") : t("Not observed")}</dd>
              {zoneInfo.nextTransitionUtc && <><dt className="text-ink-3">{t("Next change")}</dt><dd>{formatDateTime(zoneInfo.nextTransitionUtc)}</dd></>}
            </dl>
            {zoneInfo.supportsDaylightSaving && <p className="t-help mt-2">{t("Working hours follow local time, so the UTC window shifts by an hour when daylight saving starts or ends.")}</p>}
          </Card>
        )}
        <Card className="p-4">
          <div className="t-eyebrow mb-2">{t("Booking relationship")}</div>
          {scheduleOnlyDuringWorkHours == null ? (
            <p className="flex gap-2 text-[12.5px] text-ink-3"><Info className="mt-0.5 size-3.5 shrink-0" />{t("The booking policy hasn't been read.")}</p>
          ) : scheduleOnlyDuringWorkHours ? (
            <p className="flex gap-2 text-[12.5px] text-ink-2"><AlertTriangle className="mt-0.5 size-3.5 shrink-0 text-warn-ink" /><span>{tr("{setting} is enabled: requests outside these hours are declined.", { setting: <Badge tone="warn" className="mr-1">ScheduleOnlyDuringWorkHours</Badge> })}</span></p>
          ) : (
            <p className="flex gap-2 text-[12.5px] text-ink-2"><Info className="mt-0.5 size-3.5 shrink-0 text-ink-3" />{t("Booking isn't limited to working hours. These hours are shown in scheduling tools but don't restrict bookings.")}</p>
          )}
        </Card>
      </div>
    </div>
  );
}
