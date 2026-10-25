import { forwardRef, useId, type HTMLAttributes, type InputHTMLAttributes, type ReactNode, type TextareaHTMLAttributes } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { Minus, Plus } from "lucide-react";
import { cn } from "@/lib/format";
import { t } from "@/lib/i18n";

// ------------------------------------------------------------------------------------------------ Badge

const badge = cva("inline-flex items-center gap-1 rounded-xs px-1.5 h-[20px] text-[11px] font-medium leading-none whitespace-nowrap [&_svg]:size-3", {
  variants: {
    tone: {
      neutral: "bg-hover text-ink-2",
      accent: "bg-[color-mix(in_srgb,var(--accent)_14%,transparent)] text-accent-text",
      ok: "bg-[color-mix(in_srgb,var(--ok)_15%,transparent)] text-ok",
      warn: "bg-[color-mix(in_srgb,var(--warn)_16%,transparent)] text-warn-ink",
      bad: "bg-[color-mix(in_srgb,var(--bad)_15%,transparent)] text-bad",
      info: "bg-[color-mix(in_srgb,var(--info)_15%,transparent)] text-info",
      demo: "bg-[color-mix(in_srgb,var(--demo)_18%,transparent)] text-demo",
      outline: "border border-line-strong text-ink-2",
    },
  },
  defaultVariants: { tone: "neutral" },
});

export function Badge({ tone, className, ...props }: HTMLAttributes<HTMLSpanElement> & VariantProps<typeof badge>) {
  return <span className={cn(badge({ tone }), className)} {...props} />;
}

export function Dot({ tone = "neutral", pulse, className }: { tone?: "ok" | "warn" | "bad" | "neutral" | "accent" | "demo"; pulse?: boolean; className?: string }) {
  const color = { ok: "bg-ok", warn: "bg-warn", bad: "bg-bad", neutral: "bg-ink-3", accent: "bg-accent", demo: "bg-demo" }[tone];
  return (
    <span className={cn("relative inline-flex size-2 shrink-0", className)} aria-hidden>
      {pulse && <span className={cn("absolute inset-0 rounded-full opacity-60 animate-ping", color)} />}
      <span className={cn("relative inline-flex size-2 rounded-full", color)} />
    </span>
  );
}

// ------------------------------------------------------------------------------------------------ Card

export function Card({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("rounded-lg border border-line bg-surface shadow-card", className)} {...props} />;
}

