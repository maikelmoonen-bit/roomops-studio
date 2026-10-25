import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Accessibility, ArrowLeft, ArrowRight, Building2, Check, CheckCircle2, Loader2, MonitorPlay, Plus, ShieldCheck, Wand2, XCircle } from "lucide-react";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { BookingPolicy, CreateRoomIntent, PlanPreview, ProvisioningStrategy, RecipientRef } from "@/bridge/contracts.gen";
import { cn, formatMinutes } from "@/lib/format";
import { Link } from "@/lib/router";
import { usePlanFlow } from "@/state/plan-flow";
import { useSession } from "@/state/session";
import { Button } from "@/components/ui/button";
import { Badge, Card, Field, Input, NumberField } from "@/components/ui/core";
import { Combobox, Segmented, Select, Switch } from "@/components/ui/controls";
import { Dialog } from "@/components/ui/overlay";
import { InlineNotice, PageHeader } from "@/components/common/domain";
import { RecipientPicker } from "@/components/common/recipient-picker";
import { IssueList } from "@/components/plan/plan-parts";
import { CreatePlaceDialog } from "@/features/places/place-dialogs";
import { useParentOptions } from "@/features/rooms/location-editor";
import { suggestAlias } from "@/features/room-lists/room-list-dialogs";
import { t, tn, tx } from "@/lib/i18n";

const steps = [tx("Basic information"), tx("Physical location"), tx("Facilities"), tx("Booking behavior"), tx("Room list"), tx("Review and apply")] as const;
type PresetId = "automatic" | "restricted" | "delegate-approval" | "hybrid";

interface State {
  displayName: string;
  alias: string;
  aliasTouched: boolean;
  domain: string | null;
  capacity: number | null;
  strategy: ProvisioningStrategy;
  parentId: string | null;
  timeZone: string;
  setHours: boolean;
  start: string;
  end: string;
  weekend: boolean;
  label: string;
  wheelchair: boolean;
  display: string;
  video: string;
  audio: string;
  mtr: boolean;
  tags: string;
  preset: PresetId;
  delegates: RecipientRef[];
  bookIn: RecipientRef[];
  window: number;
  maxDuration: number;
  recurring: boolean;
  listMode: "existing" | "new" | "none";
  listId: string | null;
  newListName: string;
}

const initial: State = {
  displayName: "", alias: "", aliasTouched: false, domain: null, capacity: null, strategy: "ExchangeFirst", parentId: null, timeZone: "W. Europe Standard Time",
  setHours: true, start: "08:00", end: "18:00", weekend: false, label: "", wheelchair: false, display: "", video: "", audio: "", mtr: false, tags: "",
  preset: "automatic", delegates: [], bookIn: [], window: 90, maxDuration: 240, recurring: true, listMode: "existing", listId: null, newListName: "",
};

