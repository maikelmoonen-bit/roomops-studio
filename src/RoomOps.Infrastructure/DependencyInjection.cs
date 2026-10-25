using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoomOps.Application;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Sessions;
using RoomOps.Infrastructure.Persistence;
using RoomOps.Infrastructure.PowerShell;
using RoomOps.Infrastructure.Sessions;

namespace RoomOps.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the complete backend. Hosts (desktop, dev host) add only an <see cref="IEventSink"/> and logging.</summary>
    public static IServiceCollection AddRoomOps(this IServiceCollection services, AppPaths paths)
    {
        services.AddRoomOpsApplication();
        services.AddSingleton(paths);
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IAuditStore, SqliteAuditStore>();
        services.AddSingleton<ITemplateStore, SqliteTemplateStore>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<SignInWindow>();
        services.AddSingleton<IServiceWorkerFactory>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsStore>();
            var window = sp.GetRequiredService<SignInWindow>();
            return new PowerShellWorkerFactory(() => new WorkerLaunchOptions
            {
                PowerShellPath = settings.Current.Connection.PowerShellPath,
                WorkDirectory = paths.Worker,
                SignInOwnerWindow = window.Handle,
            }, sp.GetRequiredService<ILoggerFactory>());
        });
        services.AddSingleton<SessionManager>();
        services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<SessionManager>());
        return services;
    }
}
