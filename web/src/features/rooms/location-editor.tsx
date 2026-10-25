import { useEffect, useMemo, useState } from "react";
import { Accessibility, CheckCircle2, CircleHelp, Lock, MonitorPlay, RotateCcw, Wand2, XCircle } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { Place, RoomDetail, SignalStatus } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Card, CardHeader, Field, Input, NumberField, Skeleton } from "@/components/ui/core";
import { Combobox, Switch } from "@/components/ui/controls";
import { ErrorState, InlineNotice, SourceBadge } from "@/components/common/domain";
import { t, tn } from "@/lib/i18n";

/** Places parents a room may have: floors and sections (Microsoft Learn hierarchy rules). */
export function useParentOptions() {
  const tree = useBridgeQuery("places.tree", { refresh: false });
  return useMemo(() => {
    const nodes = tree.data?.nodes.map((n) => n.place) ?? [];
    const byId = new Map(nodes.map((p) => [p.placeId, p]));
    const path = (p: Place) => {
      const parts: string[] = [];
      let current: Place | undefined = p;
      const seen = new Set<string>();
      while (current && !seen.has(current.placeId)) {
        seen.add(current.placeId);
        parts.unshift(current.displayName);
        current = current.parentId ? byId.get(current.parentId) : undefined;
      }
      return parts.join(" / ");
    };
    return {
      available: tree.data?.available ?? false,
      loading: tree.isLoading,
      options: nodes.filter((p) => p.type === "Floor" || p.type === "Section").map((p) => ({ value: p.placeId, label: path(p), hint: p.type, group: p.type === "Floor" ? "Floors" : "Sections" })).sort((a, b) => a.label.localeCompare(b.label)),
      path: (id: string | null) => (id && byId.get(id) ? path(byId.get(id)!) : null),
    };
  }, [tree.data, tree.isLoading]);
}

function TagInput({ value, onChange }: { value: string[]; onChange: (v: string[]) => void }) {
  const [text, setText] = useState("");
  const add = () => {
    const tag = text.trim();
    if (tag && !value.some((v) => v.toLowerCase() === tag.toLowerCase())) onChange([...value, tag]);
    setText("");
  };
  return (
    <div className="flex min-h-[var(--ctl-h)] flex-wrap items-center gap-1 rounded-sm border border-line-strong bg-sunken/60 p-1 dark:bg-sunken">
      {value.map((tag) => (
        <span key={tag} className="inline-flex h-6 items-center gap-1 rounded-full border border-line-strong px-2 text-[12px]">
          {tag}<button type="button" aria-label={t("Remove tag {tag}", { tag })} className="text-ink-3 hover:text-ink" onClick={() => onChange(value.filter((v) => v !== tag))}>×</button>
        </span>
      ))}
      <input value={text} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter" || e.key === ",") { e.preventDefault(); add(); } }} onBlur={add} placeholder={t("Add tag")} className="h-6 min-w-24 flex-1 bg-transparent px-1 text-[12.5px] outline-none" aria-label={t("Add tag")} />
    </div>
  );
}

interface PlacesDraft {
  ParentId: string | null;
  Capacity: number | null;
  Label: string | null;
  IsWheelChairAccessible: boolean;
  AudioDeviceName: string | null;
  VideoDeviceName: string | null;
  DisplayDeviceName: string | null;
  MTREnabled: boolean;
  Tags: string[];
  FloorLabel: string | null;
}

const fromPlace = (p: Place): PlacesDraft => ({
  ParentId: p.parentId,
  Capacity: p.capacity,
  Label: p.label,
  IsWheelChairAccessible: p.isWheelChairAccessible ?? false,
  AudioDeviceName: p.audioDeviceName,
  VideoDeviceName: p.videoDeviceName,
  DisplayDeviceName: p.displayDeviceName,
  MTREnabled: p.mtrEnabled ?? false,
  Tags: p.tags,
  FloorLabel: p.floorLabel,
});

