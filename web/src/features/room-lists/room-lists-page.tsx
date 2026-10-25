import { useMemo, useState, type DragEvent } from "react";
import { AlertTriangle, ArrowRight, ChevronDown, GripVertical, Kanban, List, Pencil, Plus, Repeat, Trash2, Undo2 } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { ChangeIntent, RoomListView, RoomSummary } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { setQuery, useRoute } from "@/lib/router";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Card, Input, Skeleton } from "@/components/ui/core";
import { Segmented } from "@/components/ui/controls";
import { Dialog } from "@/components/ui/overlay";
import { EmptyState, ErrorState, Identifier, InlineNotice, PageHeader, RoomName } from "@/components/common/domain";
import { ConvertGroupDialog, CreateRoomListDialog } from "./room-list-dialogs";
import { t, tn } from "@/lib/i18n";

const UNASSIGNED = "__unassigned";

interface Move {
  room: RoomSummary;
  from: string;
  to: string;
  copy: boolean;
}

export default function RoomListsPage() {
  const route = useRoute();
  const lists = useBridgeQuery("roomLists.list", { refresh: false });
  const rooms = useBridgeQuery("rooms.list", { refresh: false });
  const { review } = usePlanFlow();
  const [view, setView] = useState<"list" | "board">("list");
  const [dialog, setDialog] = useState<"create" | "convert" | null>(route.query.get("create") ? "create" : null);
  const [moves, setMoves] = useState<Move[]>([]);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [renaming, setRenaming] = useState<{ id: string; name: string } | null>(null);

  const listMembers = useMemo(() => {
    const map = new Map<string, Set<string>>();
    (lists.data ?? []).forEach((l) => map.set(l.id, new Set(l.members.map((m) => m.primarySmtpAddress.toLowerCase()))));
    return map;
  }, [lists.data]);

  // Board state = server state with pending moves applied.
  const board = useMemo(() => {
    const columns = new Map<string, RoomSummary[]>();
    const all = rooms.data?.rooms ?? [];
    (lists.data ?? []).forEach((l) => columns.set(l.id, all.filter((r) => listMembers.get(l.id)?.has(r.primarySmtpAddress.toLowerCase()))));
    columns.set(UNASSIGNED, all.filter((r) => r.roomLists.length === 0));
    for (const m of moves) {
      if (!m.copy && m.from !== UNASSIGNED) columns.set(m.from, (columns.get(m.from) ?? []).filter((r) => r.id !== m.room.id));
      if (m.to !== UNASSIGNED && !(columns.get(m.to) ?? []).some((r) => r.id === m.room.id)) columns.set(m.to, [...(columns.get(m.to) ?? []), m.room]);
      if (m.to === UNASSIGNED) columns.set(UNASSIGNED, [...(columns.get(UNASSIGNED) ?? []), m.room]);
    }
    return columns;
  }, [rooms.data, lists.data, listMembers, moves]);

  const onDrop = (e: DragEvent, to: string) => {
    e.preventDefault();
    const data = JSON.parse(e.dataTransfer.getData("application/x-roomops-room") || "null") as { roomId: string; from: string } | null;
    if (!data || data.from === to) return;
    const room = rooms.data?.rooms.find((r) => r.id === data.roomId);
    if (!room) return;
    if (to === UNASSIGNED && data.from === UNASSIGNED) return;
    setMoves((m) => [...m, { room, from: data.from, to, copy: e.altKey || e.ctrlKey || data.from === UNASSIGNED }]);
  };

  const pendingIntents = (): ChangeIntent[] => {
    const adds = new Map<string, Set<string>>();
    const removes = new Map<string, Set<string>>();
    for (const m of moves) {
      if (m.to !== UNASSIGNED) adds.set(m.to, new Set([...(adds.get(m.to) ?? []), m.room.id]));
      if (!m.copy && m.from !== UNASSIGNED) removes.set(m.from, new Set([...(removes.get(m.from) ?? []), m.room.primarySmtpAddress]));
      if (m.to === UNASSIGNED && m.from !== UNASSIGNED) removes.set(m.from, new Set([...(removes.get(m.from) ?? []), m.room.primarySmtpAddress]));
    }
    const ids = new Set([...adds.keys(), ...removes.keys()]);
    return [...ids].map((id) => ({ kind: "updateRoomList", roomListId: id, displayName: null, addRoomIds: [...(adds.get(id) ?? [])], removeMemberAddresses: [...(removes.get(id) ?? [])] }));
  };

  const listName = (id: string) => (id === UNASSIGNED ? t("No room list") : lists.data?.find((l) => l.id === id)?.displayName ?? id);

  return (
    <div>
      <PageHeader
        title={t("Room lists")}
        description={t("Distribution groups that contain room mailboxes, used by Outlook Room Finder. Membership is separate from the Microsoft Places building and floor hierarchy.")}
        actions={
          <>
            <Button icon={<Repeat />} onClick={() => setDialog("convert")}>{t("Convert a group")}</Button>
            <Button variant="primary" icon={<Plus />} onClick={() => setDialog("create")}>{t("Create room list")}</Button>
          </>
        }
      />
      <div className="mb-3 flex items-center justify-between">
        <Segmented value={view} onChange={setView} options={[{ value: "list", label: t("Lists"), icon: <List /> }, { value: "board", label: t("Board"), icon: <Kanban /> }]} />
        {lists.data && <span className="text-[12px] text-ink-3">{tn(lists.data.length, "{n} room list", "{n} room lists")} · {tn(board.get(UNASSIGNED)?.length ?? 0, "{n} room in no list", "{n} rooms in no list")}</span>}
      </div>

      {(lists.isLoading || rooms.isLoading) && <Skeleton className="h-96" />}
      {lists.error && <Card><ErrorState error={lists.error} onRetry={() => void lists.refetch()} /></Card>}

      {lists.data && view === "list" && (
        <div className="space-y-2">
          {lists.data.length === 0 && <Card><EmptyState title={t("No room lists")} description={t("Create a room list so rooms can be browsed in Outlook Room Finder.")} action={<Button variant="primary" onClick={() => setDialog("create")}>{t("Create room list")}</Button>} /></Card>}
          {lists.data.map((l: RoomListView) => (
            <Card key={l.id} className="overflow-hidden">
              <div className="flex items-center gap-3 px-4 py-3">
                <button type="button" onClick={() => setExpanded(expanded === l.id ? null : l.id)} className="flex min-w-0 flex-1 items-center gap-3 text-left" aria-expanded={expanded === l.id}>
                  <ChevronDown className={cn("size-4 shrink-0 text-ink-3 transition-transform", expanded === l.id && "rotate-180")} />
                  <span className="min-w-0">
                    {renaming?.id === l.id ? null : <span className="block text-[13.5px] font-semibold">{l.displayName}</span>}
                    <span className="block text-[11.5px] text-ink-3"><span className="t-mono">{l.primarySmtpAddress}</span>{l.buildings.length > 0 && ` · ${t("rooms in {buildings}", { buildings: l.buildings.join(", ") })}`}</span>
                  </span>
                </button>
                {renaming?.id === l.id && (
                  <form className="flex items-center gap-2" onSubmit={(e) => { e.preventDefault(); review({ kind: "updateRoomList", roomListId: l.id, displayName: renaming.name, addRoomIds: [], removeMemberAddresses: [] }); setRenaming(null); }}>
                    <Input autoFocus value={renaming.name} onChange={(e) => setRenaming({ id: l.id, name: e.target.value })} className="w-64" />
                    <Button type="submit" size="sm" variant="primary">{t("Preview")}</Button>
                    <Button size="sm" variant="ghost" onClick={() => setRenaming(null)}>{t("Cancel")}</Button>
                  </form>
                )}
                {l.isDirSynced && <Badge tone="warn">{t("Synchronized")}</Badge>}
                {l.unexpectedMemberCount > 0 && <Badge tone="warn"><AlertTriangle />{tn(l.unexpectedMemberCount, "{n} non-room member", "{n} non-room members")}</Badge>}
                <Badge tone="outline">{tn(l.roomCount, "{n} room", "{n} rooms")}</Badge>
                <Button size="icon-sm" variant="ghost" aria-label={t("Rename {name}", { name: l.displayName })} disabled={l.isDirSynced} onClick={() => setRenaming({ id: l.id, name: l.displayName })}><Pencil /></Button>
              </div>
              {expanded === l.id && (
                <div className="border-t border-line">
                  {l.members.length === 0 && <p className="px-4 py-3 text-[12.5px] text-ink-3">{t("No members.")}</p>}
                  <ul className="divide-y divide-line">
                    {l.members.map((m) => (
                      <li key={m.id} className="flex items-center gap-3 px-4 py-2 pl-11">
                        <span className="min-w-0 flex-1"><RoomName name={m.displayName} className="text-[12.5px]" /></span>
                        <Identifier value={m.primarySmtpAddress} className="hidden md:inline-flex" />
                        {m.recipientTypeDetails !== "RoomMailbox" ? <Badge tone="warn">{m.recipientTypeDetails}</Badge> : <Badge tone="neutral">{t("Room")}</Badge>}
                        <Button size="icon-sm" variant="ghost" aria-label={t("Remove {name}", { name: m.displayName })} onClick={() => review({ kind: "updateRoomList", roomListId: l.id, displayName: null, addRoomIds: [], removeMemberAddresses: [m.primarySmtpAddress] })}><Trash2 /></Button>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </Card>
          ))}
        </div>
      )}

      {lists.data && rooms.data && view === "board" && (
        <>
          <InlineNotice tone="info" className="mb-3">{t("Drag rooms between lists to propose membership changes. Dragging moves a room; hold Alt or Ctrl to add it without removing it from its current list. Nothing changes until you preview and apply.")}</InlineNotice>
          {moves.length > 0 && (
            <Card className="mb-3 flex flex-wrap items-center gap-3 border-[color-mix(in_srgb,var(--accent)_45%,transparent)] px-4 py-2.5">
              <span className="text-[12.5px] font-semibold">{tn(moves.length, "{n} proposed change", "{n} proposed changes")}</span>
              <ul className="flex min-w-0 flex-1 flex-wrap gap-1.5">
                {moves.map((m, i) => (
                  <li key={i}><Badge tone="outline" className="gap-1">{m.room.displayName.split(" ").slice(0, 2).join(" ")} {m.copy ? "+" : ""}<ArrowRight />{listName(m.to)}</Badge></li>
                ))}
              </ul>
              <Button size="sm" variant="ghost" icon={<Undo2 />} onClick={() => setMoves((m) => m.slice(0, -1))}>{t("Undo")}</Button>
              <Button size="sm" variant="ghost" onClick={() => setMoves([])}>{t("Discard")}</Button>
              <Button size="sm" variant="primary" onClick={() => review({ kind: "composite", title: t("Update room list membership"), intents: pendingIntents() }, { onCompleted: () => setMoves([]) })}>{t("Preview changes")}</Button>
            </Card>
          )}
          <div className="scroll-area flex gap-3 pb-3">
            {[UNASSIGNED, ...lists.data.map((l) => l.id)].map((id) => (
              <div key={id} onDragOver={(e) => e.preventDefault()} onDrop={(e) => onDrop(e, id)} className={cn("flex w-[260px] shrink-0 flex-col rounded-lg border bg-panel", id === UNASSIGNED ? "border-dashed border-line-strong" : "border-line")} data-testid={`board-column-${id}`}>
                <div className="flex items-center justify-between border-b border-line px-3 py-2">
                  <span className="truncate text-[12.5px] font-semibold">{listName(id)}</span>
                  <Badge tone="neutral">{board.get(id)?.length ?? 0}</Badge>
                </div>
                <div className="scroll-area flex max-h-[520px] min-h-[120px] flex-col gap-1.5 p-2">
                  {(board.get(id) ?? []).map((room) => (
                    <div
                      key={room.id}
                      draggable
                      onDragStart={(e) => e.dataTransfer.setData("application/x-roomops-room", JSON.stringify({ roomId: room.id, from: id }))}
                      className="group flex cursor-grab items-center gap-1.5 rounded-sm border border-line bg-surface px-2 py-1.5 active:cursor-grabbing"
                    >
                      <GripVertical className="size-3.5 shrink-0 text-ink-3" />
                      <RoomName name={room.displayName} className="min-w-0 text-[12px]" />
                      {room.capacity && <span className="ml-auto text-[11px] tabular text-ink-3">{room.capacity}</span>}
                    </div>
                  ))}
                  {(board.get(id) ?? []).length === 0 && <p className="py-6 text-center text-[11.5px] text-ink-3">{t("Drop rooms here")}</p>}
                </div>
              </div>
            ))}
          </div>
        </>
      )}

      <Dialog open={dialog !== null} onOpenChange={(o) => { if (!o) { setDialog(null); setQuery("create", null); } }}>
        {dialog === "create" && <CreateRoomListDialog onClose={() => { setDialog(null); setQuery("create", null); }} />}
        {dialog === "convert" && <ConvertGroupDialog onClose={() => setDialog(null)} />}
      </Dialog>
    </div>
  );
}
