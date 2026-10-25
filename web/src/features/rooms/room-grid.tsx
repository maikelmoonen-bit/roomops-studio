import { Fragment, useRef, useState, type ReactElement, type ReactNode } from "react";
import { flexRender, getCoreRowModel, getSortedRowModel, useReactTable, type ColumnDef, type SortingState, type Table } from "@tanstack/react-table";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDownUp } from "lucide-react";
import type { RoomSummary } from "@/bridge/contracts.gen";
import { cn } from "@/lib/format";
import { navigate } from "@/lib/router";
import { Card } from "@/components/ui/core";
import { RoomName } from "@/components/common/domain";
import { t, tn } from "@/lib/i18n";

/**
 * The room table of the Rooms page: sticky sortable headers, resizable columns, and virtualized rows that open a room on click or
 * Enter. `wrapRow` can wrap each row, for example in a context menu.
 */
export function RoomGrid({ table, total, onOpen, wrapRow, hint }: { table: Table<RoomSummary>; total: number; onOpen: (room: RoomSummary) => void; wrapRow?: (room: RoomSummary, row: ReactElement) => ReactNode; hint?: ReactNode }) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const rowModel = table.getRowModel().rows;
  const virtualizer = useVirtualizer({ count: rowModel.length, getScrollElement: () => scrollRef.current, estimateSize: () => 52, overscan: 12 });

  return (
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
              const element = (
                <div
                  role="row"
                  tabIndex={0}
                  onClick={() => onOpen(row.original)}
                  onKeyDown={(e) => e.key === "Enter" && onOpen(row.original)}
                  className={cn("absolute left-0 flex w-full cursor-default items-center border-b border-line text-[13px] transition-colors hover:bg-hover focus-visible:bg-hover focus-visible:outline-none", row.getIsSelected() && "bg-[color-mix(in_srgb,var(--accent)_7%,transparent)]")}
                  style={{ top: v.start, height: v.size }}
                >
                  {row.getVisibleCells().map((cell) => (
                    <div key={cell.id} role="gridcell" className="shrink-0 overflow-hidden px-3" style={{ width: cell.column.getSize() }}>
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </div>
                  ))}
                </div>
              );
              return <Fragment key={row.id}>{wrapRow ? wrapRow(row.original, element) : element}</Fragment>;
            })}
          </div>
        </div>
      </div>
      <div className="flex items-center justify-between border-t border-line px-3 py-2 text-[12px] text-ink-3">
        <span>{rowModel.length !== total ? tn(rowModel.length, "{n} room of {total}", "{n} rooms of {total}", { total }) : tn(rowModel.length, "{n} room", "{n} rooms")}</span>
        {hint && <span>{hint}</span>}
      </div>
    </Card>
  );
}

/** The Room column shared by every room table: plate, name and address. */
export const roomColumn: ColumnDef<RoomSummary> = {
  id: "room",
  size: 300,
  accessorFn: (r) => r.displayName,
  header: () => t("Room"),
  cell: ({ row }) => (
    <div className="min-w-0">
      <RoomName name={row.original.displayName} className="font-medium text-ink" />
      <div className="t-mono mt-0.5 truncate text-[11px] text-ink-3">{row.original.primarySmtpAddress}</div>
    </div>
  ),
};

/** A yes/no value read from the booking policy; a dash when it hasn't been read. */
export function FlagCell({ value }: { value: boolean | null }) {
  if (value == null) return <span className="text-ink-3">—</span>;
  return value ? <span>{t("Yes")}</span> : <span className="text-ink-3">{t("No")}</span>;
}

/** A Rooms-style table of `rooms` with page-specific columns; a row opens that room under `basePath`. */
export function RoomOverviewTable({ rooms, total, columns, basePath }: { rooms: RoomSummary[]; total: number; columns: ColumnDef<RoomSummary>[]; basePath: string }) {
  const [sorting, setSorting] = useState<SortingState>([{ id: "room", desc: false }]);
  const table = useReactTable({
    data: rooms,
    columns,
    state: { sorting },
    getRowId: (r) => r.id,
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    enableColumnResizing: true,
    columnResizeMode: "onChange",
  });
  return <RoomGrid table={table} total={total} onOpen={(r) => navigate(`${basePath}/${encodeURIComponent(r.id)}`)} hint={t("Click a room to open it · drag a column edge to resize")} />;
}
