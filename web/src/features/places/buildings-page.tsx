import { useMemo, useState, type DragEvent } from "react";
import { Building2, ChevronRight, DoorOpen, Grid2x2, Layers3, MapPin, Plus, Search } from "lucide-react";
import { useBridgeQuery } from "@/bridge/hooks";
import type { Place, PlaceNode, PlaceType } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { navigate } from "@/lib/router";
import { usePlanFlow } from "@/state/plan-flow";
import { Button } from "@/components/ui/button";
import { Badge, Card, CardHeader, Input, Skeleton } from "@/components/ui/core";
import { Dialog } from "@/components/ui/overlay";
import { EmptyState, ErrorState, InlineNotice, PageHeader, RoomName } from "@/components/common/domain";
import { CreatePlaceDialog, PlaceEditor } from "./place-dialogs";
import { t, tn } from "@/lib/i18n";

const icon: Record<string, React.ReactNode> = { Building: <Building2 />, Floor: <Layers3 />, Section: <Grid2x2 />, Room: <DoorOpen /> };
const allowedParents: Record<string, PlaceType[]> = { Floor: ["Building"], Section: ["Floor"], Room: ["Floor", "Section"] };

interface Dragging {
  kind: "place" | "unplacedRoom";
  id: string;
  type: PlaceType;
  name: string;
  roomId?: string;
}

