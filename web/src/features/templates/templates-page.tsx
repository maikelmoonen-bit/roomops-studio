import { useMemo, useState } from "react";
import { Download, Layers, Pencil, Plus, Search, Trash2, Upload, Wand2 } from "lucide-react";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { ConfigurationTemplate, PolicyFieldInfo } from "@/bridge/contracts.gen";
import { cn, formatRelative } from "@/lib/format";
import { openText, saveText } from "@/lib/files";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Card, Field, Input, NumberField, Skeleton, Textarea } from "@/components/ui/core";
import { Checkbox, Combobox, Select, Switch } from "@/components/ui/controls";
import { Dialog, DialogContent } from "@/components/ui/overlay";
import { EmptyState, ErrorState, InlineNotice, PageHeader, RoomName } from "@/components/common/domain";
import { t, tn } from "@/lib/i18n";

function describeValue(f: PolicyFieldInfo | undefined, v: unknown) {
  if (typeof v === "boolean") return v ? t("On") : t("Off");
  if (typeof v === "number" && f?.unit === "days") return tn(v, "{n} day", "{n} days");
  if (typeof v === "number" && f?.unit === "minutes") return v === 0 ? t("Unlimited") : t("{m} min", { m: v });
  if (typeof v === "number" && f?.unit === "percent") return `${v}%`;
  return String(v);
}

