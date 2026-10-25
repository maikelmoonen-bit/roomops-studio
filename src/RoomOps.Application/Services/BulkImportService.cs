using System.Globalization;
using System.Text;
using System.Text.Json;
using RoomOps.Application.Execution;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Application.Services;

public sealed record BulkTargetField(string Key, string Label, string Group, string Kind, string? Help, IReadOnlyList<string>? Options);

public sealed record BulkColumn(string Name, string? SuggestedField, IReadOnlyList<string> Samples);

public sealed record BulkRowPreview(int RowNumber, string RoomIdentity, string? ResolvedRoomId, string? ResolvedRoomName, IReadOnlyDictionary<string, string> Values, IReadOnlyList<ValidationIssue> Issues);

public sealed record BulkParseRequest(string Format, string Content, IReadOnlyDictionary<string, string>? Mapping);

public sealed record BulkParseResult(
    IReadOnlyList<BulkColumn> Columns,
    IReadOnlyList<BulkTargetField> Fields,
    IReadOnlyDictionary<string, string> Mapping,
    IReadOnlyList<BulkRowPreview> Rows,
    IReadOnlyList<string> Errors,
    int ValidRows,
    BulkImportIntent? Intent);

/// <summary>Parses CSV/JSON room configuration files into validated bulk rows. Nothing is planned or executed here.</summary>
public sealed class BulkImportService(InventoryService inventory)
{
    public const string RoomKey = "Room";
    private static readonly string[] WorkingHoursKeys = ["WorkDays", "WorkingHoursStartTime", "WorkingHoursEndTime", "WorkingHoursTimeZone"];

    /// <summary>Built on access so labels and hints follow the interface language.</summary>
    public static IReadOnlyList<BulkTargetField> TargetFields => BuildFields();

    private static IReadOnlyList<BulkTargetField> BuildFields()
    {
        var fields = new List<BulkTargetField>
        {
            new(RoomKey, Text.L("Room (address, alias or id)", "Ruimte (adres, alias of id)"), "Identity", "text", Text.L("Required. Identifies the room mailbox.", "Verplicht. Identificeert het ruimtepostvak."), null),
            new("Capacity", Text.L("Capacity (Exchange)", "Capaciteit (Exchange)"), Text.L("Mailbox", "Postvak"), "integer", "Set-Mailbox -ResourceCapacity", null),
        };
        fields.AddRange(BookingPolicyFields.All.Where(f => f.Kind != PolicyFieldKind.RecipientList).Select(f => new BulkTargetField(
            f.Name, f.Label, Text.L("Booking policy", "Boekingsbeleid"), f.Kind switch
            {
                PolicyFieldKind.Boolean => "boolean",
                PolicyFieldKind.Integer => "integer",
                PolicyFieldKind.Enum => "enum",
                _ => "text",
            }, f.Help, f.EnumValues)));
        fields.Add(new("WorkDays", Text.L("Work days", "Werkdagen"), Text.L("Working hours", "Werktijden"), "days", Text.L("e.g. Weekdays or Monday,Tuesday", "bijv. Weekdays of Monday,Tuesday"), null));
        fields.Add(new("WorkingHoursStartTime", Text.L("Start time", "Begintijd"), Text.L("Working hours", "Werktijden"), "time", "HH:mm", null));
        fields.Add(new("WorkingHoursEndTime", Text.L("End time", "Eindtijd"), Text.L("Working hours", "Werktijden"), "time", "HH:mm", null));
        fields.Add(new("WorkingHoursTimeZone", Text.L("Time zone", "Tijdzone"), Text.L("Working hours", "Werktijden"), "timezone", Text.L("Windows time zone name, e.g. W. Europe Standard Time", "Windows-tijdzonenaam, bijv. W. Europe Standard Time"), null));
        fields.Add(new("Places.Label", "Label", "Microsoft Places", "text", null, null));
        fields.Add(new("Places.Tags", "Tags", "Microsoft Places", "list", Text.L("Separate tags with ';' or '|'", "Scheid tags met ';' of '|'"), null));
        fields.Add(new("Places.IsWheelChairAccessible", Text.L("Wheelchair accessible", "Rolstoeltoegankelijk"), "Microsoft Places", "boolean", null, null));
        fields.Add(new("Places.AudioDeviceName", Text.L("Audio device", "Audioapparaat"), "Microsoft Places", "text", null, null));
        fields.Add(new("Places.VideoDeviceName", Text.L("Video device", "Videoapparaat"), "Microsoft Places", "text", null, null));
        fields.Add(new("Places.DisplayDeviceName", Text.L("Display device", "Beeldscherm"), "Microsoft Places", "text", null, null));
        fields.Add(new("Places.MTREnabled", Text.L("Teams Rooms system (MTR)", "Teams Rooms-systeem (MTR)"), "Microsoft Places", "boolean", null, null));
        fields.Add(new("Places.FloorLabel", Text.L("Floor label", "Verdiepingslabel"), "Microsoft Places", "text", null, null));
        fields.Add(new("RoomList", Text.L("Add to room list", "Aan ruimtelijst toevoegen"), Text.L("Room lists", "Ruimtelijsten"), "text", Text.L("Room list name or address", "Naam of adres van de ruimtelijst"), null));
        return fields;
    }

