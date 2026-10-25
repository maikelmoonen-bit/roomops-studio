import { useState, type ReactNode } from "react";
import { Checkbox as CB, Collapsible as C, Select as S, Switch as SW, Tabs as TB } from "radix-ui";
import { Command } from "cmdk";
import { Check, ChevronDown, ChevronsUpDown, Minus, Search } from "lucide-react";
import { cn } from "@/lib/format";
import { Popover, PopoverContent, PopoverTrigger } from "./overlay";
import { t } from "@/lib/i18n";

// ------------------------------------------------------------------------------------------------ Switch / Checkbox

export function Switch({ checked, onCheckedChange, disabled, id, "aria-label": label }: { checked: boolean; onCheckedChange: (v: boolean) => void; disabled?: boolean; id?: string; "aria-label"?: string }) {
  return (
    <SW.Root
      id={id}
      aria-label={label}
      checked={checked}
      onCheckedChange={onCheckedChange}
      disabled={disabled}
      className="relative inline-flex h-[18px] w-8 shrink-0 items-center rounded-full border border-line-strong bg-hover transition-colors duration-150 data-[state=checked]:border-accent data-[state=checked]:bg-accent disabled:opacity-45"
    >
      <SW.Thumb className="block size-3.5 translate-x-[1px] rounded-full bg-ink-2 shadow transition-transform duration-150 ease-snap data-[state=checked]:translate-x-[15px] data-[state=checked]:bg-accent-ink" />
    </SW.Root>
  );
}

export function Checkbox({ checked, onCheckedChange, disabled, id, "aria-label": label }: { checked: boolean | "indeterminate"; onCheckedChange: (v: boolean) => void; disabled?: boolean; id?: string; "aria-label"?: string }) {
  return (
    <CB.Root
      id={id}
      aria-label={label}
      checked={checked}
      onCheckedChange={(v) => onCheckedChange(v === true)}
      disabled={disabled}
      onClick={(e) => e.stopPropagation()}
      className="flex size-4 shrink-0 items-center justify-center rounded-[4px] border border-line-strong bg-sunken transition-colors data-[state=checked]:border-accent data-[state=checked]:bg-accent data-[state=indeterminate]:border-accent data-[state=indeterminate]:bg-accent disabled:opacity-45"
    >
      <CB.Indicator className="text-accent-ink">{checked === "indeterminate" ? <Minus className="size-3" strokeWidth={3} /> : <Check className="size-3" strokeWidth={3} />}</CB.Indicator>
    </CB.Root>
  );
}

/** A labelled toggle row used throughout the policy designer. */
export function ToggleRow({ label, description, property, checked, onChange, disabled, badge }: { label: ReactNode; description?: ReactNode; property?: string; checked: boolean; onChange: (v: boolean) => void; disabled?: boolean; badge?: ReactNode }) {
  return (
    <label className={cn("flex items-start justify-between gap-4 rounded-md px-3 py-2.5 transition-colors hover:bg-hover", disabled && "opacity-55")}>
      <span className="min-w-0">
        <span className="flex items-center gap-2 text-[13px] font-medium text-ink">
          {label}
          {badge}
        </span>
        {description && <span className="t-help mt-0.5 block">{description}</span>}
        {property && <span className="t-mono mt-1 block text-[10.5px] text-ink-3">{property}</span>}
      </span>
      <span className="pt-0.5">
        <Switch checked={checked} onCheckedChange={onChange} disabled={disabled} />
      </span>
    </label>
  );
}

// ------------------------------------------------------------------------------------------------ Segmented control

export function Segmented<T extends string>({ value, onChange, options, size = "md", className }: { value: T; onChange: (v: T) => void; options: { value: T; label: ReactNode; icon?: ReactNode }[]; size?: "sm" | "md"; className?: string }) {
  return (
    <div role="radiogroup" className={cn("inline-flex rounded-sm border border-line-strong bg-sunken/60 p-0.5 dark:bg-sunken", className)}>
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={value === o.value}
          onClick={() => onChange(o.value)}
          className={cn(
            "inline-flex items-center gap-1.5 rounded-[5px] px-2.5 font-medium text-ink-2 transition-colors [&_svg]:size-3.5",
            size === "sm" ? "h-6 text-[12px]" : "h-[calc(var(--ctl-h)-6px)] text-[12.5px]",
            value === o.value ? "bg-surface text-ink shadow-[0_1px_2px_rgb(0_0_0/0.15)] dark:bg-raised" : "hover:text-ink",
          )}
        >
          {o.icon}
          {o.label}
        </button>
      ))}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ Select

