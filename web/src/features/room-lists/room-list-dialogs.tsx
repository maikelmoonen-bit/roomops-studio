import { useEffect, useMemo, useState } from "react";
import { CheckCircle2, Search, XCircle } from "lucide-react";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { ConversionAssessment, ConvertibleGroup } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Field, Input, Skeleton } from "@/components/ui/core";
import { Checkbox, Select } from "@/components/ui/controls";
import { DialogContent } from "@/components/ui/overlay";
import { InlineNotice, RoomName } from "@/components/common/domain";
import { t } from "@/lib/i18n";

export function suggestAlias(name: string) {
  return name.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/[^a-z0-9]+/g, ".").replace(/^\.+|\.+$/g, "").slice(0, 64);
}

export function CreateRoomListDialog({ onClose }: { onClose: () => void }) {
  const domains = useBridgeQuery("rooms.acceptedDomains", {});
  const rooms = useBridgeQuery("rooms.list", { refresh: false });
  const { review } = usePlanFlow();
  const [name, setName] = useState("");
  const [alias, setAlias] = useState("");
  const [aliasTouched, setAliasTouched] = useState(false);
  const [domain, setDomain] = useState<string | null>(null);
  const [members, setMembers] = useState<Set<string>>(new Set());
  const [filter, setFilter] = useState("");

  useEffect(() => { if (!domain && domains.data?.length) setDomain(domains.data[0].domainName); }, [domains.data, domain]);
  useEffect(() => { if (!aliasTouched) setAlias(suggestAlias(name)); }, [name, aliasTouched]);
  const visible = useMemo(() => (rooms.data?.rooms ?? []).filter((r) => r.displayName.toLowerCase().includes(filter.toLowerCase())), [rooms.data, filter]);
  const valid = name.trim().length > 0 && /^[a-z0-9!#$%&'*+\-/=?^_`{|}~]+(\.[a-z0-9!#$%&'*+\-/=?^_`{|}~]+)*$/i.test(alias) && !!domain;

  return (
    <DialogContent
      wide
      title={t("Create room list")}
      description={t("Room lists are distribution groups that contain only room mailboxes. Outlook Room Finder uses them to browse rooms.")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("Cancel")}</Button><Button variant="primary" disabled={!valid} onClick={() => { onClose(); review({ kind: "createRoomList", name: name.trim(), displayName: name.trim(), alias, primarySmtpAddress: `${alias}@${domain}`, memberRoomIds: [...members] }); }}>{t("Preview changes")}</Button></>}
    >
      <div className="grid gap-4 md:grid-cols-2">
        <div className="space-y-3">
          <Field label={t("Name")} help={t("Microsoft recommends one room list per building, named exactly like the building, when you use Room Finder.")}>{(id) => <Input id={id} value={name} onChange={(e) => setName(e.target.value)} placeholder={t("Eindhoven Headquarters")} autoFocus />}</Field>
          <Field label={t("Alias")} property="Alias">{(id) => <Input id={id} value={alias} mono onChange={(e) => { setAlias(e.target.value); setAliasTouched(true); }} />}</Field>
          <Field label={t("Domain")}>{(id) => <Select id={id} value={domain} onChange={setDomain} options={(domains.data ?? []).map((d) => ({ value: d.domainName, label: d.domainName, hint: d.isDefault ? t("Default") : undefined }))} />}</Field>
          <div className="rounded-md bg-sunken/60 px-3 py-2 text-[12px]"><span className="text-ink-3">{t("Address")} </span><span className="t-mono">{alias || t("alias")}@{domain ?? t("domain")}</span></div>
        </div>
        <div>
          <div className="mb-1.5 flex items-center justify-between text-[12.5px] font-medium">{t("Rooms")} <Badge tone="accent">{t("{n} selected", { n: members.size })}</Badge></div>
          <Input leading={<Search />} placeholder={t("Filter rooms")} value={filter} onChange={(e) => setFilter(e.target.value)} className="mb-2" />
          <div className="scroll-area max-h-72 rounded-md border border-line">
            {rooms.isLoading && <Skeleton className="m-2 h-40" />}
            {visible.map((r) => (
              <label key={r.id} className="flex items-center gap-2.5 border-b border-line px-3 py-2 last:border-b-0 hover:bg-hover">
                <Checkbox checked={members.has(r.id)} onCheckedChange={(v) => setMembers((m) => { const n = new Set(m); if (v) n.add(r.id); else n.delete(r.id); return n; })} />
                <RoomName name={r.displayName} className="text-[12.5px]" />
              </label>
            ))}
          </div>
        </div>
      </div>
    </DialogContent>
  );
}

