import { useEffect, useRef, useState, type ReactNode } from "react";
import { AnimatePresence, motion } from "motion/react";
import { FlaskConical, KeyRound, Unplug } from "lucide-react";
import { bridge } from "@/bridge/client";
import { t, tr } from "@/lib/i18n";
import { navigate, useRoute } from "@/lib/router";
import { useSession } from "@/state/session";
import { Kbd } from "@/components/ui/core";
import { Dialog, DialogContent } from "@/components/ui/overlay";
import { LogoMark } from "@/components/common/domain";
import { CommandPalette } from "./command-palette";
import { allNavItems } from "./nav";
import { Sidebar } from "./sidebar";
import { TopBar } from "./topbar";

function DemoBand() {
  const { session } = useSession();
  return (
    <div className="demo-stripes flex h-7 shrink-0 items-center justify-center gap-3 border-b border-[color-mix(in_srgb,var(--demo)_40%,transparent)] text-[11.5px] font-medium text-ink" role="status" data-testid="demo-band">
      <FlaskConical className="size-3.5 text-demo" />
      <span><span className="font-semibold text-demo">{t("DEMO MODE")}</span> · {t("{tenant} is a synthetic tenant. Nothing is sent to Microsoft 365.", { tenant: session?.tenantName })}</span>
      <button type="button" className="rounded-[4px] px-1.5 text-ink-2 underline-offset-2 hover:text-ink hover:underline" onClick={() => void bridge.call("session.end", {}).then(() => navigate("/"))}>
        {t("Exit demo")}</button>
    </div>
  );
}

function SignInBanner() {
  const { signInPrompt, session } = useSession();
  const connecting = session?.services.find((s) => s.status === "Connecting");
  if (!connecting) return null;
  return (
    <div className="flex shrink-0 items-center gap-3 border-b border-line bg-[color-mix(in_srgb,var(--accent)_8%,transparent)] px-4 py-2 text-[12.5px]">
      <KeyRound className="size-4 text-accent" />
      {signInPrompt?.userCode ? (
        <span>
          {tr("Sign in to {service} at {uri} with code {code}", {
            service: connecting.service,
            uri: <span className="t-mono">{signInPrompt.verificationUri}</span>,
            code: <span className="t-mono rounded-[3px] bg-surface px-1.5 py-0.5 font-semibold text-ink">{signInPrompt.userCode}</span>,
          })}
        </span>
      ) : (
        <span>{t("Waiting for Microsoft sign-in for {service}. Complete multi-factor authentication or Conditional Access prompts in the sign-in window.", { service: connecting.service })}</span>
      )}
    </div>
  );
}

const shortcuts = (): [string, string][] => [
  ["Ctrl K", t("Open the command palette")],
  ["Ctrl B", t("Collapse or expand the sidebar")],
  ["Ctrl Shift R", t("Refresh the inventory")],
  ["/", t("Focus the search field on the current page")],
  ["?", t("Show keyboard shortcuts")],
  ...allNavItems.filter((i) => i.shortcut).map((i) => [i.shortcut!, t("Go to {page}", { page: t(i.label) })] as [string, string]),
];

