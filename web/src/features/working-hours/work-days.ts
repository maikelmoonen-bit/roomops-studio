import { locale, t, tx } from "@/lib/i18n";

/** Work-day helpers for Set-MailboxCalendarConfiguration -WorkDays values (named combinations or day lists). */
export const DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"] as const;
const NAMED: Record<string, string[]> = {
  Weekdays: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
  WeekEndDays: ["Saturday", "Sunday"],
  AllDays: [...DAYS],
  None: [],
};

/** Display names of the -WorkDays values, translated where they are shown. */
const dayLabels: Record<string, string> = {
  Monday: tx("Monday"), Tuesday: tx("Tuesday"), Wednesday: tx("Wednesday"), Thursday: tx("Thursday"), Friday: tx("Friday"), Saturday: tx("Saturday"), Sunday: tx("Sunday"),
};

export function parseDays(value: string): Set<string> {
  const set = new Set<string>();
  value.split(/[,\s]+/).filter(Boolean).forEach((p) => (NAMED[p] ?? [p]).forEach((d) => set.add(d)));
  return set;
}

/** Work days for display in the current language, e.g. "Monday–Friday" or "Monday · Wednesday · Friday". */
export function describeDays(value: string): string {
  const days = parseDays(value);
  if (days.size === 0) return t("None");
  if (days.size === 7) return t("Every day");
  if (days.size === 5 && NAMED.Weekdays.every((d) => days.has(d))) return t("Monday–Friday");
  return DAYS.filter((d) => days.has(d)).map((d) => t(dayLabels[d])).join(" · ");
}

/** Abbreviated day name in the interface language ("Mon", "ma"). */
export function shortDay(day: string): string {
  const index = DAYS.indexOf(day as (typeof DAYS)[number]);
  // 1 January 2024 was a Monday.
  return new Intl.DateTimeFormat(locale(), { weekday: "short", timeZone: "UTC" }).format(new Date(Date.UTC(2024, 0, 1 + index, 12)));
}

/** The -WorkDays parameter value (English day names, as Exchange expects them). */
export function formatDays(days: Set<string>): string {
  if (days.size === 0) return "None";
  return DAYS.filter((d) => days.has(d)).join(", ");
}
