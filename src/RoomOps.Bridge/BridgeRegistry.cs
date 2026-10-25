using Microsoft.Extensions.DependencyInjection;

namespace RoomOps.Bridge;

public enum BridgeMethodKind
{
    /// <summary>Reads state. Never changes Microsoft 365 or local configuration.</summary>
    Query,
    /// <summary>Changes local state (session, settings, templates) or executes a confirmed plan.</summary>
    Command,
}

public sealed record BridgeMethod(
    string Name,
    BridgeMethodKind Kind,
    Type RequestType,
    Type ResponseType,
    Func<IServiceProvider, object, CancellationToken, Task<object?>> Handler);

public sealed record BridgeEvent(string Topic, Type PayloadType);

/// <summary>
/// The complete, closed set of operations the frontend can invoke. Each method has a typed request and response; there is no
/// generic command endpoint and no way to pass script text to the backend.
/// </summary>
public sealed class BridgeRegistry
{
    private readonly Dictionary<string, BridgeMethod> _methods = new(StringComparer.Ordinal);
    private readonly List<BridgeEvent> _events = [];

    public IReadOnlyDictionary<string, BridgeMethod> Methods => _methods;
    public IReadOnlyList<BridgeEvent> Events => _events;

    public BridgeRegistry Query<TService, TRequest, TResponse>(string name, Func<TService, TRequest, CancellationToken, Task<TResponse>> handler)
        where TService : notnull => Add<TService, TRequest, TResponse>(name, BridgeMethodKind.Query, handler);

    public BridgeRegistry Command<TService, TRequest, TResponse>(string name, Func<TService, TRequest, CancellationToken, Task<TResponse>> handler)
        where TService : notnull => Add<TService, TRequest, TResponse>(name, BridgeMethodKind.Command, handler);

    private BridgeRegistry Add<TService, TRequest, TResponse>(string name, BridgeMethodKind kind, Func<TService, TRequest, CancellationToken, Task<TResponse>> handler)
        where TService : notnull
    {
        if (!_methods.TryAdd(name, new BridgeMethod(name, kind, typeof(TRequest), typeof(TResponse),
                async (sp, request, ct) => await handler(sp.GetRequiredService<TService>(), (TRequest)request, ct))))
        {
            throw new InvalidOperationException($"Bridge method '{name}' is registered twice.");
        }
        return this;
    }

    public BridgeRegistry Event<TPayload>(string topic)
    {
        _events.Add(new BridgeEvent(topic, typeof(TPayload)));
        return this;
    }
}
