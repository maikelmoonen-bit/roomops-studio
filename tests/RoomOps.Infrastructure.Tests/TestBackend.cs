using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Execution;
using RoomOps.Application.Services;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Rooms;
using RoomOps.Infrastructure.Persistence;

namespace RoomOps.Infrastructure.Tests;

/// <summary>A clock tests can advance; provisioning waits in the executor advance it instead of sleeping.</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private readonly object _gate = new();

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public void Advance(TimeSpan by) { lock (_gate) _now += by; }
}

public sealed class RecordingSink : IEventSink
{
    public ConcurrentQueue<(string Topic, object Payload)> Events { get; } = new();

    public void Publish(string topic, object payload) => Events.Enqueue((topic, payload));
}

/// <summary>The real backend composition (session manager, planners, executor, SQLite stores) in demo mode.</summary>
public sealed class TestBackend : IAsyncDisposable
{
    private readonly string _directory;

    private TestBackend(string directory, ServiceProvider services, ManualTime time)
    {
        _directory = directory;
        Services = services;
        Time = time;
    }

    public ServiceProvider Services { get; }
    public ManualTime Time { get; }
    public RecordingSink Events => (RecordingSink)Services.GetRequiredService<IEventSink>();
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    public ISessionManager Sessions => Get<ISessionManager>();

    public static async Task<TestBackend> StartDemoAsync(Func<AppSettings, AppSettings>? settings = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "roomops-tests", Guid.NewGuid().ToString("N"));
        var time = new ManualTime();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRoomOps(new AppPaths(directory));
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IEventSink, RecordingSink>();
        var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<ExecutionEngine>();
        engine.Delay = (delay, _) =>
        {
            time.Advance(delay);
            return Task.CompletedTask;
        };
        await provider.GetRequiredService<ISettingsStore>().UpdateAsync(s => (settings ?? (x => x))(s with { Execution = s.Execution with { PacingMilliseconds = 0 } }), CancellationToken.None);
        await provider.GetRequiredService<ISessionManager>().StartDemoAsync(CancellationToken.None);
        return new TestBackend(directory, provider, time);
    }

    public Task<RoomMailbox> Room(string alias) => Get<InventoryService>().ResolveRoomAsync($"{alias}@contoso-demo.example", CancellationToken.None);

    public Task<PlanPreview> PlanAsync(ChangeIntent intent) => Get<PlanService>().CreateAsync(intent, CancellationToken.None);

    public async Task<ExecutionState> ExecuteAsync(PlanPreview plan, bool acknowledgeRemovals = false)
    {
        var started = Get<PlanService>().Execute(new ExecuteRequest(plan.PlanId, plan.TenantId, acknowledgeRemovals));
        return await Get<ExecutionEngine>().WaitAsync(started.ExecutionId);
    }

    public async Task<ExecutionState> PlanAndExecuteAsync(ChangeIntent intent, bool acknowledgeRemovals = false) =>
        await ExecuteAsync(await PlanAsync(intent), acknowledgeRemovals);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
