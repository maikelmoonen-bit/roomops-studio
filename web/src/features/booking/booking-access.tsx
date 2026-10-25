import type { ReactNode } from "react";
import { AlertTriangle, CheckCircle2, CircleDot, ShieldCheck, SlidersHorizontal, UserCheck, Users } from "lucide-react";
import type { BookingPolicy, BookingPreset, PolicyFieldInfo, RecipientRef } from "@/bridge/contracts.gen";
import { t, tn, tx } from "@/lib/i18n";
import { cn } from "@/lib/format";
import { Badge, Card } from "@/components/ui/core";
import { InlineNotice } from "@/components/common/domain";
import { RecipientPicker } from "@/components/common/recipient-picker";
import { formatValue, getField } from "./policy-utils";

/** Parameters an access model sets or clears. Changes to these are what a model selection means. */
export const accessFields = [
  "AutomateProcessing", "BookingType", "AllBookInPolicy", "BookInPolicy", "AllRequestInPolicy", "RequestInPolicy",
  "AllRequestOutOfPolicy", "RequestOutOfPolicy", "ResourceDelegates", "ForwardRequestsToDelegates", "TentativePendingApproval",
];

/** Required inputs of the chosen model that are still empty. */
export function missingInputs(preset: BookingPreset | undefined, policy: BookingPolicy): string[] {
  return (preset?.requiredInputs ?? []).filter((name) => ((getField(policy, name) as RecipientRef[] | undefined) ?? []).length === 0);
}

const requiredInputMeta: Record<string, { label: string; help: string; icon: ReactNode; placeholder: string; missing: string }> = {
  BookInPolicy: {
    label: tx("Can book automatically"),
    help: tx("People or groups whose requests are accepted without approval. Only direct members of a listed group count."),
    icon: <UserCheck />,
    placeholder: tx("Add people or groups"),
    missing: tx("Add at least one person or group who can book automatically."),
  },
  ResourceDelegates: {
    label: tx("Approve requests (booking delegates)"),
    help: tx("Delegates receive requests that need approval and accept or decline them. This doesn't give access to the mailbox."),
    icon: <ShieldCheck />,
    placeholder: tx("Add delegates"),
    missing: tx("Add at least one delegate to approve requests."),
  },
};

function names(list: RecipientRef[]) {
  return list.map((r) => r.displayName ?? r.primarySmtpAddress ?? r.identity).join(", ");
}

interface SummaryRow {
  label: string;
  value: string;
  detail?: string;
  tone?: "ok" | "warn" | "bad";
}