    public async Task<BulkParseResult> ParseAsync(BulkParseRequest request, CancellationToken ct)
    {
        var errors = new List<string>();
        List<Dictionary<string, string>> records;
        List<string> headers;
        try
        {
            (headers, records) = request.Format.Equals("json", StringComparison.OrdinalIgnoreCase) ? ParseJson(request.Content) : ParseCsv(request.Content);
        }
        catch (FormatException ex)
        {
            return new BulkParseResult([], TargetFields, new Dictionary<string, string>(), [], [ex.Message], 0, null);
        }
        if (records.Count == 0) errors.Add(Text.L("The file contains no data rows.", "Het bestand bevat geen gegevensrijen."));
        if (records.Count > 5000) errors.Add(Text.L("Imports are limited to 5000 rows per batch.", "Importeren is beperkt tot 5000 rijen per keer."));

        var mapping = request.Mapping is { Count: > 0 }
            ? request.Mapping.Where(m => headers.Contains(m.Key) && TargetFields.Any(f => f.Key == m.Value)).ToDictionary(m => m.Key, m => m.Value)
            : headers.Select(h => (Header: h, Field: Suggest(h))).Where(x => x.Field is not null).GroupBy(x => x.Field).ToDictionary(g => g.First().Header, g => g.Key!);
        var columns = headers.Select(h => new BulkColumn(h, Suggest(h), records.Take(3).Select(r => r.GetValueOrDefault(h) ?? string.Empty).ToList())).ToList();

        if (!mapping.ContainsValue(RoomKey)) errors.Add(Text.L("Map one column to 'Room' so each row can be matched to a room mailbox.", "Koppel één kolom aan 'Ruimte', zodat elke rij aan een ruimtepostvak kan worden gekoppeld."));
        var mappedWh = WorkingHoursKeys.Count(mapping.ContainsValue);
        if (mappedWh is > 0 and < 4) errors.Add(Text.L("Working hours need all four columns: work days, start time, end time and time zone.", "Werktijden vereisen alle vier de kolommen: werkdagen, begintijd, eindtijd en tijdzone."));
        if (errors.Count > 0 && records.Count == 0) return new BulkParseResult(columns, TargetFields, mapping, [], errors, 0, null);

        var previews = new List<BulkRowPreview>();
        var rows = new List<BulkRow>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < records.Count && index < 5000; index++)
        {
            var record = records[index];
            var rowNumber = index + 2; // header is row 1
            var issues = new List<ValidationIssue>();
            var values = mapping.ToDictionary(m => m.Value, m => record.GetValueOrDefault(m.Key)?.Trim() ?? string.Empty);
            var identity = values.GetValueOrDefault(RoomKey) ?? string.Empty;
            var room = identity.Length == 0 ? null : await inventory.TryResolveRoomAsync(identity, ct);
            if (identity.Length == 0) issues.Add(new(RoomKey, Text.L("No room specified.", "Geen ruimte opgegeven."), IssueLevel.Error));
            else if (room is null) issues.Add(new(RoomKey, Text.L($"No room mailbox matches '{identity}'.", $"Geen ruimtepostvak komt overeen met '{identity}'."), IssueLevel.Error));
            else if (seen.TryGetValue(room.Id, out var first)) issues.Add(new(RoomKey, Text.L($"Duplicate of row {first}; combine the rows.", $"Duplicaat van rij {first}; voeg de rijen samen."), IssueLevel.Error));
            else seen[room.Id] = rowNumber;

            var booking = new Dictionary<string, JsonElement>();
            var placesFields = new Dictionary<string, JsonElement>();
            int? capacity = null;
            WorkingHoursConfig? hours = null;
            string? roomList = null;
            foreach (var (key, raw) in values)
            {
                if (key == RoomKey || raw.Length == 0) continue;
                var field = TargetFields.First(f => f.Key == key);
                try
                {
                    if (key == "Capacity") capacity = ParseInt(raw, field.Label);
                    else if (key == "RoomList") roomList = raw;
                    else if (key.StartsWith("Places.", StringComparison.Ordinal))
                    {
                        var name = key["Places.".Length..];
                        placesFields[name] = field.Kind switch
                        {
                            "boolean" => JsonSerializer.SerializeToElement(ParseBool(raw, field.Label)),
                            "list" => JsonSerializer.SerializeToElement(raw.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                            _ => JsonSerializer.SerializeToElement(raw),
                        };
                    }
                    else if (BookingPolicyFields.Find(key) is { } policyField)
                    {
                        var json = policyField.Kind switch
                        {
                            PolicyFieldKind.Boolean => JsonSerializer.SerializeToElement(ParseBool(raw, field.Label)),
                            PolicyFieldKind.Integer => JsonSerializer.SerializeToElement(ParseInt(raw, field.Label)),
                            _ => JsonSerializer.SerializeToElement(raw),
                        };
                        policyField.ApplyJson(BookingPolicy.ExchangeDefaults, json); // type check
                        if (policyField.Kind == PolicyFieldKind.Integer)
                        {
                            var n = json.GetInt32();
                            if (policyField.Min is { } min && n < min || policyField.Max is { } max && n > max)
                            {
                                throw new FormatException(Text.L($"{policyField.Label} must be between {policyField.Min} and {policyField.Max}.", $"{policyField.Label} moet tussen {policyField.Min} en {policyField.Max} liggen."));
                            }
                        }
                        booking[key] = json;
                    }
                }
                catch (FormatException ex)
                {
                    issues.Add(new(key, ex.Message, IssueLevel.Error));
                }
            }
            if (mappedWh == 4)
            {
                var whValues = WorkingHoursKeys.Select(k => values.GetValueOrDefault(k) ?? string.Empty).ToArray();
                if (whValues.Any(v => v.Length > 0))
                {
                    try
                    {
                        hours = new WorkingHoursConfig(WorkingHoursConfig.ParseWorkDays(whValues[0]), WorkingHoursConfig.ParseTime(whValues[1]), WorkingHoursConfig.ParseTime(whValues[2]), whValues[3]);
                        issues.AddRange(WorkingHoursValidator.Validate(hours, TimeZoneCatalog.IsKnown).Where(i => i.Level == IssueLevel.Error));
                    }
                    catch (FormatException ex)
                    {
                        issues.Add(new("WorkingHours", ex.Message, IssueLevel.Error));
                    }
                }
            }
            if (issues.Count == 0 && booking.Count == 0 && placesFields.Count == 0 && capacity is null && hours is null && roomList is null)
            {
                issues.Add(new("row", Text.L("The row contains no values to apply.", "De rij bevat geen waarden om toe te passen."), IssueLevel.Warning));
            }
            previews.Add(new BulkRowPreview(rowNumber, identity, room?.Id, room?.DisplayName, values, issues));
            if (room is not null && issues.All(i => i.Level != IssueLevel.Error) && (booking.Count > 0 || placesFields.Count > 0 || capacity is not null || hours is not null || roomList is not null))
            {
                rows.Add(new BulkRow
                {
                    RowNumber = rowNumber,
                    RoomIdentity = room.Id,
                    BookingFields = booking,
                    PlacesFields = placesFields,
                    Capacity = capacity,
                    WorkingHours = hours,
                    AddToRoomList = roomList,
                });
            }
        }
        return new BulkParseResult(columns, TargetFields, mapping, previews, errors, rows.Count, rows.Count > 0 ? new BulkImportIntent(rows) : null);
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? Suggest(string header)
    {
        var n = Normalize(header);
        if (n is "room" or "identity" or "email" or "emailaddress" or "primarysmtpaddress" or "smtp" or "mailbox" or "roommailbox" or "address" or "upn") return RoomKey;
        if (n is "capacity" or "resourcecapacity" or "seats") return "Capacity";
        if (n is "roomlist" or "list") return "RoomList";
        var match = TargetFields.FirstOrDefault(f => Normalize(f.Key) == n || Normalize(f.Label) == n || Normalize(f.Key.Replace("Places.", string.Empty, StringComparison.Ordinal)) == n);
        return match?.Key;
    }

    private static bool ParseBool(string raw, string label) => raw.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "1" or "on" or "$true" or "enabled" => true,
        "false" or "no" or "n" or "0" or "off" or "$false" or "disabled" => false,
        _ => throw new FormatException(Text.L($"{label}: '{raw}' is not a yes/no value.", $"{label}: '{raw}' is geen ja/nee-waarde.")),
    };

