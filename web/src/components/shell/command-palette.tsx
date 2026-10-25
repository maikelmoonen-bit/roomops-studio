import { useEffect, useState } from "react";
import { Command } from "cmdk";
import { CornerDownLeft, FlaskConical, LogOut, Moon, Plus, ScanSearch, Search, Sun, Upload } from "lucide-react";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import { t, tr } from "@/lib/i18n";
import { navigate } from "@/lib/router";
import { useSession } from "@/state/session";
import { Dialog } from "@/components/ui/overlay";
import { Dialog as D } from "radix-ui";
import { Kbd } from "@/components/ui/core";
import { RoomName } from "@/components/common/domain";
import { allNavItems } from "./nav";

const itemClass = "flex h-9 cursor-default items-center gap-2.5 rounded-sm px-2.5 text-[13px] text-ink-2 outline-none data-[selected=true]:bg-hover data-[selected=true]:text-ink [&_svg]:size-4 [&_svg]:text-ink-3";
const groupClass = "[&_[cmdk-group-heading]]:t-eyebrow [&_[cmdk-group-heading]]:px-2.5 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:pt-3";

export function CommandPalette({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { session, isDemo, setTheme, resolvedTheme } = useSession();
  const [query, setQuery] = useState("");
  const hasSession = session != null && session.mode !== "None";
  const rooms = useBridgeQuery("rooms.list", { refresh: false }, { enabled: open && hasSession, staleTime: 60_000 });

  useEffect(() => {
    if (!open) setQuery("");
  }, [open]);

  const run = (action: () => void) => {
    onOpenChange(false);
    action();
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <D.Portal>
        <D.Overlay className="fixed inset-0 z-50 bg-black/40" />
        <D.Content className="fixed left-1/2 top-[14vh] z-50 w-[min(640px,calc(100vw-32px))] -translate-x-1/2 overflow-hidden rounded-xl border border-line bg-raised shadow-pop outline-none data-[state=open]:animate-[dialog-in_140ms_var(--ease-snap)]">
          <D.Title className="sr-only">{t("Command palette")}</D.Title>
          <D.Description className="sr-only">{t("Search rooms, pages and actions")}</D.Description>
          <Command loop label={t("Command palette")}>
            <div className="flex items-center gap-2.5 border-b border-line px-3.5">
              <Search className="size-4 text-ink-3" />
              <Command.Input value={query} onValueChange={setQuery} autoFocus placeholder={t("Search rooms, pages and actions…")} className="h-12 w-full bg-transparent text-[14px] outline-none" />
              <Kbd>Esc</Kbd>
            </div>
            <Command.List className="scroll-area max-h-[420px] p-1.5">
              <Command.Empty className="py-10 text-center text-[12.5px] text-ink-3">{t("No results for “{query}”.", { query })}</Command.Empty>
              {hasSession && (
                <Command.Group heading={t("Actions")} className={groupClass}>
                  <Command.Item className={itemClass} onSelect={() => run(() => navigate("/rooms/new"))}><Plus />{t("Create room")}</Command.Item>
                  <Command.Item className={itemClass} onSelect={() => run(() => navigate("/room-lists?create=1"))}><Plus />{t("Create room list")}</Command.Item>
                  <Command.Item className={itemClass} onSelect={() => run(() => void bridge.call("scan.start", {}).then(() => toast.info(t("Configuration scan started"), { description: t("Booking policies and working hours are read for every room.") })))}><ScanSearch />{t("Run configuration scan")}</Command.Item>
                  <Command.Item className={itemClass} onSelect={() => run(() => navigate("/bulk"))}><Upload />{t("Import a bulk configuration file")}</Command.Item>
                </Command.Group>
              )}
              <Command.Group heading={t("Go to")} className={groupClass}>
                {allNavItems.map((item) => (
                  <Command.Item key={item.key} value={`go ${t(item.label)}`} className={itemClass} onSelect={() => run(() => navigate(item.path))}>
                    <item.icon />
                    {t(item.label)}
                    {item.shortcut && <span className="ml-auto flex gap-1">{item.shortcut.split(" ").map((k) => <Kbd key={k}>{k}</Kbd>)}</span>}
                  </Command.Item>
                ))}
              </Command.Group>
              {rooms.data && query.length > 0 && (
                <Command.Group heading={t("Rooms")} className={groupClass}>
                  {rooms.data.rooms.slice(0, 400).map((room) => (
                    <Command.Item key={room.id} value={`room ${room.displayName} ${room.primarySmtpAddress} ${room.alias} ${room.building ?? ""}`} className={itemClass} onSelect={() => run(() => navigate(`/rooms/${encodeURIComponent(room.id)}`))}>
                      <RoomName name={room.displayName} className="min-w-0 flex-1 text-ink" />
                      <span className="t-mono truncate text-[11px] text-ink-3">{room.primarySmtpAddress}</span>
                    </Command.Item>
                  ))}
                </Command.Group>
              )}
              <Command.Group heading={t("Preferences")} className={groupClass}>
                <Command.Item className={itemClass} onSelect={() => run(() => setTheme(resolvedTheme === "dark" ? "Light" : "Dark"))}>
                  {resolvedTheme === "dark" ? <Sun /> : <Moon />}{resolvedTheme === "dark" ? t("Switch to light theme") : t("Switch to dark theme")}</Command.Item>
                {!isDemo && <Command.Item className={itemClass} onSelect={() => run(() => void bridge.call("session.startDemo", {}).then(() => navigate("/")))}><FlaskConical />{t("Explore demo mode")}</Command.Item>}
                {hasSession && <Command.Item className={itemClass} onSelect={() => run(() => void bridge.call("session.end", {}).then(() => navigate("/")))}><LogOut />{isDemo ? t("Exit demo mode") : t("Disconnect and clear session")}</Command.Item>}
              </Command.Group>
            </Command.List>
            <div className="flex items-center gap-3 border-t border-line px-3.5 py-2 text-[11px] text-ink-3">
              <span className="flex items-center gap-1"><Kbd>↑</Kbd><Kbd>↓</Kbd> {t("navigate")}</span>
              <span className="flex items-center gap-1"><Kbd><CornerDownLeft className="size-3" /></Kbd> {t("open")}</span>
              <span className="ml-auto">{tr("Press {key} for keyboard shortcuts", { key: <Kbd>?</Kbd> })}</span>
            </div>
          </Command>
        </D.Content>
      </D.Portal>
    </Dialog>
  );
}