export function AppShell({ children }: { children: ReactNode }) {
  const { isDemo, bridgeConnected } = useSession();
  const route = useRoute();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(() => readFlag("roomops.sidebarCollapsed"));
  const pending = useRef<string | null>(null);

  const toggleSidebar = () =>
    setCollapsed((c) => {
      writeFlag("roomops.sidebarCollapsed", !c);
      return !c;
    });

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement;
      const typing = target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName);
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setPaletteOpen((o) => !o);
        return;
      }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "b") {
        e.preventDefault();
        toggleSidebar();
        return;
      }
      if (typing || e.ctrlKey || e.metaKey || e.altKey) return;
      if (e.key === "?") {
        setHelpOpen(true);
        return;
      }
      if (e.key === "/") {
        const search = document.querySelector<HTMLInputElement>("[data-page-search]");
        if (search) {
          e.preventDefault();
          search.focus();
        }
        return;
      }
      if (pending.current === "g") {
        pending.current = null;
        const match = allNavItems.find((i) => i.shortcut?.toLowerCase() === `g ${e.key.toLowerCase()}`);
        if (match) navigate(match.path);
        return;
      }
      if (e.key === "g") {
        pending.current = "g";
        window.setTimeout(() => (pending.current = null), 900);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  return (
    <div className="flex h-full flex-col">
      {isDemo && <DemoBand />}
      {!bridgeConnected && (
        <div className="flex h-7 shrink-0 items-center justify-center gap-2 bg-[color-mix(in_srgb,var(--bad)_15%,transparent)] text-[12px] text-bad">
          <Unplug className="size-3.5" />{t("Connection to the RoomOps backend was lost. Reconnecting…")}</div>
      )}
      <div className="flex min-h-0 flex-1">
        <Sidebar collapsed={collapsed} onToggle={toggleSidebar} />
        <div className="flex min-w-0 flex-1 flex-col">
          <TopBar onOpenPalette={() => setPaletteOpen(true)} />
          <SignInBanner />
          <main className="scroll-area min-h-0 flex-1" id="main">
            <AnimatePresence mode="wait" initial={false}>
              <motion.div
                key={route.path.split("/").slice(0, 3).join("/")}
                initial={{ opacity: 0, y: 4 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0 }}
                transition={{ duration: 0.14, ease: [0.2, 0.8, 0.2, 1] }}
                className="mx-auto w-full max-w-[1480px] p-[var(--page-pad)]"
              >
                {children}
              </motion.div>
            </AnimatePresence>
          </main>
        </div>
      </div>
      <CommandPalette open={paletteOpen} onOpenChange={setPaletteOpen} />
      <Dialog open={helpOpen} onOpenChange={setHelpOpen}>
        <DialogContent title={t("Keyboard shortcuts")} description={t("RoomOps is designed to be driven from the keyboard.")}>
          <ul className="divide-y divide-line">
            {shortcuts().map(([keys, label]) => (
              <li key={keys} className="flex items-center justify-between py-2 text-[13px]">
                <span className="text-ink-2">{label}</span>
                <span className="flex gap-1">{keys.split(" ").map((k) => <Kbd key={k}>{k}</Kbd>)}</span>
              </li>
            ))}
          </ul>
        </DialogContent>
      </Dialog>
    </div>
  );
}

/** Per-viewer UI conveniences live in localStorage; failures (private mode, cleared data) just fall back to defaults. */
function readFlag(key: string): boolean {
  try {
    return localStorage.getItem(key) === "1";
  } catch {
    return false;
  }
}

function writeFlag(key: string, value: boolean) {
  try {
    localStorage.setItem(key, value ? "1" : "0");
  } catch {
    // Not persisted; the preference applies for this session only.
  }
}

export function LoadingScreen() {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-5 bg-bg">
      <motion.div initial={{ opacity: 0, scale: 0.94 }} animate={{ opacity: 1, scale: 1 }} transition={{ duration: 0.3 }} className="flex items-center gap-3">
        <LogoMark size={40} />
        <div>
          <div className="font-display text-[24px] font-semibold tracking-wide">RoomOps <span className="text-ink-3">Studio</span></div>
          <div className="text-[11px] font-medium uppercase tracking-[0.1em] text-ink-3">{t("Intelligent Workspace Administration")}</div>
        </div>
      </motion.div>
      <div className="h-[2px] w-48 overflow-hidden rounded-full bg-hover">
        <div className="h-full w-1/3 animate-[loading-bar_1.2s_var(--ease-snap)_infinite] bg-accent" />
      </div>
      <p className="text-[11.5px] text-ink-3">{t("Starting the RoomOps backend…")}</p>
    </div>
  );
}
