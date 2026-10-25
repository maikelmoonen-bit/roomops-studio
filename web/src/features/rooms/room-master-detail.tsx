import { useMemo, useState, type ReactNode } from "react";
import { ArrowLeft, Search } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { RoomDetail, RoomSummary } from "@/bridge/contracts.gen";
import { navigate } from "@/lib/router";
import { Card, Input, Skeleton } from "@/components/ui/core";
import { Combobox } from "@/components/ui/controls";
import { EmptyState, ErrorState } from "@/components/common/domain";
import { t, tn } from "@/lib/i18n";

/**
 * Without a room selected: a full-width overview of every room (filtered by the search box above it). With a room selected:
 * its editor, with a way back to all rooms and a switcher. The selection lives in the URL.
 */
export function RoomMasterDetail({ basePath, roomId, overview, children }: { basePath: string; roomId?: string; overview: (rooms: RoomSummary[]) => ReactNode; children: (detail: RoomDetail, refetch: () => void) => ReactNode }) {
  const rooms = useBridgeQuery("rooms.list", { refresh: false });
  const [search, setSearch] = useState("");
  const detail = useBridgeQuery("rooms.get", { roomId: roomId ?? "", refresh: false }, { enabled: !!roomId });
  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (rooms.data?.rooms ?? []).filter((r) => !q || `${r.displayName} ${r.primarySmtpAddress} ${r.building ?? ""}`.toLowerCase().includes(q));
  }, [rooms.data, search]);

  if (!roomId) {
    return (
      <div className="space-y-3">
        <div className="flex items-center gap-3">
          <Input data-page-search className="w-80" leading={<Search />} placeholder={t("Find a room")} value={search} onChange={(e) => setSearch(e.target.value)} aria-label={t("Find a room")} />
          {rooms.data && <span className="text-[12px] text-ink-3">{tn(filtered.length, "{n} room", "{n} rooms")}</span>}
        </div>
        {rooms.isLoading && <div className="space-y-3"><Skeleton className="h-24" /><Skeleton className="h-80" /></div>}
        {rooms.error && <Card><ErrorState error={rooms.error} onRetry={() => void rooms.refetch()} /></Card>}
        {rooms.data && (rooms.data.rooms.length === 0
          ? <EmptyState title={t("No rooms")} />
          : filtered.length === 0 ? <p className="px-1 py-6 text-[12.5px] text-ink-3">{t("No rooms match.")}</p> : overview(filtered))}
      </div>
    );
  }

  return (
    <div className="min-w-0">
      <div className="mb-3 flex items-center gap-2">
        <button type="button" onClick={() => navigate(basePath)} className="inline-flex h-[var(--ctl-h)] items-center gap-1 rounded-sm px-2 text-[12.5px] text-ink-3 hover:bg-hover hover:text-ink"><ArrowLeft className="size-3.5" />{t("All rooms")}</button>
        <div className="w-80">
          <Combobox value={roomId} onChange={(id) => navigate(`${basePath}/${encodeURIComponent(id)}`)} options={(rooms.data?.rooms ?? []).map((r) => ({ value: r.id, label: r.displayName, hint: r.primarySmtpAddress }))} searchPlaceholder={t("Switch room")} />
        </div>
      </div>
      {detail.isLoading && <div className="space-y-3"><Skeleton className="h-36" /><Skeleton className="h-80" /></div>}
      {detail.error && <Card><ErrorState error={detail.error} onRetry={() => void detail.refetch()} /></Card>}
      {detail.data && children(detail.data, () => void detail.refetch())}
    </div>
  );
}
