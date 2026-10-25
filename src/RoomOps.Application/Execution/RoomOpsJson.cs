using System.Text.Json;
using System.Text.Json.Serialization;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Commands;

namespace RoomOps.Application.Execution;

public static class RoomOpsJson
{
    /// <summary>Wire format shared by the bridge, persistence and stored intents: camelCase, string enums.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
            MaxDepth = 64,
            // Polymorphic intents may arrive with the "kind" discriminator anywhere in the object.
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Converts commands to and from storable data. Restored commands are rebuilt through <see cref="PsCommand.Create(string, IEnumerable{KeyValuePair{string, object?}})"/>, so the allowlist applies again.</summary>
public static class CommandStorage
{
    private const string SwitchMarker = "$switch";

    public static StoredCommand ToStored(PlannedOperation operation)
    {
        var parameters = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in operation.Command.Parameters)
        {
            parameters[key] = value is PsSwitch sw
                ? JsonSerializer.SerializeToElement(new Dictionary<string, bool> { [SwitchMarker] = sw.IsPresent })
                : JsonSerializer.SerializeToElement(value);
        }
        return new StoredCommand(operation.Command.Name, parameters, operation.Description);
    }

    public static PsCommand FromStored(StoredCommand stored) =>
        PsCommand.Create(stored.Name, stored.Parameters.Select(p => new KeyValuePair<string, object?>(p.Key, ToValue(p.Value))));

    private static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString(),
        // The cast matters: without it the conditional widens every integer to long.
        JsonValueKind.Number => e.TryGetInt32(out var i) ? (object)i : e.GetInt64(),
        JsonValueKind.Array => e.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray(),
        JsonValueKind.Object when e.TryGetProperty(SwitchMarker, out var sw) => new PsSwitch(sw.GetBoolean()),
        _ => throw new FormatException("Unsupported stored parameter value."),
    };
}
