import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { Loader2 } from "lucide-react";
import { cn } from "@/lib/format";

const button = cva(
  "inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-sm font-medium transition-[background,color,box-shadow,border-color] duration-150 ease-snap select-none disabled:pointer-events-none disabled:opacity-45 [&_svg]:size-4 [&_svg]:shrink-0",
  {
    variants: {
      variant: {
        primary: "bg-accent text-accent-ink hover:brightness-110 active:brightness-95 shadow-[0_1px_0_rgb(255_255_255/0.15)_inset]",
        secondary: "bg-surface text-ink border border-line-strong hover:bg-hover hover:border-line-strong",
        ghost: "text-ink-2 hover:text-ink hover:bg-hover",
        danger: "bg-bad text-white hover:brightness-110",
        subtle: "bg-hover text-ink hover:bg-[color-mix(in_srgb,var(--ink)_9%,transparent)]",
        link: "text-accent-text hover:underline underline-offset-2 px-0 h-auto",
      },
      size: {
        sm: "h-7 px-2.5 text-[12px]",
        md: "h-[var(--ctl-h)] px-3 text-[13px]",
        lg: "h-10 px-4 text-[13.5px]",
        icon: "size-[var(--ctl-h)] p-0",
        "icon-sm": "size-7 p-0 [&_svg]:size-3.5",
      },
    },
    defaultVariants: { variant: "secondary", size: "md" },
  },
);

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement>, VariantProps<typeof button> {
  loading?: boolean;
  icon?: ReactNode;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(({ className, variant, size, loading, icon, children, disabled, type = "button", ...props }, ref) => (
  <button ref={ref} type={type} className={cn(button({ variant, size }), className)} disabled={disabled || loading} aria-busy={loading || undefined} {...props}>
    {loading ? <Loader2 className="animate-spin" /> : icon}
    {children}
  </button>
));
Button.displayName = "Button";
