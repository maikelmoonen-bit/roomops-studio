import type { RoomSummary } from "@/bridge/contracts.gen";
import { navigate } from "@/lib/router";
import { Badge, Card } from "@/components/ui/core";
import { PageHeader, RoomName } from "@/components/common/domain";
import { RoomMasterDetail } from "@/features/rooms/room-master-detail";
import { DelegateManager } from "./delegate-manager";
import { t, tn } from "@/lib/i18n";

function Overview({ rooms }: { rooms: RoomSummary[] }) {
  return (
    <Card>
      <ul className="divide-y divide-line">
        {rooms.map((r) => (
          <li key={r.id}>
            <button type="button" onClick={() => navigate(`/delegates/${encodeURIComponent(r.id)}`)} className="grid w-full grid-cols-[minmax(0,1fr)_auto] items-center gap-4 px-4 py-2.5 text-left hover:bg-hover">
              <span className="min-w-0">
                <RoomName name={r.displayName} className="text-[12.5px] font-medium" />
                <span className="t-mono block truncate text-[10.5px] text-ink-3">{r.primarySmtpAddress}</span>
              </span>
              {r.delegateCount == null
                ? <span className="text-[11.5px] text-ink-3">{t("Open to read")}</span>
                : r.delegateCount > 0
                  ? <Badge tone="accent">{tn(r.delegateCount, "{n} delegate", "{n} delegates")}</Badge>
                  : <span className="text-[11.5px] text-ink-3">{t("No booking delegates")}</span>}
            </button>
          </li>
        ))}
      </ul>
    </Card>
  );
}

export default function DelegatesPage({ roomId }: { roomId?: string }) {
  return (
    <div>
      <PageHeader title={t("Resource delegates")} description={t("Booking delegates approve requests. Calendar folder permissions, Full Access, Send As and Send on Behalf are separate mechanisms and are never granted as a side effect.")} />
      <RoomMasterDetail basePath="/delegates" roomId={roomId} overview={(rooms) => <Overview rooms={rooms} />}>
        {(detail) => <DelegateManager roomId={detail.mailbox.id} />}
      </RoomMasterDetail>
    </div>
  );
}