export function PlacesRoomEditor({ detail, onApplied }: { detail: RoomDetail; onApplied: () => void }) {
  const { review } = usePlanFlow();
  const parents = useParentOptions();
  const place = detail.place;
  const [draft, setDraft] = useState<PlacesDraft | null>(place ? fromPlace(place) : null);
  useEffect(() => setDraft(place ? fromPlace(place) : null), [place]);

  if (!detail.placesAvailable) {
    return <Card><CardHeader title={t("Microsoft Places metadata")} description={t("Connect Microsoft Places to manage location, facilities and devices.")} actions={<SourceBadge source="MicrosoftPlaces" />} /></Card>;
  }
  if (!place || !draft) {
    return (
      <Card>
        <CardHeader title={t("Microsoft Places metadata")} actions={<SourceBadge source="MicrosoftPlaces" />} />
        <div className="border-t border-line p-4"><InlineNotice tone="warn">{t("This mailbox has no matching Places room object yet. Places synchronizes new room mailboxes asynchronously, which can take up to 24 hours.")}</InlineNotice></div>
      </Card>
    );
  }
  const original = fromPlace(place);
  const changes = Object.fromEntries(Object.entries(draft).filter(([k, v]) => JSON.stringify(v) !== JSON.stringify(original[k as keyof PlacesDraft])));
  const set = <K extends keyof PlacesDraft>(key: K, value: PlacesDraft[K]) => setDraft((d) => (d ? { ...d, [key]: value } : d));
  const text = (key: "Label" | "AudioDeviceName" | "VideoDeviceName" | "DisplayDeviceName" | "FloorLabel", label: string, placeholder?: string) => (
    <Field label={label} property={key}>{(id) => <Input id={id} value={draft[key] ?? ""} placeholder={placeholder ?? t("Not set")} onChange={(e) => set(key, e.target.value || null)} maxLength={200} />}</Field>
  );

  return (
    <Card>
      <CardHeader title={t("Microsoft Places metadata")} description={t("The Places room object linked to this mailbox. It is a separate directory object; address details come from its building.")} actions={<SourceBadge source="MicrosoftPlaces" full />} />
      <div className="grid gap-4 border-t border-line p-4 md:grid-cols-2">
        <Field label={t("Parent location")} property="ParentId" help={t("A room must belong to a floor or a section.")} className="md:col-span-2">
          {(id) => parents.loading ? <Skeleton className="h-8" /> : <Combobox id={id} value={draft.ParentId} onChange={(v) => set("ParentId", v)} options={parents.options} placeholder={t("No parent — choose a floor or section")} />}
        </Field>
        <Field label={t("Capacity (Places)")} property="Capacity" help={detail.mailbox.resourceCapacity != null && draft.Capacity !== detail.mailbox.resourceCapacity ? t("Exchange ResourceCapacity is {n}.", { n: detail.mailbox.resourceCapacity }) : undefined}>
          {(id) => <NumberField id={id} value={draft.Capacity} onChange={(v) => set("Capacity", v)} min={0} max={10000} />}
        </Field>
        {text("Label", "Label", "e.g. Boardroom")}
        {text("DisplayDeviceName", t("Display device"))}
        {text("VideoDeviceName", t("Video device"))}
        {text("AudioDeviceName", t("Audio device"))}
        {text("FloorLabel", t("Floor label (Room Finder)"))}
        <Field label={t("Tags")} property="Tags" className="md:col-span-2" help={t("The whole set is replaced on update.")}>{() => <TagInput value={draft.Tags} onChange={(v) => set("Tags", v)} />}</Field>
        <label className="flex items-center justify-between rounded-md border border-line px-3 py-2.5 text-[12.5px]"><span className="flex items-center gap-2"><Accessibility className="size-4 text-ink-3" />{t("Wheelchair accessible")}</span><Switch checked={draft.IsWheelChairAccessible} onCheckedChange={(v) => set("IsWheelChairAccessible", v)} /></label>
        <label className="flex items-center justify-between rounded-md border border-line px-3 py-2.5 text-[12.5px]"><span className="flex items-center gap-2"><MonitorPlay className="size-4 text-ink-3" />{t("Teams Rooms system (MTR)")}</span><Switch checked={draft.MTREnabled} onCheckedChange={(v) => set("MTREnabled", v)} /></label>
      </div>
      <div className="flex items-center justify-between gap-2 border-t border-line px-4 py-3">
        <span className="t-help">{Object.keys(changes).length ? tn(Object.keys(changes).length, "{n} change. Places can take up to 24 hours to show room updates.", "{n} changes. Places can take up to 24 hours to show room updates.") : t("No changes.")}</span>
        <div className="flex gap-2">
          <Button variant="ghost" icon={<RotateCcw />} disabled={!Object.keys(changes).length} onClick={() => setDraft(original)}>{t("Reset")}</Button>
          <Button variant="primary" icon={<Wand2 />} disabled={!Object.keys(changes).length} onClick={() => review({ kind: "updatePlacesRoom", roomIds: [detail.mailbox.id], fields: changes }, { onCompleted: onApplied })}>{t("Preview changes")}</Button>
        </div>
      </div>
    </Card>
  );
}

