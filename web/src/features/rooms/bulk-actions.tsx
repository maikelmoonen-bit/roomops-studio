import { useState } from "react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { PolicyFieldInfo, RoomSummary } from "@/bridge/contracts.gen";

import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Field, NumberField } from "@/components/ui/core";
import { Checkbox, Combobox, Select, Switch } from "@/components/ui/controls";
import { Dialog, DialogContent } from "@/components/ui/overlay";
import { InlineNotice } from "@/components/common/domain";
import { t, tn } from "@/lib/i18n";

type Mode = "template" | "booking" | "roomList" | null;

/** Selection-driven bulk actions. Each opens the shared plan review — nothing is executed without the preview. */
export function RoomBulkDialogs({ mode, onClose, rooms }: { mode: Mode; onClose: () => void; rooms: RoomSummary[] }) {
  return (
    <Dialog open={mode !== null} onOpenChange={(o) => !o && onClose()}>
      {mode === "template" && <TemplateDialog rooms={rooms} onClose={onClose} />}
      {mode === "booking" && <BookingDialog rooms={rooms} onClose={onClose} />}
      {mode === "roomList" && <RoomListDialog rooms={rooms} onClose={onClose} />}
    </Dialog>
  );
}

function TemplateDialog({ rooms, onClose }: { rooms: RoomSummary[]; onClose: () => void }) {
  const templates = useBridgeQuery("templates.list", {});
  const [templateId, setTemplateId] = useState<string | null>(null);
  const [assign, setAssign] = useState(true);
  const { review } = usePlanFlow();
  const template = templates.data?.find((tpl) => tpl.id === templateId);
  return (
    <DialogContent
      title={tn(rooms.length, "Apply a template to {n} room", "Apply a template to {n} rooms")}
      description={t("Only the settings defined in the template are compared and applied. You review every difference before anything changes.")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("Cancel")}</Button><Button variant="primary" disabled={!templateId} onClick={() => { onClose(); review({ kind: "applyTemplate", templateId: templateId!, roomIds: rooms.map((r) => r.id), excludedFields: [], assignTemplate: assign }); }}>{t("Preview changes")}</Button></>}
    >
      <div className="space-y-4">
        <Field label={t("Template")}>
          {(id) => <Combobox id={id} value={templateId} onChange={setTemplateId} options={(templates.data ?? []).map((tpl) => ({ value: tpl.id, label: tpl.name, hint: tn(Object.keys(tpl.bookingFields).length, "{n} setting", "{n} settings") }))} placeholder={t("Choose a template")} />}
        </Field>
        {template && <p className="t-help">{template.description}</p>}
        <label className="flex items-center gap-2 text-[12.5px]"><Checkbox checked={assign} onCheckedChange={setAssign} />{t("Assign the template so the Validation Center reports drift later")}</label>
      </div>
    </DialogContent>
  );
}

function BookingDialog({ rooms, onClose }: { rooms: RoomSummary[]; onClose: () => void }) {
  const fields = useBridgeQuery("booking.fields", {});
  const { review } = usePlanFlow();
  const [values, setValues] = useState<Record<string, unknown>>({});
  const editable = (fields.data ?? []).filter((f) => f.kind !== "RecipientList" && f.kind !== "Text");
  const toggle = (f: PolicyFieldInfo, on: boolean) => setValues((v) => {
    const next = { ...v };
    if (on) next[f.name] = f.defaultValue;
    else delete next[f.name];
    return next;
  });
  return (
    <DialogContent
      wide
      title={tn(rooms.length, "Change booking settings for {n} room", "Change booking settings for {n} rooms")}
      description={t("Select the settings to set. Unselected settings stay as they are on each room.")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("Cancel")}</Button><Button variant="primary" disabled={Object.keys(values).length === 0} onClick={() => { onClose(); review({ kind: "patchBookingPolicy", roomIds: rooms.map((r) => r.id), fields: values }); }}>{t("Preview changes")}</Button></>}
    >
      <div className="grid gap-x-6 gap-y-1 md:grid-cols-2">
        {editable.map((f) => {
          const on = f.name in values;
          return (
            <div key={f.name} className="flex items-center gap-3 rounded-sm px-1 py-1.5">
              <Checkbox checked={on} onCheckedChange={(v) => toggle(f, v)} aria-label={t("Change {setting}", { setting: f.label })} />
              <div className="min-w-0 flex-1">
                <div className="truncate text-[12.5px] font-medium">{f.label}</div>
                <div className="t-mono text-[10.5px] text-ink-3">{f.name}</div>
              </div>
              <div className="w-36 shrink-0">
                {f.kind === "Boolean" && <Switch disabled={!on} checked={on ? (values[f.name] as boolean) : (f.defaultValue as boolean)} onCheckedChange={(v) => setValues((s) => ({ ...s, [f.name]: v }))} aria-label={f.label} />}
                {f.kind === "Integer" && <NumberField disabled={!on} value={on ? (values[f.name] as number) : (f.defaultValue as number)} min={f.min ?? 0} max={f.max ?? 100000} unit={f.unit === "days" ? "d" : f.unit === "minutes" ? "min" : f.unit === "percent" ? "%" : undefined} onChange={(v) => setValues((s) => ({ ...s, [f.name]: v ?? 0 }))} aria-label={f.label} />}
                {f.kind === "Enum" && <Select disabled={!on} value={(on ? values[f.name] : f.defaultValue) as string} onChange={(v) => setValues((s) => ({ ...s, [f.name]: v }))} options={(f.enumValues ?? []).map((e) => ({ value: e, label: e }))} />}
              </div>
            </div>
          );
        })}
      </div>
    </DialogContent>
  );
}

function RoomListDialog({ rooms, onClose }: { rooms: RoomSummary[]; onClose: () => void }) {
  const lists = useBridgeQuery("roomLists.list", { refresh: false });
  const [listId, setListId] = useState<string | null>(null);
  const { review } = usePlanFlow();
  return (
    <DialogContent
      title={tn(rooms.length, "Add {n} room to a room list", "Add {n} rooms to a room list")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("Cancel")}</Button><Button variant="primary" disabled={!listId} onClick={() => { onClose(); review({ kind: "updateRoomList", roomListId: listId!, displayName: null, addRoomIds: rooms.map((r) => r.id), removeMemberAddresses: [] }); }}>{t("Preview changes")}</Button></>}
    >
      <div className="space-y-3">
        <Field label={t("Room list")}>
          {(id) => <Combobox id={id} value={listId} onChange={setListId} options={(lists.data ?? []).map((l) => ({ value: l.id, label: l.displayName, hint: tn(l.roomCount, "{n} room", "{n} rooms") }))} placeholder={t("Choose a room list")} />}
        </Field>
        <InlineNotice tone="info">{t("Rooms that are already members are reported as “No change required”. Room lists affect Outlook Room Finder only; the Places hierarchy is not changed.")}</InlineNotice>
      </div>
    </DialogContent>
  );
}
