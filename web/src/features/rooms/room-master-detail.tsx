import { useMemo, useState, type ReactNode } from "react";
import { ArrowLeft, Search } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { RoomDetail, RoomSummary } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { navigate } from "@/lib/router";
import { Card, Input, Skeleton } from "@/components/ui/core";
import { Combobox } from "@/components/ui/controls";
import { EmptyState, ErrorState, RoomName } from "@/components/common/domain";
import { t } from "@/lib/i18n";

/** Room list on the left, an editor for the selected room on the right. The selection lives in the URL. */
export function RoomMasterDetail({ basePath, roomId, meta, empty, children }: { basePath: string; roomId?: string; meta: (r: RoomSummary) => ReactNode; empty: ReactNode; children: (detail: RoomDetail, refetch: () => void) => ReactNode }) {
  const rooms = useBridgeQuery("rooms.list", { refresh: false });
  const [search, setSearch] = useState("");
  const detail = useBridgeQuery("rooms.get", { roomId: roomId ?? "", refresh: false }, { enabled: !!roomId });
  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (rooms.data?.rooms ?? []).filter((r) => !q || `${r.displayName} ${r.primarySmtpAddress} ${r.building ?? ""}`.toLowerCase().includes(q));
  }, [rooms.data, search]);

  return (
    <div className={cn("grid gap-4", roomId ? "min-[1800px]:grid-cols-[300px_minmax(0,1fr)]" : "lg:grid-cols-[300px_minmax(0,1fr)]")}>
      {/* With a room open, the list gives way to a compact switcher until the window is wide enough for both. */}
      <Card className={cn("max-h-[calc(100vh-200px)] flex-col overflow-hidden lg:sticky lg:top-0", roomId ? "hidden min-[1800px]:flex" : "flex")}>
        <div className="border-b border-line p-2">
          <Input data-page-search leading={<Search />} placeholder={t("Find a room")} value={search} onChange={(e) => setSearch(e.target.value)} aria-label={t("Find a room")} />
        </div>
        <div className="scroll-area flex-1 p-1">
          {rooms.isLoading && Array.from({ length: 8 }, (_, i) => <Skeleton key={i} className="m-1 h-11" />)}
          {filtered.map((r) => (
            <button
              key={r.id}
              type="button"
              onClick={() => navigate(`${basePath}/${encodeURIComponent(r.id)}`)}
              className={cn("flex w-full flex-col gap-0.5 rounded-sm px-2.5 py-2 text-left", r.id === roomId ? "bg-[color-mix(in_srgb,var(--accent)_12%,transparent)]" : "hover:bg-hover")}
              aria-current={r.id === roomId ? "true" : undefined}
            >
              <RoomName name={r.displayName} className="text-[12.5px] font-medium" />
              <span className="truncate text-[11.5px] text-ink-3">{meta(r)}</span>
            </button>
          ))}
          {rooms.data && filtered.length === 0 && <p className="px-3 py-6 text-center text-[12px] text-ink-3">{t("No rooms match.")}</p>}
        </div>
      </Card>
      <div className="min-w-0">
        {roomId && (
          <div className="mb-3 flex items-center gap-2 min-[1800px]:hidden">
            <button type="button" onClick={() => navigate(basePath)} className="inline-flex h-[var(--ctl-h)] items-center gap-1 rounded-sm px-2 text-[12.5px] text-ink-3 hover:bg-hover hover:text-ink"><ArrowLeft className="size-3.5" />{t("All rooms")}</button>
            <div className="w-80">
              <Combobox value={roomId} onChange={(id) => navigate(`${basePath}/${encodeURIComponent(id)}`)} options={(rooms.data?.rooms ?? []).map((r) => ({ value: r.id, label: r.displayName, hint: r.primarySmtpAddress }))} searchPlaceholder={t("Switch room")} />
            </div>
          </div>
        )}
        {!roomId && empty}
        {roomId && detail.isLoading && <div className="space-y-3"><Skeleton className="h-36" /><Skeleton className="h-80" /></div>}
        {roomId && detail.error && <Card><ErrorState error={detail.error} onRetry={() => void detail.refetch()} /></Card>}
        {roomId && detail.data && children(detail.data, () => void detail.refetch())}
        {!roomId && rooms.data?.rooms.length === 0 && <EmptyState title={t("No rooms")} />}
      </div>
    </div>
  );
}
