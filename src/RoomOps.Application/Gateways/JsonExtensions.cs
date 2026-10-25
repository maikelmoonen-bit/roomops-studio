using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoomOps.Application.Gateways;

/// <summary>
/// Tolerant readers for objects returned by the worker. PowerShell output is normalized by the worker (primitives, ISO dates,
/// string arrays), but some REST-backed cmdlets return booleans or numbers as strings; these helpers accept both.
/// </summary>
public static class JsonObjectExtensions
{
    public static string? Str(this JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var node) || node is null) return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return string.IsNullOrEmpty(s) ? null : s;
            return v.ToJsonString().Trim('"');
        }
        return node.ToJsonString();
    }

    public static string StrOr(this JsonObject o, string name, string fallback) => o.Str(name) ?? fallback;

    public static int? Int(this JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var node) || node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (v.TryGetValue<double>(out var d) && Math.Abs(d % 1) < double.Epsilon) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    public static bool? Bool(this JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var node) || node is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    public static DateTimeOffset? Date(this JsonObject o, string name)
    {
        var s = o.Str(name);
        return s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    public static IReadOnlyList<string> StrList(this JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var node) || node is null) return [];
        if (node is JsonArray arr)
        {
            return arr.Where(n => n is not null)
                .Select(n => n is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : n!.ToJsonString().Trim('"'))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        var single = o.Str(name);
        return string.IsNullOrWhiteSpace(single) ? [] : [single];
    }

    public static JsonElement ToElement(this JsonObject o) => JsonSerializer.SerializeToElement(o);
}
