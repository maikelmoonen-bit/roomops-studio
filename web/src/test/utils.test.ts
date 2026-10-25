import { describe, expect, it } from "vitest";
import { tokenize } from "@/components/common/code-block";
import { formatMinutes, formatOffset, humanize, roomPlate } from "@/lib/format";
import { toCsv } from "@/lib/files";
import { applyFilters, emptyFilters } from "@/features/rooms/room-filters";
import { changedFields, formatValue, setField } from "@/features/booking/policy-utils";
import { formatDays, parseDays } from "@/features/working-hours/work-days";
import type { BookingPolicy, PolicyFieldInfo, RoomSummary } from "@/bridge/contracts.gen";

describe("PowerShell tokenizer", () => {
  it("classifies cmdlets, parameters, strings and literals", () => {
    const tokens = tokenize("Set-CalendarProcessing -Identity 'o''brien@x.example' -AllowConflicts $false -BookingWindowInDays 90");
    expect(tokens.filter((t) => t.kind === "cmdlet").map((t) => t.text)).toEqual(["Set-CalendarProcessing"]);
    expect(tokens.filter((t) => t.kind === "string").map((t) => t.text)).toEqual(["'o''brien@x.example'"]);
    expect(tokens.filter((t) => t.kind === "param").map((t) => t.text.trim())).toEqual(["-Identity", "-AllowConflicts", "-BookingWindowInDays"]);
    expect(tokens.some((t) => t.kind === "variable" && t.text === "$false")).toBe(true);
    expect(tokens.map((t) => t.text).join("")).toBe("Set-CalendarProcessing -Identity 'o''brien@x.example' -AllowConflicts $false -BookingWindowInDays 90");
  });

  it("treats comment lines as comments", () => {
    expect(tokenize("# Tenant: Contoso")).toEqual([{ text: "# Tenant: Contoso", kind: "comment" }]);
  });
});

describe("formatting", () => {
  it("splits signage-style room names into a plate", () => {
    expect(roomPlate("EHV 3.12 Boardroom")).toEqual({ code: "EHV", number: "3.12", name: "Boardroom" });
    expect(roomPlate("Remote Studio (Podcast)")).toEqual({ code: null, number: null, name: "Remote Studio (Podcast)" });
  });

  it("formats durations and offsets for people", () => {
    expect(formatMinutes(0)).toBe("Unlimited");
    expect(formatMinutes(120)).toBe("2 hours");
    expect(formatMinutes(90)).toBe("1 h 30 min");
    expect(formatOffset(60)).toBe("UTC+01:00");
    expect(formatOffset(-210)).toBe("UTC−03:30");
    expect(humanize("PendingPropagation")).toBe("Pending Propagation");
  });

  it("writes Excel-safe CSV", () => {
    const csv = toCsv(["Name", "Lists"], [["Room, A", 'He said "hi"'], [null, 3]]);
    expect(csv.startsWith("﻿")).toBe(true);
    expect(csv).toContain('"Room, A","He said ""hi"""');
    expect(csv.endsWith(",3")).toBe(true);
  });

  it("neutralizes spreadsheet formulas in text cells", () => {
    expect(toCsv(["Name"], [["=HYPERLINK(\"x\")"], ["@SUM(A1)"], [-5]])).toContain("'=HYPERLINK");
    expect(toCsv(["Name"], [["@SUM(A1)"]])).toContain("'@SUM(A1)");
    expect(toCsv(["n"], [[-5]]).endsWith("-5")).toBe(true);
  });
});

describe("work days", () => {
  it("round-trips named combinations and day lists", () => {
    expect([...parseDays("Weekdays")]).toEqual(["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]);
    expect(formatDays(parseDays("Weekdays, Saturday"))).toBe("Monday, Tuesday, Wednesday, Thursday, Friday, Saturday");
    expect(formatDays(parseDays("None"))).toBe("None");
  });
});

describe("policy diff", () => {
  const fields: PolicyFieldInfo[] = [
    { name: "BookingWindowInDays", label: "Booking window", section: "Scheduling", kind: "Integer", min: 0, max: 1080, unit: "days", enumValues: null, autoAcceptOnly: false, help: "", defaultValue: 180 },
    { name: "ResourceDelegates", label: "Booking delegates", section: "Permissions", kind: "RecipientList", min: null, max: null, unit: null, enumValues: null, autoAcceptOnly: false, help: "", defaultValue: [] },
  ];
  const base = { bookingWindowInDays: 180, resourceDelegates: [{ identity: "a", primarySmtpAddress: "A@x.example", displayName: "A", recipientTypeDetails: null, resolution: "Resolved" }] } as unknown as BookingPolicy;

  it("ignores recipient order and case, detects real changes", () => {
    const reordered = setField(base, "ResourceDelegates", [{ identity: "a", primarySmtpAddress: "a@X.example", displayName: "A", recipientTypeDetails: null, resolution: "Resolved" }]);
    expect(changedFields(base, reordered, fields)).toHaveLength(0);
    const changed = setField(base, "BookingWindowInDays", 90);
    expect(changedFields(base, changed, fields).map((f) => f.name)).toEqual(["BookingWindowInDays"]);
    expect(formatValue(fields[0], 90)).toBe("90 days");
  });
});

describe("room filters", () => {
  const room = (over: Partial<RoomSummary>): RoomSummary => ({
    id: "1", displayName: "EHV 1.02 Focus", primarySmtpAddress: "ehv-1.02@x.example", alias: "ehv-1.02", capacity: 2, placesCapacity: null, building: "Eindhoven Headquarters", floor: "Floor 1", section: null, placeId: "p",
    locationPath: "Eindhoven Headquarters / Floor 1", roomLists: [], bookingMode: null, bookingModeLabel: null, delegateCount: null, bookingWindowInDays: null, maximumDurationInMinutes: null, configurationLoadedAt: null, timeZone: null, workingHours: null,
    findings: { critical: 0, warning: 0, recommendation: 0, informational: 0, unableToVerify: 1 }, isDirSynced: false, mtrEnabled: null, wheelchairAccessible: null, tags: [], devices: [], templateName: null, whenCreated: null, ...over,
  });
  it("combines search, building and quick filters", () => {
    const rooms = [room({}), room({ id: "2", displayName: "AMS 4.05 Huddle", building: "Amsterdam Zuidas", capacity: null }), room({ id: "3", displayName: "Studio", building: null, locationPath: null })];
    expect(applyFilters(rooms, { ...emptyFilters, search: "huddle" }).map((r) => r.id)).toEqual(["2"]);
    expect(applyFilters(rooms, { ...emptyFilters, quick: ["missingCapacity"] }).map((r) => r.id)).toEqual(["2"]);
    expect(applyFilters(rooms, { ...emptyFilters, quick: ["noLocation"] }).map((r) => r.id)).toEqual(["3"]);
    expect(applyFilters(rooms, { ...emptyFilters, buildings: ["(none)"] }).map((r) => r.id)).toEqual(["3"]);
  });
});
