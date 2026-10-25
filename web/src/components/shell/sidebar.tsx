import { PanelLeftClose, PanelLeftOpen } from "lucide-react";
import { motion } from "motion/react";
import { useBridgeQuery } from "@/bridge/hooks";
import { cn } from "@/lib/format";
import { t } from "@/lib/i18n";
import { Link, useRoute } from "@/lib/router";
import { useSession } from "@/state/session";
import { Tooltip } from "@/components/ui/overlay";
import { LogoMark } from "@/components/common/domain";
import { activeKey, navigation } from "./nav";

export function Sidebar({ collapsed, onToggle }: { collapsed: boolean; onToggle: () => void }) {
  const route = useRoute();
  const { session, appInfo } = useSession();
  const active = activeKey(route.path);
  const hasSession = session != null && session.mode !== "None";
  const validation = useBridgeQuery("validation.run", { refresh: false }, { enabled: hasSession, staleTime: 60_000 });
  const urgent = validation.data?.findings.filter((f) => f.severity === "Critical").length ?? 0;
  const connectionProblem = session?.services.some((s) => s.status === "Expired" || s.status === "Failed");

  return (
    <motion.nav
      aria-label={t("Primary")}
      animate={{ width: collapsed ? 56 : 236 }}
      transition={{ duration: 0.18, ease: [0.2, 0.8, 0.2, 1] }}
      className="flex h-full shrink-0 flex-col overflow-hidden border-r border-line bg-panel"
    >
      <div className={cn("flex h-[52px] shrink-0 items-center gap-2.5", collapsed ? "justify-center px-0" : "px-4")}>
        <LogoMark size={26} />
        {!collapsed && (
          <div className="min-w-0 leading-none">
            <div className="font-display text-[17px] font-semibold tracking-wide text-ink">RoomOps<span className="font-medium text-ink-3"> Studio</span></div>
          </div>
        )}
      </div>

      <div className="scroll-area flex-1 px-2 pb-2">
        {navigation.map((group) => (
          <div key={group.label} className="mt-3 first:mt-1">
            {!collapsed ? <div className="t-eyebrow mb-1 px-2.5">{t(group.label)}</div> : <div className="mx-auto mb-1.5 h-px w-6 bg-line" />}
            <ul className="space-y-px">
              {group.items.map((item) => {
                const Icon = item.icon;
                const isActive = active === item.key;
                const badge = item.key === "validation" && urgent > 0 ? urgent : null;
                const dot = item.key === "connections" && connectionProblem;
                const link = (
                  <Link
                    to={item.path}
                    aria-current={isActive ? "page" : undefined}
                    className={cn(
                      "group relative flex h-8 items-center gap-2.5 rounded-sm text-[13px] font-medium transition-colors duration-100",
                      collapsed ? "justify-center px-0" : "px-2.5",
                      isActive ? "bg-[color-mix(in_srgb,var(--accent)_13%,transparent)] text-ink" : "text-ink-2 hover:bg-hover hover:text-ink",
                    )}
                  >
                    {isActive && <motion.span layoutId="nav-active" className="absolute left-0 top-1.5 bottom-1.5 w-[2px] rounded-full bg-accent" transition={{ duration: 0.18 }} />}
                    <Icon className={cn("size-[17px] shrink-0", isActive ? "text-accent" : "text-ink-3 group-hover:text-ink-2")} strokeWidth={1.8} />
                    {!collapsed && <span className="truncate">{t(item.label)}</span>}
                    {badge != null && (
                      <span className={cn("rounded-full bg-[color-mix(in_srgb,var(--bad)_18%,transparent)] px-1.5 text-[10.5px] font-semibold tabular leading-[16px] text-bad", collapsed ? "absolute right-1 top-0.5" : "ml-auto")} aria-label={t("{n} critical findings", { n: badge })}>
                        {badge}
                      </span>
                    )}
                    {dot && <span className={cn("size-1.5 rounded-full bg-bad", collapsed ? "absolute right-2.5 top-2" : "ml-auto")} aria-label={t("Connection needs attention")} />}
                  </Link>
                );
                return <li key={item.key}>{collapsed ? <Tooltip content={t(item.label)} side="right">{link}</Tooltip> : link}</li>;
              })}
            </ul>
          </div>
        ))}
      </div>

      <div className={cn("flex shrink-0 items-center border-t border-line py-2", collapsed ? "justify-center" : "justify-between px-3")}>
        {!collapsed && <span className="t-mono text-[10.5px] text-ink-3">v{appInfo?.version ?? "—"} · {appInfo?.host === "desktop" ? t("Desktop") : appInfo?.host === "devhost" ? t("Dev host") : "—"}</span>}
        <Tooltip content={collapsed ? t("Expand sidebar (Ctrl+B)") : t("Collapse sidebar (Ctrl+B)")} side="right">
          <button type="button" onClick={onToggle} className="rounded-sm p-1.5 text-ink-3 hover:bg-hover hover:text-ink" aria-label={t("Toggle sidebar")}>
            {collapsed ? <PanelLeftOpen className="size-4" /> : <PanelLeftClose className="size-4" />}
          </button>
        </Tooltip>
      </div>
    </motion.nav>
  );
}