export default function CreateRoomWizard() {
  const { session, isDemo } = useSession();
  const [step, setStep] = useState(0);
  const [s, setS] = useState<State>(initial);
  const set = <K extends keyof State>(k: K, v: State[K]) => setS((x) => ({ ...x, [k]: v }));
  const domains = useBridgeQuery("rooms.acceptedDomains", {});
  const lists = useBridgeQuery("roomLists.list", { refresh: false });
  const fields = useBridgeQuery("booking.fields", {}, { staleTime: Infinity });
  const presets = useBridgeQuery("booking.presets", {}, { staleTime: Infinity });
  const zones = useBridgeQuery("timeZones.list", {}, { staleTime: Infinity });
  const parents = useParentOptions();
  const required = presets.data?.find((p) => p.id === s.preset)?.requiredInputs ?? [];
  const needsDelegates = required.includes("ResourceDelegates");
  const needsBookIn = required.includes("BookInPolicy");
  const placesConnected = !!session?.services.some((x) => x.service === "Places" && x.status === "Connected");
  const [availability, setAvailability] = useState<{ checking: boolean; addressFree: boolean | null; aliasFree: boolean | null; issues: string[] }>({ checking: false, addressFree: null, aliasFree: null, issues: [] });
  const [creatingFloor, setCreatingFloor] = useState(false);
  const [validation, setValidation] = useState<PlanPreview | null>(null);
  const [validating, setValidating] = useState(false);
  const { reviewPreview } = usePlanFlow();

  useEffect(() => { if (!s.domain && domains.data?.length) set("domain", domains.data[0].domainName); }, [domains.data, s.domain]);
  useEffect(() => { if (!s.aliasTouched) set("alias", suggestAlias(s.displayName)); }, [s.displayName, s.aliasTouched]);
  useEffect(() => { if (!s.listId && lists.data?.length) set("listId", lists.data[0].id); }, [lists.data, s.listId]);
  const smtp = s.alias && s.domain ? `${s.alias}@${s.domain}` : "";

  useEffect(() => {
    if (!s.alias || !smtp) return;
    setAvailability((a) => ({ ...a, checking: true }));
    const h = window.setTimeout(() => {
      bridge.call("rooms.checkAvailability", { alias: s.alias, primarySmtpAddress: smtp, displayName: s.displayName })
        .then((r) => setAvailability({ checking: false, addressFree: r.addressAvailable, aliasFree: r.aliasAvailable, issues: r.issues.filter((i) => i.level === "Error").map((i) => i.message) }))
        .catch(() => setAvailability({ checking: false, addressFree: null, aliasFree: null, issues: [t("Availability could not be checked.")] }));
    }, 350);
    return () => window.clearTimeout(h);
  }, [s.alias, smtp, s.displayName]);

  const stepErrors = useMemo(() => {
    const e: string[][] = steps.map(() => []);
    if (!s.displayName.trim()) e[0].push(t("Enter a display name."));
    if (!s.alias) e[0].push(t("Enter a mail alias."));
    if (availability.issues.length) e[0].push(...availability.issues);
    if (s.strategy === "PlacesFirst" && !s.parentId) e[1].push(t("Creating a room through Places requires a floor or section."));
    if (s.setHours && s.end <= s.start) e[1].push(t("Working hours must end after they start."));
    if (needsBookIn && s.bookIn.length === 0) e[3].push(t("Add at least one person or group who can book automatically."));
    if (needsDelegates && s.delegates.length === 0) e[3].push(t("Add at least one delegate to approve requests."));
    if (s.listMode === "existing" && !s.listId) e[4].push(t("Choose a room list."));
    if (s.listMode === "new" && !s.newListName.trim()) e[4].push(t("Enter a name for the new room list."));
    return e;
  }, [s, availability.issues, needsBookIn, needsDelegates]);

  const buildIntent = async (): Promise<CreateRoomIntent & { kind: "createRoom" }> => {
    const defaults = Object.fromEntries((fields.data ?? []).map((f) => [f.name.charAt(0).toLowerCase() + f.name.slice(1), f.kind === "RecipientList" ? [] : f.defaultValue])) as unknown as BookingPolicy;
    let policy = await bridge.call("booking.applyPreset", { policy: defaults, presetId: s.preset });
    policy = { ...policy, bookingWindowInDays: s.window, maximumDurationInMinutes: s.maxDuration, allowRecurringMeetings: s.recurring, resourceDelegates: needsDelegates ? s.delegates : [], bookInPolicy: needsBookIn ? s.bookIn : [] };
    const places: Record<string, unknown> = {};
    if (s.label) places.Label = s.label;
    if (s.wheelchair) places.IsWheelChairAccessible = true;
    if (s.display) places.DisplayDeviceName = s.display;
    if (s.video) places.VideoDeviceName = s.video;
    if (s.audio) places.AudioDeviceName = s.audio;
    if (s.mtr) places.MTREnabled = true;
    const tags = s.tags.split(",").map((t) => t.trim()).filter(Boolean);
    if (tags.length) places.Tags = tags;
    const listAlias = suggestAlias(s.newListName);
    return {
      kind: "createRoom",
      strategy: s.strategy,
      name: s.alias,
      displayName: s.displayName.trim(),
      alias: s.alias,
      primarySmtpAddress: smtp,
      capacity: s.capacity,
      placesParentId: s.parentId,
      placesFields: placesConnected ? places : {},
      bookingPresetId: s.preset,
      booking: policy,
      workingHours: s.setHours ? { days: s.weekend ? "Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday" : "Monday, Tuesday, Wednesday, Thursday, Friday", start: `${s.start}:00`, end: `${s.end}:00`, timeZoneId: s.timeZone } : null,
      roomListId: s.listMode === "existing" ? s.listId : null,
      newRoomList: s.listMode === "new" ? { name: s.newListName.trim(), displayName: s.newListName.trim(), alias: listAlias, primarySmtpAddress: `${listAlias}@${s.domain}` } : null,
    };
  };

  const validate = async (open: boolean) => {
    setValidating(true);
    try {
      const preview = await bridge.call("plans.create", await buildIntent());
      setValidation(preview);
      if (open) reviewPreview(preview);
    } catch (error) {
      setValidation(null);
      throw error;
    } finally {
      setValidating(false);
    }
  };

  const canAdvance = stepErrors[step].length === 0;

  return (
    <div>
      <Link to="/rooms" className="mb-3 inline-flex items-center gap-1 text-[12px] text-ink-3 hover:text-ink"><ArrowLeft className="size-3.5" />{t("Rooms")}</Link>
      <PageHeader title={t("Create room")} description={t("Configure the mailbox, location, facilities, booking behavior and room list in one flow. Nothing is created until you review and apply.")} />
      <div className="grid gap-5 lg:grid-cols-[230px_minmax(0,1fr)]">
        <nav aria-label={t("Steps")}>
          <ol className="space-y-1">
            {steps.map((label, i) => {
              const done = i < step && stepErrors[i].length === 0;
              return (
                <li key={label}>
                  <button type="button" onClick={() => (i <= step || stepErrors.slice(0, i).every((e) => e.length === 0)) && setStep(i)} className={cn("flex w-full items-center gap-2.5 rounded-sm px-2 py-2 text-left text-[13px]", i === step ? "bg-[color-mix(in_srgb,var(--accent)_12%,transparent)] font-semibold text-ink" : "text-ink-2 hover:bg-hover")} aria-current={i === step ? "step" : undefined}>
                    <span className={cn("flex size-6 items-center justify-center rounded-full border font-display text-[12px] font-semibold", done ? "border-ok text-ok" : i === step ? "border-accent text-accent-text" : "border-line-strong text-ink-3")}>{done ? <Check className="size-3.5" /> : i + 1}</span>
                    {t(label)}
                  </button>
                </li>
              );
            })}
          </ol>
          <div className="mt-4 rounded-md border border-line px-3 py-2.5 text-[11.5px] text-ink-3">
            <ShieldCheck className="mb-1 size-4 text-accent" />
            {t("Target tenant:")}{" "}<span className="font-medium text-ink">{session?.tenantName}</span>{isDemo && <Badge tone="demo" className="ml-1">{t("Demo")}</Badge>}
          </div>
        </nav>

        <Card className="min-w-0 p-5">
          <h2 className="font-display text-[20px] font-semibold">{t(steps[step])}</h2>
          <div className="mt-4">
            {step === 0 && (
              <div className="grid max-w-3xl gap-4 md:grid-cols-2">
                <Field label={t("Display name")} help={t("Shown in Outlook, Teams and Room Finder.")} className="md:col-span-2">{(id) => <Input id={id} autoFocus value={s.displayName} onChange={(e) => set("displayName", e.target.value)} placeholder={t("EHV 3.24 Meeting Room")} maxLength={256} />}</Field>
                <Field label={t("Mail alias")} property="Alias">{(id) => <Input id={id} mono value={s.alias} onChange={(e) => { set("alias", e.target.value); set("aliasTouched", true); }} maxLength={64} />}</Field>
                <Field label={t("Accepted domain")}>{(id) => <Select id={id} value={s.domain} onChange={(v) => set("domain", v)} options={(domains.data ?? []).map((d) => ({ value: d.domainName, label: d.domainName, hint: d.isDefault ? t("Default") : undefined }))} />}</Field>
                <div className="md:col-span-2 flex flex-wrap items-center gap-3 rounded-md bg-sunken/60 px-3 py-2.5 text-[12.5px]">
                  <span className="text-ink-3">{t("Primary SMTP address")}</span><span className="t-mono text-ink">{smtp || "—"}</span>
                  <span className="ml-auto flex items-center gap-1.5">
                    {availability.checking ? <><Loader2 className="size-3.5 animate-spin text-ink-3" />{t("Checking")}</> : availability.addressFree === true && availability.aliasFree === true ? <><CheckCircle2 className="size-3.5 text-ok" />{t("Available")}</> : availability.addressFree === false || availability.aliasFree === false ? <><XCircle className="size-3.5 text-bad" />{t("Already in use")}</> : null}
                  </span>
                </div>
                <Field label={t("Capacity")} property="ResourceCapacity" help={t("Number of seats.")}>{(id) => <NumberField id={id} value={s.capacity} onChange={(v) => set("capacity", v)} min={0} max={10000} />}</Field>
                {stepErrors[0].length > 0 && s.displayName && <IssueList issues={stepErrors[0].map((m) => ({ field: "basic", message: m, level: "Error" }))} />}
              </div>
            )}

            {step === 1 && (
              <div className="max-w-3xl space-y-4">
                <Field label={t("Provisioning strategy")} help={t("Exactly one object-creating command is used, so the room is never created twice.")}>
                  {() => (
                    <div className="grid gap-2 md:grid-cols-2">
                      {([["ExchangeFirst", t("Exchange first"), t("New-Mailbox -Room creates the mailbox immediately. Places synchronizes it (up to 24 hours) and RoomOps then links it to the location.")], ["PlacesFirst", t("Places first"), t("New-Place -Type Room creates the Places room and provisions the mailbox, which can take a while. Exchange settings follow once the mailbox exists.")]] as const).map(([value, title, text]) => (
                        <button key={value} type="button" disabled={value === "PlacesFirst" && !placesConnected} onClick={() => set("strategy", value)} className={cn("flex flex-col justify-start rounded-lg border p-3 text-left disabled:opacity-45", s.strategy === value ? "border-accent bg-[color-mix(in_srgb,var(--accent)_8%,transparent)]" : "border-line hover:border-line-strong")}>
                          <div className="text-[13px] font-semibold">{title}</div>
                          <p className="mt-1 text-[11.5px] leading-snug text-ink-3">{text}</p>
                        </button>
                      ))}
                    </div>
                  )}
                </Field>
                <Field label={t("Location in Microsoft Places")} property="ParentId" help={placesConnected ? t("Choose a floor or section. Buildings provide the address.") : t("Microsoft Places is not connected; the room can be placed later.")}>
                  {(id) => (
                    <div className="flex gap-2">
                      <div className="min-w-0 flex-1"><Combobox id={id} disabled={!placesConnected} value={s.parentId} onChange={(v) => set("parentId", v)} options={[{ value: "", label: t("No location yet") }, ...parents.options]} placeholder={t("Netherlands / Eindhoven / Headquarters / Floor 3")} /></div>
                      <Button icon={<Plus />} disabled={!placesConnected} onClick={() => setCreatingFloor(true)}>{t("New building or floor")}</Button>
                    </div>
                  )}
                </Field>
                <div className="rounded-lg border border-line p-4">
                  <label className="flex items-center justify-between text-[13px] font-medium">{t("Set working hours")}<Switch checked={s.setHours} onCheckedChange={(v) => set("setHours", v)} /></label>
                  <p className="t-help mt-0.5">{t("New room mailboxes default to Pacific Standard Time; set the room's real time zone here.")}</p>
                  {s.setHours && (
                    <div className="mt-3 grid gap-3 md:grid-cols-[1fr_120px_120px_auto]">
                      <Field label={t("Time zone")} property="WorkingHoursTimeZone">{(id) => <Combobox id={id} value={s.timeZone} onChange={(v) => set("timeZone", v)} options={(zones.data ?? []).map((z) => ({ value: z.id, label: z.id }))} />}</Field>
                      <Field label={t("Opens")}>{(id) => <Input id={id} type="time" step={900} value={s.start} onChange={(e) => set("start", e.target.value)} />}</Field>
                      <Field label={t("Closes")}>{(id) => <Input id={id} type="time" step={900} value={s.end} onChange={(e) => set("end", e.target.value)} />}</Field>
                      <Field label={t("Days")}>{() => <Segmented value={s.weekend ? "all" : "weekdays"} onChange={(v) => set("weekend", v === "all")} options={[{ value: "weekdays", label: t("Mon–Fri") }, { value: "all", label: t("Every day") }]} />}</Field>
                    </div>
                  )}
                </div>
                <IssueList issues={stepErrors[1].map((m) => ({ field: "location", message: m, level: "Error" }))} />
              </div>
            )}

            {step === 2 && (
              <div className="max-w-3xl space-y-4">
                {!placesConnected && <InlineNotice tone="warn">{t("Facility metadata is stored in Microsoft Places, which is not connected. These fields are skipped; you can add them later from the room's Location tab.")}</InlineNotice>}
                <div className={cn("grid gap-4 md:grid-cols-2", !placesConnected && "pointer-events-none opacity-50")}>
                  <Field label={t("Label")} property="Label" help={t("Short description, e.g. “Boardroom”.")}>{(id) => <Input id={id} value={s.label} onChange={(e) => set("label", e.target.value)} maxLength={200} />}</Field>
                  <Field label={t("Tags")} property="Tags" help={t("Comma-separated.")}>{(id) => <Input id={id} value={s.tags} onChange={(e) => set("tags", e.target.value)} placeholder={t("Whiteboard, Video")} />}</Field>
                  <Field label={t("Display device")} property="DisplayDeviceName">{(id) => <Input id={id} value={s.display} onChange={(e) => set("display", e.target.value)} placeholder={t("75\" display")} />}</Field>
                  <Field label={t("Video device")} property="VideoDeviceName">{(id) => <Input id={id} value={s.video} onChange={(e) => set("video", e.target.value)} />}</Field>
                  <Field label={t("Audio device")} property="AudioDeviceName">{(id) => <Input id={id} value={s.audio} onChange={(e) => set("audio", e.target.value)} />}</Field>
                  <div className="space-y-2 pt-6">
                    <label className="flex items-center justify-between rounded-md border border-line px-3 py-2 text-[12.5px]"><span className="flex items-center gap-2"><Accessibility className="size-4 text-ink-3" />{t("Wheelchair accessible")}</span><Switch checked={s.wheelchair} onCheckedChange={(v) => set("wheelchair", v)} /></label>
                    <label className="flex items-center justify-between rounded-md border border-line px-3 py-2 text-[12.5px]"><span className="flex items-center gap-2"><MonitorPlay className="size-4 text-ink-3" />{t("Teams Rooms system")}</span><Switch checked={s.mtr} onCheckedChange={(v) => set("mtr", v)} /></label>
                  </div>
                </div>
                {s.mtr && <InlineNotice tone="info">{t("This only records Teams Rooms metadata in Places. A Teams Rooms device additionally needs an enabled resource account with a Teams Rooms license; RoomOps checks readiness but never assigns licenses or passwords.")}</InlineNotice>}
              </div>
            )}

            {step === 3 && (
              <div className="space-y-4">
                <div className="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
                  {(presets.data ?? []).map((p) => (
                    <button key={p.id} type="button" onClick={() => set("preset", p.id as PresetId)} className={cn("flex flex-col justify-start rounded-lg border p-3 text-left", s.preset === p.id ? "border-accent bg-[color-mix(in_srgb,var(--accent)_8%,transparent)]" : "border-line hover:border-line-strong")}>
                      <div className="text-[13px] font-semibold">{p.name}</div>
                      <p className="mt-1 text-[11.5px] leading-snug text-ink-3">{p.description}</p>
                    </button>
                  ))}
                </div>
                <p className="t-help">{presets.data?.find((p) => p.id === s.preset)?.source}</p>
                {needsBookIn && <Field label={t("Can book automatically")} property="BookInPolicy" help={t("Only direct members of listed groups are honored.")}>{() => <RecipientPicker value={s.bookIn} onChange={(v) => set("bookIn", v)} placeholder={t("Add people or groups")} />}</Field>}
                {needsDelegates && <Field label={t("Approve requests (booking delegates)")} property="ResourceDelegates">{() => <RecipientPicker value={s.delegates} onChange={(v) => set("delegates", v)} placeholder={t("Add delegates")} />}</Field>}
                <div className="grid max-w-3xl gap-4 md:grid-cols-3">
                  <Field label={t("Booking window")} property="BookingWindowInDays">{(id) => <NumberField id={id} value={s.window} onChange={(v) => set("window", v ?? 0)} min={0} max={1080} step={30} unit="days" />}</Field>
                  <Field label={t("Maximum duration")} property="MaximumDurationInMinutes" help={formatMinutes(s.maxDuration)}>{(id) => <NumberField id={id} value={s.maxDuration} onChange={(v) => set("maxDuration", v ?? 0)} min={0} max={100000} step={30} unit="min" />}</Field>
                  <Field label={t("Recurring meetings")} property="AllowRecurringMeetings">{() => <Switch checked={s.recurring} onCheckedChange={(v) => set("recurring", v)} />}</Field>
                </div>
                <p className="t-help">{t("Every other setting starts at the Exchange Online default. Fine-tune all parameters in the policy designer after the room exists.")}</p>
                <IssueList issues={stepErrors[3].map((m) => ({ field: "booking", message: m, level: "Error" }))} />
              </div>
            )}

            {step === 4 && (
              <div className="max-w-3xl space-y-4">
                <Segmented value={s.listMode} onChange={(v) => set("listMode", v)} options={[{ value: "existing", label: t("Existing room list") }, { value: "new", label: t("New room list") }, { value: "none", label: t("No room list") }]} />
                {s.listMode === "existing" && <Field label={t("Room list")}>{(id) => <Combobox id={id} value={s.listId} onChange={(v) => set("listId", v)} options={(lists.data ?? []).map((l) => ({ value: l.id, label: l.displayName, hint: tn(l.roomCount, "{n} room", "{n} rooms") }))} />}</Field>}
                {s.listMode === "new" && (
                  <Field label={t("Room list name")} help={s.newListName ? t("Address {address}", { address: `${suggestAlias(s.newListName)}@${s.domain}` }) : t("Name it after the building for consistent Room Finder browsing.")}>{(id) => <Input id={id} value={s.newListName} onChange={(e) => set("newListName", e.target.value)} />}</Field>
                )}
                {s.listMode === "none" && <InlineNotice tone="warn">{t("Without a room list the room isn't browsable in Outlook Room Finder.")}</InlineNotice>}
                <IssueList issues={stepErrors[4].map((m) => ({ field: "list", message: m, level: "Error" }))} />
              </div>
            )}

            {step === 5 && (
              <div className="space-y-4">
                <div className="grid gap-3 md:grid-cols-2">
                  <Summary title={t("Mailbox")} rows={[[tx("Display name"), s.displayName], [tx("Address"), smtp], [tx("Capacity"), s.capacity ?? t("Not set")], [tx("Strategy"), s.strategy === "ExchangeFirst" ? t("Exchange first (New-Mailbox)") : t("Places first (New-Place)")]]} />
                  <Summary title={t("Location")} rows={[[tx("Places"), parents.path(s.parentId) ?? t("No location")], [tx("Working hours"), s.setHours ? `${s.weekend ? t("Every day") : t("Monday–Friday")} ${s.start}–${s.end}` : t("Exchange default")], [tx("Time zone"), s.setHours ? s.timeZone : "—"]]} />
                  <Summary title={t("Booking")} rows={[[tx("Booking access"), presets.data?.find((p) => p.id === s.preset)?.name ?? s.preset], [tx("Window"), tn(s.window, "{n} day", "{n} days")], [tx("Maximum"), formatMinutes(s.maxDuration)], [tx("Recurring"), s.recurring ? t("Allowed") : t("Not allowed")]]} />
                  <Summary title={t("Room list")} rows={[[tx("Membership"), s.listMode === "existing" ? lists.data?.find((l) => l.id === s.listId)?.displayName ?? "—" : s.listMode === "new" ? t("{name} (new)", { name: s.newListName }) : t("None")]]} />
                </div>
                {validation && (
                  <InlineNotice tone={validation.totals.blocked ? "bad" : "ok"} title={validation.totals.blocked ? t("Validation found blocking issues") : tn(validation.totals.operations, "Ready: {n} command planned", "Ready: {n} commands planned")}>
                    {validation.targets.flatMap((tg) => tg.issues).length > 0 ? <IssueList issues={validation.targets.flatMap((tg) => tg.issues)} /> : t("Uniqueness, accepted domain, hierarchy and booking rules all passed.")}
                  </InlineNotice>
                )}
                <div className="flex gap-2">
                  <Button loading={validating} onClick={() => void validate(false)}>{t("Validate")}</Button>
                  <Button variant="primary" icon={<Wand2 />} loading={validating} disabled={stepErrors.some((e) => e.length > 0)} onClick={() => void validate(true)}>{t("Preview changes")}</Button>
                </div>
                <p className="t-help">{t("The preview lists the exact PowerShell operations, expected objects and warnings. “Apply configuration” in the preview creates the room and reports each provisioning stage: requested, accepted by Microsoft 365, object created, directory synchronization, fully verified.")}</p>
              </div>
            )}
          </div>

          <div className="mt-6 flex items-center justify-between border-t border-line pt-4">
            <Button variant="ghost" icon={<ArrowLeft />} disabled={step === 0} onClick={() => setStep(step - 1)}>{t("Back")}</Button>
            {step < steps.length - 1 && <Button variant="primary" disabled={!canAdvance} onClick={() => setStep(step + 1)}>{t("Continue")}<ArrowRight /></Button>}
          </div>
        </Card>
      </div>
      <Dialog open={creatingFloor} onOpenChange={setCreatingFloor}>
        {creatingFloor && <CreatePlaceDialog type={parents.options.length === 0 ? "Building" : "Floor"} parent={null} onClose={() => setCreatingFloor(false)} />}
      </Dialog>
    </div>
  );
}

function Summary({ title, rows }: { title: string; rows: [string, ReactNode][] }) {
  return (
    <div className="rounded-lg border border-line">
      <div className="flex items-center gap-2 border-b border-line px-3 py-2 text-[12.5px] font-semibold"><Building2 className="size-3.5 text-ink-3" />{title}</div>
      <dl className="grid grid-cols-[120px_1fr] gap-y-1 px-3 py-2.5 text-[12.5px]">
        {rows.map(([k, v]) => <div key={k} className="contents"><dt className="text-ink-3">{t(k)}</dt><dd className="truncate">{v}</dd></div>)}
      </dl>
    </div>
  );
}