/** The effective access configuration of a draft, in terms an administrator recognizes. Computed from the draft on every edit. */
export function accessSummary(p: BookingPolicy): SummaryRow[] {
  if (p.automateProcessing !== "AutoAccept") {
    return [{ label: t("Booking requests"), value: p.automateProcessing === "None" ? t("Not processed automatically") : t("Kept tentative, never accepted automatically"), tone: "warn" }];
  }
  if (p.bookingType === "Reserved") {
    return [{ label: t("Booking requests"), value: t("The room is reserved and can't be booked"), tone: "warn" }];
  }
  const requestsNeedApproval = !p.allBookInPolicy && (p.allRequestInPolicy || p.requestInPolicy.length > 0);
  const outOfPolicyApproval = p.allRequestOutOfPolicy || p.requestOutOfPolicy.length > 0;

  const allowed: SummaryRow = p.allBookInPolicy
    ? { label: t("Allowed"), value: t("Everyone"), tone: "ok" }
    : p.bookInPolicy.length > 0
      ? { label: t("Allowed"), value: tn(p.bookInPolicy.length, "{n} selected user or group", "{n} selected users or groups"), detail: names(p.bookInPolicy) }
      : { label: t("Allowed"), value: t("Nobody books automatically"), tone: p.allRequestInPolicy || p.requestInPolicy.length > 0 ? undefined : "warn" };

  const others: SummaryRow = p.allBookInPolicy
    ? { label: t("Other users"), value: t("Not applicable — everyone can book") }
    : p.allRequestInPolicy
      ? { label: t("Other users"), value: t("Can request, a delegate approves") }
      : p.requestInPolicy.length > 0
        ? { label: t("Other users"), value: t("Booking denied, except listed requesters"), detail: names(p.requestInPolicy) }
        : { label: t("Other users"), value: t("Booking denied") };

  const approval: SummaryRow = requestsNeedApproval
    ? { label: t("Approval required"), value: p.bookInPolicy.length > 0 && p.allRequestInPolicy ? t("Yes, for users who aren't selected") : p.allRequestInPolicy ? t("Yes, for every request") : t("Yes, for listed requesters") }
    : outOfPolicyApproval
      ? { label: t("Approval required"), value: t("Only for requests outside the booking limits") }
      : { label: t("Approval required"), value: t("No") };

  const needsDelegates = requestsNeedApproval || outOfPolicyApproval;
  const delegates: SummaryRow = p.resourceDelegates.length > 0
    ? { label: t("Booking delegates"), value: tn(p.resourceDelegates.length, "{n} delegate", "{n} delegates"), detail: names(p.resourceDelegates), tone: needsDelegates ? undefined : "warn" }
    : { label: t("Booking delegates"), value: needsDelegates ? t("None — requests can't be approved") : t("None"), tone: needsDelegates ? "bad" : undefined };
  if (p.resourceDelegates.length > 0 && !needsDelegates) delegates.detail = `${delegates.detail} · ${t("not used: no request needs approval")}`;

  const outside: SummaryRow = {
    label: t("Outside booking limits"),
    value: p.allRequestOutOfPolicy ? t("Anyone can request, a delegate approves") : p.requestOutOfPolicy.length > 0 ? t("Listed users can request, a delegate approves") : t("Declined"),
    detail: !p.allRequestOutOfPolicy && p.requestOutOfPolicy.length > 0 ? names(p.requestOutOfPolicy) : undefined,
  };
  return [allowed, others, approval, delegates, outside];
}