export function MailboxEditor({ detail, onApplied }: { detail: RoomDetail; onApplied: () => void }) {
  const { review } = usePlanFlow();
  const [name, setName] = useState(detail.mailbox.displayName);
  const [capacity, setCapacity] = useState<number | null>(detail.mailbox.resourceCapacity);
  useEffect(() => { setName(detail.mailbox.displayName); setCapacity(detail.mailbox.resourceCapacity); }, [detail.mailbox]);
  const synced = detail.mailbox.isDirSynced;
  const changed = name.trim() !== detail.mailbox.displayName || capacity !== detail.mailbox.resourceCapacity;
  return (
    <Card>
      <CardHeader title={t("Exchange mailbox properties")} description={t("Name and capacity of the room mailbox, used by Outlook and Room Finder.")} actions={<SourceBadge source="ExchangeOnline" full />} />
      <div className="space-y-3 border-t border-line p-4">
        {synced && <InlineNotice tone="warn" icon={<Lock />} title={t("Managed on-premises")}>{t("This mailbox is synchronized from on-premises Active Directory. Change its name and capacity in on-premises Exchange; RoomOps does not attempt cloud-side updates.")}</InlineNotice>}
        <Field label={t("Display name")} property="DisplayName">{(id) => <Input id={id} value={name} disabled={synced} onChange={(e) => setName(e.target.value)} maxLength={256} />}</Field>
        <Field label={t("Capacity")} property="ResourceCapacity">{(id) => <NumberField id={id} value={capacity} disabled={synced} onChange={setCapacity} min={0} max={10000} />}</Field>
        <div className="flex justify-end">
          <Button variant="primary" icon={<Wand2 />} disabled={!changed || synced} onClick={() => review({ kind: "updateMailbox", roomId: detail.mailbox.id, displayName: name.trim() !== detail.mailbox.displayName ? name.trim() : null, resourceCapacity: capacity, clearCapacity: capacity == null }, { onCompleted: onApplied })}>{t("Preview changes")}</Button>
        </div>
      </div>
    </Card>
  );
}

const signalIcon: Record<SignalStatus, React.ReactNode> = {
  Ready: <CheckCircle2 className="size-4 text-ok" />,
  Attention: <XCircle className="size-4 text-warn-ink" />,
  NotApplicable: <CircleHelp className="size-4 text-ink-3" />,
  Unverifiable: <CircleHelp className="size-4 text-ink-3" />,
};

export function TeamsReadiness({ roomId }: { roomId: string }) {
  const readiness = useBridgeQuery("teamsRooms.readiness", { roomId, refresh: false });
  return (
    <Card>
      <CardHeader
        title={<span className="flex items-center gap-2"><MonitorPlay className="size-4 text-ink-3" />{t("Teams Rooms readiness")}</span>}
        description={t("Signals for using this room's account to sign in to a Microsoft Teams Rooms device. A room mailbox alone is not a ready resource account.")}
        actions={<Button size="sm" variant="ghost" onClick={() => void readiness.refetch()}>{t("Recheck")}</Button>}
      />
      <div className="border-t border-line">
        {readiness.isLoading && <div className="space-y-2 p-4"><Skeleton className="h-6" /><Skeleton className="h-6" /><Skeleton className="h-6" /></div>}
        {readiness.error && <ErrorState error={readiness.error} compact />}
        {readiness.data && (
          <>
            <p className="px-4 pt-3 text-[12.5px] text-ink-2">{readiness.data.summary}</p>
            <ul className="divide-y divide-line">
              {readiness.data.signals.map((s) => (
                <li key={s.key} className="grid grid-cols-[20px_190px_1fr] items-start gap-3 px-4 py-2.5">
                  <span className="pt-0.5">{signalIcon[s.status]}</span>
                  <span>
                    <span className="block text-[12.5px] font-medium">{s.label}</span>
                    <span className="block text-[11px] text-ink-3">{s.source}</span>
                  </span>
                  <span className="min-w-0">
                    <span className={cn("block text-[12.5px]", s.status === "Unverifiable" ? "text-ink-3" : "text-ink")}>{s.value}</span>
                    <span className="block text-[11.5px] text-ink-3">{s.explanation}</span>
                  </span>
                </li>
              ))}
            </ul>
            <div className="border-t border-line px-4 py-2.5 text-[11.5px] text-ink-3">
              {t("RoomOps does not change passwords, license assignments, Conditional Access or device settings.")}{" "}{readiness.data.detectedTeamsRoomsLicenses.length > 0 && <Badge tone="ok" className="ml-1">{readiness.data.detectedTeamsRoomsLicenses.join(", ")}</Badge>}
            </div>
          </>
        )}
      </div>
    </Card>
  );
}