export function Select<T extends string>({ value, onChange, options, placeholder, className, disabled, id, "aria-label": label }: { value: T | null; onChange: (v: T) => void; options: { value: T; label: ReactNode; hint?: ReactNode }[]; placeholder?: string; className?: string; disabled?: boolean; id?: string; "aria-label"?: string }) {
  return (
    <S.Root value={value ?? undefined} onValueChange={(v) => onChange(v as T)} disabled={disabled}>
      <S.Trigger id={id} aria-label={label} className={cn("flex h-[var(--ctl-h)] w-full items-center justify-between gap-2 rounded-sm border border-line-strong bg-sunken/60 px-2.5 text-left text-[13px] text-ink hover:border-[color-mix(in_srgb,var(--ink)_28%,transparent)] focus:border-accent focus:outline-none data-[placeholder]:text-ink-3 disabled:opacity-50 dark:bg-sunken", className)}>
        <span className="truncate"><S.Value placeholder={placeholder} /></span>
        <S.Icon><ChevronDown className="size-3.5 text-ink-3" /></S.Icon>
      </S.Trigger>
      <S.Portal>
        <S.Content position="popper" sideOffset={4} className="z-50 max-h-80 min-w-[var(--radix-select-trigger-width)] overflow-hidden rounded-md border border-line bg-raised shadow-pop data-[state=open]:animate-[pop-in_120ms_var(--ease-snap)]">
          <S.Viewport className="p-1">
            {options.map((o) => (
              <S.Item key={o.value} value={o.value} className="relative flex cursor-default select-none flex-col rounded-sm py-1.5 pl-7 pr-2 text-[13px] text-ink outline-none data-[highlighted]:bg-hover">
                <S.ItemIndicator className="absolute left-2 top-2"><Check className="size-3.5 text-accent" /></S.ItemIndicator>
                <S.ItemText>{o.label}</S.ItemText>
                {o.hint && <span className="t-help">{o.hint}</span>}
              </S.Item>
            ))}
          </S.Viewport>
        </S.Content>
      </S.Portal>
    </S.Root>
  );
}

// ------------------------------------------------------------------------------------------------ Combobox (searchable select)

export interface ComboOption {
  value: string;
  label: string;
  hint?: string;
  group?: string;
  keywords?: string[];
}

export function Combobox({ value, onChange, options, placeholder = t("Select…"), searchPlaceholder = t("Search…"), emptyText = t("No matches."), className, disabled, id, renderValue }: { value: string | null; onChange: (v: string) => void; options: ComboOption[]; placeholder?: string; searchPlaceholder?: string; emptyText?: string; className?: string; disabled?: boolean; id?: string; renderValue?: (o: ComboOption) => ReactNode }) {
  const [open, setOpen] = useState(false);
  const selected = options.find((o) => o.value === value);
  const groups = [...new Set(options.map((o) => o.group ?? ""))];
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild disabled={disabled}>
        <button id={id} type="button" role="combobox" aria-expanded={open} className={cn("flex h-[var(--ctl-h)] w-full items-center justify-between gap-2 rounded-sm border border-line-strong bg-sunken/60 px-2.5 text-left text-[13px] hover:border-[color-mix(in_srgb,var(--ink)_28%,transparent)] focus:border-accent focus:outline-none disabled:opacity-50 dark:bg-sunken", className)}>
          <span className={cn("truncate", !selected && "text-ink-3")}>{selected ? (renderValue ? renderValue(selected) : selected.label) : placeholder}</span>
          <ChevronsUpDown className="size-3.5 shrink-0 text-ink-3" />
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-[var(--radix-popover-trigger-width)] min-w-64 p-0">
        <Command className="flex flex-col" loop>
          <div className="flex items-center gap-2 border-b border-line px-2.5">
            <Search className="size-3.5 text-ink-3" />
            <Command.Input autoFocus placeholder={searchPlaceholder} className="h-9 w-full bg-transparent text-[13px] outline-none" />
          </div>
          <Command.List className="scroll-area max-h-72 p-1">
            <Command.Empty className="px-2 py-4 text-center text-[12.5px] text-ink-3">{emptyText}</Command.Empty>
            {groups.map((g) => (
              <Command.Group key={g} heading={g || undefined} className="[&_[cmdk-group-heading]]:t-eyebrow [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:pt-2">
                {options.filter((o) => (o.group ?? "") === g).map((o) => (
                  <Command.Item
                    key={o.value}
                    value={`${o.label} ${o.value}`}
                    keywords={o.keywords}
                    onSelect={() => {
                      onChange(o.value);
                      setOpen(false);
                    }}
                    className="flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-[13px] outline-none data-[selected=true]:bg-hover"
                  >
                    <Check className={cn("size-3.5 shrink-0 text-accent", o.value === value ? "opacity-100" : "opacity-0")} />
                    <span className="min-w-0 flex-1 truncate">{o.label}</span>
                    {o.hint && <span className="t-mono shrink-0 truncate text-[11px] text-ink-3">{o.hint}</span>}
                  </Command.Item>
                ))}
              </Command.Group>
            ))}
          </Command.List>
        </Command>
      </PopoverContent>
    </Popover>
  );
}

