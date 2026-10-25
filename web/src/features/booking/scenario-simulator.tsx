import { useState } from "react";
import { CheckCircle2, CircleHelp, FlaskConical, Info, Play, XCircle } from "lucide-react";
import { bridge } from "@/bridge/client";
import type { BookingPolicy, ListMembership, SimulatedOutcome, SimulationResult, StepCertainty, WorkingHoursConfig } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { Button } from "@/components/ui/button";
import { Badge, Field, Input, NumberField } from "@/components/ui/core";
import { Segmented, Select, Switch } from "@/components/ui/controls";
import { InlineNotice } from "@/components/common/domain";
import { t, tr, tx } from "@/lib/i18n";

const outcomeMeta: Record<SimulatedOutcome, { label: string; tone: "ok" | "warn" | "bad" | "neutral" | "accent" }> = {
  Accepted: { label: tx("Likely accepted"), tone: "ok" },
  AcceptedWithDeclinedOccurrences: { label: tx("Accepted, some occurrences declined"), tone: "warn" },
  ForwardedForApproval: { label: tx("Waits for delegate approval"), tone: "accent" },
  Declined: { label: tx("Likely declined"), tone: "bad" },
  NotProcessed: { label: tx("Not processed automatically"), tone: "warn" },
  Indeterminate: { label: tx("Can't be decided"), tone: "neutral" },
};

const certaintyMeta: Record<StepCertainty, { label: string; className: string }> = {
  Deterministic: { label: tx("From configuration"), className: "text-ink-3" },
  Assumption: { label: tx("Assumption"), className: "text-warn-ink" },
  RequiresLiveData: { label: tx("Needs live data"), className: "text-info" },
};

function nextWeekday(): string {
  const d = new Date();
  d.setDate(d.getDate() + 1);
  while (d.getDay() === 0 || d.getDay() === 6) d.setDate(d.getDate() + 1);
  return d.toISOString().slice(0, 10);
}

const membershipOptions: { value: ListMembership; label: string }[] = [
  { value: "NotListed", label: tx("Not listed") },
  { value: "ListedDirectly", label: tx("Listed directly") },
  { value: "MemberOfListedGroup", label: tx("Member of a listed group") },
];

