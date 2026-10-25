import { useEffect, useState } from "react";
import { Save } from "lucide-react";
import { toast } from "sonner";
import { useBridgeQuery } from "@/bridge/hooks";
import type { AppSettings, FindingSeverity } from "@/bridge/contracts.gen";
import { useSession } from "@/state/session";
import { Button } from "@/components/ui/button";
import { Badge, Card, CardHeader, Field, Input, NumberField } from "@/components/ui/core";
import { Combobox, Segmented, Select, Switch } from "@/components/ui/controls";
import { Identifier, PageHeader, severityLabel } from "@/components/common/domain";
import { languages, t } from "@/lib/i18n";

type Tri = "unset" | "true" | "false";
const toTri = (v: boolean | null): Tri => (v == null ? "unset" : v ? "true" : "false");
const fromTri = (v: Tri): boolean | null => (v === "unset" ? null : v === "true");

export function SettingsPage() {
  const { settings, updateSettings, appInfo, session } = useSession();
  const zones = useBridgeQuery("timeZones.list", {}, { requireSession: false, staleTime: Infinity });
  const rules = useBridgeQuery("validation.run", { refresh: false }, { enabled: session?.mode !== "None" });
  const [draft, setDraft] = useState<AppSettings | null>(settings);
  const [zonePick, setZonePick] = useState<string | null>(null);
  useEffect(() => setDraft(settings), [settings]);
  if (!draft) return null;
  const dirty = JSON.stringify(draft) !== JSON.stringify(settings);
  const save = async () => {
    await updateSettings(() => draft);
    toast.success(t("Settings saved"));
  };
  const baselineRules = (rules.data?.rules ?? []).filter((r) => r.category === "OrganizationalBaseline");
  const privacy = draft.validation.privacy;

  return (
    <div className="max-w-[980px]">
      <PageHeader title={t("Settings")} description={t("Preferences are stored in your user profile on this device. RoomOps never stores passwords or tokens.")} actions={<Button variant="primary" icon={<Save />} disabled={!dirty} onClick={() => void save()}>{t("Save settings")}</Button>} />
      <div className="space-y-3">
        <Card>
          <CardHeader title={t("Appearance")} />
          <div className="grid gap-4 border-t border-line p-4 md:grid-cols-2">
            <Field label={t("Theme")}>{() => <Segmented value={draft.appearance.theme} onChange={(v) => setDraft({ ...draft, appearance: { ...draft.appearance, theme: v } })} options={[{ value: "System", label: t("System") }, { value: "Dark", label: t("Dark") }, { value: "Light", label: t("Light") }]} />}</Field>
            <Field label={t("Density")} help={t("Compact fits more rows on smaller displays.")}>{() => <Segmented value={draft.appearance.density} onChange={(v) => setDraft({ ...draft, appearance: { ...draft.appearance, density: v } })} options={[{ value: "Comfortable", label: t("Comfortable") }, { value: "Compact", label: t("Compact") }]} />}</Field>
            <Field label={t("Motion")} help={t("System follows the operating system's reduced-motion setting.")}>{() => <Segmented value={draft.appearance.motion} onChange={(v) => setDraft({ ...draft, appearance: { ...draft.appearance, motion: v } })} options={[{ value: "System", label: t("System") }, { value: "Reduced", label: t("Reduced") }, { value: "Full", label: t("Full") }]} />}</Field>
            <Field label={t("Language")} help={t("Also available from the EN | NL switch at the top right.")}>{(id) => <Select id={id} value={draft.appearance.language} onChange={(v) => setDraft({ ...draft, appearance: { ...draft.appearance, language: v } })} options={languages.map((l) => ({ value: l.value, label: l.label }))} />}</Field>
          </div>
        </Card>

        <Card>
          <CardHeader title={t("Connections")} description={t("Applied to the next connection.")} />
          <div className="grid gap-4 border-t border-line p-4 md:grid-cols-2">
            <Field label={t("PowerShell 7 path")} help={t("Leave empty to find pwsh automatically.")} className="md:col-span-2">{(id) => <Input id={id} mono value={draft.connection.powerShellPath ?? ""} placeholder="C:\Program Files\PowerShell\7\pwsh.exe" onChange={(e) => setDraft({ ...draft, connection: { ...draft.connection, powerShellPath: e.target.value || null } })} />}</Field>
            <Field label={t("Exchange sign-in")}>{(id) => <Select id={id} value={draft.connection.exchangeSignIn} onChange={(v) => setDraft({ ...draft, connection: { ...draft.connection, exchangeSignIn: v } })} options={[{ value: "Browser", label: t("System browser") }, { value: "WindowsAccountManager", label: t("Windows account (WAM)") }, { value: "DeviceCode", label: t("Device code") }]} />}</Field>
            <Field label={t("Default administrator account")}>{(id) => <Input id={id} mono value={draft.connection.defaultAccount ?? ""} onChange={(e) => setDraft({ ...draft, connection: { ...draft.connection, defaultAccount: e.target.value || null } })} />}</Field>
          </div>
        </Card>

        <Card>
          <CardHeader title={t("Execution safeguards")} description={t("How RoomOps paces changes against Microsoft 365 service limits.")} />
          <div className="grid gap-4 border-t border-line p-4 md:grid-cols-3">
            <Field label={t("Pause between objects")} help={t("Applies to live tenants during bulk changes.")}>{(id) => <NumberField id={id} value={draft.execution.pacingMilliseconds} onChange={(v) => setDraft({ ...draft, execution: { ...draft.execution, pacingMilliseconds: v ?? 0 } })} min={0} max={10000} step={100} unit="ms" />}</Field>
            <Field label={t("Retries when throttled")} help={t("Exponential back-off between attempts.")}>{(id) => <NumberField id={id} value={draft.execution.maxThrottleRetries} onChange={(v) => setDraft({ ...draft, execution: { ...draft.execution, maxThrottleRetries: v ?? 0 } })} min={0} max={10} />}</Field>
            <Field label={t("Provisioning wait")} help={t("How long to wait for a new object before saving remaining steps for later.")}>{(id) => <NumberField id={id} value={draft.execution.provisioningWaitSeconds} onChange={(v) => setDraft({ ...draft, execution: { ...draft.execution, provisioningWaitSeconds: v ?? 0 } })} min={0} max={1800} step={30} unit="s" />}</Field>
          </div>
        </Card>

        <Card>
          <CardHeader title={t("Validation baseline")} description={t("Your organization's conventions. These drive the organizational baseline rules — never the technical ones.")} />
          <div className="space-y-4 border-t border-line p-4">
            <Field label={t("Expected time zones")} help={t("Rooms whose working hours use another time zone are reported.")}>
              {() => (
                <div className="space-y-2">
                  <div className="flex flex-wrap gap-1.5">
                    {draft.validation.expectedTimeZones.map((z) => (
                      <Badge key={z} tone="outline" className="h-6 gap-1.5">{z}<button type="button" aria-label={t("Remove {name}", { name: z })} onClick={() => setDraft({ ...draft, validation: { ...draft.validation, expectedTimeZones: draft.validation.expectedTimeZones.filter((x) => x !== z) } })}>×</button></Badge>
                    ))}
                    {draft.validation.expectedTimeZones.length === 0 && <span className="text-[12px] text-ink-3">{t("No expected time zones — the time-zone rule is inactive.")}</span>}
                  </div>
                  <div className="max-w-sm">
                    <Combobox value={zonePick} onChange={(z) => { setZonePick(null); if (!draft.validation.expectedTimeZones.includes(z)) setDraft({ ...draft, validation: { ...draft.validation, expectedTimeZones: [...draft.validation.expectedTimeZones, z] } }); }} options={(zones.data ?? []).map((z) => ({ value: z.id, label: z.id }))} placeholder={t("Add a time zone")} />
                  </div>
                </div>
              )}
            </Field>
            <div>
              <div className="mb-2 text-[12.5px] font-medium">{t("Calendar privacy baseline")}</div>
              <div className="grid gap-2 md:grid-cols-2">
                {(["deleteSubject", "addOrganizerToSubject", "deleteComments", "removePrivateProperty"] as const).map((key) => (
                  <div key={key} className="flex items-center justify-between rounded-md border border-line px-3 py-2">
                    <span className="t-mono text-[11.5px]">{key.charAt(0).toUpperCase() + key.slice(1)}</span>
                    <Segmented size="sm" value={toTri(privacy[key])} onChange={(v) => setDraft({ ...draft, validation: { ...draft.validation, privacy: { ...privacy, [key]: fromTri(v) } } })} options={[{ value: "unset", label: t("Any") }, { value: "true", label: t("On") }, { value: "false", label: t("Off") }]} />
                  </div>
                ))}
              </div>
            </div>
            <div>
              <div className="mb-2 text-[12.5px] font-medium">{t("Baseline rules")}</div>
              <div className="divide-y divide-line rounded-md border border-line">
                {baselineRules.length === 0 && <p className="px-3 py-3 text-[12px] text-ink-3">{t("Connect a tenant or start demo mode to configure baseline rules.")}</p>}
                {baselineRules.map((r) => {
                  const setting = draft.validation.rules[r.id] ?? { enabled: true, severity: null };
                  return (
                    <div key={r.id} className="grid grid-cols-[1fr_180px_auto] items-center gap-3 px-3 py-2">
                      <div><div className="text-[12.5px] font-medium">{r.title}</div><div className="t-mono text-[10.5px] text-ink-3">{r.id}</div></div>
                      <Select value={setting.severity ?? r.defaultSeverity} onChange={(v) => setDraft({ ...draft, validation: { ...draft.validation, rules: { ...draft.validation.rules, [r.id]: { ...setting, severity: v as FindingSeverity } } } })} options={(["Critical", "Warning", "Recommendation", "Informational"] as FindingSeverity[]).map((s) => ({ value: s, label: severityLabel(s) }))} aria-label={`${r.title} severity`} />
                      <Switch checked={setting.enabled} onCheckedChange={(v) => setDraft({ ...draft, validation: { ...draft.validation, rules: { ...draft.validation.rules, [r.id]: { ...setting, enabled: v } } } })} aria-label={t("Enable {rule}", { rule: r.title })} />
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        </Card>

        <Card>
          <CardHeader title={t("Data and privacy")} />
          <div className="space-y-2 border-t border-line p-4 text-[12.5px] text-ink-2">
            <p>{t("Stored on this device: preferences, configuration templates and the local change history (SQLite). Logs omit tokens and redact anything token-like.")}</p>
            <p>{t("Not stored: passwords, access tokens and Microsoft 365 data beyond what a change record needs.")}</p>
            {appInfo && <div className="flex items-center gap-2">{t("Data directory")} <Identifier value={appInfo.dataDirectory} /></div>}
          </div>
        </Card>

        <Card>
          <CardHeader title={t("About")} />
          <dl className="grid grid-cols-[140px_1fr] gap-y-1.5 border-t border-line p-4 text-[12.5px]">
            <dt className="text-ink-3">{t("Version")}</dt><dd>{appInfo?.version}</dd>
            <dt className="text-ink-3">{t("Host")}</dt><dd>{appInfo?.host === "desktop" ? t("Desktop (WebView2)") : appInfo?.host === "devhost" ? t("Development host (browser)") : appInfo?.host}</dd>
            <dt className="text-ink-3">{t("Platform")}</dt><dd>{appInfo?.platform}</dd>
            <dt className="text-ink-3">{t("Runtime")}</dt><dd>{appInfo?.runtime}</dd>
          </dl>
        </Card>
      </div>
    </div>
  );
}