export function ConvertGroupDialog({ onClose }: { onClose: () => void }) {
  const { review } = usePlanFlow();
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<ConvertibleGroup[]>([]);
  const [selected, setSelected] = useState<ConvertibleGroup | null>(null);
  const [assessment, setAssessment] = useState<ConversionAssessment | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (query.trim().length < 2) { setResults([]); return; }
    const h = window.setTimeout(() => void bridge.call("groups.search", { query }).then(setResults), 250);
    return () => window.clearTimeout(h);
  }, [query]);

  useEffect(() => {
    if (!selected) { setAssessment(null); return; }
    setLoading(true);
    void bridge.call("groups.assess", { id: selected.primarySmtpAddress }).then(setAssessment).finally(() => setLoading(false));
  }, [selected]);

  return (
    <DialogContent
      wide
      title={t("Convert a distribution group to a room list")}
      description={t("Microsoft documents converting regular distribution groups that contain room mailboxes. RoomOps checks the membership first and never converts a group silently.")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("Cancel")}</Button><Button variant="primary" disabled={!assessment?.canConvert} onClick={() => { onClose(); review({ kind: "convertToRoomList", groupIdentity: selected!.primarySmtpAddress }); }}>{t("Preview conversion")}</Button></>}
    >
      <div className="grid gap-4 md:grid-cols-[1fr_1.2fr]">
        <div>
          <Input autoFocus leading={<Search />} placeholder={t("Search distribution groups")} value={query} onChange={(e) => setQuery(e.target.value)} />
          <ul className="scroll-area mt-2 max-h-72 rounded-md border border-line">
            {results.length === 0 && <li className="px-3 py-6 text-center text-[12px] text-ink-3">{query.length < 2 ? t("Type at least two characters.") : t("No groups found.")}</li>}
            {results.map((g) => (
              <li key={g.id}>
                <button type="button" onClick={() => setSelected(g)} className={cn("w-full border-b border-line px-3 py-2 text-left last:border-b-0", selected?.id === g.id ? "bg-[color-mix(in_srgb,var(--accent)_12%,transparent)]" : "hover:bg-hover")}>
                  <span className="block text-[12.5px] font-medium">{g.displayName}</span>
                  <span className="t-mono block text-[10.5px] text-ink-3">{g.primarySmtpAddress} · {g.recipientTypeDetails}</span>
                </button>
              </li>
            ))}
          </ul>
        </div>
        <div>
          {!selected && <p className="pt-10 text-center text-[12.5px] text-ink-3">{t("Select a group to check whether it can be converted.")}</p>}
          {loading && <Skeleton className="h-40" />}
          {assessment && !loading && (
            <div className="space-y-3">
              {assessment.canConvert
                ? <InlineNotice tone="ok" icon={<CheckCircle2 />} title={t("Can be converted")}>{t("All {n} members are room mailboxes.", { n: assessment.members.length })}</InlineNotice>
                : <InlineNotice tone="bad" icon={<XCircle />} title={t("Can't be converted")}><ul>{assessment.reasons.map((r) => <li key={r}>· {r}</li>)}</ul></InlineNotice>}
              <ul className="scroll-area max-h-56 rounded-md border border-line">
                {assessment.members.map((m) => (
                  <li key={m.id} className="flex items-center justify-between border-b border-line px-3 py-1.5 text-[12.5px] last:border-b-0">
                    <span>{m.displayName}</span>
                    <Badge tone={m.recipientTypeDetails === "RoomMailbox" ? "ok" : "bad"}>{m.recipientTypeDetails}</Badge>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      </div>
    </DialogContent>
  );
}
