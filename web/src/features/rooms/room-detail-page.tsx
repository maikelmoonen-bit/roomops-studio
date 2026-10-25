import type { ReactNode } from "react";
import { ArrowLeft, CalendarCog, Clock4, Layers, ListTree, MapPin, MonitorPlay, RefreshCw, ShieldAlert, Users } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { RoomDetail, SourceSystem } from "@/bridge/contracts.gen";
import { formatDateTime, formatMinutes, formatRelative } from "@/lib/format";
import { Link, navigate, setQuery, useRoute } from "@/lib/router";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Card, CardHeader, Skeleton } from "@/components/ui/core";
import { Tabs, TabsContent, TabsList } from "@/components/ui/controls";
import { EmptyState, ErrorState, Identifier, InlineNotice, RoomName, SourceBadge } from "@/components/common/domain";
import { ExecutionStatusBadge } from "@/components/plan/plan-parts";
import { PolicyDesigner } from "@/features/booking/policy-designer";
import { DelegateManager } from "@/features/delegates/delegate-manager";
import { WorkingHoursEditor } from "@/features/working-hours/working-hours-editor";
import { FindingRow } from "@/features/validation/finding-row";
import { BookingModeBadge } from "./rooms-page";
import { MailboxEditor, PlacesRoomEditor, TeamsReadiness } from "./location-editor";
import { t, tn } from "@/lib/i18n";

function Prop({ label, value, source, mono }: { label: string; value: ReactNode; source?: SourceSystem; mono?: boolean }) {
  return (
    <div className="grid grid-cols-[170px_1fr_auto] items-center gap-3 border-b border-line px-4 py-2 text-[12.5px] last:border-b-0">
      <span className="text-ink-3">{label}</span>
      <span className={mono ? "min-w-0" : "min-w-0 truncate"}>{value ?? <span className="text-ink-3">—</span>}</span>
      {source && <SourceBadge source={source} />}
    </div>
  );
}

function Stat({ label, children, sub }: { label: string; children: ReactNode; sub?: ReactNode }) {
  return (
    <div className="min-w-0 border-l border-line pl-4 first:border-l-0 first:pl-0">
      <div className="t-eyebrow mb-1">{label}</div>
      <div className="text-[14px] font-semibold text-ink">{children}</div>
      {sub && <div className="mt-0.5 truncate text-[11.5px] text-ink-3">{sub}</div>}
    </div>
  );
}

