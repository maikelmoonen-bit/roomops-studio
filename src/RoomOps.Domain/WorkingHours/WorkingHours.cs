using System.Globalization;
using RoomOps.Domain.Common;

namespace RoomOps.Domain.WorkingHours;

/// <summary>Set-MailboxCalendarConfiguration -WorkDays values. Exchange stores a flag set; Weekdays/WeekEndDays/AllDays are named combinations.</summary>
[Flags]
public enum WorkDays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    WeekEndDays = Saturday | Sunday,
    AllDays = Weekdays | WeekEndDays,
}

/// <summary>
/// Mailbox working hours. The Exchange model is a single start/end time shared by every selected work day plus one time zone;
/// per-day ranges cannot be represented and the editor does not pretend otherwise.
/// </summary>
public sealed record WorkingHoursConfig(WorkDays Days, TimeOnly Start, TimeOnly End, string TimeZoneId)
{
    public static WorkingHoursConfig ExchangeDefault { get; } = new(WorkDays.Weekdays, new TimeOnly(8, 0), new TimeOnly(17, 0), "Pacific Standard Time");

    public static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public bool Includes(DayOfWeek day) => Days.HasFlag(ToFlag(day));

    public static WorkDays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => WorkDays.Sunday,
        DayOfWeek.Monday => WorkDays.Monday,
        DayOfWeek.Tuesday => WorkDays.Tuesday,
        DayOfWeek.Wednesday => WorkDays.Wednesday,
        DayOfWeek.Thursday => WorkDays.Thursday,
        DayOfWeek.Friday => WorkDays.Friday,
        DayOfWeek.Saturday => WorkDays.Saturday,
        _ => WorkDays.None,
    };

    /// <summary>Exchange parameter value: named combinations where they match exactly, otherwise a comma-separated day list.</summary>
    public static string FormatWorkDays(WorkDays days)
    {
        if (days == WorkDays.None) return "None";
        if (days == WorkDays.AllDays) return "AllDays";
        if (days == WorkDays.Weekdays) return "Weekdays";
        if (days == WorkDays.WeekEndDays) return "WeekEndDays";
        return string.Join(",", WeekOrder.Where(d => days.HasFlag(ToFlag(d))).Select(d => d.ToString()));
    }

    /// <summary>Parses Exchange output such as "Weekdays", "Monday, Tuesday" or "Weekdays, Saturday".</summary>
    public static WorkDays ParseWorkDays(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return WorkDays.None;
        var result = WorkDays.None;
        foreach (var part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<WorkDays>(part, true, out var flag))
            {
                result |= flag;
            }
            else
            {
                throw new FormatException($"'{part}' is not a valid WorkDays value.");
            }
        }
        return result;
    }

    public static TimeOnly ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TimeOnly(0, 0);
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span) && span >= TimeSpan.Zero && span < TimeSpan.FromDays(1))
        {
            return TimeOnly.FromTimeSpan(span);
        }
        if (TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var time)) return time;
        throw new FormatException(Text.L($"'{text}' is not a valid working-hours time.", $"'{text}' is geen geldige tijd voor werktijden."));
    }

    public static string FormatTime(TimeOnly time) => time.ToString("HH\\:mm\\:ss", CultureInfo.InvariantCulture);

    public string Describe()
    {
        var days = DescribeDays(Days);
        if (Days == WorkDays.None) return Text.L($"No working days ({TimeZoneId})", $"Geen werkdagen ({TimeZoneId})");
        return $"{days}, {Start:HH\\:mm}–{End:HH\\:mm} ({TimeZoneId})";
    }

    public static string DescribeDays(WorkDays days)
    {
        if (days == WorkDays.None) return Text.L("No days", "Geen dagen");
        if (days == WorkDays.AllDays) return Text.L("Every day", "Elke dag");
        if (days == WorkDays.Weekdays) return Text.L("Monday–Friday", "maandag–vrijdag");
        if (days == WorkDays.WeekEndDays) return Text.L("Saturday–Sunday", "zaterdag–zondag");
        var selected = WeekOrder.Where(d => days.HasFlag(ToFlag(d))).ToList();
        // Collapse consecutive runs (Mon–Thu, Sat).
        var parts = new List<string>();
        var i = 0;
        while (i < selected.Count)
        {
            var start = selected[i];
            var j = i;
            while (j + 1 < selected.Count && Array.IndexOf(WeekOrder, selected[j + 1]) == Array.IndexOf(WeekOrder, selected[j]) + 1) j++;
            parts.Add(j - i >= 2 ? $"{DayName(start)}–{DayName(selected[j])}" : j == i ? DayName(start) : $"{DayName(start)}, {DayName(selected[j])}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Text.L("Monday", "maandag"),
        DayOfWeek.Tuesday => Text.L("Tuesday", "dinsdag"),
        DayOfWeek.Wednesday => Text.L("Wednesday", "woensdag"),
        DayOfWeek.Thursday => Text.L("Thursday", "donderdag"),
        DayOfWeek.Friday => Text.L("Friday", "vrijdag"),
        DayOfWeek.Saturday => Text.L("Saturday", "zaterdag"),
        _ => Text.L("Sunday", "zondag"),
    };
}

public static class WorkingHoursValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(WorkingHoursConfig config, Func<string, bool> isKnownTimeZone)
    {
        var issues = new List<ValidationIssue>();
        if (config.End <= config.Start)
        {
            issues.Add(new("end", Text.L("The end time must be later than the start time. Exchange applies one time range to every work day; overnight ranges can't be represented.", "De eindtijd moet later zijn dan de begintijd. Exchange past één tijdsbereik toe op elke werkdag; bereiken over middernacht zijn niet mogelijk."), IssueLevel.Error));
        }
        if (string.IsNullOrWhiteSpace(config.TimeZoneId) || !isKnownTimeZone(config.TimeZoneId))
        {
            issues.Add(new("timeZone", $"'{config.TimeZoneId}' is not a recognized Windows time zone name.", IssueLevel.Error));
        }
        if (config.Days == WorkDays.None)
        {
            issues.Add(new("days", Text.L("No work days are selected. Combined with 'Only during working hours', every request would be declined.", "Er zijn geen werkdagen geselecteerd. In combinatie met 'Alleen tijdens werktijden' zou elke aanvraag worden geweigerd."), IssueLevel.Warning));
        }
        var length = config.End.ToTimeSpan() - config.Start.ToTimeSpan();
        if (length > TimeSpan.Zero && length < TimeSpan.FromHours(4))
        {
            issues.Add(new("end", Text.L($"Working hours span only {length.TotalHours:0.#} hours. Confirm this is intentional.", $"De werktijden beslaan maar {length.TotalHours:0.#} uur. Controleer of dit de bedoeling is."), IssueLevel.Warning));
        }
        if (config.Start.Minute % 15 != 0 || config.End.Minute % 15 != 0)
        {
            issues.Add(new("start", Text.L("Times that are not on a quarter hour are unusual for room working hours.", "Tijden die niet op een kwartier vallen, zijn ongebruikelijk voor werktijden van ruimtes."), IssueLevel.Info));
        }
        return issues;
    }
}

