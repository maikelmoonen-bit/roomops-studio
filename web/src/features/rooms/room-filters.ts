import type { RoomSummary } from "@/bridge/contracts.gen";
import { tx } from "@/lib/i18n";

export type QuickFilter = "missingCapacity" | "noRoomList" | "noLocation" | "delegateApproval" | "unusualBooking" | "critical" | "notScanned";

export const quickFilters: { key: QuickFilter; label: string; test: (r: RoomSummary) => boolean }[] = [
  { key: "missingCapacity", label: tx("Capacity missing"), test: (r) => !r.capacity },
  { key: "noRoomList", label: tx("Not in a room list"), test: (r) => r.roomLists.length === 0 },
  { key: "noLocation", label: tx("No Places location"), test: (r) => !r.locationPath },
  { key: "delegateApproval", label: tx("Delegate approval"), test: (r) => r.bookingMode === "DelegateApproval" || r.bookingMode === "Hybrid" },
  { key: "unusualBooking", label: tx("Unusual booking"), test: (r) => r.bookingMode === "Custom" || r.bookingMode === "ProcessingDisabled" || r.bookingMode === "ManualAutoUpdate" || r.bookingMode === "Reserved" },
  { key: "critical", label: tx("Critical findings"), test: (r) => r.findings.critical > 0 },
  { key: "notScanned", label: tx("Booking not read"), test: (r) => r.configurationLoadedAt == null },
];

export interface RoomFilterState {
  search: string;
  buildings: string[];
  modes: string[];
  roomList: string | null;
  quick: QuickFilter[];
}

export const emptyFilters: RoomFilterState = { search: "", buildings: [], modes: [], roomList: null, quick: [] };

export function applyFilters(rooms: RoomSummary[], f: RoomFilterState): RoomSummary[] {
  const q = f.search.trim().toLowerCase();
  return rooms.filter((r) => {
    if (q && !`${r.displayName} ${r.primarySmtpAddress} ${r.alias} ${r.building ?? ""} ${r.floor ?? ""} ${r.tags.join(" ")}`.toLowerCase().includes(q)) return false;
    if (f.buildings.length && !f.buildings.includes(r.building ?? "(none)")) return false;
    if (f.modes.length && !f.modes.includes(r.bookingMode ?? "NotScanned")) return false;
    if (f.roomList && !r.roomLists.some((l) => l.id === f.roomList)) return false;
    for (const key of f.quick) {
      const quick = quickFilters.find((qf) => qf.key === key);
      if (quick && !quick.test(r)) return false;
    }
    return true;
  });
}