// ------------------------------------------------------------------------------------------------ Tabs

export const Tabs = TB.Root;
export const TabsContent = TB.Content;

export function TabsList({ tabs, className }: { tabs: { value: string; label: ReactNode; count?: number | null; tone?: "bad" | "warn" }[]; className?: string }) {
  return (
    <TB.List className={cn("flex gap-1 border-b border-line", className)}>
      {tabs.map((t) => (
        <TB.Trigger
          key={t.value}
          value={t.value}
          className="relative -mb-px flex h-9 items-center gap-1.5 border-b-2 border-transparent px-2.5 text-[13px] font-medium text-ink-3 transition-colors hover:text-ink data-[state=active]:border-accent data-[state=active]:text-ink"
        >
          {t.label}
          {t.count != null && t.count > 0 && (
            <span className={cn("rounded-full px-1.5 text-[10.5px] tabular leading-[16px]", t.tone === "bad" ? "bg-[color-mix(in_srgb,var(--bad)_18%,transparent)] text-bad" : t.tone === "warn" ? "bg-[color-mix(in_srgb,var(--warn)_18%,transparent)] text-warn-ink" : "bg-hover text-ink-2")}>{t.count}</span>
          )}
        </TB.Trigger>
      ))}
    </TB.List>
  );
}

// ------------------------------------------------------------------------------------------------ Collapsible

export function Disclosure({ title, description, defaultOpen, open: controlledOpen, onOpenChange, children, badge, id }: { title: ReactNode; description?: ReactNode; defaultOpen?: boolean; open?: boolean; onOpenChange?: (open: boolean) => void; children: ReactNode; badge?: ReactNode; id?: string }) {
  const [uncontrolledOpen, setUncontrolledOpen] = useState(!!defaultOpen);
  const open = controlledOpen ?? uncontrolledOpen;
  const setOpen = onOpenChange ?? setUncontrolledOpen;
  return (
    <C.Root id={id} open={open} onOpenChange={setOpen} className="scroll-mt-4 rounded-lg border border-line bg-surface">
      <C.Trigger className="flex w-full items-center justify-between gap-3 px-4 py-3 text-left">
        <span>
          <span className="flex items-center gap-2 t-section">{title}{badge}</span>
          {description && <span className="t-help mt-0.5 block">{description}</span>}
        </span>
        <ChevronDown className={cn("size-4 shrink-0 text-ink-3 transition-transform duration-200", open && "rotate-180")} />
      </C.Trigger>
      <C.Content className="overflow-hidden border-t border-line data-[state=closed]:animate-[collapse-up_160ms_ease-out] data-[state=open]:animate-[collapse-down_180ms_var(--ease-snap)]">
        <div className="p-2">{children}</div>
      </C.Content>
    </C.Root>
  );
}