/// <summary>A Windows time zone name accepted by -WorkingHoursTimeZone, with offsets computed for the UI.</summary>
public sealed record TimeZoneEntry(string Id, string DisplayName, int BaseOffsetMinutes, int CurrentOffsetMinutes, bool SupportsDaylightSaving, DateTimeOffset? NextTransitionUtc);

public static class TimeZoneCatalog
{
    private static readonly Lazy<IReadOnlyList<TimeZoneEntry>> Entries = new(Build);

    public static IReadOnlyList<TimeZoneEntry> All => Entries.Value;

    public static bool IsKnown(string id) => All.Any(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static TimeZoneEntry? Find(string id) => All.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Exchange expects Windows time zone names. On Windows those are the system ids; elsewhere the IANA zones are mapped to
    /// Windows names through ICU so that the catalog is identical on every platform.
    /// </summary>
    private static IReadOnlyList<TimeZoneEntry> Build()
    {
        var now = DateTimeOffset.UtcNow;
        var map = new Dictionary<string, TimeZoneInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
        {
            string windowsId;
            if (tz.HasIanaId)
            {
                if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(tz.Id, out var converted) || converted is null) continue;
                windowsId = converted;
            }
            else
            {
                windowsId = tz.Id;
            }
            if (!map.ContainsKey(windowsId))
            {
                // Prefer resolving by the Windows id itself so offsets reflect the canonical zone.
                map[windowsId] = TryFind(windowsId) ?? tz;
            }
        }
        if (!map.ContainsKey("UTC") && TryFind("UTC") is { } utc) map["UTC"] = utc;

        return map
            .Select(kv => Describe(kv.Key, kv.Value, now))
            .OrderBy(e => e.BaseOffsetMinutes)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static TimeZoneInfo? TryFind(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    private static TimeZoneEntry Describe(string windowsId, TimeZoneInfo tz, DateTimeOffset now)
    {
        var current = tz.GetUtcOffset(now);
        DateTimeOffset? next = null;
        if (tz.SupportsDaylightSavingTime)
        {
            // Walk forward day by day (max one year) to find the next offset change; cheap and rule-agnostic.
            var probe = now;
            for (var d = 1; d <= 370; d++)
            {
                var candidate = now.AddDays(d);
                if (tz.GetUtcOffset(candidate) != current)
                {
                    var lo = probe;
                    var hi = candidate;
                    while (hi - lo > TimeSpan.FromMinutes(1))
                    {
                        var mid = lo + (hi - lo) / 2;
                        if (tz.GetUtcOffset(mid) == current) lo = mid; else hi = mid;
                    }
                    next = new DateTimeOffset(hi.UtcDateTime.Ticks - hi.UtcDateTime.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
                    break;
                }
                probe = candidate;
            }
        }
        var display = tz.DisplayName.StartsWith("(UTC", StringComparison.Ordinal) ? tz.DisplayName : $"{FormatOffset(tz.BaseUtcOffset)} {tz.DisplayName}";
        return new TimeZoneEntry(windowsId, display, (int)tz.BaseUtcOffset.TotalMinutes, (int)current.TotalMinutes, tz.SupportsDaylightSavingTime, next);
    }

    public static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"(UTC{sign}{abs.Hours:00}:{abs.Minutes:00})";
    }

    public static TimeZoneInfo? Resolve(string windowsId) => TryFind(windowsId);
}
