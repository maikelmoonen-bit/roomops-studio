import { useEffect, useState } from "react";
import { Command } from "cmdk";
import { AlertTriangle, CircleHelp, Loader2, Plus, User, Users, X } from "lucide-react";
import { bridge } from "@/bridge/client";
import type { Recipient, RecipientRef } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { Popover, PopoverContent, PopoverTrigger, Tooltip } from "@/components/ui/overlay";
import { t } from "@/lib/i18n";

const isGroup = (type: string | null | undefined) => !!type && (/Group/.test(type) || type === "RoomList");

export function toRef(r: Recipient): RecipientRef {
  return { identity: r.primarySmtpAddress, displayName: r.displayName, primarySmtpAddress: r.primarySmtpAddress, recipientTypeDetails: r.recipientTypeDetails, resolution: "Resolved" };
}

export const refKey = (r: RecipientRef) => (r.primarySmtpAddress ?? r.identity).toLowerCase();

function Chip({ value, onRemove, disabled }: { value: RecipientRef; onRemove: () => void; disabled?: boolean }) {
  const problem = value.resolution === "Unresolved" ? t("This reference no longer resolves to a recipient.") : value.resolution === "Ambiguous" ? t("This name matches more than one recipient.") : value.recipientTypeDetails === "DynamicDistributionGroup" ? t("Dynamic distribution groups aren't supported here.") : null;
  const group = isGroup(value.recipientTypeDetails);
  return (
    <Tooltip content={problem ?? (group ? t("Group — members of nested groups are not honored by Exchange") : value.primarySmtpAddress ?? value.identity)}>
      <span className={cn("inline-flex h-6 max-w-full items-center gap-1.5 rounded-full border pl-1.5 pr-1 text-[12px]", problem ? "border-[color-mix(in_srgb,var(--bad)_45%,transparent)] bg-[color-mix(in_srgb,var(--bad)_8%,transparent)] text-ink" : "border-line-strong bg-surface text-ink")}>
        {problem ? <AlertTriangle className="size-3 shrink-0 text-bad" /> : group ? <Users className="size-3 shrink-0 text-ink-3" /> : <User className="size-3 shrink-0 text-ink-3" />}
        <span className="truncate">{value.displayName ?? value.identity}</span>
        {!disabled && (
          <button type="button" onClick={onRemove} aria-label={t("Remove {name}", { name: value.displayName ?? value.identity })} className="rounded-full p-0.5 text-ink-3 hover:bg-hover hover:text-ink">
            <X className="size-3" />
          </button>
        )}
      </span>
    </Tooltip>
  );
}

/**
 * Multi-recipient picker backed by Exchange ambiguous-name resolution. Unresolved and ambiguous references from the current
 * configuration stay visible (in red) so the administrator decides what happens to them.
 */
export function RecipientPicker({ value, onChange, placeholder = t("Add people or groups"), disabled, types, allowGroups = true }: { value: RecipientRef[]; onChange: (v: RecipientRef[]) => void; placeholder?: string; disabled?: boolean; types?: string[]; allowGroups?: boolean }) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<Recipient[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (query.trim().length < 2) {
      setResults([]);
      return;
    }
    setLoading(true);
    setError(null);
    const handle = window.setTimeout(() => {
      bridge
        .call("recipients.search", { query, recipientTypeDetails: types ?? null })
        .then((r) => setResults(allowGroups ? r : r.filter((x) => !isGroup(x.recipientTypeDetails))))
        .catch((e: unknown) => setError(e instanceof Error ? e.message : t("Search failed")))
        .finally(() => setLoading(false));
    }, 250);
    return () => window.clearTimeout(handle);
  }, [query, types, allowGroups]);

  const keys = new Set(value.map(refKey));

  return (
    <div className={cn("flex min-h-[var(--ctl-h)] flex-wrap items-center gap-1 rounded-sm border border-line-strong bg-sunken/60 p-1 dark:bg-sunken", disabled && "opacity-50")}>
      {value.map((r) => <Chip key={refKey(r)} value={r} disabled={disabled} onRemove={() => onChange(value.filter((x) => refKey(x) !== refKey(r)))} />)}
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild disabled={disabled}>
          <button type="button" className="inline-flex h-6 items-center gap-1 rounded-full px-2 text-[12px] text-ink-3 hover:bg-hover hover:text-ink">
            <Plus className="size-3" />{value.length === 0 ? placeholder : t("Add")}
          </button>
        </PopoverTrigger>
        <PopoverContent className="w-80 p-0">
          <Command shouldFilter={false}>
            <div className="flex items-center gap-2 border-b border-line px-2.5">
              {loading ? <Loader2 className="size-3.5 animate-spin text-ink-3" /> : <User className="size-3.5 text-ink-3" />}
              <Command.Input autoFocus value={query} onValueChange={setQuery} placeholder={t("Search by name or address…")} className="h-9 w-full bg-transparent text-[13px] outline-none" />
            </div>
            <Command.List className="scroll-area max-h-64 p-1">
              {query.trim().length < 2 && <p className="px-2 py-3 text-[12px] text-ink-3">{t("Type at least two characters. Search uses Exchange ambiguous name resolution.")}</p>}
              {error && <p className="px-2 py-3 text-[12px] text-bad">{error}</p>}
              {query.trim().length >= 2 && !loading && !error && results.length === 0 && <p className="px-2 py-3 text-[12px] text-ink-3">{t("No recipients found.")}</p>}
              {results.map((r) => {
                const added = keys.has(r.primarySmtpAddress.toLowerCase());
                const unsupported = r.recipientTypeDetails === "DynamicDistributionGroup";
                return (
                  <Command.Item
                    key={r.id}
                    value={r.id}
                    disabled={added || unsupported}
                    onSelect={() => { onChange([...value, toRef(r)]); setQuery(""); setOpen(false); }}
                    className="flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-[13px] outline-none data-[disabled=true]:opacity-45 data-[selected=true]:bg-hover"
                  >
                    {isGroup(r.recipientTypeDetails) ? <Users className="size-3.5 text-ink-3" /> : <User className="size-3.5 text-ink-3" />}
                    <span className="min-w-0 flex-1">
                      <span className="block truncate">{r.displayName}</span>
                      <span className="t-mono block truncate text-[10.5px] text-ink-3">{r.primarySmtpAddress}</span>
                    </span>
                    <span className="shrink-0 text-[10.5px] text-ink-3">{unsupported ? t("Not supported") : added ? t("Added") : r.recipientTypeDetails.replace("Mailbox", "")}</span>
                  </Command.Item>
                );
              })}
            </Command.List>
          </Command>
        </PopoverContent>
      </Popover>
      {value.some((v) => v.resolution === "NotChecked") && <Tooltip content={t("Some entries have not been resolved yet.")}><CircleHelp className="size-3.5 text-ink-3" /></Tooltip>}
    </div>
  );
}
