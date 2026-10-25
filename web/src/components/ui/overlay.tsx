import type { ComponentProps, ReactNode } from "react";
import { ContextMenu as CM, Dialog as D, DropdownMenu as DM, Popover as P, Tooltip as T } from "radix-ui";
import { X } from "lucide-react";
import { cn } from "@/lib/format";
import { t } from "@/lib/i18n";

// ------------------------------------------------------------------------------------------------ Dialog

export const Dialog = D.Root;
export const DialogTrigger = D.Trigger;
export const DialogClose = D.Close;

export function DialogContent({
  title,
  description,
  children,
  className,
  footer,
  wide,
  onInteractOutside,
  hideClose,
}: {
  title: ReactNode;
  description?: ReactNode;
  children: ReactNode;
  className?: string;
  footer?: ReactNode;
  wide?: boolean | "xl";
  onInteractOutside?: ComponentProps<typeof D.Content>["onInteractOutside"];
  hideClose?: boolean;
}) {
  return (
    <D.Portal>
      <D.Overlay className="fixed inset-0 z-50 bg-black/45 backdrop-blur-[2px] data-[state=open]:animate-[fade-in_150ms_ease-out]" />
      <D.Content
        onInteractOutside={onInteractOutside}
        className={cn(
          "fixed left-1/2 top-1/2 z-50 flex max-h-[min(88vh,900px)] w-[calc(100vw-48px)] -translate-x-1/2 -translate-y-1/2 flex-col rounded-xl border border-line bg-surface shadow-pop outline-none",
          "data-[state=open]:animate-[dialog-in_160ms_var(--ease-snap)]",
          wide === "xl" ? "max-w-[1120px]" : wide ? "max-w-[860px]" : "max-w-[520px]",
          className,
        )}
      >
        <div className="flex items-start justify-between gap-4 border-b border-line px-5 py-4">
          <div className="min-w-0">
            <D.Title className="text-[15px] font-semibold text-ink">{title}</D.Title>
            {description ? <D.Description className="t-help mt-1">{description}</D.Description> : <D.Description className="sr-only">{String(title)}</D.Description>}
          </div>
          {!hideClose && (
            <D.Close className="rounded-sm p-1 text-ink-3 hover:bg-hover hover:text-ink" aria-label={t("Close")}>
              <X className="size-4" />
            </D.Close>
          )}
        </div>
        <div className="scroll-area min-h-0 flex-1 px-5 py-4">{children}</div>
        {footer && <div className="flex items-center justify-end gap-2 border-t border-line px-5 py-3">{footer}</div>}
      </D.Content>
    </D.Portal>
  );
}

// ------------------------------------------------------------------------------------------------ Sheet (side panel)

export function SheetContent({ title, description, children, footer, width = 520 }: { title: ReactNode; description?: ReactNode; children: ReactNode; footer?: ReactNode; width?: number }) {
  return (
    <D.Portal>
      <D.Overlay className="fixed inset-0 z-50 bg-black/35" />
      <D.Content
        className="fixed inset-y-0 right-0 z-50 flex max-w-[100vw] flex-col border-l border-line bg-surface shadow-pop outline-none data-[state=open]:animate-[sheet-in_200ms_var(--ease-snap)]"
        style={{ width }}
      >
        <div className="flex items-start justify-between gap-4 border-b border-line px-5 py-4">
          <div>
            <D.Title className="text-[15px] font-semibold">{title}</D.Title>
            {description ? <D.Description className="t-help mt-1">{description}</D.Description> : <D.Description className="sr-only">{String(title)}</D.Description>}
          </div>
          <D.Close className="rounded-sm p-1 text-ink-3 hover:bg-hover hover:text-ink" aria-label={t("Close")}>
            <X className="size-4" />
          </D.Close>
        </div>
        <div className="scroll-area min-h-0 flex-1 px-5 py-4">{children}</div>
        {footer && <div className="flex items-center justify-end gap-2 border-t border-line px-5 py-3">{footer}</div>}
      </D.Content>
    </D.Portal>
  );
}

// ------------------------------------------------------------------------------------------------ Popover

export const Popover = P.Root;
export const PopoverTrigger = P.Trigger;
export const PopoverAnchor = P.Anchor;

export function PopoverContent({ className, align = "start", sideOffset = 6, ...props }: ComponentProps<typeof P.Content>) {
  return (
    <P.Portal>
      <P.Content
        align={align}
        sideOffset={sideOffset}
        className={cn("z-50 rounded-md border border-line bg-raised p-1 shadow-pop outline-none data-[state=open]:animate-[pop-in_120ms_var(--ease-snap)]", className)}
        {...props}
      />
    </P.Portal>
  );
}

// ------------------------------------------------------------------------------------------------ Tooltip

export const TooltipProvider = T.Provider;

export function Tooltip({ content, children, side = "top" }: { content: ReactNode; children: ReactNode; side?: "top" | "right" | "bottom" | "left" }) {
  if (!content) return <>{children}</>;
  return (
    <T.Root>
      <T.Trigger asChild>{children}</T.Trigger>
      <T.Portal>
        <T.Content side={side} sideOffset={6} className="z-[60] max-w-72 rounded-sm border border-line bg-raised px-2 py-1.5 text-[12px] leading-snug text-ink shadow-pop data-[state=delayed-open]:animate-[pop-in_100ms_var(--ease-snap)]">
          {content}
        </T.Content>
      </T.Portal>
    </T.Root>
  );
}

// ------------------------------------------------------------------------------------------------ Menus

const menuContent = "z-50 min-w-48 rounded-md border border-line bg-raised p-1 shadow-pop outline-none data-[state=open]:animate-[pop-in_120ms_var(--ease-snap)]";
const menuItem =
  "flex h-8 cursor-default select-none items-center gap-2 rounded-sm px-2 text-[13px] text-ink outline-none data-[highlighted]:bg-hover data-[disabled]:opacity-40 [&_svg]:size-4 [&_svg]:text-ink-3";

export const DropdownMenu = DM.Root;
export const DropdownMenuTrigger = DM.Trigger;

export function DropdownMenuContent({ className, align = "end", ...props }: ComponentProps<typeof DM.Content>) {
  return (
    <DM.Portal>
      <DM.Content align={align} sideOffset={6} className={cn(menuContent, className)} {...props} />
    </DM.Portal>
  );
}

export function DropdownMenuItem({ className, danger, ...props }: ComponentProps<typeof DM.Item> & { danger?: boolean }) {
  return <DM.Item className={cn(menuItem, danger && "text-bad [&_svg]:text-bad", className)} {...props} />;
}

export function DropdownMenuLabel({ className, ...props }: ComponentProps<typeof DM.Label>) {
  return <DM.Label className={cn("t-eyebrow px-2 pb-1 pt-1.5", className)} {...props} />;
}

export const DropdownMenuSeparator = () => <DM.Separator className="my-1 h-px bg-line" />;

export const ContextMenu = CM.Root;
export const ContextMenuTrigger = CM.Trigger;

export function ContextMenuContent({ className, ...props }: ComponentProps<typeof CM.Content>) {
  return (
    <CM.Portal>
      <CM.Content className={cn(menuContent, className)} {...props} />
    </CM.Portal>
  );
}

export function ContextMenuItem({ className, danger, ...props }: ComponentProps<typeof CM.Item> & { danger?: boolean }) {
  return <CM.Item className={cn(menuItem, danger && "text-bad [&_svg]:text-bad", className)} {...props} />;
}

export const ContextMenuSeparator = () => <CM.Separator className="my-1 h-px bg-line" />;