/** Configuration analysis of how a request would be treated. Explicitly not an Exchange booking transaction. */
export function ScenarioSimulator({ policy, workingHours, capacity, roomId }: { policy: BookingPolicy; workingHours: WorkingHoursConfig | null; capacity: number | null; roomId: string }) {
  const [organizer, setOrganizer] = useState<"Internal" | "External">("Internal");
  const [address, setAddress] = useState("");
  const [bookIn, setBookIn] = useState<ListMembership>("NotListed");
  const [requestIn, setRequestIn] = useState<ListMembership>("NotListed");
  const [requestOut, setRequestOut] = useState<ListMembership>("NotListed");
  const [date, setDate] = useState(nextWeekday);
  const [time, setTime] = useState("10:00");
  const [duration, setDuration] = useState<number | null>(60);
  const [recurring, setRecurring] = useState(false);
  const [occurrences, setOccurrences] = useState<number | null>(10);
  const [conflicts, setConflicts] = useState<number | null>(0);
  const [attendees, setAttendees] = useState<number | null>(null);
  const [result, setResult] = useState<SimulationResult | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = async () => {
    setRunning(true);
    setError(null);
    try {
      setResult(await bridge.call("booking.simulate", {
        roomId,
        policy,
        workingHours,
        capacity,
        scenario: {
          organizer,
          organizerAddress: address.trim() || null,
          inBookInPolicy: bookIn,
          inRequestInPolicy: requestIn,
          inRequestOutOfPolicy: requestOut,
          requestedAt: new Date().toISOString(),
          startLocal: `${date}T${time}:00`,
          durationMinutes: duration ?? 60,
          recurring,
          occurrences: recurring ? occurrences ?? 1 : 1,
          conflictingOccurrences: conflicts ?? 0,
          attendees,
        },
      }));
    } catch (e) {
      setError(e instanceof Error ? e.message : t("Simulation failed"));
    } finally {
      setRunning(false);
    }
  };

  const restricted = !policy.allBookInPolicy;

  return (
    <div className="space-y-3">
      <InlineNotice tone="info" icon={<FlaskConical />}>
        {tr("Configuration analysis of the policy you are editing. It is {not} an Exchange booking transaction, and it can't see real availability.", { not: <span className="font-medium text-ink">{t("not")}</span> })}</InlineNotice>
      <div className="grid grid-cols-2 gap-3">
        <Field label={t("Organizer")} className="col-span-2">
          {() => <Segmented value={organizer} onChange={setOrganizer} options={[{ value: "Internal", label: t("Internal") }, { value: "External", label: t("External") }]} />}
        </Field>
        <Field label={t("Organizer address")} help={t("Optional. Detects direct listing in the policy.")} className="col-span-2">
          {(id) => <Input id={id} value={address} onChange={(e) => setAddress(e.target.value)} placeholder="user@contoso.com" mono />}
        </Field>
        {restricted && (
          <Field label={t("In automatic booking list")} className="col-span-2">
            {(id) => <Select id={id} value={bookIn} onChange={setBookIn} options={membershipOptions.map((o) => ({ ...o, label: t(o.label) }))} />}
          </Field>
        )}
        {!policy.allRequestInPolicy && policy.requestInPolicy.length > 0 && (
          <Field label={t("In request list")} className="col-span-2">{(id) => <Select id={id} value={requestIn} onChange={setRequestIn} options={membershipOptions.map((o) => ({ ...o, label: t(o.label) }))} />}</Field>
        )}
        {!policy.allRequestOutOfPolicy && policy.requestOutOfPolicy.length > 0 && (
          <Field label={t("In out-of-policy list")} className="col-span-2">{(id) => <Select id={id} value={requestOut} onChange={setRequestOut} options={membershipOptions.map((o) => ({ ...o, label: t(o.label) }))} />}</Field>
        )}
        <Field label={t("Date")} help={workingHours ? workingHours.timeZoneId : undefined}>{(id) => <Input id={id} type="date" value={date} onChange={(e) => setDate(e.target.value)} />}</Field>
        <Field label={t("Start time")}>{(id) => <Input id={id} type="time" value={time} onChange={(e) => setTime(e.target.value)} />}</Field>
        <Field label={t("Duration")}>{(id) => <NumberField id={id} value={duration} onChange={setDuration} min={5} max={1440} step={15} unit="min" />}</Field>
        <Field label={t("Attendees")} help={capacity ? t("Room capacity {n}", { n: capacity }) : t("Capacity not set")}>{(id) => <NumberField id={id} value={attendees} onChange={setAttendees} min={1} max={10000} />}</Field>
        <label className="col-span-2 flex items-center justify-between gap-2 text-[12.5px] font-medium">{t("Recurring series")}<Switch checked={recurring} onCheckedChange={setRecurring} /></label>
        {recurring && <Field label={t("Occurrences")}>{(id) => <NumberField id={id} value={occurrences} onChange={setOccurrences} min={2} max={500} />}</Field>}
        <Field label={recurring ? t("Conflicting occurrences") : t("Room already booked")} className={recurring ? "" : "col-span-2"}>
          {(id) => recurring ? <NumberField id={id} value={conflicts} onChange={setConflicts} min={0} max={occurrences ?? 500} /> : <Segmented value={conflicts ? "yes" : "no"} onChange={(v) => setConflicts(v === "yes" ? 1 : 0)} options={[{ value: "no", label: t("Free") }, { value: "yes", label: t("Conflict") }]} />}
        </Field>
      </div>
      <Button variant="primary" icon={<Play />} onClick={() => void run()} loading={running} className="w-full">{t("Analyze request")}</Button>
      {error && <p className="text-[12px] text-bad">{error}</p>}
      {result && (
        <div className="rounded-lg border border-line bg-sunken/40 p-3" aria-live="polite">
          <Badge tone={outcomeMeta[result.outcome].tone} className="mb-1.5">{t(outcomeMeta[result.outcome].label)}</Badge>
          <p className="text-[12.5px] text-ink">{result.headline}</p>
          <ol className="mt-3 space-y-2">
            {result.steps.map((s, i) => (
              <li key={i} className="flex gap-2 text-[12px]">
                {s.passed ? <CheckCircle2 className="mt-[1px] size-3.5 shrink-0 text-ok" /> : <XCircle className="mt-[1px] size-3.5 shrink-0 text-bad" />}
                <span className="min-w-0">
                  <span className="font-medium text-ink">{s.rule}</span>
                  <span className={cn("ml-1.5 text-[10.5px]", certaintyMeta[s.certainty].className)}>{t(certaintyMeta[s.certainty].label)}</span>
                  <span className="t-mono ml-1.5 text-[10.5px] text-ink-3">{s.parameter}</span>
                  <span className="block text-ink-2">{s.explanation}</span>
                </span>
              </li>
            ))}
          </ol>
          {result.assumptions.length > 0 && (
            <div className="mt-3 border-t border-line pt-2">
              <div className="t-eyebrow mb-1 flex items-center gap-1"><CircleHelp className="size-3" />{t("Assumptions")}</div>
              <ul className="space-y-0.5 text-[11.5px] text-ink-2">{result.assumptions.map((a) => <li key={a}>· {a}</li>)}</ul>
            </div>
          )}
          <div className="mt-2 border-t border-line pt-2">
            <div className="t-eyebrow mb-1 flex items-center gap-1"><Info className="size-3" />{t("Limitations")}</div>
            <ul className="space-y-0.5 text-[11.5px] text-ink-3">{result.limitations.map((a) => <li key={a}>· {a}</li>)}</ul>
          </div>
        </div>
      )}
    </div>
  );
}