export function BookingAccessPanel({
  presets,
  activeId,
  originalId,
  draft,
  original,
  fieldInfo,
  onChoose,
  onInput,
}: {
  presets: BookingPreset[];
  /** The model shown as selected: the one the administrator chose, or the one the draft matches. Null for a custom configuration. */
  activeId: string | null;
  /** The model the room's current configuration matches. Null when it is custom. */
  originalId: string | null;
  draft: BookingPolicy;
  original: BookingPolicy;
  fieldInfo: (name: string) => PolicyFieldInfo | undefined;
  onChoose: (preset: BookingPreset) => void;
  onInput: (name: string, value: RecipientRef[]) => void;
}) {
  const active = presets.find((p) => p.id === activeId);
  const changed = accessFields
    .map((name) => fieldInfo(name))
    .filter((f): f is PolicyFieldInfo => !!f && formatValue(f, getField(original, f.name)) !== formatValue(f, getField(draft, f.name)));
  const replacesCustom = originalId === null && changed.length > 0;
  const summary = accessSummary(draft);

  return (
    <Card id="section-access" className="scroll-mt-4">
      <div className="flex items-start gap-3 px-4 pb-2 pt-3.5">
        <span className="mt-0.5 text-ink-3 [&_svg]:size-4"><Users /></span>
        <div className="min-w-0 flex-1">
          <h3 className="t-section">{t("Booking access")}</h3>
          <p className="t-help mt-0.5">{t("Choose who can book this room. RoomOps sets the matching Exchange permissions, delegates and forwarding for you; booking limits and other settings stay as they are.")}</p>
        </div>
        {activeId === null && <Badge tone="info"><SlidersHorizontal className="size-3" />{t("Custom configuration")}</Badge>}
      </div>

      <div role="radiogroup" aria-label={t("Booking access model")} className="grid gap-2 px-4 pb-3 md:grid-cols-2 2xl:grid-cols-4">
        {presets.map((p) => {
          const selected = p.id === activeId;
          return (
            <button
              key={p.id}
              type="button"
              role="radio"
              aria-checked={selected}
              data-testid={`access-${p.id}`}
              onClick={() => onChoose(p)}
              className={cn("flex flex-col justify-start rounded-lg border p-3 text-left transition-colors", selected ? "border-accent bg-[color-mix(in_srgb,var(--accent)_8%,transparent)]" : "border-line bg-surface hover:border-line-strong")}
            >
              <span className="flex items-start justify-between gap-2 text-[13px] font-semibold text-ink">
                <span className="flex items-start gap-2">
                  <CircleDot className={cn("mt-0.5 size-3.5 shrink-0", selected ? "text-accent" : "text-ink-3/50")} />
                  {p.name}
                </span>
                {p.id === originalId && <Badge tone="outline">{t("Current")}</Badge>}
              </span>
              <span className="mt-1 pl-5.5 text-[11.5px] leading-snug text-ink-3">{p.description}</span>
            </button>
          );
        })}
      </div>

      {activeId === null && (
        <InlineNotice tone="info" className="mx-4 mb-3" title={t("Custom configuration")}>
          {t("The current permissions don't match one of these models, so nothing has been changed. Choose a model to replace them, or edit each parameter under Advanced booking configuration.")}
        </InlineNotice>
      )}

      <div className="grid gap-4 border-t border-line px-4 py-3.5 lg:grid-cols-[minmax(0,1fr)_300px]">
        <div className="min-w-0 space-y-3">
          {(active?.requiredInputs ?? []).map((name) => {
            const meta = requiredInputMeta[name];
            if (!meta) return null;
            const value = (getField(draft, name) as RecipientRef[]) ?? [];
            return (
              <div key={name} id={`access-${name}`}>
                <div className="flex items-center gap-2 text-[13px] font-medium text-ink"><span className="text-ink-3 [&_svg]:size-4">{meta.icon}</span>{t(meta.label)}</div>
                <p className="t-help mb-1.5 mt-0.5">{t(meta.help)}</p>
                <RecipientPicker value={value} onChange={(v) => onInput(name, v)} placeholder={t(meta.placeholder)} />
                {value.length === 0 && <p className="mt-1 text-[11.5px] text-bad">{t(meta.missing)}</p>}
              </div>
            );
          })}
          {active && active.requiredInputs.length === 0 && (
            <p className="flex items-center gap-2 text-[12.5px] text-ink-2"><CheckCircle2 className="size-4 text-ok" />{t("Nothing else to fill in: everyone can book within the booking limits.")}</p>
          )}
          {changed.length > 0 && (
            <div className={cn("rounded-md border px-3 py-2.5", replacesCustom ? "border-[color-mix(in_srgb,var(--warn)_40%,transparent)]" : "border-line")}>
              <div className="flex items-center gap-2 text-[12.5px] font-medium text-ink">
                {replacesCustom && <AlertTriangle className="size-3.5 text-warn-ink" />}
                {replacesCustom ? t("These existing settings will be replaced") : t("Settings this changes")}
              </div>
              <ul className="mt-1.5 space-y-1">
                {changed.map((f) => (
                  <li key={f.name} className="text-[11.5px] text-ink-3">
                    <span className="text-ink-2">{f.label}</span> <span className="t-mono text-[10.5px]">({f.name})</span>:{" "}
                    <span className="line-through">{formatValue(f, getField(original, f.name))}</span> → <span className="text-ink-2">{formatValue(f, getField(draft, f.name))}</span>
                  </li>
                ))}
              </ul>
              <p className="t-help mt-1.5">{t("Nothing is written until you preview and confirm the change.")}</p>
            </div>
          )}
        </div>

        <div className="self-start rounded-md border border-line bg-sunken/50 p-3" data-testid="access-summary">
          <div className="t-eyebrow mb-2">{t("Booking policy summary")}</div>
          <dl className="space-y-2">
            {summary.map((row) => (
              <div key={row.label}>
                <dt className="text-[11px] text-ink-3">{row.label}</dt>
                <dd className={cn("text-[12.5px] font-medium", row.tone === "bad" ? "text-bad" : row.tone === "warn" ? "text-warn-ink" : row.tone === "ok" ? "text-ok" : "text-ink")}>{row.value}</dd>
                {row.detail && <dd className="truncate text-[11px] text-ink-3" title={row.detail}>{row.detail}</dd>}
              </div>
            ))}
          </dl>
        </div>
      </div>
    </Card>
  );
}
