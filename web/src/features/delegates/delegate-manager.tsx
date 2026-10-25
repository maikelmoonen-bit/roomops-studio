import { useEffect, useState } from "react";
import { FolderLock, KeyRound, Mail, Send, ShieldCheck, Trash2, UserPlus, Wand2 } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { MailboxAccessEntry, RecipientRef } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Card, CardHeader, Skeleton } from "@/components/ui/core";
import { Checkbox, Select, Switch } from "@/components/ui/controls";
import { ErrorState, Identifier, InlineNotice } from "@/components/common/domain";
import { RecipientPicker, refKey } from "@/components/common/recipient-picker";
import { Tooltip } from "@/components/ui/overlay";
import { t, tn } from "@/lib/i18n";

const FOLDER_ROLES = ["None", "AvailabilityOnly", "LimitedDetails", "Editor"];
// Default covers everyone in the organization and Anonymous everyone outside it; neither is offered edit rights.
const BUILT_IN_FOLDER_ROLES = ["None", "AvailabilityOnly", "LimitedDetails"];

function Mechanism({ number, title, description, icon, editable, children }: { number: string; title: string; description: string; icon: React.ReactNode; editable: boolean; children: React.ReactNode }) {
  return (
    <Card>
      <CardHeader
        eyebrow={<span className="flex items-center gap-1.5">{number} · {editable ? t("Managed here") : t("View only")}</span>}
        title={<span className="flex items-center gap-2"><span className="text-ink-3 [&_svg]:size-4">{icon}</span>{title}</span>}
        description={description}
      />
      <div className="border-t border-line px-4 py-3">{children}</div>
    </Card>
  );
}

function Trustee({ entry }: { entry: MailboxAccessEntry }) {
  return (
    <span className="min-w-0">
      <span className="block truncate">{entry.displayName ?? entry.trustee}</span>
      {entry.displayName && <span className="t-mono block truncate text-[10.5px] text-ink-3">{entry.trustee}</span>}
    </span>
  );
}

/**
 * Booking delegates, calendar folder permissions, Full Access, Send As and Send on Behalf are five separate mechanisms.
 * Only the first two are edited here; RoomOps never grants mailbox access as a side effect of booking delegation.
 */
