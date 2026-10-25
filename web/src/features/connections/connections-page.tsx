import { useState } from "react";
import { CheckCircle2, CircleAlert, CircleDashed, FlaskConical, Loader2, LogOut, PlugZap, Terminal, Unplug } from "lucide-react";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { ServiceConnection, ServiceKind, SignInMethod } from "@/bridge/contracts.gen";
import { cn, formatDateTime } from "@/lib/format";
import { navigate, useRoute } from "@/lib/router";
import { useSession } from "@/state/session";
import { Button } from "@/components/ui/button";
import { Badge, Card, CardHeader, Dot, Field, Input, Skeleton } from "@/components/ui/core";
import { Select } from "@/components/ui/controls";
import { Identifier, InlineNotice, PageHeader } from "@/components/common/domain";
import { t, tr, tx } from "@/lib/i18n";

const services: { kind: ServiceKind; title: string; purpose: string; roles: string; permissions: string }[] = [
  {
    kind: "Exchange",
    title: tx("Exchange Online"),
    purpose: tx("Room mailboxes, booking policies, delegates, working hours and room lists. Establishes the tenant identity for the session."),
    roles: tx("Exchange Administrator, or a custom role group with Recipient Management and Organization Configuration for calendar processing."),
    permissions: tx("ExchangeOnlineManagement module, delegated sign-in."),
  },
  {
    kind: "Places",
    title: tx("Microsoft Places"),
    purpose: tx("Buildings, floors, sections and room metadata. Confirmed against the Exchange tenant by matching room mailboxes."),
    roles: tx("Places Administrator, Exchange Administrator, or a custom role with TenantPlacesManagement. Roles assigned through groups aren't honored by Exchange."),
    permissions: tx("MicrosoftPlaces module 2.0.0 or later."),
  },
  {
    kind: "Graph",
    title: tx("Microsoft Graph"),
    purpose: tx("Read-only account and license signals for Teams Rooms readiness. RoomOps never writes through Graph."),
    roles: tx("Delegated consent for User.Read.All. No directory write permissions are requested."),
    permissions: tx("Microsoft.Graph.Authentication module; scope User.Read.All."),
  },
];

function StatusLine({ connection }: { connection?: ServiceConnection }) {
  const status = connection?.status ?? "NotConnected";
  const map = {
    Connected: { icon: <CheckCircle2 className="size-4 text-ok" />, text: t("Connected") },
    Connecting: { icon: <Loader2 className="size-4 animate-spin text-accent" />, text: t("Waiting for sign-in") },
    NotConnected: { icon: <CircleDashed className="size-4 text-ink-3" />, text: t("Not connected") },
    Failed: { icon: <CircleAlert className="size-4 text-bad" />, text: t("Connection failed") },
    Expired: { icon: <CircleAlert className="size-4 text-warn-ink" />, text: t("Session expired") },
  } as const;
  return <span className="flex items-center gap-1.5 text-[12.5px] font-medium">{map[status].icon}{map[status].text}</span>;
}