export default function RoomDetailPage({ roomId }: { roomId: string }) {
  const route = useRoute();
  const tab = route.query.get("tab") ?? "overview";
  const query = useBridgeQuery("rooms.get", { roomId, refresh: false });
  const client = useQueryClient();
  const refresh = async () => {
    await bridge.call("rooms.get", { roomId, refresh: true });
    await client.invalidateQueries();
  };

  if (query.isLoading) return <div className="space-y-4"><Skeleton className="h-28" /><Skeleton className="h-10" /><Skeleton className="h-96" /></div>;
  if (query.error || !query.data) return <Card><ErrorState error={query.error} onRetry={() => void query.refetch()} /></Card>;
  const d = query.data;
  const findingsCount = d.findings.filter((f) => f.severity !== "UnableToVerify").length;

  return (
    <div>
      <Link to="/rooms" className="mb-3 inline-flex items-center gap-1 text-[12px] text-ink-3 hover:text-ink"><ArrowLeft className="size-3.5" />{t("Rooms")}</Link>
      <Card className="relative mb-4 overflow-hidden">
        <div className="blueprint pointer-events-none absolute inset-y-0 right-0 w-1/2 opacity-70 [mask-image:linear-gradient(to_left,black,transparent)]" />
        <div className="relative flex flex-wrap items-start justify-between gap-4 px-5 pb-4 pt-5">
          <div className="min-w-0">
            <RoomName name={d.mailbox.displayName} size="lg" className="font-display text-[26px] font-semibold tracking-[0.005em]" />
            <div className="mt-2 flex flex-wrap items-center gap-2 text-[12.5px] text-ink-2">
              <Identifier value={d.mailbox.primarySmtpAddress} />
              {d.placePath.length > 0 && <span className="inline-flex items-center gap-1"><MapPin className="size-3.5 text-ink-3" />{[...d.placePath.map((p) => p.displayName)].join(" / ")}</span>}
              {d.mailbox.isDirSynced && <Badge tone="warn">{t("Synchronized from on-premises")}</Badge>}
              {d.place?.mtrEnabled && <Badge tone="accent"><MonitorPlay />{t("Teams Rooms")}</Badge>}
            </div>
          </div>
          <div className="flex gap-2">
            <Button icon={<RefreshCw />} onClick={() => void refresh()}>{t("Reread")}</Button>
            <Button icon={<CalendarCog />} variant="primary" onClick={() => setQuery("tab", "booking")}>{t("Edit booking policy")}</Button>
          </div>
        </div>
        <div className="relative grid grid-cols-2 gap-y-3 border-t border-line px-5 py-3.5 md:grid-cols-5">
          <Stat label={t("Capacity")} sub={d.place?.capacity != null && d.place.capacity !== d.mailbox.resourceCapacity ? t("Places reports {n}", { n: d.place.capacity }) : t("Exchange ResourceCapacity")}>{d.mailbox.resourceCapacity ?? <span className="text-ink-3">{t("Not set")}</span>}</Stat>
          <Stat label={t("Booking")} sub={d.booking ? `${tn(d.booking.bookingWindowInDays, "{n} day", "{n} days")} · ${t("max {duration}", { duration: formatMinutes(d.booking.maximumDurationInMinutes) })}` : t("Not read")}><BookingModeBadge mode={d.summary.bookingMode} label={d.summary.bookingModeLabel} /></Stat>
          <Stat label={t("Delegates")} sub={d.booking?.forwardRequestsToDelegates ? t("Requests forwarded") : t("Not forwarded")}>{d.booking ? d.booking.resourceDelegates.length : "—"}</Stat>
          <Stat label={t("Working hours")} sub={d.workingHours?.timeZoneId}>{d.workingHours ? `${d.workingHours.start.slice(0, 5)}–${d.workingHours.end.slice(0, 5)}` : "—"}</Stat>
          <Stat label={t("Validation")} sub={d.findings.length ? t("{n} critical", { n: d.findings.filter((f) => f.severity === "Critical").length }) : t("All checks passed")}>{findingsCount}</Stat>
        </div>
      </Card>

      {d.loadErrors.length > 0 && <InlineNotice tone="warn" className="mb-4" title={t("Some configuration could not be read")}>{d.loadErrors.join(" · ")}</InlineNotice>}

      <Tabs value={tab} onValueChange={(v) => setQuery("tab", v === "overview" ? null : v)}>
        <TabsList className="mb-4" tabs={[
          { value: "overview", label: t("Overview") },
          { value: "booking", label: t("Booking rules") },
          { value: "delegates", label: t("Delegates & access") },
          { value: "hours", label: t("Working hours") },
          { value: "location", label: t("Location & facilities") },
          { value: "lists", label: t("Room lists"), count: d.roomLists.length },
          { value: "advanced", label: t("Advanced") },
          { value: "history", label: t("Configuration history"), count: d.history.length },
        ]} />
        <TabsContent value="overview"><Overview d={d} /></TabsContent>
        <TabsContent value="booking">{d.booking ? <PolicyDesigner detail={d} onApplied={() => void query.refetch()} /> : <Card><EmptyState title={t("Booking policy not available")} description={t("Calendar processing couldn't be read for this room.")} action={<Button onClick={() => void refresh()}>{t("Try again")}</Button>} /></Card>}</TabsContent>
        <TabsContent value="delegates"><DelegateManager roomId={d.mailbox.id} /></TabsContent>
        <TabsContent value="hours">{d.workingHours ? <WorkingHoursEditor roomId={d.mailbox.id} roomName={d.mailbox.displayName} current={d.workingHours} scheduleOnlyDuringWorkHours={d.booking?.scheduleOnlyDuringWorkHours ?? null} onApplied={() => void query.refetch()} /> : <Card><EmptyState title={t("Working hours not available")} /></Card>}</TabsContent>
        <TabsContent value="location">
          <div className="grid gap-3 xl:grid-cols-[1.4fr_1fr]">
            <PlacesRoomEditor detail={d} onApplied={() => void query.refetch()} />
            <div className="space-y-3">
              <MailboxEditor detail={d} onApplied={() => void query.refetch()} />
              <TeamsReadiness roomId={d.mailbox.id} />
            </div>
          </div>
        </TabsContent>
        <TabsContent value="lists"><RoomListsTab d={d} onChanged={() => void query.refetch()} /></TabsContent>
        <TabsContent value="advanced"><Advanced d={d} /></TabsContent>
        <TabsContent value="history"><HistoryTab d={d} /></TabsContent>
      </Tabs>
    </div>
  );
}