export default function TemplatesPage() {
  const templates = useBridgeQuery("templates.list", {});
  const assignments = useBridgeQuery("templates.assignments", {});
  const fields = useBridgeQuery("booking.fields", {}, { staleTime: Infinity });
  const [editing, setEditing] = useState<ConfigurationTemplate | null>(null);
  const [applying, setApplying] = useState<ConfigurationTemplate | null>(null);
  const [deleting, setDeleting] = useState<ConfigurationTemplate | null>(null);
  const fieldMap = useMemo(() => new Map((fields.data ?? []).map((f) => [f.name, f])), [fields.data]);

  const importFile = async () => {
    const file = await openText(["json"]);
    if (!file) return;
    try {
      const imported = await bridge.call("templates.import", { content: file.content });
      await templates.refetch();
      toast.success(tn(imported.length, "Imported {n} template", "Imported {n} templates"));
    } catch (error) {
      toast.error(t("Import failed"), { description: error instanceof Error ? error.message : undefined });
    }
  };
  const exportAll = async (ids: string[]) => {
    const file = await bridge.call("templates.export", { ids });
    if (await saveText(file.fileName, file.mimeType, file.content)) toast.success(t("Templates exported"));
  };
  const remove = async (tpl: ConfigurationTemplate) => {
    setDeleting(null);
    await bridge.call("templates.delete", { id: tpl.id });
    await templates.refetch();
    toast.success(t("Template deleted"));
  };

  return (
    <div>
      <PageHeader
        title={t("Configuration templates")}
        description={t("Reusable, sparse room configurations. Applying a template compares only the settings it defines and leaves everything else on the room untouched.")}
        actions={
          <>
            <Button icon={<Upload />} onClick={() => void importFile()}>{t("Import JSON")}</Button>
            <Button icon={<Download />} onClick={() => void exportAll([])}>{t("Export all")}</Button>
            <Button variant="primary" icon={<Plus />} onClick={() => setEditing({ id: "", name: "", description: "", bookingFields: {}, workingHours: null, version: 0, updatedAt: new Date().toISOString(), isExample: false })}>{t("New template")}</Button>
          </>
        }
      />
      {templates.isLoading && <Skeleton className="h-64" />}
      {templates.error && <Card><ErrorState error={templates.error} onRetry={() => void templates.refetch()} /></Card>}
      {templates.data?.length === 0 && <Card><EmptyState icon={<Layers />} title={t("No templates yet")} description={t("Create a template to apply consistent booking settings across rooms.")} /></Card>}
      <div className="grid gap-3 lg:grid-cols-2">
        {(templates.data ?? []).map((tpl) => {
          const assigned = (assignments.data ?? []).filter((a) => a.templateId === tpl.id).length;
          return (
            <Card key={tpl.id} className="flex flex-col">
              <div className="flex items-start justify-between gap-3 px-4 pt-4">
                <div>
                  <div className="flex items-center gap-2 text-[14px] font-semibold">{tpl.name}{tpl.isExample && <Badge tone="outline">{t("Example")}</Badge>}</div>
                  <p className="mt-1 text-[12.5px] text-ink-2">{tpl.description || t("No description.")}</p>
                </div>
                <Badge tone={assigned ? "accent" : "neutral"}>{tn(assigned, "{n} room", "{n} rooms")}</Badge>
              </div>
              <div className="mt-3 flex flex-wrap gap-1.5 px-4">
                {Object.entries(tpl.bookingFields).map(([k, v]) => <Badge key={k} tone="outline" className="gap-1"><span className="text-ink-3">{fieldMap.get(k)?.label ?? k}</span>{describeValue(fieldMap.get(k), v)}</Badge>)}
                {tpl.workingHours && <Badge tone="outline" className="gap-1"><span className="text-ink-3">{t("Working hours")}</span>{tpl.workingHours.start.slice(0, 5)}–{tpl.workingHours.end.slice(0, 5)} · {tpl.workingHours.timeZoneId}</Badge>}
              </div>
              <div className="mt-auto flex items-center gap-1.5 border-t border-line px-3 py-2.5 pt-2.5">
                <span className="mr-auto text-[11.5px] text-ink-3">{tpl.isExample ? t("Illustrative starting point — not a Microsoft default") : t("Version {version} · updated {time}", { version: tpl.version, time: formatRelative(tpl.updatedAt) })}</span>
                <Button size="icon-sm" variant="ghost" aria-label={t("Export")} onClick={() => void exportAll([tpl.id])}><Download /></Button>
                <Button size="icon-sm" variant="ghost" aria-label={t("Delete")} onClick={() => setDeleting(tpl)}><Trash2 /></Button>
                <Button size="sm" variant="ghost" icon={<Pencil />} onClick={() => setEditing(tpl)}>{t("Edit")}</Button>
                <Button size="sm" icon={<Wand2 />} onClick={() => setApplying(tpl)}>{t("Apply to rooms")}</Button>
              </div>
            </Card>
          );
        })}
      </div>
      <Dialog open={!!editing} onOpenChange={(o) => !o && setEditing(null)}>
        {editing && <TemplateEditor template={editing} fields={fields.data ?? []} onClose={() => setEditing(null)} onSaved={() => void templates.refetch()} />}
      </Dialog>
      <Dialog open={!!deleting} onOpenChange={(o) => !o && setDeleting(null)}>
        {deleting && (
          <DialogContent title={t("Delete “{name}”?", { name: deleting.name })} description={t("The template and its room assignments are removed from this device. Room configurations in Microsoft 365 are not changed.")}
            footer={<><Button variant="ghost" onClick={() => setDeleting(null)}>{t("Cancel")}</Button><Button variant="danger" icon={<Trash2 />} onClick={() => void remove(deleting)}>{t("Delete template")}</Button></>}>
            <p className="text-[12.5px] text-ink-2">{t("Export the template first if you may need it again.")}</p>
          </DialogContent>
        )}
      </Dialog>
      <Dialog open={!!applying} onOpenChange={(o) => !o && setApplying(null)}>
        {applying && <ApplyTemplateDialog template={applying} fieldMap={fieldMap} onClose={() => setApplying(null)} />}
      </Dialog>
    </div>
  );
}

