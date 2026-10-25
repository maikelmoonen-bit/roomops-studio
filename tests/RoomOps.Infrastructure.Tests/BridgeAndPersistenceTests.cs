using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Execution;
using RoomOps.Bridge;
using RoomOps.Domain.Changes;
using RoomOps.Infrastructure.Persistence;
using RoomOps.Infrastructure.PowerShell;

namespace RoomOps.Infrastructure.Tests;

public class BridgeDispatcherTests
{
    private sealed class TestHost : IHostServices
    {
        public AppInfo Info { get; } = new("RoomOps Studio", "test", "test", "test", "test", new HostCapabilities(false, false), "/tmp", false);
        public Task<SaveFileResult> SaveFileAsync(SaveFileRequest request, CancellationToken cancellationToken) => Task.FromResult(new SaveFileResult(false, null, true));
        public Task<OpenFileResult> OpenFileAsync(OpenFileRequest request, CancellationToken cancellationToken) => Task.FromResult(new OpenFileResult(false, null, null, true));
        public void Ready() { }
        public void SetTheme(bool dark) { }
        public bool OpenExternal(Uri uri) => true;
    }

    private static async Task<(TestBackend Backend, BridgeDispatcher Dispatcher)> CreateAsync()
    {
        var backend = await TestBackend.StartDemoAsync();
        var dispatcher = new BridgeDispatcher(new HostedProvider(backend.Services), BridgeContract.Build(), NullLogger<BridgeDispatcher>.Instance);
        return (backend, dispatcher);
    }