export function DelegateManager({ roomId }: { roomId: string }) {
  const overview = useBridgeQuery("delegates.get", { roomId, refresh: false });
  const { review } = usePlanFlow();
  const [delegates, setDelegates] = useState<RecipientRef[]>([]);
  const [forward, setForward] = useState(true);
  const [grantUser, setGrantUser] = useState<RecipientRef[]>([]);
  const [grantRole, setGrantRole] = useState("Editor");
  const [grantDelegate, setGrantDelegate] = useState(true);

  useEffect(() => {
    if (overview.data) {
      setDelegates(overview.data.bookingDelegates);
      setForward(overview.data.forwardRequestsToDelegates);
    }
  }, [overview.data]);

  if (overview.isLoading) return <div className="space-y-3"><Skeleton className="h-40" /><Skeleton className="h-40" /></div>;
  if (overview.error || !overview.data) return <Card><ErrorState error={overview.error} onRetry={() => void overview.refetch()} /></Card>;
  const data = overview.data;
  const refresh = () => void overview.refetch();

  const before = data.bookingDelegates.map(refKey).sort().join("|");
  const changed = delegates.map(refKey).sort().join("|") !== before || forward !== data.forwardRequestsToDelegates;
  const unresolved = delegates.filter((d) => d.resolution === "Unresolved" || d.resolution === "Ambiguous");

  // Full Access trustees come back as UPNs and folder permissions as display names; the backend resolves both forms.
  const fullAccessKeys = new Set(data.fullAccess.items.flatMap((e) => [e.trustee, e.displayName, e.primarySmtpAddress]).filter((k): k is string => !!k).map((k) => k.toLowerCase()));
  const hasFullAccess = (user: string, smtp: string | null) => fullAccessKeys.has(user.toLowerCase()) || (!!smtp && fullAccessKeys.has(smtp.toLowerCase()));
  const overlapping = data.folderPermissions.items.filter((p) => hasFullAccess(p.user, p.userSmtp)).length;

  return (
    <div className="space-y-3">
      <InlineNotice tone="info" icon={<ShieldCheck />}>
        {t("These are five independent permission mechanisms. Assigning a booking delegate does not grant Full Access, Send As or folder permissions — each is shown and changed separately.")}</InlineNotice>

      <Mechanism number="1" editable title={t("Resource booking delegates")} icon={<UserPlus />} description={t("People who approve or decline requests for this room (Set-CalendarProcessing -ResourceDelegates).")}>
        <p className="mb-2 text-[12.5px] text-ink-2">{data.approvalSummary}</p>
        <RecipientPicker value={delegates} onChange={setDelegates} placeholder={t("Add booking delegates")} />
        {unresolved.length > 0 && (
          <InlineNotice tone="bad" className="mt-2" action={<Button size="sm" onClick={() => setDelegates(delegates.filter((d) => !unresolved.includes(d)))}>{t("Remove")}{" "}{unresolved.length}</Button>}>
            {tn(unresolved.length, "{n} delegate reference no longer resolves to a single recipient. Exchange rejects updates to this list until it is removed.", "{n} delegate references no longer resolve to a single recipient. Exchange rejects updates to this list until they are removed.")}</InlineNotice>
        )}
        <label className="mt-3 flex items-center justify-between gap-4 text-[12.5px]">
          <span><span className="font-medium">{t("Forward requests to delegates")}</span><span className="t-mono ml-2 text-[10.5px] text-ink-3">ForwardRequestsToDelegates</span></span>
          <Switch checked={forward} onCheckedChange={setForward} />
        </label>
        <div className="mt-3 flex justify-end">
          <Button variant="primary" icon={<Wand2 />} disabled={!changed} onClick={() => review({ kind: "updateBookingPolicy", roomId, policy: { ...data.policy, resourceDelegates: delegates, forwardRequestsToDelegates: forward } }, { onCompleted: refresh })}>
            {t("Preview changes")}</Button>
        </div>
      </Mechanism>

      <Mechanism number="2" editable title={t("Calendar folder permissions")} icon={<FolderLock />} description={t("Who can see or edit the room's calendar. The folder name is read from the mailbox, so localized names such as “Agenda” are handled.")}>
        {data.calendarFolder && <div className="mb-2 flex items-center gap-2 text-[12px] text-ink-3">{t("Folder")}{" "}<Identifier value={data.calendarFolder.identity} /></div>}
        {data.folderPermissions.error ? <p className="text-[12.5px] text-bad">{data.folderPermissions.error}</p> : (
          <table className="w-full text-[12.5px]">
            <thead><tr className="text-left text-[11.5px] text-ink-3"><th className="py-1 font-medium">{t("User")}</th><th className="py-1 font-medium">{t("Access rights")}</th><th className="py-1 font-medium">{t("Flags")}</th><th /></tr></thead>
            <tbody>
              {data.folderPermissions.items.map((p) => {
                const builtIn = p.user === "Default" || p.user === "Anonymous";
                const current = p.accessRights.join(", ");
                const roles = builtIn ? BUILT_IN_FOLDER_ROLES : FOLDER_ROLES;
                const isDelegate = p.sharingPermissionFlags.includes("Delegate");
                const fullAccess = hasFullAccess(p.user, p.userSmtp);
                return (
                  <tr key={p.user} className="border-t border-line">
                    <td className="py-1.5">{p.user}{p.userSmtp && <span className="t-mono ml-2 text-[10.5px] text-ink-3">{p.userSmtp}</span>}</td>
                    <td className="py-1">
                      <Select
                        value={current || null}
                        onChange={(role) => {
                          if (role !== current) review({ kind: "setFolderPermission", roomId, user: p.userSmtp ?? p.user, action: "Grant", accessRights: role, delegateFlag: isDelegate && role === "Editor" }, { onCompleted: refresh });
                        }}
                        options={(current && !roles.includes(current) ? [current, ...roles] : roles).map((r) => ({ value: r, label: r }))}
                        aria-label={t("Access rights for {name}", { name: p.user })}
                        className="h-7 w-44 text-[12.5px]"
                      />
                    </td>
                    <td className="py-1.5">
                      {p.sharingPermissionFlags.length || fullAccess ? (
                        <span className="flex flex-wrap gap-1">
                          {p.sharingPermissionFlags.map((f) => <Badge key={f} tone="accent">{f}</Badge>)}
                          {fullAccess && (
                            <Tooltip content={t("Also has Full Access to the room mailbox, which includes full edit rights on this calendar whatever the role shown here.")}>
                              <span><Badge tone="warn">{t("Full Access")}</Badge></span>
                            </Tooltip>
                          )}
                        </span>
                      ) : <span className="text-ink-3">—</span>}
                    </td>
                    <td className="py-1.5 text-right">
                      {!builtIn && (
                        <Button size="icon-sm" variant="ghost" aria-label={t("Remove {name}", { name: p.user })} onClick={() => review({ kind: "setFolderPermission", roomId, user: p.userSmtp ?? p.user, action: "Remove", accessRights: p.accessRights[0] ?? "Reviewer", delegateFlag: false }, { onCompleted: refresh })}>
                          <Trash2 />
                        </Button>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
        {overlapping > 0 && (
          <InlineNotice tone="warn" className="mt-3">
            {tn(overlapping, "{n} person in this list also has Full Access to the room mailbox (section 3). Full Access opens the whole mailbox, including this calendar with full edit rights, so the access rights shown here don't limit them.", "{n} people in this list also have Full Access to the room mailbox (section 3). Full Access opens the whole mailbox, including this calendar with full edit rights, so the access rights shown here don't limit them.")}</InlineNotice>
        )}
        <div className="mt-3 grid items-end gap-2 rounded-md border border-dashed border-line-strong p-3 md:grid-cols-[1fr_140px_auto_auto]">
          <div>
            <div className="mb-1 text-[12px] font-medium">{t("Grant access")}</div>
            <RecipientPicker value={grantUser} onChange={(v) => setGrantUser(v.slice(-1))} placeholder={t("Choose a person")} allowGroups={false} />
          </div>
          <Select value={grantRole} onChange={setGrantRole} aria-label={t("Access role")} options={["Reviewer", "LimitedDetails", "AvailabilityOnly", "Author", "Editor"].map((r) => ({ value: r, label: r }))} />
          <label className={cn("flex h-[var(--ctl-h)] items-center gap-2 text-[12px]", grantRole !== "Editor" && "opacity-50")}>
            <Checkbox checked={grantDelegate && grantRole === "Editor"} disabled={grantRole !== "Editor"} onCheckedChange={setGrantDelegate} />{t("Delegate flag")}</label>
          <Button disabled={grantUser.length === 0} onClick={() => review({ kind: "setFolderPermission", roomId, user: grantUser[0].primarySmtpAddress!, action: "Grant", accessRights: grantRole, delegateFlag: grantDelegate && grantRole === "Editor" }, { onCompleted: () => { setGrantUser([]); refresh(); } })}>
            {t("Preview")}</Button>
        </div>
      </Mechanism>

      <div className="px-1 pt-2">
        <h3 className="t-section text-ink">{t("Access to the room mailbox itself")}</h3>
        <p className="t-help mt-0.5">{t("The next three apply to the whole room mailbox, not just its calendar: they list the people who can open it or send mail as the room. RoomOps shows them so unexpected access stands out, but doesn't change them. Use the Exchange admin center (mailbox delegation) or PowerShell for that.")}</p>
      </div>
      <div className="grid gap-3 lg:grid-cols-3">
        <Mechanism number="3" editable={false} title={t("Full Access")} icon={<KeyRound />} description={t("Can open the entire room mailbox, including this calendar with full edit rights, regardless of the folder permissions above. Not needed for booking delegation.")}>
          {data.fullAccess.error ? <p className="text-[12px] text-bad">{data.fullAccess.error}</p> : data.fullAccess.items.length === 0 ? <p className="text-[12.5px] text-ink-3">{t("No explicit Full Access entries.")}</p> : (
            <ul className="space-y-1.5 text-[12.5px]">{data.fullAccess.items.map((e) => <li key={e.trustee} className="flex items-start justify-between gap-2"><Trustee entry={e} />{e.isInherited && <Badge tone="neutral">{t("Inherited")}</Badge>}</li>)}</ul>
          )}
        </Mechanism>
        <Mechanism number="4" editable={false} title={t("Send As")} icon={<Mail />} description={t("Can send mail that appears to come from the room itself.")}>
          {data.sendAs.error ? <p className="text-[12px] text-bad">{data.sendAs.error}</p> : data.sendAs.items.length === 0 ? <p className="text-[12.5px] text-ink-3">{t("No Send As entries besides the mailbox itself.")}</p> : (
            <ul className="space-y-1.5 text-[12.5px]">{data.sendAs.items.map((e) => <li key={e.trustee}><Trustee entry={e} /></li>)}</ul>
          )}
        </Mechanism>
        <Mechanism number="5" editable={false} title={t("Send on Behalf")} icon={<Send />} description={t("Can send mail on behalf of the room; recipients see both names (GrantSendOnBehalfTo).")}>
          {data.sendOnBehalf.error ? <p className="text-[12px] text-bad">{data.sendOnBehalf.error}</p> : data.sendOnBehalf.items.length === 0 ? <p className="text-[12.5px] text-ink-3">{t("No Send on Behalf entries.")}</p> : (
            <ul className="space-y-1 text-[12.5px]">{data.sendOnBehalf.items.map((e) => <li key={e}>{e}</li>)}</ul>
          )}
        </Mechanism>
      </div>
    </div>
  );
}