function TemplateEditor({ template, fields, onClose, onSaved }: { template: ConfigurationTemplate; fields: PolicyFieldInfo[]; onClose: () => void; onSaved: () => void }) {
  const zones = useBridgeQuery("timeZones.list", {}, { staleTime: Infinity });
  const [name, setName] = useState(template.name);
  const [description, setDescription] = useState(template.description);
  const [values, setValues] = useState<Record<string, unknown>>(template.bookingFields);
  const [hours, setHours] = useState(template.workingHours);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const editable = fields.filter((f) => f.kind !== "RecipientList");

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      await bridge.call("templates.save", { ...template, name, description, bookingFields: values, workingHours: hours });
      toast.success(t("Template saved"));
      onSaved();
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : t("The template could not be saved"));
    } finally {
      setSaving(false);
    }
  };

  return (
    <DialogContent wide title={template.id ? t("Edit “{name}”", { name: template.name }) : t("New template")} description={t("Include only the settings this template should control. Recipient lists are room-specific and can't be part of a template.")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("Cancel")}</Button><Button variant="primary" loading={saving} disabled={!name.trim() || (Object.keys(values).length === 0 && !hours)} onClick={() => void save()}>{t("Save template")}</Button></>}>
      <div className="space-y-4">
        {error && <InlineNotice tone="bad">{error}</InlineNotice>}
        <div className="grid gap-3 md:grid-cols-2">
          <Field label={t("Name")}>{(id) => <Input id={id} value={name} onChange={(e) => setName(e.target.value)} autoFocus />}</Field>
          <Field label={t("Description")}>{(id) => <Textarea id={id} className="min-h-[var(--ctl-h)]" rows={1} value={description} onChange={(e) => setDescription(e.target.value)} />}</Field>
        </div>
        <div className="rounded-md border border-line">
          <div className="t-eyebrow border-b border-line px-3 py-2">{t("Booking settings")}</div>
          <div className="grid divide-line md:grid-cols-2">
            {editable.map((f) => {
              const on = f.name in values;
              return (
                <div key={f.name} className={cn("flex items-center gap-3 border-b border-line px-3 py-2", !on && "opacity-70")}>
                  <Checkbox checked={on} onCheckedChange={(v) => setValues((s) => { const n = { ...s }; if (v) n[f.name] = f.defaultValue; else delete n[f.name]; return n; })} aria-label={t("Include {setting}", { setting: f.label })} />
                  <div className="min-w-0 flex-1"><div className="truncate text-[12.5px] font-medium">{f.label}</div><div className="t-mono text-[10.5px] text-ink-3">{f.name}</div></div>
                  <div className="w-36 shrink-0">
                    {f.kind === "Boolean" && <Switch disabled={!on} checked={(on ? values[f.name] : f.defaultValue) as boolean} onCheckedChange={(v) => setValues((s) => ({ ...s, [f.name]: v }))} aria-label={f.label} />}
                    {f.kind === "Integer" && <NumberField disabled={!on} value={(on ? values[f.name] : f.defaultValue) as number} onChange={(v) => setValues((s) => ({ ...s, [f.name]: v ?? 0 }))} min={f.min ?? 0} max={f.max ?? 100000} aria-label={f.label} />}
                    {f.kind === "Enum" && <Select disabled={!on} value={(on ? values[f.name] : f.defaultValue) as string} onChange={(v) => setValues((s) => ({ ...s, [f.name]: v }))} options={(f.enumValues ?? []).map((e) => ({ value: e, label: e }))} />}
                    {f.kind === "Text" && <Input disabled={!on} value={((on ? values[f.name] : "") as string) ?? ""} onChange={(e) => setValues((s) => ({ ...s, [f.name]: e.target.value }))} />}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
        <div className="rounded-md border border-line p-3">
          <label className="flex items-center justify-between text-[12.5px] font-medium">{t("Include working hours")}<Switch checked={!!hours} onCheckedChange={(v) => setHours(v ? { days: "Monday, Tuesday, Wednesday, Thursday, Friday", start: "08:00:00", end: "18:00:00", timeZoneId: "W. Europe Standard Time" } : null)} /></label>
          {hours && (
            <div className="mt-3 grid gap-3 md:grid-cols-4">
              <Field label={t("Work days")}>{(id) => <Select id={id} value={hours.days.includes("Saturday") ? "all" : "weekdays"} onChange={(v) => setHours({ ...hours, days: v === "all" ? "Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday" : "Monday, Tuesday, Wednesday, Thursday, Friday" })} options={[{ value: "weekdays", label: t("Monday–Friday") }, { value: "all", label: t("Every day") }]} />}</Field>
              <Field label={t("Start")}>{(id) => <Input id={id} type="time" step={900} value={hours.start.slice(0, 5)} onChange={(e) => setHours({ ...hours, start: `${e.target.value}:00` })} />}</Field>
              <Field label={t("End")}>{(id) => <Input id={id} type="time" step={900} value={hours.end.slice(0, 5)} onChange={(e) => setHours({ ...hours, end: `${e.target.value}:00` })} />}</Field>
              <Field label={t("Time zone")}>{(id) => <Combobox id={id} value={hours.timeZoneId} onChange={(v) => setHours({ ...hours, timeZoneId: v })} options={(zones.data ?? []).map((z) => ({ value: z.id, label: z.id }))} />}</Field>
            </div>
          )}
        </div>
      </div>
    </DialogContent>
  );
}

function ApplyTemplateDialog({ template, fieldMap, onClose }: { template: ConfigurationTemplate; fieldMap: Map<string, PolicyFieldInfo>; onClose: () => void }) {
  const rooms = useBridgeQuery("rooms.list", { refresh: false });
  const { review } = usePlanFlow();
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [excluded, setExcluded] = useState<Set<string>>(new Set());
  const [assign, setAssign] = useState(true);
  const [filter, setFilter] = useState("");
  const visible = (rooms.data?.rooms ?? []).filter((r) => `${r.displayName} ${r.building ?? ""}`.toLowerCase().includes(filter.toLowerCase()));
  const keys = [...Object.keys(template.bookingFields), ...(template.workingHours ? ["WorkingHours"] : [])];

  return (
    <DialogContent wide title={t("Apply “{name}”", { name: template.name })} description={t("You'll see a before/after comparison for every room before anything changes.")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("Cancel")}</Button><Button variant="primary" disabled={selected.size === 0 || excluded.size === keys.length} onClick={() => { onClose(); review({ kind: "applyTemplate", templateId: template.id, roomIds: [...selected], excludedFields: [...excluded], assignTemplate: assign }); }}>{t("Preview")}{" "}{tn(selected.size, "{n} room", "{n} rooms")}</Button></>}>
      <div className="grid gap-4 md:grid-cols-[1fr_260px]">
        <div>
          <div className="mb-2 flex items-center gap-2">
            <Input leading={<Search />} placeholder={t("Filter rooms")} value={filter} onChange={(e) => setFilter(e.target.value)} />
            <Button size="sm" variant="ghost" onClick={() => setSelected(new Set(visible.map((r) => r.id)))}>{t("Select all")}</Button>
          </div>
          <div className="scroll-area max-h-80 rounded-md border border-line">
            {visible.map((r) => (
              <label key={r.id} className="flex items-center gap-2.5 border-b border-line px-3 py-2 last:border-b-0 hover:bg-hover">
                <Checkbox checked={selected.has(r.id)} onCheckedChange={(v) => setSelected((s) => { const n = new Set(s); if (v) n.add(r.id); else n.delete(r.id); return n; })} />
                <RoomName name={r.displayName} className="text-[12.5px]" />
                {r.templateName && <Badge tone="outline" className="ml-auto">{r.templateName}</Badge>}
              </label>
            ))}
          </div>
        </div>
        <div>
          <div className="mb-1.5 text-[12.5px] font-medium">{t("Preserve exceptions")}</div>
          <p className="t-help mb-2">{t("Unchecked settings are left as they are on every selected room.")}</p>
          <div className="space-y-1">
            {keys.map((k) => (
              <label key={k} className="flex items-center gap-2 text-[12.5px]">
                <Checkbox checked={!excluded.has(k)} onCheckedChange={(v) => setExcluded((s) => { const n = new Set(s); if (v) n.delete(k); else n.add(k); return n; })} />
                {k === "WorkingHours" ? t("Working hours") : fieldMap.get(k)?.label ?? k}
              </label>
            ))}
          </div>
          <label className="mt-4 flex items-start gap-2 text-[12.5px]"><Checkbox checked={assign} onCheckedChange={setAssign} /><span>{t("Assign the template, so drift from it is reported in the Validation Center")}</span></label>
        </div>
      </div>
    </DialogContent>
  );
}
