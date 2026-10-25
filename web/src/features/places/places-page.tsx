import { CheckCircle2, ListTree, MapPinned, XCircle } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import { cn } from "@/lib/format";
import { navigate } from "@/lib/router";
import { Button } from "@/components/ui/button";
import { Badge, Card, CardHeader, Progress, Skeleton } from "@/components/ui/core";
import { CodeBlock } from "@/components/common/code-block";
import { ErrorState, InlineNotice, PageHeader } from "@/components/common/domain";
import { t, tn } from "@/lib/i18n";

function Model({ title, icon, description, count, total, items }: { title: string; icon: React.ReactNode; description: string; count: number; total: number; items: string[] }) {
  return (
    <Card className="p-5">
      <div className="flex items-center gap-2 text-[15px] font-semibold"><span className="text-ink-3 [&_svg]:size-5">{icon}</span>{title}</div>
      <p className="mt-1 text-[12.5px] leading-relaxed text-ink-2">{description}</p>
      <div className="mt-4 flex items-end gap-3">
        <span className="t-metric">{count}</span>
        <span className="pb-1 text-[12px] text-ink-3">{tn(total, "of {n} room discoverable", "of {n} rooms discoverable")}</span>
      </div>
      <Progress value={total ? (count / total) * 100 : 0} className="mt-2" />
      <ul className="mt-4 space-y-1 text-[12px] text-ink-2">{items.map((i) => <li key={i}>· {i}</li>)}</ul>
    </Card>
  );
}

