import { useEffect, useMemo, useState, type ReactNode } from "react";
import { keepPreviousData } from "@tanstack/react-query";
import { AlertOctagon, AlertTriangle, CalendarCheck2, CircleSlash, EyeOff, Globe2, Info, Lock, MessageSquareText, RotateCcw, Scale, ShieldCheck, Timer, UserCheck, Wand2 } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { BookingPolicy, BookingPreset, PolicyFieldInfo, PolicyStatement, RecipientRef, RoomDetail, StatementKind } from "@/bridge/contracts.gen";
import { cn, formatMinutes } from "@/lib/format";
import { navigate } from "@/lib/router";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Card, NumberField, Textarea } from "@/components/ui/core";
import { Disclosure, Segmented, Switch, Tabs, TabsContent, TabsList } from "@/components/ui/controls";
import { InlineNotice } from "@/components/common/domain";
import { RecipientPicker } from "@/components/common/recipient-picker";
import { changedFields, formatValue, getField, sentenceTokens, setField } from "./policy-utils";
import { ScenarioSimulator } from "./scenario-simulator";
import { describeDays } from "@/features/working-hours/work-days";
import { accessFields, BookingAccessPanel, missingInputs } from "./booking-access";
import { t, tn, tx } from "@/lib/i18n";

function focusField(name: string) {
  const el = document.getElementById(`field-${name}`) ?? document.getElementById(`access-${name}`);
  if (!el) return;
  el.scrollIntoView({ behavior: "smooth", block: "center" });
  el.classList.add("ring-2", "ring-accent", "ring-offset-2", "ring-offset-[var(--surface)]");
  window.setTimeout(() => el.classList.remove("ring-2", "ring-accent", "ring-offset-2", "ring-offset-[var(--surface)]"), 1400);
}

/** The signature element: the generated policy sentence with its values as chips that jump to their controls. */
function PolicySentence({ text, onJump }: { text: string; onJump: (field: string) => void }) {
  const parts: ReactNode[] = [];
  const matches: { start: number; end: number; field: string }[] = [];
  for (const token of sentenceTokens) {
    const global = new RegExp(token.pattern.source, "g");
    for (const m of text.matchAll(global)) {
      const start = m.index ?? 0;
      if (!matches.some((x) => start < x.end && start + m[0].length > x.start)) matches.push({ start, end: start + m[0].length, field: token.field });
    }
  }
  matches.sort((a, b) => a.start - b.start);
  let last = 0;
  for (const m of matches) {
    if (m.start > last) parts.push(text.slice(last, m.start));
    parts.push(
      <button key={m.start} type="button" onClick={() => onJump(m.field)} className="mx-0.5 rounded-[5px] border-b-2 border-accent bg-[color-mix(in_srgb,var(--accent)_12%,transparent)] px-1 text-ink transition-colors hover:bg-[color-mix(in_srgb,var(--accent)_22%,transparent)]" title={t("Jump to this setting")}>
        {text.slice(m.start, m.end)}
      </button>,
    );
    last = m.end;
  }
  parts.push(text.slice(last));
  return <p className="font-display text-[21px] font-medium leading-[1.45] tracking-[0.005em] text-ink">{parts}</p>;
}

const statementIcon: Record<StatementKind, ReactNode> = {
  Access: <UserCheck />, Approval: <ShieldCheck />, Constraint: <Timer />, Conflict: <Scale />, Privacy: <EyeOff />, External: <Globe2 />, Response: <MessageSquareText />, Caution: <AlertTriangle />,
};

function Section({ id, title, description, icon, children }: { id: string; title: string; description: string; icon: ReactNode; children: ReactNode }) {
  return (
    <Card id={id} className="scroll-mt-4">
      <div className="flex items-start gap-3 px-4 pb-2 pt-3.5">
        <span className="mt-0.5 text-ink-3 [&_svg]:size-4">{icon}</span>
        <div>
          <h3 className="t-section">{title}</h3>
          <p className="t-help mt-0.5">{description}</p>
        </div>
      </div>
      <div className="px-1.5 pb-2">{children}</div>
    </Card>
  );
}

