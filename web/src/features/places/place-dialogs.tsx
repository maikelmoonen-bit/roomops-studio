import { useEffect, useState } from "react";
import { Wand2 } from "lucide-react";
import type { Place, PlaceType } from "@/bridge/contracts.gen";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Card, CardHeader, Field, Input, NumberField } from "@/components/ui/core";
import { DialogContent } from "@/components/ui/overlay";
import { InlineNotice, SourceBadge } from "@/components/common/domain";
import { t, tx } from "@/lib/i18n";

export function CreatePlaceDialog({ type, parent, onClose }: { type: PlaceType; parent: Place | null; onClose: () => void }) {
  const { review } = usePlanFlow();
  const [name, setName] = useState("");
  const [label, setLabel] = useState("");
  const [sortOrder, setSortOrder] = useState<number | null>(null);
  const [address, setAddress] = useState({ street: "", city: "", state: "", postalCode: "", countryOrRegion: "" });
  const title = type === "Building" ? t("Create building") : type === "Floor" ? t("Create floor") : t("Create section");
  return (
    <DialogContent
      title={title}
      description={type === "Building" ? t("Buildings are the top level of the Places hierarchy. Rooms inherit the building address.") : type === "Floor" ? t("The new floor is created under {parent}.", { parent: parent?.displayName }) : t("The new section is created under {parent}.", { parent: parent?.displayName })}
      footer={<><Button variant="ghost" onClick={onClose}>{t("Cancel")}</Button><Button variant="primary" disabled={!name.trim()} onClick={() => { onClose(); review({ kind: "createPlace", type, displayName: name.trim(), parentId: parent?.placeId ?? null, label: label.trim() || null, sortOrder, street: address.street || null, city: address.city || null, state: address.state || null, postalCode: address.postalCode || null, countryOrRegion: address.countryOrRegion || null }); }}>{t("Preview changes")}</Button></>}
    >
      <div className="grid gap-3 md:grid-cols-2">
        <Field label={t("Name")} property="DisplayName" className="md:col-span-2">{(id) => <Input id={id} autoFocus value={name} onChange={(e) => setName(e.target.value)} maxLength={200} placeholder={type === "Floor" ? t("Floor 4") : type === "Section" ? t("North wing") : t("Eindhoven Headquarters")} />}</Field>
        <Field label={t("Label")} property="Label">{(id) => <Input id={id} value={label} onChange={(e) => setLabel(e.target.value)} maxLength={200} />}</Field>
        {type === "Floor" && <Field label={t("Sort order")} property="SortOrder" help={t("0 shows this floor first.")}>{(id) => <NumberField id={id} value={sortOrder} onChange={setSortOrder} min={-10} max={200} />}</Field>}
        {type === "Building" && (
          <>
            <Field label={t("Street")} property="Street" className="md:col-span-2">{(id) => <Input id={id} value={address.street} onChange={(e) => setAddress({ ...address, street: e.target.value })} />}</Field>
            <Field label={t("Postal code")} property="PostalCode">{(id) => <Input id={id} value={address.postalCode} onChange={(e) => setAddress({ ...address, postalCode: e.target.value })} />}</Field>
            <Field label={t("City")} property="City">{(id) => <Input id={id} value={address.city} onChange={(e) => setAddress({ ...address, city: e.target.value })} />}</Field>
            <Field label={t("State or province")} property="State">{(id) => <Input id={id} value={address.state} onChange={(e) => setAddress({ ...address, state: e.target.value })} />}</Field>
            <Field label={t("Country or region")} property="CountryOrRegion" help={t("Two-letter ISO code, e.g. NL.")}>{(id) => <Input id={id} value={address.countryOrRegion} maxLength={2} onChange={(e) => setAddress({ ...address, countryOrRegion: e.target.value.toUpperCase() })} />}</Field>
          </>
        )}
      </div>
    </DialogContent>
  );
}

type Draft = Record<string, string | number | null>;

const editableFields: Record<string, { key: string; label: string; kind?: "number" }[]> = {
  Building: [{ key: "DisplayName", label: tx("Name") }, { key: "Label", label: tx("Label") }, { key: "Street", label: tx("Street") }, { key: "PostalCode", label: tx("Postal code") }, { key: "City", label: tx("City") }, { key: "State", label: tx("State or province") }, { key: "CountryOrRegion", label: tx("Country or region") }],
  Floor: [{ key: "DisplayName", label: tx("Name") }, { key: "Label", label: tx("Label") }, { key: "SortOrder", label: tx("Sort order"), kind: "number" }],
  Section: [{ key: "DisplayName", label: tx("Name") }, { key: "Label", label: tx("Label") }],
};

const valueOf = (p: Place, key: string): string | number | null => {
  const map: Record<string, string | number | null> = { DisplayName: p.displayName, Label: p.label, Street: p.street, PostalCode: p.postalCode, City: p.city, State: p.state, CountryOrRegion: p.countryOrRegion, SortOrder: p.sortOrder };
  return map[key] ?? null;
};

export function PlaceEditor({ place }: { place: Place }) {
  const { review } = usePlanFlow();
  const fields = editableFields[place.type] ?? [];
  const initial = () => Object.fromEntries(fields.map((f) => [f.key, valueOf(place, f.key)])) as Draft;
  const [draft, setDraft] = useState<Draft>(initial);
  useEffect(() => setDraft(initial()), [place]); // eslint-disable-line react-hooks/exhaustive-deps
  const changes = Object.fromEntries(fields.filter((f) => (draft[f.key] ?? null) !== (valueOf(place, f.key) ?? null)).map((f) => [f.key, draft[f.key] === "" ? null : draft[f.key]]));
  return (
    <Card>
      <CardHeader eyebrow={place.type} title={place.displayName} actions={<SourceBadge source="MicrosoftPlaces" full />} />
      <div className="grid gap-3 border-t border-line p-4 md:grid-cols-2">
        {fields.map((f) => (
          <Field key={f.key} label={t(f.label)} property={f.key} className={f.key === "Street" || f.key === "DisplayName" ? "md:col-span-2" : undefined}>
            {(id) => f.kind === "number"
              ? <NumberField id={id} value={(draft[f.key] as number | null) ?? null} onChange={(v) => setDraft({ ...draft, [f.key]: v })} min={-10} max={200} />
              : <Input id={id} value={(draft[f.key] as string | null) ?? ""} onChange={(e) => setDraft({ ...draft, [f.key]: e.target.value })} maxLength={200} />}
          </Field>
        ))}
        {place.type === "Building" && <InlineNotice tone="info" className="md:col-span-2">{t("Rooms parented under this building inherit its address. Address values on individual rooms are ignored by Places.")}</InlineNotice>}
      </div>
      <div className="flex items-center justify-between border-t border-line px-4 py-3">
        <span className="t-mono text-[10.5px] text-ink-3">{place.placeId}</span>
        <Button variant="primary" icon={<Wand2 />} disabled={Object.keys(changes).length === 0} onClick={() => review({ kind: "updatePlace", placeId: place.placeId, fields: changes })}>{t("Preview changes")}</Button>
      </div>
    </Card>
  );
}
