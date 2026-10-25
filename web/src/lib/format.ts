import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";
import { locale, t, tn } from "./i18n";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

/** Formatters follow the interface language; they are created once per language. */
const formatters = new Map<string, { relative: Intl.RelativeTimeFormat; dateTime: Intl.DateTimeFormat; dateOnly: Intl.DateTimeFormat; number: Intl.NumberFormat }>();
function fmt() {
  const tag = locale();
  let f = formatters.get(tag);
  if (!f) {
    f = {
      relative: new Intl.RelativeTimeFormat(tag, { numeric: "auto" }),
      dateTime: new Intl.DateTimeFormat(tag, { dateStyle: "medium", timeStyle: "short" }),
      dateOnly: new Intl.DateTimeFormat(tag, { dateStyle: "medium" }),
      number: new Intl.NumberFormat(tag),
    };
    formatters.set(tag, f);
  }
  return f;
}

export function formatRelative(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "—";
  const diff = (new Date(iso).getTime() - now) / 1000;
  const abs = Math.abs(diff);
  if (abs < 45) return t("just now");
  if (abs < 3600) return fmt().relative.format(Math.round(diff / 60), "minute");
  if (abs < 86400) return fmt().relative.format(Math.round(diff / 3600), "hour");
  if (abs < 86400 * 30) return fmt().relative.format(Math.round(diff / 86400), "day");
  return fmt().dateOnly.format(new Date(iso));
}

export const formatDateTime = (iso: string | null | undefined) => (iso ? fmt().dateTime.format(new Date(iso)) : "—");
export const formatDate = (iso: string | null | undefined) => (iso ? fmt().dateOnly.format(new Date(iso)) : "—");
export const formatNumber = (n: number | null | undefined) => (n == null ? "—" : fmt().number.format(n));

export function formatMinutes(minutes: number): string {
  if (minutes === 0) return t("Unlimited");
  if (minutes % 60 === 0) return tn(minutes / 60, "{n} hour", "{n} hours");
  if (minutes > 60) return t("{h} h {m} min", { h: Math.floor(minutes / 60), m: minutes % 60 });
  return t("{m} min", { m: minutes });
}

export function formatOffset(minutes: number): string {
  const sign = minutes < 0 ? "−" : "+";
  const abs = Math.abs(minutes);
  return `UTC${sign}${String(Math.floor(abs / 60)).padStart(2, "0")}:${String(abs % 60).padStart(2, "0")}`;
}

/** Splits "EHV 3.12 Boardroom" into a plate code ("EHV", "3.12") and the remaining name, for the room-plate treatment. */
export function roomPlate(displayName: string): { code: string | null; number: string | null; name: string } {
  const match = /^([A-Z]{2,5})\s+(\d{1,2}\.\d{1,3})\s*(.*)$/.exec(displayName.trim());
  if (!match) return { code: null, number: null, name: displayName };
  return { code: match[1], number: match[2], name: match[3] || displayName };
}

export function humanize(pascal: string): string {
  return pascal.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/^./, (c) => c.toUpperCase());
}

export function initials(name: string | null | undefined): string {
  if (!name) return "?";
  const parts = name.replace(/@.*/, "").split(/[\s._-]+/).filter(Boolean);
  return ((parts[0]?.[0] ?? "") + (parts[1]?.[0] ?? "")).toUpperCase() || "?";
}