function Row({ field, label, help, children, note, dim }: { field: string; label: string; help?: string; children: ReactNode; note?: ReactNode; dim?: boolean }) {
  return (
    <div id={`field-${field}`} className={cn("grid grid-cols-[1fr_auto] items-start gap-4 rounded-md px-2.5 py-2.5 transition-[box-shadow,opacity]", dim && "opacity-55")}>
      <div className="min-w-0">
        <div className="flex items-center gap-2 text-[13px] font-medium text-ink">{label}{note}</div>
        {help && <p className="t-help mt-0.5 max-w-xl">{help}</p>}
        <p className="t-mono mt-1 text-[10.5px] text-ink-3">{field}</p>
      </div>
      <div className="flex min-w-[140px] justify-end pt-0.5">{children}</div>
    </div>
  );
}

function QuickValues({ values, current, onPick, format }: { values: number[]; current: number; onPick: (v: number) => void; format: (v: number) => string }) {
  return (
    <div className="mt-1.5 flex flex-wrap justify-end gap-1">
      {values.map((v) => (
        <button key={v} type="button" onClick={() => onPick(v)} className={cn("h-6 rounded-full border px-2 text-[11px] font-medium", current === v ? "border-accent text-accent-text" : "border-line text-ink-3 hover:text-ink")}>
          {format(v)}
        </button>
      ))}
    </div>
  );
}

