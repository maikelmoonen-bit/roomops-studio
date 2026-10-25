import { useState } from "react";
import { ArrowRight, FlaskConical, PlugZap, ShieldCheck } from "lucide-react";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import { formatRelative } from "@/lib/format";
import { navigate } from "@/lib/router";
import { useSession } from "@/state/session";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/core";
import { LogoMark } from "@/components/common/domain";
import { t } from "@/lib/i18n";

export function WelcomePage() {
  const { settings, appInfo } = useSession();
  const [starting, setStarting] = useState(false);
  const known = settings?.knownTenants ?? [];

  const startDemo = async () => {
    setStarting(true);
    try {
      await bridge.call("session.startDemo", {});
      navigate("/");
    } catch (error) {
      toast.error(t("Demo mode could not start"), { description: error instanceof Error ? error.message : undefined });
    } finally {
      setStarting(false);
    }
  };

  return (
    <div className="mx-auto flex max-w-[880px] flex-col pt-[6vh]">
      <div className="flex items-center gap-3">
        <LogoMark size={36} />
        <span className="t-eyebrow">{t("RoomOps Studio")}</span>
      </div>
      <h1 className="mt-5 font-display text-[40px] font-semibold leading-[1.05] tracking-[0.005em] text-ink">{t("Meeting room administration")}<br />{t("for Microsoft 365.")}</h1>
      <p className="mt-4 max-w-[620px] text-[14px] leading-relaxed text-ink-2">
        {t("Rooms, room lists, booking policies, delegates, working hours and Microsoft Places in one place. Every change is previewed, confirmed, executed and read back before it counts as done.")}</p>

      <div className="mt-9 grid gap-3 md:grid-cols-2">
        <Card className="group flex flex-col p-5">
          <div className="flex size-9 items-center justify-center rounded-md bg-[color-mix(in_srgb,var(--accent)_14%,transparent)] text-accent"><PlugZap className="size-5" /></div>
          <h2 className="mt-4 text-[15px] font-semibold">{t("Connect to Microsoft 365")}</h2>
          <p className="mt-1 flex-1 text-[12.5px] leading-relaxed text-ink-3">
            {t("Sign in to Exchange Online with your administrator account. Microsoft Places and Microsoft Graph connect separately, each in its own isolated session.")}</p>
          <Button variant="primary" className="mt-5 self-start" onClick={() => navigate("/connections")} disabled={appInfo?.liveConnectionsAllowed === false}>
            {t("Open connections")}<ArrowRight />
          </Button>
          {appInfo?.liveConnectionsAllowed === false && <p className="t-help mt-2">{t("Live connections are disabled in this development host. Use the desktop application or start the host with --allow-live.")}</p>}
        </Card>
        <Card className="flex flex-col p-5">
          <div className="flex size-9 items-center justify-center rounded-md bg-[color-mix(in_srgb,var(--demo)_16%,transparent)] text-demo"><FlaskConical className="size-5" /></div>
          <h2 className="mt-4 text-[15px] font-semibold">{t("Explore demo mode")}</h2>
          <p className="mt-1 flex-1 text-[12.5px] leading-relaxed text-ink-3">
            {t("A synthetic tenant with three buildings, 26 rooms, room lists, delegate scenarios and validation findings. Clearly labeled, and nothing is ever sent to Microsoft 365.")}</p>
          <Button className="mt-5 self-start" onClick={() => void startDemo()} loading={starting} data-testid="start-demo">
            {t("Start demo mode")}<ArrowRight />
          </Button>
        </Card>
      </div>

      {known.length > 0 && (
        <div className="mt-8">
          <div className="t-eyebrow mb-2">{t("Recent tenants")}</div>
          <Card className="divide-y divide-line">
            {known.map((tenant) => (
              <button key={tenant.tenantId} type="button" onClick={() => navigate(`/connections?account=${encodeURIComponent(tenant.lastAccount ?? "")}`)} className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-hover">
                <ShieldCheck className="size-4 text-ink-3" />
                <span className="min-w-0 flex-1">
                  <span className="block text-[13px] font-medium">{tenant.displayName ?? tenant.tenantId}</span>
                  <span className="t-mono block text-[11px] text-ink-3">{tenant.tenantId} · {tenant.lastAccount}</span>
                </span>
                <span className="text-[11.5px] text-ink-3">{formatRelative(tenant.lastConnectedAt)}</span>
              </button>
            ))}
          </Card>
        </div>
      )}

      <p className="mt-10 max-w-[620px] text-[11.5px] leading-relaxed text-ink-3">
        {t("RoomOps uses Microsoft's own PowerShell modules (ExchangeOnlineManagement, MicrosoftPlaces, Microsoft.Graph.Authentication) with modern authentication, MFA and Conditional Access. No passwords or tokens are stored by RoomOps.")}</p>
    </div>
  );
}