function Overview({ d }: { d: RoomDetail }) {
  return (
    <div className="grid gap-3 xl:grid-cols-[1.25fr_1fr]">
      <div className="space-y-3">
        {d.explanation && (
          <Card className="px-5 py-4">
            <div className="t-eyebrow mb-1.5">{t("Booking policy in plain language")}</div>
            <p className="font-display text-[18px] font-medium leading-snug">{d.explanation.summary}</p>
            <Button size="sm" variant="link" className="mt-2" onClick={() => setQuery("tab", "booking")}>{t("Open the policy designer")}</Button>
          </Card>
        )}
        <Card>
          <CardHeader title={<span className="flex items-center gap-2"><ShieldAlert className="size-4 text-ink-3" />{t("Validation findings")}</span>} description={t("Technical findings and organizational baseline checks for this room.")} />
          <div className="divide-y divide-line border-t border-line">
            {d.findings.length === 0 ? <p className="px-4 py-4 text-[12.5px] text-ink-3">{t("No findings.")}</p> : d.findings.map((f) => <FindingRow key={f.id} finding={f} />)}
          </div>
        </Card>
      </div>
      <div className="space-y-3">
        <Card>
          <CardHeader title={t("Exchange Online room mailbox")} description={t("Recipient object in Exchange Online.")} actions={<SourceBadge source="ExchangeOnline" full />} />
          <div className="border-t border-line">
            <Prop label={t("Display name")} value={d.mailbox.displayName} />
            <Prop label={t("Alias")} value={<Identifier value={d.mailbox.alias} />} mono />
            <Prop label={t("User principal name")} value={<Identifier value={d.mailbox.userPrincipalName} />} mono />
            <Prop label={t("Capacity")} value={d.mailbox.resourceCapacity ?? t("Not set")} />
            <Prop label={t("Room lists")} value={d.roomLists.map((l) => l.displayName).join(", ") || t("None")} />
            <Prop label={t("Created")} value={formatDateTime(d.mailbox.whenCreated)} />
            <Prop label={t("Directory")} value={d.mailbox.isDirSynced ? t("Synchronized from on-premises") : t("Cloud")} />
          </div>
        </Card>
        <Card>
          <CardHeader title={t("Microsoft Places room")} description={t("Directory object in Microsoft Places — separate from the mailbox.")} actions={<SourceBadge source="MicrosoftPlaces" full />} />
          <div className="border-t border-line">
            {!d.placesAvailable ? <p className="px-4 py-3 text-[12.5px] text-ink-3">{t("Microsoft Places is not connected.")}</p> : !d.place ? <p className="px-4 py-3 text-[12.5px] text-ink-3">{t("No Places room is linked to this mailbox yet.")}</p> : (
              <>
                <Prop label={t("Location")} value={d.placePath.map((p) => p.displayName).join(" / ") || t("No parent")} />
                <Prop label={t("Capacity")} value={d.place.capacity ?? t("Not set")} />
                <Prop label={t("Devices")} value={[d.place.displayDeviceName, d.place.videoDeviceName, d.place.audioDeviceName].filter(Boolean).join(" · ") || t("None recorded")} />
                <Prop label={t("Accessibility")} value={d.place.isWheelChairAccessible ? t("Wheelchair accessible") : t("Not specified")} />
                <Prop label={t("Tags")} value={d.place.tags.join(", ") || t("None")} />
              </>
            )}
          </div>
        </Card>
        {d.template && (
          <Card className="px-4 py-3">
            <div className="flex items-center gap-2 text-[13px] font-semibold"><Layers className="size-4 text-ink-3" />{t("Template:")}{" "}{d.template.templateName}</div>
            <p className="t-help mt-1">{d.template.driftFields.length === 0 ? t("The booking policy matches the template.") : t("Differs from the template: {fields}.", { fields: d.template.driftFields.join(", ") })}{d.template.excludedFields.length ? ` ${t("Exceptions: {fields}.", { fields: d.template.excludedFields.join(", ") })}` : ""}</p>
          </Card>
        )}
      </div>
    </div>
  );
}

function RoomListsTab({ d, onChanged }: { d: RoomDetail; onChanged: () => void }) {
  const lists = useBridgeQuery("roomLists.list", { refresh: false });
  const { review } = usePlanFlow();
  const memberIds = new Set(d.roomLists.map((l) => l.id));
  return (
    <div className="grid gap-3 lg:grid-cols-2">
      <Card>
        <CardHeader title={<span className="flex items-center gap-2"><ListTree className="size-4 text-ink-3" />{t("Member of")}</span>} description={t("Room lists drive Outlook Room Finder. They are independent of the Places building hierarchy.")} />
        <ul className="divide-y divide-line border-t border-line">
          {d.roomLists.length === 0 && <li className="px-4 py-4 text-[12.5px] text-ink-3">{t("This room is not in any room list.")}</li>}
          {d.roomLists.map((l) => (
            <li key={l.id} className="flex items-center justify-between px-4 py-2.5">
              <span><span className="block text-[13px] font-medium">{l.displayName}</span><Identifier value={l.primarySmtpAddress} /></span>
              <Button size="sm" variant="ghost" onClick={() => review({ kind: "updateRoomList", roomListId: l.id, displayName: null, addRoomIds: [], removeMemberAddresses: [d.mailbox.primarySmtpAddress] }, { onCompleted: onChanged })}>{t("Remove")}</Button>
            </li>
          ))}
        </ul>
      </Card>
      <Card>
        <CardHeader title={t("Add to a room list")} />
        <ul className="divide-y divide-line border-t border-line">
          {(lists.data ?? []).filter((l) => !memberIds.has(l.id)).map((l) => (
            <li key={l.id} className="flex items-center justify-between px-4 py-2.5">
              <span><span className="block text-[13px] font-medium">{l.displayName}</span><span className="text-[11.5px] text-ink-3">{tn(l.roomCount, "{n} room", "{n} rooms")}{l.buildings.length ? ` · ${l.buildings.join(", ")}` : ""}</span></span>
              <Button size="sm" onClick={() => review({ kind: "updateRoomList", roomListId: l.id, displayName: null, addRoomIds: [d.mailbox.id], removeMemberAddresses: [] }, { onCompleted: onChanged })}>{t("Add")}</Button>
            </li>
          ))}
        </ul>
      </Card>
    </div>
  );
}

