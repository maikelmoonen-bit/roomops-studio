import { useEffect, useMemo, useRef, useState } from "react";
import { flexRender, getCoreRowModel, getSortedRowModel, useReactTable, type ColumnDef, type SortingState, type VisibilityState } from "@tanstack/react-table";
import { useVirtualizer } from "@tanstack/react-virtual";
import { AlertOctagon, AlertTriangle, ArrowDownUp, Bookmark, Check, CircleHelp, Columns3, Download, Filter, LayoutGrid, Accessibility, MonitorPlay, Plus, Rows3, ScanSearch, Search, Trash2, Users, X, Layers, ListPlus, CalendarCog, RefreshCcw } from "lucide-react";
import { toast } from "sonner";
import { bridge } from "@/bridge/client";
import { useBridgeQuery } from "@/bridge/hooks";
import type { BookingMode, RoomSummary } from "@/bridge/contracts.gen";
import { cn, formatRelative } from "@/lib/format";
import { saveText, toCsv } from "@/lib/files";
import { Link, navigate, useRoute } from "@/lib/router";
import { useSession } from "@/state/session";
import { Button } from "@/components/ui/button";
import { Badge, Card, Input, Skeleton } from "@/components/ui/core";
import { Checkbox, Segmented } from "@/components/ui/controls";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger, Popover, PopoverContent, PopoverTrigger, Tooltip, ContextMenu, ContextMenuContent, ContextMenuItem, ContextMenuSeparator, ContextMenuTrigger } from "@/components/ui/overlay";
import { EmptyState, ErrorState, PageHeader, RoomName } from "@/components/common/domain";
import { applyFilters, emptyFilters, quickFilters, type QuickFilter, type RoomFilterState } from "./room-filters";
import { RoomBulkDialogs } from "./bulk-actions";
import { t, tn, tx } from "@/lib/i18n";

const modeLabels: Record<BookingMode | "NotScanned", string> = {
  Automatic: tx("Everyone"),
  DelegateApproval: tx("Delegate approval"),
  Restricted: tx("Selected users only"),
  Hybrid: tx("Hybrid"),
  Custom: tx("Custom"),
  Reserved: tx("Reserved"),
  ManualAutoUpdate: tx("Manual (AutoUpdate)"),
  ProcessingDisabled: tx("Processing disabled"),
  NotScanned: tx("Not read yet"),
};

export function BookingModeBadge({ mode, label }: { mode: BookingMode | null; label?: string | null }) {
  if (!mode) return <Tooltip content={t("Calendar processing hasn't been read for this room. Open it or run a configuration scan.")}><span className="inline-flex items-center gap-1 text-[12px] text-ink-3"><CircleHelp className="size-3.5" />{t("Not read")}</span></Tooltip>;
  const tone = mode === "Automatic" ? "ok" : mode === "DelegateApproval" || mode === "Restricted" || mode === "Hybrid" ? "accent" : mode === "Custom" ? "info" : "warn";
  return <Badge tone={tone}>{label ?? t(modeLabels[mode])}</Badge>;
}

function HealthCell({ room }: { room: RoomSummary }) {
  const f = room.findings;
  if (f.critical + f.warning + f.recommendation === 0 && f.unableToVerify === 0) return <span className="inline-flex items-center gap-1 text-[12px] text-ok"><Check className="size-3.5" />{t("No findings")}</span>;
  return (
    <span className="flex items-center gap-2 text-[12px] tabular">
      {f.critical > 0 && <span className="inline-flex items-center gap-0.5 text-bad"><AlertOctagon className="size-3.5" />{f.critical}</span>}
      {f.warning > 0 && <span className="inline-flex items-center gap-0.5 text-warn-ink"><AlertTriangle className="size-3.5" />{f.warning}</span>}
      {f.recommendation > 0 && <span className="text-info">{f.recommendation} {t("rec.")}</span>}
      {f.critical + f.warning + f.recommendation === 0 && f.unableToVerify > 0 && <span className="inline-flex items-center gap-1 text-ink-3"><CircleHelp className="size-3.5" />{t("Unverified")}</span>}
    </span>
  );
}

