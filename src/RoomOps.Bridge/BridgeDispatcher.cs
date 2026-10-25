using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Execution;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Common;

namespace RoomOps.Bridge;

/// <summary>
/// Routes bridge messages to registered methods. Message format (JSON):
///   request  { "type": "request", "id": string, "method": string, "params": object }
///   response { "type": "response", "id": string, "ok": true, "result": any } | { ..., "ok": false, "error": { code, message, kind, detail } }
///   event    { "type": "event", "topic": string, "payload": any }
/// Unknown methods, malformed messages and oversized payloads are rejected without side effects.
/// </summary>
public sealed class BridgeDispatcher(IServiceProvider services, BridgeRegistry registry, ILogger<BridgeDispatcher> logger)
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions Strict = new(RoomOpsJson.Options)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
    };

    public async Task<string> DispatchAsync(string message, CancellationToken cancellationToken)
    {
        string? id = null;
        string? method = null;
        try
        {
            if (message.Length > MaxMessageBytes)
            {
                throw RoomOpsException.Validation("bridge.tooLarge", Text.L("The request is too large.", "Het verzoek is te groot."));
            }
            var node = JsonNode.Parse(message) as JsonObject ?? throw RoomOpsException.Validation("bridge.malformed", Text.L("Malformed request.", "Ongeldig verzoek."));
            id = node["id"]?.GetValue<string>();
            method = node["method"]?.GetValue<string>();
            if (node["type"]?.GetValue<string>() != "request" || id is null || method is null)
            {
                throw RoomOpsException.Validation("bridge.malformed", Text.L("Malformed request.", "Ongeldig verzoek."));
            }
            if (!registry.Methods.TryGetValue(method, out var definition))
            {
                throw RoomOpsException.Validation("bridge.unknownMethod", Text.L($"Unknown method '{method}'.", $"Onbekende methode '{method}'."));
            }

            var parameters = node["params"] ?? new JsonObject();
            object request;
            try
            {
                request = parameters.Deserialize(definition.RequestType, Strict)
                    ?? throw RoomOpsException.Validation("bridge.invalidParams", Text.L("Missing parameters.", "Parameters ontbreken."));
            }
            catch (JsonException ex)
            {
                throw RoomOpsException.Validation("bridge.invalidParams", Text.L($"Invalid parameters for {method}: {ex.Message}", $"Ongeldige parameters voor {method}: {ex.Message}"));
            }
            catch (InvalidOperationException ex)
            {
                throw RoomOpsException.Validation("bridge.invalidParams", Text.L($"Invalid parameters for {method}: {ex.Message}", $"Ongeldige parameters voor {method}: {ex.Message}"));
            }

            if (definition.Kind == BridgeMethodKind.Command)
            {
                logger.LogInformation("Bridge command {Method}", method);
            }
            var result = await definition.Handler(services, request, cancellationToken);
            return Respond(id, true, JsonSerializer.SerializeToNode(result, definition.ResponseType, RoomOpsJson.Options), null);
        }
        catch (RoomOpsException ex)
        {
            // Only an allowlisted command identifies the service whose session failed. Worker operations such as "connect" are not
            // catalog commands: a failed sign-in is already recorded for its own service and must not expire another one.
            if (ex.Error.Kind is ErrorKind.Authentication && ex is Application.Commands.CommandFailedException cfe
                && Application.Commands.CommandCatalog.IsAllowed(cfe.Command))
            {
                services.GetService<ISessionManager>()?.ReportAuthenticationFailure(Application.Commands.CommandCatalog.Get(cfe.Command).Service, ex.Message);
            }
            logger.LogInformation("Bridge {Method} failed: {Code} {Message}", method, ex.Error.Code, ex.Message);
            return Respond(id, false, null, ex.Error);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Respond(id, false, null, new DomainError("bridge.malformed", Text.L("Malformed request.", "Ongeldig verzoek."), ErrorKind.Validation, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in bridge method {Method}", method);
            return Respond(id, false, null, new DomainError("internal", Text.L("An unexpected error occurred. Details were written to the RoomOps log.", "Er is een onverwachte fout opgetreden. Details zijn naar het RoomOps-logboek geschreven."), ErrorKind.Internal));
        }
    }

    private static string Respond(string? id, bool ok, JsonNode? result, DomainError? error)
    {
        var response = new JsonObject
        {
            ["type"] = "response",
            ["id"] = id,
            ["ok"] = ok,
        };
        if (ok)
        {
            response["result"] = result;
        }
        else
        {
            response["error"] = JsonSerializer.SerializeToNode(error, RoomOpsJson.Options);
        }
        return response.ToJsonString();
    }

    public static string EventMessage(string topic, object payload) =>
        new JsonObject { ["type"] = "event", ["topic"] = topic, ["payload"] = JsonSerializer.SerializeToNode(payload, payload.GetType(), RoomOpsJson.Options) }.ToJsonString();
}

/// <summary>Event sink that forwards events to the host transport (WebView2 PostWebMessage or the dev host WebSocket).</summary>
public sealed class BridgeEventSink : IEventSink
{
    private readonly List<Action<string>> _subscribers = [];
    private readonly object _gate = new();

    public IDisposable Subscribe(Action<string> send)
    {
        lock (_gate) _subscribers.Add(send);
        return new Unsubscriber(() => { lock (_gate) _subscribers.Remove(send); });
    }

    public void Publish(string topic, object payload)
    {
        var message = BridgeDispatcher.EventMessage(topic, payload);
        Action<string>[] targets;
        lock (_gate) targets = [.. _subscribers];
        foreach (var send in targets)
        {
            try { send(message); }
            catch (Exception) { /* A closed transport must not break publishers. */ }
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public static class BridgeServiceCollectionExtensions
{
    public static IServiceCollection AddRoomOpsBridge(this IServiceCollection services)
    {
        services.AddSingleton(BridgeContract.Build());
        services.AddSingleton<BridgeDispatcher>();
        services.AddSingleton<BridgeEventSink>();
        services.AddSingleton<IEventSink>(sp => sp.GetRequiredService<BridgeEventSink>());
        services.AddSingleton<LiveConnectionGate>();
        return services;
    }

    /// <summary>Forwards session changes and sign-in prompts as bridge events.</summary>
    public static void WireSessionEvents(this IServiceProvider services)
    {
        var sessions = services.GetRequiredService<ISessionManager>();
        var sink = services.GetRequiredService<IEventSink>();
        var plans = services.GetRequiredService<PlanRegistry>();
        long lastEpoch = sessions.Current.Epoch;
        sessions.Changed += snapshot =>
        {
            if (snapshot.Epoch != Interlocked.Exchange(ref lastEpoch, snapshot.Epoch))
            {
                plans.Clear();
            }
            sink.Publish("session.changed", snapshot);
        };
        sessions.SignInPromptRaised += prompt => sink.Publish("signIn.prompt", prompt);
    }
}
