import type { BookingPolicy, PolicyFieldInfo, RecipientRef } from "@/bridge/contracts.gen";
import { t, tn } from "@/lib/i18n";

const recipientFields = ["BookInPolicy", "RequestInPolicy", "RequestOutOfPolicy", "ResourceDelegates"] as const;

const camel = (name: string) => name.charAt(0).toLowerCase() + name.slice(1);

export function getField(policy: BookingPolicy, name: string): unknown {
  return (policy as unknown as Record<string, unknown>)[camel(name)];
}

export function setField(policy: BookingPolicy, name: string, value: unknown): BookingPolicy {
  return { ...policy, [camel(name)]: value } as BookingPolicy;
}

function normalize(value: unknown): string {
  if (Array.isArray(value)) return (value as RecipientRef[]).map((r) => (r.primarySmtpAddress ?? r.identity).toLowerCase()).sort().join("|");
  if (value === "" || value == null) return "";
  return String(value);
}

/** Field names whose values differ between two policies, using the same equality rules as the backend. */
export function changedFields(a: BookingPolicy, b: BookingPolicy, fields: PolicyFieldInfo[]): PolicyFieldInfo[] {
  return fields.filter((f) => normalize(getField(a, f.name)) !== normalize(getField(b, f.name)));
}

export function isRecipientField(name: string) {
  return (recipientFields as readonly string[]).includes(name);
}

export function formatValue(f: PolicyFieldInfo, value: unknown): string {
  if (Array.isArray(value)) return value.length === 0 ? t("(none)") : (value as RecipientRef[]).map((r) => r.displayName ?? r.primarySmtpAddress ?? r.identity).join(", ");
  if (typeof value === "boolean") return value ? t("Enabled") : t("Disabled");
  if (typeof value === "number" && f.unit === "days") return tn(value, "{n} day", "{n} days");
  if (typeof value === "number" && f.unit === "minutes") return value === 0 && f.name === "MaximumDurationInMinutes" ? t("Unlimited") : t("{m} min", { m: value });
  if (typeof value === "number" && f.unit === "percent") return `${value}%`;
  if (value == null || value === "") return t("(not set)");
  return String(value);
}

/**
 * Maps phrases in the generated policy sentence to the setting that controls them, for the clickable sentence. The backend writes
 * the sentence in the interface language, so each pattern covers the English and the Dutch wording.
 */
export const sentenceTokens: { pattern: RegExp; field: string }[] = [
  { pattern: /\b\d+ days?\b|\btoday\b|\b\d+ dagen\b|\bvandaag\b/, field: "BookingWindowInDays" },
  { pattern: /\b\d+ h \d+ min\b|\b\d+ hours?\b|\b\d+ min\b|\b24 hours\b|\b\d+ u \d+ min\b|\b\d+ uur\b/, field: "MaximumDurationInMinutes" },
  { pattern: /during configured working hours|tijdens de ingestelde werktijden/, field: "ScheduleOnlyDuringWorkHours" },
  { pattern: /there is no scheduling conflict|er geen planningsconflict is/, field: "AllowConflicts" },
  { pattern: /eligible users|nobody|listed users or groups|one listed user or group|bevoegde gebruikers|niemand|vermelde gebruikers of groepen|één vermelde gebruiker of groep/, field: "AllBookInPolicy" },
  { pattern: /for approval|its delegates|ter goedkeuring|de gemachtigden/, field: "ResourceDelegates" },
];