function windowText(r: RoomSummary) {
  if (r.bookingWindowInDays == null) return "—";
  const max = r.maximumDurationInMinutes === 0 ? "∞" : r.maximumDurationInMinutes! % 60 === 0 ? `${r.maximumDurationInMinutes! / 60} h` : `${r.maximumDurationInMinutes} m`;
  return `${r.bookingWindowInDays} d · ${max}`;
}

const columnLabels: Record<string, string> = {
  room: tx("Room"), capacity: tx("Capacity"), location: tx("Location"), roomLists: tx("Room lists"), bookingMode: tx("Booking mode"), delegates: tx("Delegates"), window: tx("Window"), health: tx("Validation"), template: tx("Template"), timeZone: tx("Time zone"),
};

const defaultVisibility: VisibilityState = { template: false, timeZone: false };

interface ViewState {
  filters: RoomFilterState;
  sorting: SortingState;
  visibility: VisibilityState;
  layout: "table" | "cards";
}

export default function RoomsPage() {
  const route = useRoute();
  const { settings, updateSettings } = useSession();
  const inventory = useBridgeQuery("rooms.list", { refresh: false });
  const [filters, setFilters] = useState<RoomFilterState>(() => ({
    ...emptyFilters,
    quick: (route.query.get("filter") ? [route.query.get("filter") as QuickFilter] : []),
    modes: route.query.get("mode") ? [route.query.get("mode")!] : [],
  }));
  const [sorting, setSorting] = useState<SortingState>([{ id: "room", desc: false }]);
  const [visibility, setVisibility] = useState<VisibilityState>(defaultVisibility);
  const [layout, setLayout] = useState<"table" | "cards">("table");
  const [selection, setSelection] = useState<Record<string, boolean>>({});
  const [bulk, setBulk] = useState<"template" | "booking" | "roomList" | null>(null);
  const savedViews = (settings?.savedViews ?? []).filter((v) => v.page === "rooms");

  const rooms = inventory.data?.rooms ?? [];
  const filtered = useMemo(() => applyFilters(rooms, filters), [rooms, filters]);
  const buildings = useMemo(() => [...new Set(rooms.map((r) => r.building ?? "(none)"))].sort(), [rooms]);
  const lists = useMemo(() => {
    const map = new Map<string, string>();
    rooms.forEach((r) => r.roomLists.forEach((l) => map.set(l.id, l.displayName)));
    return [...map.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [rooms]);
  const selectedRooms = rooms.filter((r) => selection[r.id]);

  const columns = useMemo<ColumnDef<RoomSummary>[]>(() => [
    {
      id: "select",
      size: 40,
      enableResizing: false,
      enableSorting: false,
      header: ({ table }) => <Checkbox aria-label={t("Select all")} checked={table.getIsAllRowsSelected() ? true : table.getIsSomeRowsSelected() ? "indeterminate" : false} onCheckedChange={(v) => table.toggleAllRowsSelected(v)} />,
      cell: ({ row }) => <Checkbox aria-label={t("Select {name}", { name: row.original.displayName })} checked={row.getIsSelected()} onCheckedChange={(v) => row.toggleSelected(v)} />,
    },
    {
      id: "room",
      size: 300,
      accessorFn: (r) => r.displayName,
      header: t("Room"),
      cell: ({ row }) => (
        <div className="min-w-0">
          <RoomName name={row.original.displayName} className="font-medium text-ink" />
          <div className="t-mono mt-0.5 truncate text-[11px] text-ink-3">{row.original.primarySmtpAddress}</div>
        </div>
      ),
    },
    { id: "capacity", size: 90, accessorFn: (r) => r.capacity ?? -1, header: t("Capacity"), cell: ({ row }) => row.original.capacity ? <span className="tabular">{row.original.capacity}</span> : <span className="text-ink-3">{t("Not set")}</span> },
    {
      id: "location",
      size: 220,
      accessorFn: (r) => r.locationPath ?? "",
      header: t("Location"),
      cell: ({ row }) => row.original.building ? (
        <div className="min-w-0 leading-tight">
          <div className="truncate">{row.original.building}</div>
          <div className="truncate text-[11.5px] text-ink-3">{[row.original.floor, row.original.section].filter(Boolean).join(" · ") || t("No floor")}</div>
        </div>
      ) : <span className="text-ink-3">{inventory.data?.placesAvailable ? t("No Places location") : t("Places not connected")}</span>,
    },
    {
      id: "roomLists",
      size: 200,
      accessorFn: (r) => r.roomLists.map((l) => l.displayName).join(", "),
      header: t("Room lists"),
      cell: ({ row }) => row.original.roomLists.length === 0 ? <span className="text-ink-3">{t("None")}</span> : (
        <div className="flex min-w-0 gap-1 overflow-hidden">
          {row.original.roomLists.slice(0, 2).map((l) => <Badge key={l.id} tone="outline" className="max-w-[140px] truncate">{l.displayName}</Badge>)}
          {row.original.roomLists.length > 2 && <Badge tone="neutral">+{row.original.roomLists.length - 2}</Badge>}
        </div>
      ),
    },
    { id: "bookingMode", size: 160, accessorFn: (r) => r.bookingModeLabel ?? "~", header: t("Booking mode"), cell: ({ row }) => <BookingModeBadge mode={row.original.bookingMode} label={row.original.bookingModeLabel} /> },
    { id: "delegates", size: 90, accessorFn: (r) => r.delegateCount ?? -1, header: t("Delegates"), cell: ({ row }) => row.original.delegateCount == null ? <span className="text-ink-3">—</span> : <span className="tabular">{row.original.delegateCount}</span> },
    { id: "window", size: 110, accessorFn: (r) => r.bookingWindowInDays ?? -1, header: t("Window"), cell: ({ row }) => <span className="tabular text-ink-2">{windowText(row.original)}</span> },
    { id: "health", size: 150, accessorFn: (r) => r.findings.critical * 1000 + r.findings.warning * 10 + r.findings.recommendation, header: t("Validation"), cell: ({ row }) => <HealthCell room={row.original} /> },
    { id: "template", size: 160, accessorFn: (r) => r.templateName ?? "", header: t("Template"), cell: ({ row }) => row.original.templateName ?? <span className="text-ink-3">—</span> },
    { id: "timeZone", size: 180, accessorFn: (r) => r.timeZone ?? "", header: t("Time zone"), cell: ({ row }) => row.original.timeZone ?? <span className="text-ink-3">—</span> },
  ], [inventory.data?.placesAvailable]);

  const table = useReactTable({
    data: filtered,
    columns,
    state: { sorting, columnVisibility: visibility, rowSelection: selection },
    getRowId: (r) => r.id,
    onSortingChange: setSorting,
    onColumnVisibilityChange: setVisibility,
    onRowSelectionChange: setSelection,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    enableColumnResizing: true,
    columnResizeMode: "onChange",
  });

  const scrollRef = useRef<HTMLDivElement>(null);
  const rowModel = table.getRowModel().rows;
  const virtualizer = useVirtualizer({ count: rowModel.length, getScrollElement: () => scrollRef.current, estimateSize: () => 52, overscan: 12 });

  useEffect(() => setSelection((s) => Object.fromEntries(Object.entries(s).filter(([id]) => rooms.some((r) => r.id === id)))), [rooms]);

  const [viewName, setViewName] = useState("");
  const saveView = async () => {
    const name = viewName.trim();
    if (!name) return;
    setViewName("");
    const state: ViewState = { filters, sorting, visibility, layout };
    await updateSettings((s) => ({ ...s, savedViews: [...s.savedViews, { id: crypto.randomUUID(), name, page: "rooms", state }] }));
    toast.success(t("View “{name}” saved", { name }));
  };
  const applyView = (state: ViewState) => {
    setFilters({ ...emptyFilters, ...state.filters });
    setSorting(state.sorting ?? []);
    setVisibility(state.visibility ?? defaultVisibility);
    setLayout(state.layout ?? "table");
  };

  const exportCsv = async () => {
    const headers = [tx("Display name"), tx("Primary SMTP address"), tx("Alias"), tx("Capacity"), tx("Building"), tx("Floor"), tx("Section"), tx("Room lists"), tx("Booking mode"), tx("Delegates"), tx("Booking window (days)"), tx("Max duration (min)"), tx("Critical"), tx("Warnings"), tx("Time zone"), tx("Directory synced")].map((h) => t(h));
    const rows = table.getSortedRowModel().rows.map(({ original: r }) => [r.displayName, r.primarySmtpAddress, r.alias, r.capacity, r.building, r.floor, r.section, r.roomLists.map((l) => l.displayName).join("; "), r.bookingModeLabel, r.delegateCount, r.bookingWindowInDays, r.maximumDurationInMinutes, r.findings.critical, r.findings.warning, r.timeZone, r.isDirSynced ? t("Yes") : t("No")]);
    if (await saveText(`roomops-rooms-${new Date().toISOString().slice(0, 10)}.csv`, "text/csv", toCsv(headers, rows))) toast.success(tn(rows.length, "Exported {n} room", "Exported {n} rooms"));
  };

  const activeFilterCount = filters.buildings.length + filters.modes.length + (filters.roomList ? 1 : 0) + filters.quick.length;

  return (
    <div>
      <PageHeader
        title={t("Rooms")}
        description={inventory.data ? `${tn(rooms.length, "{n} room mailbox", "{n} room mailboxes")} · ${t("inventory loaded {time}", { time: formatRelative(inventory.data.loadedAt) })} · ${t("booking configuration read for {n}", { n: inventory.data.scannedCount })}` : t("Exchange Online room mailboxes with their Places location and booking configuration.")}
        actions={
          <>
            <Button icon={<ScanSearch />} onClick={() => void bridge.call("scan.start", {}).then(() => toast.info(t("Configuration scan started")))}>{t("Run scan")}</Button>
            <Button variant="primary" icon={<Plus />} onClick={() => navigate("/rooms/new")}>{t("Create room")}</Button>
          </>
        }
      />

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <Input data-page-search className="w-72" leading={<Search />} placeholder={t("Search name, address, building, tag")} value={filters.search} onChange={(e) => setFilters((f) => ({ ...f, search: e.target.value }))} aria-label={t("Search rooms")} />
        <Popover>
          <PopoverTrigger asChild>
            <Button icon={<Filter />}>{t("Filters")}{activeFilterCount > 0 && <Badge tone="accent" className="ml-1">{activeFilterCount}</Badge>}</Button>
          </PopoverTrigger>
          <PopoverContent className="w-[560px] p-0">
            <div className="grid grid-cols-3 divide-x divide-line">
              <FilterColumn title={t("Building")} options={buildings.map((b) => ({ value: b, label: b }))} selected={filters.buildings} onChange={(v) => setFilters((f) => ({ ...f, buildings: v }))} />
              <FilterColumn title={t("Booking mode")} options={Object.entries(modeLabels).map(([value, label]) => ({ value, label: t(label) }))} selected={filters.modes} onChange={(v) => setFilters((f) => ({ ...f, modes: v }))} />
              <FilterColumn title={t("Room list")} options={lists.map(([value, label]) => ({ value, label }))} selected={filters.roomList ? [filters.roomList] : []} onChange={(v) => setFilters((f) => ({ ...f, roomList: v.at(-1) ?? null }))} />
            </div>
          </PopoverContent>
        </Popover>
        {quickFilters.map((q) => {
          const on = filters.quick.includes(q.key);
          return (
            <button key={q.key} type="button" onClick={() => setFilters((f) => ({ ...f, quick: on ? f.quick.filter((k) => k !== q.key) : [...f.quick, q.key] }))}
              className={cn("h-7 rounded-full border px-2.5 text-[12px] font-medium transition-colors", on ? "border-accent bg-[color-mix(in_srgb,var(--accent)_12%,transparent)] text-accent-text" : "border-line text-ink-2 hover:border-line-strong hover:text-ink")}>
              {t(q.label)}
            </button>
          );
        })}
        {activeFilterCount + (filters.search ? 1 : 0) > 0 && <Button size="sm" variant="ghost" icon={<X />} onClick={() => setFilters(emptyFilters)}>{t("Clear")}</Button>}
        <div className="ml-auto flex items-center gap-1.5">
          <DropdownMenu>
            <DropdownMenuTrigger asChild><Button icon={<Bookmark />}>{t("Views")}</Button></DropdownMenuTrigger>
            <DropdownMenuContent className="w-60">
              <DropdownMenuLabel>{t("Saved views")}</DropdownMenuLabel>
              {savedViews.length === 0 && <div className="px-2 pb-2 text-[12px] text-ink-3">{t("No saved views yet.")}</div>}
              {savedViews.map((v) => (
                <DropdownMenuItem key={v.id} onSelect={() => applyView(v.state as ViewState)}>
                  <span className="flex-1 truncate">{v.name}</span>
                  <button type="button" aria-label={t("Delete view {name}", { name: v.name })} className="rounded-[3px] p-0.5 hover:bg-hover" onClick={(e) => { e.stopPropagation(); void updateSettings((s) => ({ ...s, savedViews: s.savedViews.filter((x) => x.id !== v.id) })); }}><Trash2 className="size-3.5" /></button>
                </DropdownMenuItem>
              ))}
              <DropdownMenuSeparator />
              <form className="flex gap-1.5 p-1.5" onSubmit={(e) => { e.preventDefault(); void saveView(); }} onKeyDown={(e) => e.stopPropagation()}>
                <Input value={viewName} onChange={(e) => setViewName(e.target.value)} placeholder={t("Name this view")} aria-label={t("View name")} />
                <Button type="submit" size="icon" variant="primary" icon={<Plus />} disabled={!viewName.trim()} aria-label={t("Save view")} />
              </form>
            </DropdownMenuContent>
          </DropdownMenu>
          {layout === "table" && (
            <DropdownMenu>
              <DropdownMenuTrigger asChild><Button icon={<Columns3 />} aria-label={t("Columns")}>{t("Columns")}</Button></DropdownMenuTrigger>
              <DropdownMenuContent className="w-52">
                <DropdownMenuLabel>{t("Visible columns")}</DropdownMenuLabel>
                {table.getAllLeafColumns().filter((c) => c.id !== "select" && c.id !== "room").map((c) => (
                  <DropdownMenuItem key={c.id} onSelect={(e) => { e.preventDefault(); c.toggleVisibility(); }}>
                    <Check className={cn(c.getIsVisible() ? "opacity-100" : "opacity-0")} />{t(columnLabels[c.id])}
                  </DropdownMenuItem>
                ))}
              </DropdownMenuContent>
            </DropdownMenu>
          )}
          <Tooltip content={t("Export the filtered rooms as CSV (opens in Excel)")}><Button size="icon" icon={<Download />} aria-label={t("Export CSV")} onClick={() => void exportCsv()} /></Tooltip>
          <Segmented value={layout} onChange={setLayout} options={[{ value: "table", label: t("Table"), icon: <Rows3 /> }, { value: "cards", label: t("Cards"), icon: <LayoutGrid /> }]} />
        </div>
      </div>

      {selectedRooms.length > 0 && (
        <div className="mb-3 flex items-center gap-2 rounded-md border border-[color-mix(in_srgb,var(--accent)_40%,transparent)] bg-[color-mix(in_srgb,var(--accent)_8%,transparent)] px-3 py-2" role="toolbar" aria-label={t("Bulk actions")}>
          <span className="text-[12.5px] font-medium">{tn(selectedRooms.length, "{n} room selected", "{n} rooms selected")}</span>
          <span className="mx-1 h-4 w-px bg-line-strong" />
          <Button size="sm" variant="ghost" icon={<Layers />} onClick={() => setBulk("template")}>{t("Apply template")}</Button>
          <Button size="sm" variant="ghost" icon={<CalendarCog />} onClick={() => setBulk("booking")}>{t("Change booking settings")}</Button>
          <Button size="sm" variant="ghost" icon={<ListPlus />} onClick={() => setBulk("roomList")}>{t("Add to room list")}</Button>
          <Button size="sm" variant="ghost" className="ml-auto" onClick={() => setSelection({})}>{t("Clear selection")}</Button>
        </div>
      )}

      {inventory.isLoading && <Card className="space-y-2 p-4">{Array.from({ length: 10 }, (_, i) => <Skeleton key={i} className="h-10" />)}</Card>}
      {inventory.error && <Card><ErrorState error={inventory.error} onRetry={() => void inventory.refetch()} /></Card>}
      {inventory.data && filtered.length === 0 && (
        <Card><EmptyState icon={<Search />} title={rooms.length === 0 ? t("No room mailboxes in this tenant") : t("No rooms match these filters")} description={rooms.length === 0 ? t("Create the first room mailbox to get started.") : t("Adjust or clear the filters to see more rooms.")} action={rooms.length === 0 ? <Button variant="primary" onClick={() => navigate("/rooms/new")}>{t("Create room")}</Button> : <Button onClick={() => setFilters(emptyFilters)}>{t("Clear filters")}</Button>} /></Card>
      )}

      {inventory.data && filtered.length > 0 && layout === "table" && (
        <Card className="overflow-hidden">
          <div ref={scrollRef} className="scroll-area max-h-[calc(100vh-290px)] min-h-[320px]" role="grid" aria-rowcount={rowModel.length}>
            <div style={{ width: table.getTotalSize(), minWidth: "100%" }}>
              <div className="sticky top-0 z-10 flex border-b border-line bg-surface" role="row">
                {table.getHeaderGroups()[0].headers.map((header) => (
                  <div key={header.id} role="columnheader" className="relative flex h-9 shrink-0 items-center px-3 text-[11.5px] font-semibold text-ink-3" style={{ width: header.getSize() }}>
                    {header.column.getCanSort() ? (
                      <button type="button" onClick={header.column.getToggleSortingHandler()} className="flex items-center gap-1 hover:text-ink">
                        {flexRender(header.column.columnDef.header, header.getContext())}
                        <ArrowDownUp className={cn("size-3", header.column.getIsSorted() ? "text-accent" : "opacity-40")} />
                      </button>
                    ) : flexRender(header.column.columnDef.header, header.getContext())}
                    {header.column.getCanResize() && (
                      <div onMouseDown={header.getResizeHandler()} onTouchStart={header.getResizeHandler()} className={cn("absolute right-0 top-2 h-5 w-1 cursor-col-resize rounded-full hover:bg-accent", header.column.getIsResizing() && "bg-accent")} aria-hidden />
                    )}
                  </div>
                ))}
              </div>
              <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
                {virtualizer.getVirtualItems().map((v) => {
                  const row = rowModel[v.index];
                  return (
                    <ContextMenu key={row.id}>
                      <ContextMenuTrigger asChild>
                        <div
                          role="row"
                          tabIndex={0}
                          onClick={() => navigate(`/rooms/${encodeURIComponent(row.original.id)}`)}
                          onKeyDown={(e) => e.key === "Enter" && navigate(`/rooms/${encodeURIComponent(row.original.id)}`)}
                          className={cn("absolute left-0 flex w-full cursor-default items-center border-b border-line text-[13px] transition-colors hover:bg-hover focus-visible:bg-hover focus-visible:outline-none", row.getIsSelected() && "bg-[color-mix(in_srgb,var(--accent)_7%,transparent)]")}
                          style={{ top: v.start, height: v.size }}
                        >
                          {row.getVisibleCells().map((cell) => (
                            <div key={cell.id} role="gridcell" className="shrink-0 overflow-hidden px-3" style={{ width: cell.column.getSize() }}>
                              {flexRender(cell.column.columnDef.cell, cell.getContext())}
                            </div>
                          ))}
                        </div>
                      </ContextMenuTrigger>
                      <ContextMenuContent>
                        <ContextMenuItem onSelect={() => navigate(`/rooms/${encodeURIComponent(row.original.id)}`)}><Rows3 />{t("Open room")}</ContextMenuItem>
                        <ContextMenuItem onSelect={() => navigate(`/booking/${encodeURIComponent(row.original.id)}`)}><CalendarCog />{t("Edit booking policy")}</ContextMenuItem>
                        <ContextMenuItem onSelect={() => navigate(`/delegates/${encodeURIComponent(row.original.id)}`)}><Users />{t("Manage delegates")}</ContextMenuItem>
                        <ContextMenuSeparator />
                        <ContextMenuItem onSelect={() => void navigator.clipboard?.writeText(row.original.primarySmtpAddress)}><RefreshCcw />{t("Copy email address")}</ContextMenuItem>
                      </ContextMenuContent>
                    </ContextMenu>
                  );
                })}
              </div>
            </div>
          </div>
          <div className="flex items-center justify-between border-t border-line px-3 py-2 text-[12px] text-ink-3">
            <span>{filtered.length !== rooms.length ? tn(filtered.length, "{n} room of {total}", "{n} rooms of {total}", { total: rooms.length }) : tn(filtered.length, "{n} room", "{n} rooms")}</span>
            <span>{t("Right-click a row for actions · drag a column edge to resize")}</span>
          </div>
        </Card>
      )}

      {inventory.data && filtered.length > 0 && layout === "cards" && (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(300px,1fr))] gap-3">
          {table.getSortedRowModel().rows.map(({ original: r }) => <RoomCard key={r.id} room={r} />)}
        </div>
      )}

      <RoomBulkDialogs mode={bulk} onClose={() => setBulk(null)} rooms={selectedRooms} />
    </div>
  );
}

