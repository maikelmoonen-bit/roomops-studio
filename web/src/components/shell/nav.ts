import type { LucideIcon } from "lucide-react";
import { Boxes, Building2, CalendarCog, Clock4, DoorOpen, History, Layers, LayoutDashboard, ListTree, MapPinned, PlugZap, Settings, ShieldCheck, UserCheck } from "lucide-react";
import { tx } from "@/lib/i18n";

export interface NavItem {
  key: string;
  path: string;
  /** English source text; translate with t() when rendering. */
  label: string;
  icon: LucideIcon;
  shortcut?: string;
}

export interface NavGroup {
  label: string;
  items: NavItem[];
}

export const navigation: NavGroup[] = [
  { label: tx("Overview"), items: [{ key: "dashboard", path: "/", label: tx("Dashboard"), icon: LayoutDashboard, shortcut: "G D" }] },
  {
    label: tx("Workspace infrastructure"),
    items: [
      { key: "rooms", path: "/rooms", label: tx("Rooms"), icon: DoorOpen, shortcut: "G R" },
      { key: "room-lists", path: "/room-lists", label: tx("Room lists"), icon: ListTree, shortcut: "G L" },
      { key: "buildings", path: "/buildings", label: tx("Buildings & floors"), icon: Building2, shortcut: "G F" },
      { key: "places", path: "/places", label: tx("Microsoft Places"), icon: MapPinned },
    ],
  },
  {
    label: tx("Configuration"),
    items: [
      { key: "booking", path: "/booking", label: tx("Booking policies"), icon: CalendarCog, shortcut: "G B" },
      { key: "delegates", path: "/delegates", label: tx("Resource delegates"), icon: UserCheck },
      { key: "working-hours", path: "/working-hours", label: tx("Working hours"), icon: Clock4 },
      { key: "templates", path: "/templates", label: tx("Configuration templates"), icon: Layers, shortcut: "G T" },
    ],
  },
  {
    label: tx("Operations"),
    items: [
      { key: "bulk", path: "/bulk", label: tx("Bulk operations"), icon: Boxes },
      { key: "validation", path: "/validation", label: tx("Validation center"), icon: ShieldCheck, shortcut: "G V" },
      { key: "history", path: "/history", label: tx("Change history"), icon: History, shortcut: "G H" },
    ],
  },
  {
    label: tx("Administration"),
    items: [
      { key: "connections", path: "/connections", label: tx("Connections"), icon: PlugZap, shortcut: "G C" },
      { key: "settings", path: "/settings", label: tx("Settings"), icon: Settings },
    ],
  },
];

export const allNavItems = navigation.flatMap((g) => g.items);

export function activeKey(path: string): string {
  if (path === "/" || path === "") return "dashboard";
  const first = path.split("/").filter(Boolean)[0];
  return allNavItems.find((i) => i.path === `/${first}`)?.key ?? "dashboard";
}