    /// <summary>Adds the bridge-level services (host, gate) on top of the backend provider.</summary>
    private sealed class HostedProvider(IServiceProvider inner) : IServiceProvider
    {
        private readonly TestHost _host = new();

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IHostServices)) return _host;
            if (serviceType == typeof(LiveConnectionGate)) return new LiveConnectionGate(inner.GetRequiredService<Application.Sessions.ISessionManager>(), _host);
            return inner.GetService(serviceType);
        }
    }

    private static async Task<JsonObject> Call(BridgeDispatcher dispatcher, string method, JsonNode? parameters = null) =>
        JsonNode.Parse(await dispatcher.DispatchAsync(new JsonObject { ["type"] = "request", ["id"] = "1", ["method"] = method, ["params"] = parameters ?? new JsonObject() }.ToJsonString(), CancellationToken.None))!.AsObject();

    [Fact]
    public async Task Unknown_methods_and_malformed_messages_are_rejected()
    {
        var (backend, dispatcher) = await CreateAsync();
        await using var _ = backend;
        var unknown = await Call(dispatcher, "powershell.invoke", new JsonObject { ["script"] = "Get-Process" });
        Assert.False(unknown["ok"]!.GetValue<bool>());
        Assert.Equal("bridge.unknownMethod", unknown["error"]!["code"]!.GetValue<string>());
        var malformed = JsonNode.Parse(await dispatcher.DispatchAsync("{not json", CancellationToken.None))!;
        Assert.False(malformed["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Parameters_are_strictly_typed()
    {
        var (backend, dispatcher) = await CreateAsync();
        await using var _ = backend;
        var extra = await Call(dispatcher, "rooms.list", new JsonObject { ["refresh"] = false, ["command"] = "Remove-Mailbox" });
        Assert.Equal("bridge.invalidParams", extra["error"]!["code"]!.GetValue<string>());
        var wrongType = await Call(dispatcher, "rooms.get", new JsonObject { ["roomId"] = 42 });
        Assert.Equal("bridge.invalidParams", wrongType["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Polymorphic_intents_round_trip_through_the_bridge()
    {
        var (backend, dispatcher) = await CreateAsync();
        await using var _ = backend;
        var room = await backend.Room("ehv-1.07");
        var plan = await Call(dispatcher, "plans.create", new JsonObject
        {
            ["roomIds"] = new JsonArray(room.Id),
            ["fields"] = new JsonObject { ["BookingWindowInDays"] = 75 },
            ["kind"] = "patchBookingPolicy",
        });
        Assert.True(plan["ok"]!.GetValue<bool>(), plan.ToJsonString());
        Assert.Equal("Changes", plan["result"]!["targets"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Policies_returned_by_the_backend_can_be_sent_back_unchanged()
    {
        var (backend, dispatcher) = await CreateAsync();
        await using var _ = backend;
        var room = await backend.Room("ehv-0.10");
        var detail = await Call(dispatcher, "rooms.get", new JsonObject { ["roomId"] = room.Id });
        var policy = detail["result"]!["booking"]!.DeepClone();
        var plan = await Call(dispatcher, "plans.create", new JsonObject { ["kind"] = "updateBookingPolicy", ["roomId"] = room.Id, ["policy"] = policy });
        Assert.True(plan["ok"]!.GetValue<bool>(), plan.ToJsonString());
        Assert.Equal("NoChange", plan["result"]!["targets"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Live_connections_are_refused_when_the_host_disallows_them()
    {
        var (backend, dispatcher) = await CreateAsync();
        await using var _ = backend;
        var response = await Call(dispatcher, "connections.connect", new JsonObject { ["service"] = "Exchange", ["userPrincipalName"] = null, ["signInMethod"] = "Browser" });
        Assert.Equal("connections.disabled", response["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task External_links_are_limited_to_microsoft_documentation_and_portals()
    {
        var (backend, dispatcher) = await CreateAsync();
        await using var _ = backend;
        Assert.True((await Call(dispatcher, "app.openExternal", new JsonObject { ["url"] = "https://learn.microsoft.com/en-us/microsoft-365/places/" }))["ok"]!.GetValue<bool>());
        Assert.False((await Call(dispatcher, "app.openExternal", new JsonObject { ["url"] = "https://evil.example/" }))["ok"]!.GetValue<bool>());
        Assert.False((await Call(dispatcher, "app.openExternal", new JsonObject { ["url"] = "file:///etc/passwd" }))["ok"]!.GetValue<bool>());
    }
}

public class LiveSessionTests
{
    private sealed class LiveHost : IHostServices
    {
        public AppInfo Info { get; } = new("RoomOps Studio", "test", "desktop", "test", "test", new HostCapabilities(true, true), "/tmp", true);
        public Task<SaveFileResult> SaveFileAsync(SaveFileRequest request, CancellationToken cancellationToken) => Task.FromResult(new SaveFileResult(false, null, true));
        public Task<OpenFileResult> OpenFileAsync(OpenFileRequest request, CancellationToken cancellationToken) => Task.FromResult(new OpenFileResult(false, null, null, true));
        public void Ready() { }
        public void SetTheme(bool dark) { }
        public bool OpenExternal(Uri uri) => true;
    }

    /// <summary>Exchange signs in; Graph fails the way Microsoft.Graph.Authentication does without a parent window.</summary>
    private sealed class ScriptedWorker(Application.Commands.ServiceKind service) : IServiceWorker
    {
        public Application.Commands.ServiceKind Service => service;
        public bool IsAlive => true;
        public event Action<string>? Exited { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ConnectInfo> ConnectAsync(Application.Sessions.ConnectRequest request, string? tenantId, Action<Application.Sessions.SignInPrompt> onPrompt, CancellationToken cancellationToken) =>
            service == Application.Commands.ServiceKind.Graph
                ? throw new Application.Commands.CommandFailedException("connect", Domain.Common.ErrorKind.Authentication,
                    "InteractiveBrowserCredential authentication failed: A window handle must be configured. See https://aka.ms/msal-net-wam#parent-window-handles")
                : Task.FromResult(new ConnectInfo("11111111-2222-3333-4444-555555555555", "admin@contoso.example", "3.9.0", []));

        public Task<Application.Commands.CommandResult> RunAsync(Application.Commands.PsCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new Application.Commands.CommandResult([], [], TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedWorkers : IServiceWorkerFactory
    {
        public IServiceWorker Create(Application.Commands.ServiceKind service) => new ScriptedWorker(service);
    }

    [Fact]
    public async Task A_failed_graph_sign_in_leaves_exchange_connected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "roomops-tests", Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRoomOps(new AppPaths(directory));
        services.AddSingleton<IServiceWorkerFactory, ScriptedWorkers>();
        services.AddSingleton<IEventSink, RecordingSink>();
        services.AddSingleton<IHostServices, LiveHost>();
        services.AddSingleton<LiveConnectionGate>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new BridgeDispatcher(provider, BridgeContract.Build(), NullLogger<BridgeDispatcher>.Instance);

        async Task<JsonObject> Connect(string service) => JsonNode.Parse(await dispatcher.DispatchAsync(new JsonObject
        {
            ["type"] = "request", ["id"] = service, ["method"] = "connections.connect",
            ["params"] = new JsonObject { ["service"] = service, ["userPrincipalName"] = null, ["signInMethod"] = "Browser" },
        }.ToJsonString(), CancellationToken.None))!.AsObject();

        Assert.True((await Connect("Exchange"))["ok"]!.GetValue<bool>());
        var graph = await Connect("Graph");
        Assert.False(graph["ok"]!.GetValue<bool>());

        var session = provider.GetRequiredService<Application.Sessions.ISessionManager>().Current;
        Assert.Equal(Application.Sessions.ConnectionStatus.Connected, session.For(Application.Commands.ServiceKind.Exchange).Status);
        Assert.Equal(Application.Sessions.ConnectionStatus.Failed, session.For(Application.Commands.ServiceKind.Graph).Status);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}

public class PersistenceTests
{
    private static AuditRecord Record(string tenant, string target, string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ExecutionId = "e",
        PlanId = "p",
        Timestamp = DateTimeOffset.UtcNow,
        Actor = "admin@x.example",
        TenantId = tenant,
        IsDemo = false,
        OperationType = "booking.update",
        OperationLabel = "Update booking policy",
        TargetKind = "Room",
        TargetId = target,
        TargetName = name,
        Outcome = AuditOutcome.Succeeded,
    };

    [Fact]
    public async Task Audit_records_are_scoped_by_tenant_and_searchable_with_literal_wildcards()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roomops-tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteAuditStore(new SqliteDatabase(new AppPaths(dir)));
        await store.AppendAsync(Record("t1", "r1", "Room 100%"), CancellationToken.None);
        await store.AppendAsync(Record("t1", "r2", "Room 1000"), CancellationToken.None);
        await store.AppendAsync(Record("t2", "r3", "Other tenant room"), CancellationToken.None);

        Assert.Equal(2, await store.CountAsync(new AuditQuery(TenantId: "t1"), CancellationToken.None));
        var literal = await store.QueryAsync(new AuditQuery(TenantId: "t1", Search: "100%"), CancellationToken.None);
        Assert.Equal("Room 100%", Assert.Single(literal).TargetName);

        var id = literal[0].Id;
        await store.UpdateVerificationAsync(id, new VerificationResult(VerificationOutcome.Verified, "ok", []), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(VerificationOutcome.Verified, (await store.GetAsync(id, CancellationToken.None))!.Verification!.Outcome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Settings_persist_without_secrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roomops-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        using (var store = new JsonSettingsStore(paths))
        {
            await store.UpdateAsync(s => s with { Appearance = s.Appearance with { Theme = ThemePreference.Dark } }, CancellationToken.None);
        }
        using var reloaded = new JsonSettingsStore(paths);
        Assert.Equal(ThemePreference.Dark, reloaded.Current.Appearance.Theme);
        var json = await File.ReadAllTextAsync(paths.Settings);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }
}