function FilterColumn({ title, options, selected, onChange }: { title: string; options: { value: string; label: string }[]; selected: string[]; onChange: (v: string[]) => void }) {
  return (
    <div className="p-2">
      <div className="t-eyebrow px-1.5 pb-1.5">{title}</div>
      <div className="scroll-area max-h-60 space-y-px">
        {options.length === 0 && <p className="px-1.5 text-[12px] text-ink-3">{t("None")}</p>}
        {options.map((o) => (
          <label key={o.value} className="flex cursor-default items-center gap-2 rounded-sm px-1.5 py-1 text-[12.5px] hover:bg-hover">
            <Checkbox checked={selected.includes(o.value)} onCheckedChange={(v) => onChange(v ? [...selected, o.value] : selected.filter((s) => s !== o.value))} />
            <span className="truncate">{o.label}</span>
          </label>
        ))}
      </div>
    </div>
  );
}

function RoomCard({ room }: { room: RoomSummary }) {
  return (
    <Link to={`/rooms/${encodeURIComponent(room.id)}`} className="group block">
      <Card className="flex h-full flex-col gap-3 p-4 transition-colors group-hover:border-line-strong">
        <div className="flex items-start justify-between gap-2">
          <RoomName name={room.displayName} size="lg" className="text-[14px] font-semibold" />
          <HealthCell room={room} />
        </div>
        <div className="text-[12px] text-ink-3">{room.locationPath ?? t("No Places location")}</div>
        <div className="flex items-end justify-between gap-3">
          <div>
            <div className="t-metric text-[26px]">{room.capacity ?? "—"}</div>
            <div className="text-[11px] text-ink-3">{room.capacity ? t("seats") : t("capacity not set")}</div>
          </div>
          <div className="flex flex-col items-end gap-1.5">
            <BookingModeBadge mode={room.bookingMode} label={room.bookingModeLabel} />
            <span className="text-[11.5px] text-ink-3">{windowText(room)}</span>
          </div>
        </div>
        <div className="flex flex-wrap gap-1 border-t border-line pt-3">
          {room.mtrEnabled && <Badge tone="accent"><MonitorPlay />{t("Teams Rooms")}</Badge>}
          {room.wheelchairAccessible && <Badge tone="outline"><Accessibility />{t("Accessible")}</Badge>}
          {room.devices.slice(0, 2).map((d) => <Badge key={d} tone="outline">{d}</Badge>)}
          {room.tags.slice(0, 2).map((tag) => <Badge key={tag} tone="neutral">{tag}</Badge>)}
          {!room.mtrEnabled && !room.wheelchairAccessible && room.devices.length === 0 && room.tags.length === 0 && <span className="text-[11.5px] text-ink-3">{t("No facility metadata in Places")}</span>}
        </div>
      </Card>
    </Link>
  );
}