export function CardHeader({ title, description, actions, className, eyebrow }: { title: ReactNode; description?: ReactNode; actions?: ReactNode; className?: string; eyebrow?: ReactNode }) {
  return (
    <div className={cn("flex items-start justify-between gap-4 px-4 pt-3.5 pb-3", className)}>
      <div className="min-w-0">
        {eyebrow && <div className="t-eyebrow mb-1">{eyebrow}</div>}
        <h3 className="t-section text-ink">{title}</h3>
        {description && <p className="t-help mt-0.5">{description}</p>}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-1.5">{actions}</div>}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ Inputs

const control =
  "w-full rounded-sm border border-line-strong bg-sunken/60 dark:bg-sunken px-2.5 text-[13px] text-ink transition-[border-color,box-shadow] duration-150 hover:border-[color-mix(in_srgb,var(--ink)_28%,transparent)] focus:border-accent focus:outline-none focus:ring-2 focus:ring-[color-mix(in_srgb,var(--accent)_25%,transparent)] disabled:opacity-50 aria-[invalid=true]:border-bad";

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement> & { mono?: boolean; leading?: ReactNode; trailing?: ReactNode }>(
  ({ className, mono, leading, trailing, ...props }, ref) => {
    if (!leading && !trailing) {
      return <input ref={ref} className={cn(control, "h-[var(--ctl-h)]", mono && "t-mono", className)} {...props} />;
    }
    return (
      <div className={cn("relative flex items-center", className)}>
        {leading && <span className="pointer-events-none absolute left-2.5 flex text-ink-3 [&_svg]:size-3.5">{leading}</span>}
        <input ref={ref} className={cn(control, "h-[var(--ctl-h)]", mono && "t-mono", leading && "pl-8", trailing && "pr-10")} {...props} />
        {trailing && <span className="absolute right-2 flex items-center text-ink-3">{trailing}</span>}
      </div>
    );
  },
);
Input.displayName = "Input";

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(({ className, ...props }, ref) => (
  <textarea ref={ref} className={cn(control, "min-h-20 py-2 leading-relaxed", className)} {...props} />
));
Textarea.displayName = "Textarea";

export function NumberField({
  value,
  onChange,
  min = 0,
  max = Number.MAX_SAFE_INTEGER,
  step = 1,
  unit,
  id,
  className,
  invalid,
  disabled,
  "aria-label": ariaLabel,
}: {
  value: number | null;
  onChange: (value: number | null) => void;
  min?: number;
  max?: number;
  step?: number;
  unit?: string;
  id?: string;
  className?: string;
  invalid?: boolean;
  disabled?: boolean;
  "aria-label"?: string;
}) {
  const clamp = (n: number) => Math.min(max, Math.max(min, n));
  return (
    <div className={cn("flex h-[var(--ctl-h)] items-stretch overflow-hidden rounded-sm border border-line-strong bg-sunken/60 dark:bg-sunken focus-within:border-accent focus-within:ring-2 focus-within:ring-[color-mix(in_srgb,var(--accent)_25%,transparent)]", invalid && "border-bad", className)}>
      <button type="button" disabled={disabled} aria-label={t("Decrease")} className="px-2 text-ink-3 hover:bg-hover hover:text-ink disabled:opacity-40" onClick={() => onChange(clamp((value ?? 0) - step))}>
        <Minus className="size-3.5" />
      </button>
      <input
        id={id}
        aria-label={ariaLabel}
        aria-invalid={invalid || undefined}
        disabled={disabled}
        inputMode="numeric"
        className="w-full min-w-0 bg-transparent text-center text-[13px] tabular text-ink outline-none"
        value={value ?? ""}
        onChange={(e) => {
          const raw = e.target.value.replace(/[^\d-]/g, "");
          onChange(raw === "" ? null : Number(raw));
        }}
        onBlur={() => value != null && onChange(clamp(value))}
      />
      {unit && <span className="flex items-center pr-2 text-[12px] text-ink-3">{unit}</span>}
      <button type="button" disabled={disabled} aria-label={t("Increase")} className="px-2 text-ink-3 hover:bg-hover hover:text-ink disabled:opacity-40" onClick={() => onChange(clamp((value ?? 0) + step))}>
        <Plus className="size-3.5" />
      </button>
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ Field

export function Field({
  label,
  help,
  error,
  property,
  children,
  className,
  htmlFor,
  aside,
}: {
  label: ReactNode;
  help?: ReactNode;
  error?: string | null;
  property?: string;
  children: ReactNode | ((id: string) => ReactNode);
  className?: string;
  htmlFor?: string;
  aside?: ReactNode;
}) {
  const generated = useId();
  const id = htmlFor ?? generated;
  return (
    <div className={cn("flex flex-col gap-1.5", className)}>
      <div className="flex flex-wrap items-baseline justify-between gap-x-2">
        <label htmlFor={id} className="text-[12.5px] font-medium text-ink">
          {label}
        </label>
        {property && <span className="t-mono max-w-full truncate text-[10.5px] text-ink-3" title={t("Exchange / Places property")}>{property}</span>}
        {aside}
      </div>
      {typeof children === "function" ? children(id) : children}
      {error ? <p className="text-[12px] text-bad">{error}</p> : help ? <p className="t-help">{help}</p> : null}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ Misc

export function Kbd({ children, className }: { children: ReactNode; className?: string }) {
  return <kbd className={cn("inline-flex h-[18px] min-w-[18px] items-center justify-center rounded-xs border border-line-strong bg-sunken px-1 font-sans text-[10.5px] font-medium text-ink-3", className)}>{children}</kbd>;
}

export function Separator({ className, vertical }: { className?: string; vertical?: boolean }) {
  return <div role="separator" className={cn(vertical ? "w-px self-stretch bg-line" : "h-px w-full bg-line", className)} />;
}

export function Skeleton({ className }: { className?: string }) {
  return (
    <div
      className={cn("rounded-sm bg-[length:200%_100%] animate-[shimmer_1.6s_linear_infinite]", className)}
      style={{ backgroundImage: "linear-gradient(90deg, var(--hover) 25%, color-mix(in srgb, var(--ink) 8%, transparent) 50%, var(--hover) 75%)" }}
    />
  );
}

export function Progress({ value, tone = "accent", className }: { value: number; tone?: "accent" | "ok" | "warn" | "bad"; className?: string }) {
  const color = { accent: "bg-accent", ok: "bg-ok", warn: "bg-warn", bad: "bg-bad" }[tone];
  return (
    <div className={cn("h-1.5 w-full overflow-hidden rounded-full bg-hover", className)} role="progressbar" aria-valuenow={Math.round(value)} aria-valuemin={0} aria-valuemax={100}>
      <div className={cn("h-full rounded-full transition-[width] duration-300 ease-snap", color)} style={{ width: `${Math.min(100, Math.max(0, value))}%` }} />
    </div>
  );
}