    private static int ParseInt(string raw, string label) =>
        int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException(Text.L($"{label}: '{raw}' is not a whole number.", $"{label}: '{raw}' is geen geheel getal."));

    private static (List<string>, List<Dictionary<string, string>>) ParseJson(string content)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(content); }
        catch (JsonException ex) { throw new FormatException(Text.L($"Invalid JSON: {ex.Message}", $"Ongeldige JSON: {ex.Message}")); }
        using (doc)
        {
            var array = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("rooms", out var rooms) && rooms.ValueKind == JsonValueKind.Array ? rooms
                : throw new FormatException(Text.L("Expected a JSON array of objects (or an object with a 'rooms' array).", "Verwacht een JSON-array met objecten (of een object met een array 'rooms')."));
            var headers = new List<string>();
            var records = new List<Dictionary<string, string>>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException(Text.L("Every array entry must be an object.", "Elk element van de array moet een object zijn."));
                var record = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in item.EnumerateObject())
                {
                    if (!headers.Contains(prop.Name)) headers.Add(prop.Name);
                    record[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        JsonValueKind.Null => string.Empty,
                        JsonValueKind.Array => string.Join(";", prop.Value.EnumerateArray().Select(e => e.ToString())),
                        _ => prop.Value.ToString(),
                    };
                }
                records.Add(record);
            }
            return (headers, records);
        }
    }

    /// <summary>RFC 4180 CSV with delimiter detection (comma, semicolon as produced by Excel in many European locales, or tab).</summary>
    public static (List<string>, List<Dictionary<string, string>>) ParseCsv(string content)
    {
        content = content.TrimStart('﻿');
        var firstLine = content.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        var rows = new List<List<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(v => v.Length > 0)) rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        if (inQuotes) throw new FormatException(Text.L("The CSV file has an unterminated quoted value.", "Het CSV-bestand bevat een waarde tussen aanhalingstekens die niet is afgesloten."));
        row.Add(field.ToString());
        if (row.Any(v => v.Length > 0)) rows.Add(row);
        if (rows.Count == 0) throw new FormatException(Text.L("The CSV file is empty.", "Het CSV-bestand is leeg."));

        var headers = rows[0].Select(h => h.Trim()).ToList();
        if (headers.Any(h => h.Length == 0)) throw new FormatException(Text.L("Every column needs a header.", "Elke kolom heeft een kop nodig."));
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count) throw new FormatException(Text.L("Column headers must be unique.", "Kolomkoppen moeten uniek zijn."));
        var records = rows.Skip(1).Select((r, idx) =>
        {
            if (r.Count > headers.Count) throw new FormatException(Text.L($"Row {idx + 2} has more values than there are columns.", $"Rij {idx + 2} heeft meer waarden dan er kolommen zijn."));
            return headers.Select((h, i) => (h, v: i < r.Count ? r[i] : string.Empty)).ToDictionary(x => x.h, x => x.v, StringComparer.Ordinal);
        }).ToList();
        return (headers, records);
    }
}