export function PolicyDesigner({ detail, onApplied }: { detail: RoomDetail; onApplied?: () => void }) {
  const original = detail.booking!;
  const [draft, setDraft] = useState<BookingPolicy>(original);
  const fields = useBridgeQuery("booking.fields", {}, { staleTime: Infinity });
  const presets = useBridgeQuery("booking.presets", {}, { staleTime: Infinity });
  const { review } = usePlanFlow();
  const [debounced, setDebounced] = useState(draft);
  // The access model the administrator picked. Null means "whatever the draft matches", which is what edits in the advanced section show.
  const [chosen, setChosen] = useState<string | null>(null);
  const originalId = (presets.data ?? []).find((p) => p.mode === detail.summary.bookingMode)?.id ?? null;
  const [advancedOpen, setAdvancedOpen] = useState(detail.summary.bookingMode === "Custom");

  useEffect(() => {
    setDraft(original);
    setChosen(null);
  }, [original]);
  useEffect(() => setAdvancedOpen(detail.summary.bookingMode === "Custom"), [detail.mailbox.id, detail.summary.bookingMode]);
  useEffect(() => {
    const h = window.setTimeout(() => setDebounced(draft), 180);
    return () => window.clearTimeout(h);
  }, [draft]);

  const analysis = useBridgeQuery("booking.analyze", { roomName: detail.mailbox.displayName, policy: debounced, workingHours: detail.workingHours, capacity: detail.mailbox.resourceCapacity }, { placeholderData: keepPreviousData, staleTime: Infinity });
  const fieldInfo = useMemo(() => new Map((fields.data ?? []).map((f) => [f.name, f])), [fields.data]);
  const changes = useMemo(() => changedFields(original, draft, fields.data ?? []), [original, draft, fields.data]);
  const errors = (analysis.data?.issues ?? []).filter((i) => i.level === "Error");
  const warnings = (analysis.data?.issues ?? []).filter((i) => i.level !== "Error");
  const autoAccept = draft.automateProcessing === "AutoAccept";
  const detectedId = changes.length === 0 ? originalId : (presets.data ?? []).find((p) => p.mode === analysis.data?.explanation.mode)?.id ?? null;
  const activeId = chosen ?? detectedId;
  const missing = missingInputs((presets.data ?? []).find((p) => p.id === activeId), draft);
  const blocked = errors.length + missing.length;

  const set = (name: string, value: unknown) => {
    // A manual edit of an access parameter makes the selection follow the configuration again (a model, or custom).
    if (accessFields.includes(name)) setChosen(null);
    setDraft((d) => setField(d, name, value));
  };
  const chooseModel = (preset: BookingPreset) => {
    setChosen(preset.id);
    setDraft((d) => Object.entries(preset.fields).reduce((policy, [name, value]) => setField(policy, name, value), d));
  };
  const jump = (name: string) => {
    if (accessFields.includes(name) && !document.getElementById(`access-${name}`) && !advancedOpen) {
      setAdvancedOpen(true);
      window.setTimeout(() => focusField(name), 240);
      return;
    }
    focusField(name);
  };
  const bool = (name: string) => getField(draft, name) as boolean;
  const info = (name: string): PolicyFieldInfo | undefined => fieldInfo.get(name);
  const toggle = (name: string, extra?: { note?: ReactNode; dim?: boolean; help?: string }) => (
    <Row field={name} label={info(name)?.label ?? name} help={extra?.help ?? info(name)?.help} note={extra?.note ?? (!autoAccept && info(name)?.autoAcceptOnly ? <Badge tone="outline">{t("No effect without AutoAccept")}</Badge> : undefined)} dim={extra?.dim}>
      <Switch checked={bool(name)} onCheckedChange={(v) => set(name, v)} aria-label={info(name)?.label ?? name} />
    </Row>
  );

  const subjectVisible = !draft.deleteSubject && !draft.addOrganizerToSubject;

  return (
    <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_340px]">
      <div className="min-w-0 space-y-3">
        <Card className="relative overflow-hidden">
          <div className="blueprint pointer-events-none absolute inset-0 opacity-60" />
          <div className="relative px-5 py-4">
            <div className="mb-2 flex items-center gap-2">
              <span className="t-eyebrow">{t("Policy in plain language")}</span>
              {analysis.data && <Badge tone="accent">{analysis.data.explanation.modeLabel}</Badge>}
              {changes.length > 0 && <Badge tone="warn">{t("Unsaved draft")}</Badge>}
            </div>
            {analysis.data ? <PolicySentence text={analysis.data.explanation.summary} onJump={jump} /> : <div className="h-16" />}
            {analysis.data && (
              <ul className="mt-4 grid gap-x-6 gap-y-1.5 md:grid-cols-2">
                {analysis.data.explanation.statements.map((s: PolicyStatement, i: number) => (
                  <li key={i} className={cn("flex gap-2 text-[12.5px] leading-snug", s.kind === "Caution" ? "text-warn-ink" : "text-ink-2")}>
                    <span className="mt-[1px] shrink-0 text-ink-3 [&_svg]:size-3.5">{statementIcon[s.kind]}</span>
                    {s.text}
                  </li>
                ))}
              </ul>
            )}
          </div>
        </Card>

        {(errors.length > 0 || warnings.length > 0) && (
          <div className="space-y-2">
            {errors.map((e, i) => <InlineNotice key={`e${i}`} tone="bad" icon={<AlertOctagon />} action={<Button size="sm" variant="ghost" onClick={() => jump(e.field)}>{t("Show")}</Button>}>{e.message}</InlineNotice>)}
            {warnings.map((w, i) => <InlineNotice key={`w${i}`} tone={w.level === "Warning" ? "warn" : "info"} action={<Button size="sm" variant="ghost" onClick={() => jump(w.field)}>{t("Show")}</Button>}>{w.message}</InlineNotice>)}
          </div>
        )}

        {presets.data && (
          <BookingAccessPanel
            presets={presets.data}
            activeId={activeId}
            originalId={originalId}
            draft={draft}
            original={original}
            fieldInfo={info}
            onChoose={chooseModel}
            onInput={(name, value) => setDraft((d) => setField(d, name, value))}
          />
        )}

        <Disclosure
          id="section-permissions"
          open={advancedOpen}
          onOpenChange={setAdvancedOpen}
          title={t("Advanced booking configuration")}
          description={t("Every booking permission parameter individually: who books automatically, who requests with approval, delegates and forwarding. Groups must not be nested.")}
          badge={activeId === null ? <Badge tone="info">{t("Custom")}</Badge> : undefined}
        >
          <div className="overflow-hidden rounded-md border border-line mx-1">
            {([
              ["AllBookInPolicy", "BookInPolicy", tx("Book automatically"), tx("In-policy requests are accepted without review.")],
              ["AllRequestInPolicy", "RequestInPolicy", tx("Request with approval"), tx("In-policy requests wait for a delegate.")],
              ["AllRequestOutOfPolicy", "RequestOutOfPolicy", tx("Request outside policy"), tx("Requests that break a limit wait for a delegate.")],
            ] as const).map(([all, list, label, desc]) => (
              <div key={all} id={`field-${all}`} className="grid grid-cols-[180px_120px_1fr] items-center gap-3 border-b border-line px-3 py-2.5 last:border-b-0">
                <div>
                  <div className="text-[13px] font-medium">{t(label)}</div>
                  <div className="t-help">{t(desc)}</div>
                </div>
                <label className="flex items-center gap-2 text-[12px] text-ink-2">
                  <Switch checked={bool(all)} onCheckedChange={(v) => set(all, v)} aria-label={t("{label}: everyone", { label: t(label) })} />{t("Everyone")}</label>
                <div id={`field-${list}`} className={cn(bool(all) && "opacity-55")}>
                  <RecipientPicker value={getField(draft, list) as RecipientRef[]} onChange={(v) => set(list, v)} placeholder={bool(all) ? t("Not needed while everyone is allowed") : t("Add specific people or groups")} />
                </div>
              </div>
            ))}
          </div>
          <Row field="ResourceDelegates" label={t("Booking delegates")} help={t("Delegates approve or decline requests. This is not mailbox Full Access, Send As or a folder permission.")}>
            <div className="w-[420px] max-w-full"><RecipientPicker value={draft.resourceDelegates} onChange={(v) => set("ResourceDelegates", v)} placeholder={t("Add delegates")} /></div>
          </Row>
          {toggle("ForwardRequestsToDelegates")}
          {toggle("TentativePendingApproval")}
        </Disclosure>

        <Section id="section-general" title={t("General booking behavior")} description={t("Whether the room processes requests at all, and how.")} icon={<CalendarCheck2 />}>
          <Row field="AutomateProcessing" label={t("Calendar processing")} help={info("AutomateProcessing")?.help}>
            <Segmented size="sm" value={draft.automateProcessing} onChange={(v) => set("AutomateProcessing", v)} options={[{ value: "AutoAccept", label: "AutoAccept" }, { value: "AutoUpdate", label: "AutoUpdate" }, { value: "None", label: t("None") }]} />
          </Row>
          <Row field="BookingType" label={t("Booking type")} help={info("BookingType")?.help}>
            <Segmented size="sm" value={draft.bookingType} onChange={(v) => set("BookingType", v)} options={[{ value: "Standard", label: t("Standard") }, { value: "Reserved", label: t("Reserved") }]} />
          </Row>
          {toggle("AllowRecurringMeetings")}
        </Section>

        <Section id="section-scheduling" title={t("Scheduling constraints")} description={t("When and for how long the room can be booked.")} icon={<Timer />}>
          <Row field="BookingWindowInDays" label={t("Booking window")} help={draft.bookingWindowInDays === 0 ? t("Users may book for today only. Valid range 0–1080.") : t("Users may book up to {days} days in advance. Valid range 0–1080.", { days: draft.bookingWindowInDays })}>
            <div className="w-56">
              <NumberField value={draft.bookingWindowInDays} onChange={(v) => set("BookingWindowInDays", v ?? 0)} min={0} max={1080} step={30} unit="days" aria-label={t("Booking window in days")} />
              <QuickValues values={[30, 90, 180, 365]} current={draft.bookingWindowInDays} onPick={(v) => set("BookingWindowInDays", v)} format={(v) => `${v} d`} />
            </div>
          </Row>
          <Row field="MaximumDurationInMinutes" label={t("Maximum duration")} help={draft.maximumDurationInMinutes === 0 ? t("No maximum: meetings of any length are in policy.") : t("Meetings longer than {duration} are out of policy. Applies to each occurrence of a series.", { duration: formatMinutes(draft.maximumDurationInMinutes) })}>
            <div className="w-56">
              <NumberField value={draft.maximumDurationInMinutes} onChange={(v) => set("MaximumDurationInMinutes", v ?? 0)} min={0} max={100000} step={30} unit="min" aria-label={t("Maximum duration in minutes")} />
              <QuickValues values={[60, 120, 240, 480, 0]} current={draft.maximumDurationInMinutes} onPick={(v) => set("MaximumDurationInMinutes", v)} format={(v) => formatMinutes(v)} />
            </div>
          </Row>
          <Row field="MinimumDurationInMinutes" label={t("Minimum duration")} help={info("MinimumDurationInMinutes")?.help} note={<Badge tone="outline">{t("Workspace mailboxes only")}</Badge>}>
            <NumberField className="w-56" value={draft.minimumDurationInMinutes} onChange={(v) => set("MinimumDurationInMinutes", v ?? 0)} min={0} max={1440} step={15} unit="min" aria-label={t("Minimum duration")} />
          </Row>
          {toggle("ScheduleOnlyDuringWorkHours", {
            help: detail.workingHours
              ? t("Mailbox working hours: {days}, {start}–{end} ({zone}). Requests outside these hours are declined.", { days: describeDays(detail.workingHours.days), start: detail.workingHours.start.slice(0, 5), end: detail.workingHours.end.slice(0, 5), zone: detail.workingHours.timeZoneId })
              : info("ScheduleOnlyDuringWorkHours")?.help,
            note: <button type="button" className="text-[11.5px] font-normal text-accent-text hover:underline" onClick={() => navigate(`/working-hours/${encodeURIComponent(detail.mailbox.id)}`)}>{t("Edit working hours")}</button>,
          })}
          {toggle("EnforceSchedulingHorizon")}
          {toggle("EnforceCapacity", { help: `${info("EnforceCapacity")?.help ?? ""} ${detail.mailbox.resourceCapacity != null ? t("Room capacity: {n}.", { n: detail.mailbox.resourceCapacity }) : t("Room capacity: not set.")}` })}
        </Section>

        <Section id="section-conflicts" title={t("Conflict management")} description={t("These limits apply to recurring series. A single meeting that conflicts is declined unless conflicts are allowed.")} icon={<Scale />}>
          {toggle("AllowConflicts", { help: t("When enabled the room can be double-booked, and the thresholds below are ignored.") })}
          <Row field="ConflictPercentageAllowed" label={t("Conflict percentage allowed")} help={t("A series is declined when more than this share of its occurrences conflict.")} dim={draft.allowConflicts}>
            <NumberField className="w-40" value={draft.conflictPercentageAllowed} onChange={(v) => set("ConflictPercentageAllowed", v ?? 0)} min={0} max={100} step={5} unit="%" disabled={draft.allowConflicts} aria-label={t("Conflict percentage")} />
          </Row>
          <Row field="MaximumConflictInstances" label={t("Maximum conflict instances")} help={t("A series is declined when more occurrences than this conflict.")} dim={draft.allowConflicts}>
            <NumberField className="w-40" value={draft.maximumConflictInstances} onChange={(v) => set("MaximumConflictInstances", v ?? 0)} min={0} max={10000} disabled={draft.allowConflicts} aria-label={t("Maximum conflict instances")} />
          </Row>
        </Section>

        <Section id="section-privacy" title={t("Calendar privacy and content")} description={t("What remains visible in the room calendar to people who can view its details.")} icon={<Lock />}>
          {subjectVisible && <InlineNotice tone="warn" className="mx-2 mb-1">{t("Meeting subjects will be stored in the room calendar and visible to anyone with access to its details.")}</InlineNotice>}
          {draft.addOrganizerToSubject && autoAccept && <InlineNotice tone="info" className="mx-2 mb-1">{t("Organizer names will be visible in the room calendar.")}</InlineNotice>}
          <div className="grid gap-3 px-2 pb-2 md:grid-cols-[1fr_260px]">
            <div className="-mx-2">
              {toggle("DeleteSubject")}
              {toggle("AddOrganizerToSubject")}
              {toggle("DeleteComments")}
              {toggle("DeleteAttachments")}
              {toggle("RemovePrivateProperty")}
            </div>
            <div className="self-start rounded-md border border-line bg-sunken/50 p-3">
              <div className="t-eyebrow mb-2">{t("Illustrative calendar entry")}</div>
              <div className="rounded-sm border-l-2 border-accent bg-surface px-2.5 py-2 text-[12px]">
                <div className="font-medium text-ink">{autoAccept ? (draft.deleteSubject ? (draft.addOrganizerToSubject ? "Daan Jansen" : t("(no subject)")) : draft.addOrganizerToSubject ? `Daan Jansen — ${t("Quarterly review")}` : t("Quarterly review")) : t("Quarterly review")}</div>
                <div className="mt-1 text-ink-3">{autoAccept && draft.deleteComments ? t("Body removed") : t("Agenda and Teams join link")}</div>
                <div className="text-ink-3">{autoAccept && draft.deleteAttachments ? t("Attachments removed") : t("1 attachment")}</div>
                <div className="text-ink-3">{draft.removePrivateProperty ? t("Private flag cleared") : t("Private flag kept")}</div>
              </div>
              <p className="t-help mt-2">{t("Example request from Daan Jansen titled “Quarterly review”. Shows the effect of these settings only.")}</p>
            </div>
          </div>
        </Section>

        <Section id="section-external" title={t("External meeting requests")} description={t("Requests from organizers outside your organization.")} icon={<Globe2 />}>
          {toggle("ProcessExternalMeetingMessages", { help: t("When disabled, external requests are rejected. Enabling it does not override mail-flow rules, connectors or sharing restrictions.") })}
        </Section>

        <Section id="section-responses" title={t("Automated responses")} description={t("Text the room adds to its replies when it processes a request.")} icon={<MessageSquareText />}>
          {toggle("AddAdditionalResponse")}
          {toggle("EnableResponseDetails")}
          <div id="field-AdditionalResponse" className="grid gap-3 px-2.5 pb-2 md:grid-cols-2">
            <div>
              <label className="text-[12.5px] font-medium" htmlFor="additional-response">{t("Additional response text")}</label>
              <Textarea id="additional-response" className="mt-1.5" maxLength={2000} disabled={!draft.addAdditionalResponse} value={draft.additionalResponse ?? ""} onChange={(e) => set("AdditionalResponse", e.target.value || null)} placeholder={t("This is a Microsoft Teams Room. Please include the meeting link in the invitation.")} />
              <p className="t-mono mt-1 text-[10.5px] text-ink-3">AdditionalResponse · {tn((draft.additionalResponse ?? "").length, "{n} character", "{n} characters")}</p>
            </div>
            <div className="rounded-md border border-line bg-sunken/50 p-3 text-[12px]">
              <div className="t-eyebrow mb-2">{t("Response preview")}</div>
              <div className="font-medium">{t("Accepted: Quarterly review")}</div>
              <div className="mt-1 text-ink-2">{t("Your request was accepted.")}</div>
              {draft.addAdditionalResponse && draft.additionalResponse ? <div className="mt-2 whitespace-pre-wrap border-t border-line pt-2 text-ink">{draft.additionalResponse}</div> : <div className="mt-2 text-ink-3">{draft.addAdditionalResponse ? t("No additional text entered.") : t("No additional text is added.")}</div>}
              <p className="t-help mt-2">{t("Illustrative — Exchange composes the actual message.")}</p>
            </div>
          </div>
        </Section>

        <Disclosure title={t("Advanced settings")} description={t("Message handling details. Defaults suit most rooms.")}>
          {["OrganizerInfo", "DeleteNonCalendarItems", "RemoveCanceledMeetings", "RemoveOldMeetingMessages", "RemoveForwardedMeetingNotifications", "AddNewRequestsTentatively"].map((name) => <div key={name}>{toggle(name)}</div>)}
        </Disclosure>
      </div>

      <aside className="space-y-3 xl:sticky xl:top-0 xl:self-start">
        <Card>
          <div className="flex items-center justify-between px-4 pb-2 pt-3.5">
            <h3 className="t-section">{t("Pending changes")}</h3>
            <Badge tone={changes.length ? "accent" : "neutral"}>{changes.length}</Badge>
          </div>
          <div className="border-t border-line px-4 py-3">
            {changes.length === 0 ? (
              <p className="flex items-center gap-2 text-[12.5px] text-ink-3"><CircleSlash className="size-3.5" />{t("The draft matches the room's current configuration.")}</p>
            ) : (
              <ul className="space-y-2">
                {changes.map((f) => (
                  <li key={f.name}>
                    <button type="button" onClick={() => focusField(f.name)} className="w-full text-left">
                      <div className="text-[12.5px] font-medium text-ink">{f.label}</div>
                      <div className="text-[11.5px] text-ink-3"><span className="line-through">{formatValue(f, getField(original, f.name))}</span> → <span className="text-ink-2">{formatValue(f, getField(draft, f.name))}</span></div>
                    </button>
                  </li>
                ))}
              </ul>
            )}
            <div className="mt-3 flex gap-2">
              <Button variant="ghost" icon={<RotateCcw />} disabled={changes.length === 0} onClick={() => { setDraft(original); setChosen(null); }}>{t("Reset")}</Button>
              <Button variant="primary" className="flex-1" icon={<Wand2 />} disabled={changes.length === 0 || blocked > 0} onClick={() => review({ kind: "updateBookingPolicy", roomId: detail.mailbox.id, policy: draft }, { onCompleted: onApplied })}>
                {t("Preview changes")}</Button>
            </div>
            {blocked > 0 && <p className="mt-2 text-[11.5px] text-bad">{tn(blocked, "Resolve {n} blocking issue first.", "Resolve {n} blocking issues first.")}</p>}
          </div>
        </Card>
        <Card>
          <Tabs defaultValue="simulate">
            <TabsList className="px-3 pt-1" tabs={[{ value: "simulate", label: t("Scenario simulator") }, { value: "about", label: t("How it works") }]} />
            <TabsContent value="simulate" className="p-3">
              <ScenarioSimulator policy={draft} workingHours={detail.workingHours} capacity={detail.mailbox.resourceCapacity} roomId={detail.mailbox.id} />
            </TabsContent>
            <TabsContent value="about" className="space-y-2 p-4 text-[12.5px] leading-relaxed text-ink-2">
              <p className="flex gap-2"><Info className="mt-0.5 size-3.5 shrink-0 text-ink-3" />{t("The sentence and statements are generated from the draft by the same rules RoomOps uses to validate it.")}</p>
              <p className="flex gap-2"><Info className="mt-0.5 size-3.5 shrink-0 text-ink-3" />{t("Only changed parameters are sent to Set-CalendarProcessing, and the result is read back before it counts as verified.")}</p>
              <p className="flex gap-2"><Info className="mt-0.5 size-3.5 shrink-0 text-ink-3" />{t("Presets set every parameter that participates in the documented scenario, never a single checkbox.")}</p>
            </TabsContent>
          </Tabs>
        </Card>
      </aside>
    </div>
  );
}