export default function PlacesPage() {
  const diagnostics = useBridgeQuery("places.diagnostics", {});
  const d = diagnostics.data;
  return (
    <div>
      <PageHeader title={t("Microsoft Places")} description={t("How people find rooms in your tenant. Outlook Room Finder browses room lists; Places Finder browses the building and floor hierarchy. They are separate models and are not kept in sync automatically.")} />
      {diagnostics.isLoading && <Skeleton className="h-80" />}
      {diagnostics.error && <Card><ErrorState error={diagnostics.error} onRetry={() => void diagnostics.refetch()} /></Card>}
      {d && (
        <div className="space-y-3">
          <div className="grid gap-3 lg:grid-cols-2">
            <Model title={t("Room Finder")} icon={<ListTree />} count={d.roomsInRoomLists} total={d.roomCount} description={t("Classic Outlook room browsing. Rooms appear when they are members of a room list.")} items={[t("Driven by Exchange room lists"), t("Microsoft recommends one room list per building, named exactly like the building"), t("Managed on the Room lists page")]} />
            <Model title={t("Places Finder")} icon={<MapPinned />} count={d.placesAvailable ? d.roomsWithPlacesParent : 0} total={d.roomCount} description={d.placesAvailable ? t("Browses by country, city, building and floor. Rooms appear when they are placed in the hierarchy.") : t("Connect Microsoft Places to evaluate this model.")} items={[t("Driven by the Microsoft Places hierarchy"), t("Room updates can take up to 24 hours to appear"), t("Managed on the Buildings & floors page")]} />
          </div>
          <Card>
            <CardHeader title={t("Coverage across both models")} />
            <div className="grid grid-cols-2 gap-px border-t border-line bg-line md:grid-cols-4">
              {[
                [t("In both"), d.roomsInBoth, "ok"],
                [t("Room lists only"), d.roomsInRoomLists - d.roomsInBoth, "neutral"],
                [t("Places only"), d.roomsWithPlacesParent - d.roomsInBoth, "neutral"],
                [t("In neither"), d.roomsInNeither, d.roomsInNeither ? "warn" : "ok"],
              ].map(([label, value, tone]) => (
                <div key={label as string} className="bg-surface px-4 py-3">
                  <div className="text-[12px] text-ink-3">{label}</div>
                  <div className={cn("t-metric mt-1 text-[26px]", tone === "warn" && "text-warn-ink")}>{value as number}</div>
                </div>
              ))}
            </div>
          </Card>
          <div className="grid gap-3 lg:grid-cols-[1.2fr_1fr]">
            <Card>
              <CardHeader title={t("Buildings and matching room lists")} description={t("Room Finder users see room lists; Places Finder users see buildings. Matching names keep both experiences consistent.")} />
              <table className="w-full border-t border-line text-[12.5px]">
                <thead><tr className="text-left text-[11.5px] text-ink-3"><th className="px-4 py-2 font-medium">{t("Building")}</th><th className="py-2 font-medium">{t("Rooms")}</th><th className="py-2 font-medium">{t("Room list")}</th><th className="px-4 py-2 font-medium" /></tr></thead>
                <tbody>
                  {d.buildings.length === 0 && <tr><td colSpan={4} className="px-4 py-4 text-ink-3">{d.placesAvailable ? t("No buildings in Microsoft Places.") : t("Microsoft Places is not connected.")}</td></tr>}
                  {d.buildings.map((b) => (
                    <tr key={b.buildingId} className="border-t border-line">
                      <td className="px-4 py-2 font-medium">{b.buildingName}</td>
                      <td className="py-2 tabular">{b.roomCount}</td>
                      <td className="py-2">{b.matchingRoomList ?? <span className="text-ink-3">{t("None")}</span>}</td>
                      <td className="px-4 py-2 text-right">{b.nameMatchesExactly ? <Badge tone="ok"><CheckCircle2 />{t("Exact match")}</Badge> : b.matchingRoomList ? <Badge tone="warn">{t("Similar name")}</Badge> : <Badge tone="neutral"><XCircle />{t("No list")}</Badge>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Card>
            <Card>
              <CardHeader title={t("Tenant settings (read-only)")} description={t("Read with Get-PlacesSettings. RoomOps never changes tenant-wide Places settings.")} />
              <div className="space-y-3 border-t border-line p-4 text-[12.5px]">
                {!d.placesAvailable ? <p className="text-ink-3">{t("Connect Microsoft Places to read its settings.")}</p> : d.settingsError ? <p className="text-bad">{d.settingsError}</p> : (
                  <dl className="grid grid-cols-[150px_1fr] gap-y-1.5">
                    <dt className="text-ink-3">PlacesFinderEnabled</dt><dd className="t-mono break-all">{d.settings?.placesFinderEnabled ?? t("Not reported")}</dd>
                    <dt className="text-ink-3">EnableBuildings</dt><dd className="t-mono break-all">{d.settings?.enableBuildings ?? t("Not reported")}</dd>
                  </dl>
                )}
                <InlineNotice tone="warn" title={t("Changing who sees Places Finder is a tenant-wide decision")}>
                  {t("It affects every user's room-finding experience and requires a privileged, deliberate change outside RoomOps. Microsoft documents the commands below; a group-scoped rollout requires a mail-enabled security group.")}</InlineNotice>
                <CodeBlock code={"# Enable Places Finder for one mail-enabled security group first\nSet-PlacesSettings -PlacesFinderEnabled 'Default:false,OID:<group-object-id>@<tenant-id>:true'\n\n# Enable for everyone\nSet-PlacesSettings -PlacesFinderEnabled 'Default:true'\n\n# Verify\nGet-PlacesSettings"} maxHeight={200} />
                <Button size="sm" variant="link" onClick={() => void import("@/bridge/client").then(({ bridge }) => bridge.call("app.openExternal", { url: "https://learn.microsoft.com/en-us/microsoft-365/places/enable-places-finder" }).then((r) => { if (!r.ok) window.open("https://learn.microsoft.com/en-us/microsoft-365/places/enable-places-finder", "_blank", "noopener"); }))}>{t("Microsoft Learn: Enable Places Finder")}</Button>
              </div>
            </Card>
          </div>
          <div className="flex gap-2">
            <Button onClick={() => navigate("/room-lists")}>{t("Manage room lists")}</Button>
            <Button onClick={() => navigate("/buildings")}>{t("Manage buildings & floors")}</Button>
          </div>
        </div>
      )}
    </div>
  );
}