function Advanced({ d }: { d: RoomDetail }) {
  const m = d.mailbox;
  const p = d.place;
  return (
    <div className="grid gap-3 lg:grid-cols-2">
      <Card>
        <CardHeader title={t("Identifiers")} description={t("Stable identifiers and the system that owns each one.")} />
        <div className="border-t border-line">
          <Prop label="ExternalDirectoryObjectId" value={<Identifier value={m.id} />} source="ExchangeOnline" mono />
          <Prop label="ExchangeGuid" value={<Identifier value={m.exchangeGuid} />} source="ExchangeOnline" mono />
          <Prop label="PrimarySmtpAddress" value={<Identifier value={m.primarySmtpAddress} />} source="ExchangeOnline" mono />
          <Prop label={t("Name")} value={<Identifier value={m.name} />} source="ExchangeOnline" mono />
          <Prop label="RecipientTypeDetails" value={m.recipientTypeDetails} source="ExchangeOnline" />
          <Prop label="PlaceId" value={<Identifier value={p?.placeId} />} source="MicrosoftPlaces" mono />
          <Prop label="ParentId" value={<Identifier value={p?.parentId} />} source="MicrosoftPlaces" mono />
        </div>
      </Card>
      <Card>
        <CardHeader title={t("Additional properties")} />
        <div className="border-t border-line">
          <Prop label="ResourceCustom" value={m.resourceCustom.join(", ") || t("None")} source="ExchangeOnline" />
          <Prop label={t("Office")} value={m.office} source="ExchangeOnline" />
          <Prop label="AccountDisabled" value={m.accountDisabled == null ? t("Unknown") : String(m.accountDisabled)} source="ExchangeOnline" />
          <Prop label="HiddenFromAddressLists" value={String(m.hiddenFromAddressLists)} source="ExchangeOnline" />
          <Prop label="IsDirSynced" value={String(m.isDirSynced)} source="ExchangeOnline" />
          <Prop label={t("Places Label")} value={p?.label} source="MicrosoftPlaces" />
          <Prop label={t("Places FloorLabel")} value={p?.floorLabel} source="MicrosoftPlaces" />
          <Prop label={t("Places IsManaged")} value={p?.isManaged == null ? null : String(p.isManaged)} source="MicrosoftPlaces" />
          <Prop label={t("Booking read at")} value={formatDateTime(d.bookingLoadedAt)} source="RoomOpsLocal" />
        </div>
      </Card>
    </div>
  );
}

function HistoryTab({ d }: { d: RoomDetail }) {
  if (d.history.length === 0) return <Card><EmptyState icon={<Clock4 />} title={t("No changes recorded")} description={t("Changes to this room made through RoomOps Studio appear here.")} /></Card>;
  return (
    <Card>
      <ul className="divide-y divide-line">
        {d.history.map((r) => (
          <li key={r.id}>
            <button type="button" onClick={() => navigate(`/history/${r.id}`)} className="grid w-full grid-cols-[1fr_auto] items-center gap-3 px-4 py-3 text-left hover:bg-hover">
              <span>
                <span className="block text-[13px] font-medium">{r.operationLabel}</span>
                <span className="block text-[12px] text-ink-3">{r.changes.slice(0, 3).map((c) => `${c.label}: ${c.before} → ${c.after}`).join(" · ")}</span>
                <span className="block text-[11.5px] text-ink-3"><Users className="mr-1 inline size-3" />{r.actor} · {formatRelative(r.timestamp)}</span>
              </span>
              <ExecutionStatusBadge status={r.outcome === "Succeeded" ? (r.verification?.outcome === "PendingPropagation" ? "PendingPropagation" : "Succeeded") : r.outcome === "NoChange" ? "NoChange" : r.outcome === "Stale" ? "Stale" : r.outcome === "PendingProvisioning" ? "PendingProvisioning" : "Failed"} verification={r.verification} />
            </button>
          </li>
        ))}
      </ul>
    </Card>
  );
}