export default function BuildingsPage() {
  const tree = useBridgeQuery("places.tree", { refresh: false });
  const rooms = useBridgeQuery("rooms.list", { refresh: false });
  const { review } = usePlanFlow();
  const [selected, setSelected] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [creating, setCreating] = useState<{ type: PlaceType; parent: Place | null } | null>(null);
  const [dragging, setDragging] = useState<Dragging | null>(null);
  const [hover, setHover] = useState<string | null>(null);
  const [filter, setFilter] = useState("");

  const nodes = tree.data?.nodes ?? [];
  const byId = useMemo(() => new Map(nodes.map((n) => [n.place.placeId, n])), [nodes]);
  const children = useMemo(() => {
    const map = new Map<string, PlaceNode[]>();
    nodes.forEach((n) => { if (n.place.parentId) map.set(n.place.parentId, [...(map.get(n.place.parentId) ?? []), n]); });
    map.forEach((list) => list.sort((a, b) => (a.place.sortOrder ?? 0) - (b.place.sortOrder ?? 0) || a.place.displayName.localeCompare(b.place.displayName)));
    return map;
  }, [nodes]);
  const buildings = nodes.filter((n) => n.place.type === "Building").sort((a, b) => a.place.displayName.localeCompare(b.place.displayName));
  const orphans = nodes.filter((n) => n.place.type !== "Building" && n.place.type !== "Room" && (!n.place.parentId || !byId.has(n.place.parentId)));
  const roomIdBySmtp = useMemo(() => new Map((rooms.data?.rooms ?? []).map((r) => [r.primarySmtpAddress.toLowerCase(), r.id])), [rooms.data]);
  const selectedNode = selected ? byId.get(selected) : undefined;

  const canDrop = (target: Place) => {
    if (!dragging) return false;
    if (dragging.id === target.placeId) return false;
    if (!(allowedParents[dragging.type] ?? []).includes(target.type)) return false;
    // Prevent cycles: target may not be a descendant of the dragged place.
    let current: Place | undefined = target;
    while (current?.parentId) {
      if (current.parentId === dragging.id) return false;
      current = byId.get(current.parentId)?.place;
    }
    return true;
  };

  const drop = (e: DragEvent, target: Place) => {
    e.preventDefault();
    setHover(null);
    if (!dragging || !canDrop(target)) return;
    if (dragging.type === "Room") {
      const roomId = dragging.roomId;
      if (!roomId) return;
      review({ kind: "updatePlacesRoom", roomIds: [roomId], fields: { ParentId: target.placeId } });
    } else {
      review({ kind: "updatePlace", placeId: dragging.id, fields: { ParentId: target.placeId } });
    }
    setDragging(null);
  };

  const matches = (n: PlaceNode): boolean => !filter || n.place.displayName.toLowerCase().includes(filter.toLowerCase()) || (children.get(n.place.placeId) ?? []).some(matches);

  const renderNode = (n: PlaceNode, depth: number): React.ReactNode => {
    if (!matches(n)) return null;
    const kids = children.get(n.place.placeId) ?? [];
    const isCollapsed = collapsed.has(n.place.placeId) && !filter;
    const droppable = canDrop(n.place);
    const roomId = n.place.type === "Room" && n.place.emailAddress ? roomIdBySmtp.get(n.place.emailAddress.toLowerCase()) : undefined;
    return (
      <li key={n.place.placeId}>
        <div
          draggable={n.place.type !== "Building"}
          onDragStart={(e) => { e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData("text/plain", n.place.placeId); setDragging({ kind: "place", id: n.place.placeId, type: n.place.type, name: n.place.displayName, roomId }); }}
          onDragEnd={() => { setDragging(null); setHover(null); }}
          onDragOver={(e) => { if (droppable) { e.preventDefault(); setHover(n.place.placeId); } }}
          onDragLeave={() => setHover((h) => (h === n.place.placeId ? null : h))}
          onDrop={(e) => drop(e, n.place)}
          onClick={() => setSelected(n.place.placeId)}
          className={cn(
            "group flex h-8 cursor-default items-center gap-1.5 rounded-sm pr-2 text-[12.5px] transition-colors",
            selected === n.place.placeId ? "bg-[color-mix(in_srgb,var(--accent)_13%,transparent)] text-ink" : "text-ink-2 hover:bg-hover hover:text-ink",
            dragging && droppable && "outline-1 outline-dashed outline-accent",
            hover === n.place.placeId && "bg-[color-mix(in_srgb,var(--accent)_22%,transparent)]",
          )}
          style={{ paddingLeft: 8 + depth * 18 }}
          role="treeitem"
          aria-expanded={kids.length ? !isCollapsed : undefined}
          aria-selected={selected === n.place.placeId}
        >
          {kids.length > 0 ? (
            <button type="button" aria-label={t("Toggle")} onClick={(e) => { e.stopPropagation(); setCollapsed((c) => { const s = new Set(c); if (s.has(n.place.placeId)) s.delete(n.place.placeId); else s.add(n.place.placeId); return s; }); }} className="rounded-[3px] p-0.5 text-ink-3 hover:text-ink">
              <ChevronRight className={cn("size-3.5 transition-transform", !isCollapsed && "rotate-90")} />
            </button>
          ) : <span className="w-[18px]" />}
          <span className="text-ink-3 [&_svg]:size-3.5">{icon[n.place.type]}</span>
          {n.place.type === "Room" ? <RoomName name={n.place.displayName} className="min-w-0" /> : <span className="truncate font-medium">{n.place.displayName}</span>}
          {n.place.type !== "Room" && n.roomCount > 0 && <span className="ml-auto text-[11px] tabular text-ink-3">{n.roomCount}</span>}
          {n.issues.length > 0 && <Badge tone="warn" className="ml-auto">!</Badge>}
        </div>
        {kids.length > 0 && !isCollapsed && <ul role="group">{kids.map((k) => renderNode(k, depth + 1))}</ul>}
      </li>
    );
  };

  if (tree.isLoading) return <div><PageHeader title={t("Buildings & floors")} /><Skeleton className="h-[520px]" /></div>;
  if (tree.error) return <div><PageHeader title={t("Buildings & floors")} /><Card><ErrorState error={tree.error} onRetry={() => void tree.refetch()} /></Card></div>;

  return (
    <div>
      <PageHeader
        title={t("Buildings & floors")}
        description={t("The Microsoft Places location hierarchy. Floors belong to buildings, sections to floors, and rooms to a floor or section. Drag to reorganize; every move is previewed, applied and read back.")}
        actions={<Button variant="primary" icon={<Plus />} disabled={!tree.data?.available} onClick={() => setCreating({ type: "Building", parent: null })}>{t("Add building")}</Button>}
      />
      {!tree.data?.available ? (
        <Card><EmptyState icon={<MapPin />} title={t("Microsoft Places is not connected")} description={tree.data?.error ?? t("Connect Microsoft Places to manage buildings, floors and sections.")} action={<Button variant="primary" onClick={() => navigate("/connections")}>{t("Open connections")}</Button>} /></Card>
      ) : (
        <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_420px]">
          <Card className="relative overflow-hidden">
            <div className="blueprint pointer-events-none absolute inset-0 opacity-50" />
            <div className="relative flex items-center gap-2 border-b border-line p-2">
              <Input data-page-search leading={<Search />} placeholder={t("Find a building, floor, section or room")} value={filter} onChange={(e) => setFilter(e.target.value)} className="max-w-sm" />
              <span className="ml-auto text-[11.5px] text-ink-3">{tn(buildings.length, "{n} building", "{n} buildings")} · {tn(nodes.filter((n) => n.place.type === "Floor").length, "{n} floor", "{n} floors")} · {tn(nodes.filter((n) => n.place.type === "Room").length, "{n} room", "{n} rooms")}</span>
            </div>
            <ul role="tree" aria-label={t("Location hierarchy")} className="relative max-h-[calc(100vh-280px)] min-h-[420px] overflow-auto p-2">
              <li className="t-eyebrow px-2 pb-1">{t("Organization")}</li>
              {buildings.map((b) => renderNode(b, 0))}
              {orphans.length > 0 && (
                <>
                  <li className="t-eyebrow px-2 pb-1 pt-3 text-warn-ink">{t("Without a valid parent")}</li>
                  {orphans.map((o) => renderNode(o, 0))}
                </>
              )}
            </ul>
          </Card>
          <div className="space-y-3">
            {selectedNode ? (
              selectedNode.place.type === "Room" ? (
                <Card>
                  <CardHeader eyebrow={t("Room")} title={selectedNode.place.displayName} description={selectedNode.place.emailAddress ?? undefined} />
                  <div className="flex gap-2 border-t border-line p-4">
                    {selectedNode.place.emailAddress && roomIdBySmtp.get(selectedNode.place.emailAddress.toLowerCase()) && (
                      <Button variant="primary" onClick={() => navigate(`/rooms/${encodeURIComponent(roomIdBySmtp.get(selectedNode.place.emailAddress!.toLowerCase())!)}?tab=location`)}>{t("Edit room metadata")}</Button>
                    )}
                  </div>
                </Card>
              ) : (
                <>
                  <PlaceEditor place={selectedNode.place} />
                  {selectedNode.place.type !== "Section" && (
                    <Button icon={<Plus />} className="w-full" onClick={() => setCreating({ type: selectedNode.place.type === "Building" ? "Floor" : "Section", parent: selectedNode.place })}>
                      {selectedNode.place.type === "Building" ? t("Add floor to {name}", { name: selectedNode.place.displayName }) : t("Add section to {name}", { name: selectedNode.place.displayName })}
                    </Button>
                  )}
                </>
              )
            ) : (
              <Card className="px-4 py-6 text-center text-[12.5px] text-ink-3">{t("Select a building, floor or section to edit it.")}</Card>
            )}
            <Card>
              <CardHeader title={t("Rooms without a location")} description={t("Drag a room onto a floor or section to place it.")} />
              <ul className="scroll-area max-h-72 divide-y divide-line border-t border-line">
                {(tree.data?.unplacedRooms ?? []).length === 0 && <li className="px-4 py-3 text-[12.5px] text-ink-3">{t("Every room has a location.")}</li>}
                {(tree.data?.unplacedRooms ?? []).map((r) => (
                  <li
                    key={r.id}
                    draggable
                    onDragStart={(e) => { e.dataTransfer.setData("text/plain", r.id); setDragging({ kind: "unplacedRoom", id: r.id, type: "Room", name: r.displayName, roomId: r.id }); }}
                    onDragEnd={() => setDragging(null)}
                    className="flex cursor-grab items-center gap-2 px-4 py-2 hover:bg-hover"
                  >
                    <DoorOpen className="size-3.5 text-ink-3" /><RoomName name={r.displayName} className="text-[12.5px]" />
                  </li>
                ))}
              </ul>
            </Card>
            <InlineNotice tone="info">{t("Places shows new buildings, floors and sections right away. Changes to rooms can take up to 24 hours to appear in Places Finder and Outlook.")}</InlineNotice>
          </div>
        </div>
      )}
      <Dialog open={creating !== null} onOpenChange={(o) => !o && setCreating(null)}>
        {creating && <CreatePlaceDialog type={creating.type} parent={creating.parent} onClose={() => setCreating(null)} />}
      </Dialog>
    </div>
  );
}