export function ConnectionsPage() {
  const route = useRoute();
  const { session, appInfo, settings, isDemo, updateSettings } = useSession();
  const environment = useBridgeQuery("connections.environment", {}, { requireSession: false, staleTime: 120_000 });
  const [account, setAccount] = useState(route.query.get("account") ?? settings?.connection.defaultAccount ?? "");
  const [method, setMethod] = useState<SignInMethod>(settings?.connection.exchangeSignIn ?? "Browser");
  // Graph always signs in through Windows (WAM) on Windows; the only alternative the module offers is device code.
  const [graphMethod, setGraphMethod] = useState<SignInMethod>(settings?.connection.graphSignIn === "DeviceCode" ? "DeviceCode" : "Browser");
  const [busy, setBusy] = useState<ServiceKind | "demo" | null>(null);
  const liveAllowed = appInfo?.liveConnectionsAllowed !== false;
  const exchangeConnected = session?.mode === "Live" && session.services.some((s) => s.service === "Exchange" && s.status === "Connected");

  const connect = async (service: ServiceKind) => {
    setBusy(service);
    try {
      await bridge.call("connections.connect", { service, userPrincipalName: account.trim() || null, signInMethod: service === "Places" ? "Browser" : service === "Graph" ? graphMethod : method });
      if (service === "Exchange") {
        await updateSettings((s) => ({ ...s, connection: { ...s.connection, defaultAccount: account.trim() || null, exchangeSignIn: method } }));
      }
      if (service === "Graph") {
        await updateSettings((s) => ({ ...s, connection: { ...s.connection, graphSignIn: graphMethod } }));
      }
      toast.success(t("{service} connected", { service: t(services.find((s) => s.kind === service)!.title) }));
    } catch (error) {
      toast.error(t("Connection failed"), { description: error instanceof Error ? error.message : undefined });
    } finally {
      setBusy(null);
    }
  };

  const disconnect = async (service: ServiceKind) => {
    setBusy(service);
    try {
      await bridge.call("connections.disconnect", { service });
      toast.success(service === "Exchange" ? t("Session closed. All service sessions and cached data were cleared.") : t("{service} disconnected", { service }));
    } finally {
      setBusy(null);
    }
  };

  return (
    <div>
      <PageHeader title={t("Connections")} description={t("Each Microsoft 365 service has its own sign-in and its own isolated PowerShell session. Signing in to one service never grants a session to another.")} />

      {!liveAllowed && (
        <InlineNotice tone="warn" className="mb-4" title={t("Live connections are disabled in this host")}>
          {tr("This is the browser development host. Use the RoomOps Studio desktop application to connect to a tenant, or restart the host with {flag}.", { flag: <span className="t-mono">--allow-live</span> })}
        </InlineNotice>
      )}

      <div className="grid gap-4 xl:grid-cols-[1fr_360px]">
        <div className="space-y-3">
          {isDemo && (
            <InlineNotice tone="demo" title={t("Demo session active")} action={<Button size="sm" onClick={() => void bridge.call("session.end", {})}>{t("Exit demo")}</Button>}>
              {t("The services below are simulated. Connecting Exchange Online ends the demo session and discards all demo data.")}</InlineNotice>
          )}
          {services.map((svc) => {
            const connection = session?.services.find((s) => s.service === svc.kind);
            const connected = connection?.status === "Connected" && !isDemo;
            const blocked = svc.kind !== "Exchange" && !exchangeConnected;
            return (
              <Card key={svc.kind} data-testid={`service-${svc.kind}`}>
                <CardHeader
                  title={<span className="flex items-center gap-2">{t(svc.title)}{connection?.moduleVersion && connection.moduleVersion !== "demo" && <Badge tone="outline">{t("module {version}", { version: connection.moduleVersion })}</Badge>}</span>}
                  description={t(svc.purpose)}
                  actions={<StatusLine connection={isDemo ? undefined : connection} />}
                />
                <div className="grid gap-4 border-t border-line px-4 py-3.5 md:grid-cols-[1fr_auto]">
                  <div className="space-y-2 text-[12.5px]">
                    {connected && (
                      <dl className="grid grid-cols-[120px_1fr] gap-y-1">
                        <dt className="text-ink-3">{t("Account")}</dt><dd>{connection?.account ?? "—"}</dd>
                        <dt className="text-ink-3">{t("Tenant")}</dt><dd><Identifier value={connection?.tenantId} /></dd>
                        <dt className="text-ink-3">{t("Tenant check")}</dt>
                        <dd className="flex items-center gap-1.5">
                          <Dot tone={connection?.verification === "Verified" || connection?.verification === "Correlated" ? "ok" : "warn"} />
                          {connection?.verification === "Verified" ? t("Tenant id reported by the service matches") : connection?.verification === "Correlated" ? t("Confirmed by matching room mailboxes") : t("Not verified — writes blocked")}
                        </dd>
                        <dt className="text-ink-3">{t("Connected")}</dt><dd>{formatDateTime(connection?.connectedAt)}</dd>
                        {connection && connection.grantedScopes.length > 0 && (<><dt className="text-ink-3">{t("Scopes")}</dt><dd className="t-mono">{connection.grantedScopes.join(", ")}</dd></>)}
                      </dl>
                    )}
                    {connection?.detail && !isDemo && <p className={cn(connection.status === "Failed" || connection.status === "Expired" ? "text-bad" : "text-ink-2")}>{connection.detail}</p>}
                    <p className="t-help"><span className="font-medium text-ink-2">{t("Required role:")} </span>{t(svc.roles)}</p>
                    <p className="t-help">{t(svc.permissions)}</p>
                    {svc.kind === "Exchange" && !connected && (
                      <div className="grid max-w-xl gap-3 pt-1 md:grid-cols-2">
                        <Field label={t("Administrator account")} help={t("Optional. Pre-fills the Microsoft sign-in.")}>
                          {(id) => <Input id={id} value={account} onChange={(e) => setAccount(e.target.value)} placeholder="admin@contoso.com" mono />}
                        </Field>
                        <Field label={t("Sign-in method")} help={t("Browser sign-in works in a background session on every system.")}>
                          {(id) => (
                            <Select id={id} value={method} onChange={setMethod} options={[
                              { value: "Browser", label: t("System browser") },
                              { value: "WindowsAccountManager", label: t("Windows account (WAM)") },
                              { value: "DeviceCode", label: t("Device code") },
                            ]} />
                          )}
                        </Field>
                      </div>
                    )}
                    {svc.kind === "Graph" && !connected && (
                      <div className="grid max-w-xl gap-3 pt-1 md:grid-cols-2">
                        <Field label={t("Sign-in method")} help={t("Microsoft Graph signs in through Windows. Choose device code if that sign-in window doesn't appear.")}>
                          {(id) => (
                            <Select id={id} value={graphMethod} onChange={setGraphMethod} options={[
                              { value: "Browser", label: t("Windows sign-in") },
                              { value: "DeviceCode", label: t("Device code") },
                            ]} />
                          )}
                        </Field>
                      </div>
                    )}
                  </div>
                  <div className="flex items-start gap-2">
                    {connected ? (
                      <Button onClick={() => void disconnect(svc.kind)} loading={busy === svc.kind} icon={svc.kind === "Exchange" ? <LogOut /> : <Unplug />}>
                        {svc.kind === "Exchange" ? t("Disconnect tenant") : t("Disconnect")}
                      </Button>
                    ) : (
                      <Button variant={svc.kind === "Exchange" ? "primary" : "secondary"} onClick={() => void connect(svc.kind)} loading={busy === svc.kind} disabled={!liveAllowed || blocked || busy !== null} icon={<PlugZap />}>
                        {connection?.status === "Expired" || connection?.status === "Failed" ? t("Reconnect") : t("Connect")}
                      </Button>
                    )}
                  </div>
                </div>
                {blocked && !isDemo && <p className="border-t border-line px-4 py-2 text-[12px] text-ink-3">{t("Connect Exchange Online first; it anchors the tenant identity.")}</p>}
              </Card>
            );
          })}
        </div>

        <div className="space-y-3">
          <Card>
            <CardHeader title={<span className="flex items-center gap-2"><Terminal className="size-4 text-ink-3" />{t("PowerShell environment")}</span>} description={t("RoomOps runs Microsoft's modules in background PowerShell 7 sessions.")} />
            <div className="border-t border-line px-4 py-3 text-[12.5px]">
              {environment.isLoading ? (
                <div className="space-y-2"><Skeleton className="h-4 w-3/4" /><Skeleton className="h-4 w-1/2" /><Skeleton className="h-4 w-2/3" /></div>
              ) : environment.data ? (
                <>
                  <div className="mb-3 flex items-center gap-2">
                    <Dot tone={environment.data.powerShellPath ? "ok" : "bad"} />
                    <span>{environment.data.powerShellPath ? `PowerShell ${environment.data.powerShellVersion ?? ""}` : t("PowerShell 7 not found")}</span>
                  </div>
                  {environment.data.powerShellPath && <Identifier value={environment.data.powerShellPath} className="mb-3 flex" />}
                  {environment.data.probeError && <p className="mb-3 text-bad">{environment.data.probeError}</p>}
                  <ul className="space-y-2.5">
                    {environment.data.modules.map((m) => (
                      <li key={m.name}>
                        <div className="flex items-center justify-between gap-2">
                          <span className="t-mono text-[11.5px] text-ink">{m.name}</span>
                          {m.installedVersion ? <Badge tone="ok">{m.installedVersion}</Badge> : <Badge tone="warn">{t("Not installed")}</Badge>}
                        </div>
                        <p className="t-help mt-0.5">{t("{purpose}. Minimum {version}.", { purpose: m.purpose, version: m.minimumVersion })}</p>
                        {!m.installedVersion && <Identifier value={m.installCommand} className="mt-1 flex" />}
                      </li>
                    ))}
                  </ul>
                </>
              ) : (
                <p className="text-ink-3">{t("The environment could not be inspected.")}</p>
              )}
            </div>
          </Card>
          <Card>
            <CardHeader title={<span className="flex items-center gap-2"><FlaskConical className="size-4 text-demo" />{t("Demo mode")}</span>} description={t("Synthetic tenant for evaluation and training. All demo data is discarded when the session ends.")} />
            <div className="border-t border-line px-4 py-3">
              {isDemo ? (
                <Button onClick={() => void bridge.call("session.end", {}).then(() => navigate("/"))}>{t("Exit demo mode")}</Button>
              ) : (
                <Button onClick={() => { setBusy("demo"); void bridge.call("session.startDemo", {}).then(() => navigate("/")).finally(() => setBusy(null)); }} loading={busy === "demo"}>
                  {session?.mode === "Live" ? t("Disconnect and start demo") : t("Start demo mode")}
                </Button>
              )}
            </div>
          </Card>
        </div>
      </div>
    </div>
  );
}
