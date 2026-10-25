import { useBridgeQuery } from "@/bridge/hooks";

import { navigate } from "@/lib/router";
import { Badge, Card, CardHeader } from "@/components/ui/core";
import { PageHeader, RoomName } from "@/components/common/domain";
import { RoomMasterDetail } from "@/features/rooms/room-master-detail";
import { FindingRow } from "@/features/validation/finding-row";
import { DelegateManager } from "./delegate-manager";
import { t, tn } from "@/lib/i18n";

function Overview() {
  const rooms = useBridgeQuery("rooms.list", { refresh: false });
  const validation = useBridgeQuery("validation.run", { refresh: false });
  const delegateFindings = (validation.data?.findings ?? []).filter((f) => ["BOOK-003", "BOOK-004", "BOOK-005"].includes(f.ruleId));
  const withDelegates = (rooms.data?.rooms ?? []).filter((r) => (r.delegateCount ?? 0) > 0);
  return (
    <div className="space-y-3">
      <Card>
        <CardHeader title={t("Delegate issues")} description={t("Unresolved delegates, approval flows without delegates, and delegates who don't receive requests.")} />
        <div className="divide-y divide-line border-t border-line">
          {delegateFindings.length === 0 ? <p className="px-4 py-4 text-[12.5px] text-ink-3">{t("No delegate issues in rooms whose booking policy has been read.")}</p> : delegateFindings.map((f) => <FindingRow key={f.id} finding={f} showTarget onNavigate={() => navigate(`/delegates/${encodeURIComponent(f.targetId)}`)} />)}
        </div>
      </Card>
      <Card>
        <CardHeader title={t("Rooms with booking delegates")} description={tn(withDelegates.length, "{n} room among those whose booking policy has been read.", "{n} rooms among those whose booking policy has been read.")} />
        <ul className="divide-y divide-line border-t border-line">
          {withDelegates.map((r) => (
            <li key={r.id}>
              <button type="button" onClick={() => navigate(`/delegates/${encodeURIComponent(r.id)}`)} className="flex w-full items-center justify-between px-4 py-2.5 hover:bg-hover">
                <RoomName name={r.displayName} className="text-[12.5px] font-medium" />
                <Badge tone="accent">{tn(r.delegateCount ?? 0, "{n} delegate", "{n} delegates")}</Badge>
              </button>
            </li>
          ))}
          {withDelegates.length === 0 && <li className="px-4 py-4 text-[12.5px] text-ink-3">{t("None found. Run a configuration scan to read every room.")}</li>}
        </ul>
      </Card>
    </div>
  );
}

export default function DelegatesPage({ roomId }: { roomId?: string }) {
  return (
    <div>
      <PageHeader title={t("Resource delegates")} description={t("Booking delegates approve requests. Calendar folder permissions, Full Access, Send As and Send on Behalf are separate mechanisms and are never granted as a side effect.")} />
      <RoomMasterDetail basePath="/delegates" roomId={roomId} meta={(r) => (r.delegateCount == null ? t("Not read") : tn(r.delegateCount, "{n} delegate", "{n} delegates"))} empty={<Overview />}>
        {(detail) => <DelegateManager roomId={detail.mailbox.id} />}
      </RoomMasterDetail>
    </div>
  );
}
