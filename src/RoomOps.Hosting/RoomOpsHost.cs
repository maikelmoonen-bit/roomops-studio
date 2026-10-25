using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoomOps.Application.Abstractions;
using RoomOps.Bridge;
using RoomOps.Domain.Common;
using RoomOps.Infrastructure;
using RoomOps.Infrastructure.Persistence;
using RoomOps.Infrastructure.PowerShell;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RoomOps.Hosting;

/// <summary>Composition root shared by the desktop shell and the development host.</summary>
public static class RoomOpsHost
{
    public static string Version =>
        typeof(RoomOpsHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static ServiceProvider Build(AppPaths paths, Func<IServiceProvider, IHostServices> hostServices, bool consoleLogging = false)
    {
        // The UI is English; keep backend-produced text (time-zone display names, number formats) consistent with it.
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.With<RedactingEnricher>()
            .WriteTo.File(Path.Combine(paths.Logs, "roomops-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Logger(l => { if (consoleLogging) l.WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information); })
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddSerilog(logger, dispose: true).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));
        services.AddRoomOps(paths);
        services.AddRoomOpsBridge();
        services.AddSingleton(hostServices);
        services.AddSingleton<IEnvironmentProbe, EnvironmentProbe>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        provider.WireSessionEvents();
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("RoomOps").LogInformation("RoomOps Studio {Version} started; data directory {Root}", Version, paths.Root);
        return provider;
    }
}

/// <summary>Scrubs anything token-like from every log event property before it is written.</summary>
internal sealed class RedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (key, value) in logEvent.Properties.ToList())
        {
            if (value is ScalarValue { Value: string s })
            {
                var clean = PowerShellWorker.Sanitize(s);
                if (!ReferenceEquals(clean, s) && clean != s) logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, clean));
            }
        }
    }
}

/// <summary>
/// Reports whether PowerShell 7 and the Microsoft modules are installed. Runs a fixed, constant probe command; no input from the
/// frontend is passed to PowerShell.
/// </summary>
internal sealed class EnvironmentProbe(ISettingsStore settings, ILogger<EnvironmentProbe> logger) : IEnvironmentProbe
{
    private static (string Name, string Purpose, string Minimum)[] Modules =>
    [
        ("ExchangeOnlineManagement", Text.L("Exchange Online room mailboxes, booking policies and room lists", "Ruimtepostvakken, boekingsbeleid en ruimtelijsten in Exchange Online"), "3.7.2"),
        ("MicrosoftPlaces", Text.L("Microsoft Places buildings, floors, sections and room metadata", "Gebouwen, verdiepingen, secties en ruimtegegevens in Microsoft Places"), "2.0.0"),
        ("Microsoft.Graph.Authentication", Text.L("Microsoft Graph account and license readiness (read-only)", "Account- en licentiegereedheid via Microsoft Graph (alleen-lezen)"), "2.0.0"),
    ];

    private const string ProbeScript =
        "$ErrorActionPreference='Stop';" +
        "$m=@(Get-Module -ListAvailable -Name ExchangeOnlineManagement,MicrosoftPlaces,Microsoft.Graph.Authentication | " +
        "Group-Object Name | ForEach-Object { $v=$_.Group | Sort-Object Version -Descending | Select-Object -First 1; @{name=$_.Name;version=$v.Version.ToString()} });" +
        "@{ps=$PSVersionTable.PSVersion.ToString();modules=$m} | ConvertTo-Json -Compress -Depth 4";

    private EnvironmentInfo? _cached;
    private DateTimeOffset _cachedAt;
    private string? _cachedLanguage;

    public async Task<EnvironmentInfo> ProbeAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null && _cachedLanguage == Text.Language && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMinutes(2)) return _cached with { KnownTenants = settings.Current.KnownTenants };
        var pwsh = PowerShellWorker.FindPowerShell(settings.Current.Connection.PowerShellPath);
        string? version = null;
        string? error = null;
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pwsh is null)
        {
            error = Text.L("PowerShell 7 (pwsh) was not found. Install PowerShell 7.2 or later, or set its path in Settings.", "PowerShell 7 (pwsh) is niet gevonden. Installeer PowerShell 7.2 of later, of stel het pad in bij Instellingen.");
        }
        else
        {
            try
            {
                var psi = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", ProbeScript }) psi.ArgumentList.Add(arg);
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("pwsh did not start.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                using var doc = JsonDocument.Parse(output.Trim());
                version = doc.RootElement.GetProperty("ps").GetString();
                if (doc.RootElement.TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in modules.EnumerateArray())
                    {
                        installed[m.GetProperty("name").GetString()!] = m.GetProperty("version").GetString()!;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception or KeyNotFoundException)
            {
                logger.LogWarning("PowerShell environment probe failed: {Message}", ex.Message);
                error = Text.L($"PowerShell could not be queried: {ex.Message}", $"PowerShell kon niet worden bevraagd: {ex.Message}");
            }
        }
        _cached = new EnvironmentInfo(pwsh, version,
            Modules.Select(m => new ModuleStatus(m.Name, m.Purpose, m.Minimum, installed.GetValueOrDefault(m.Name), $"Install-Module -Name {m.Name} -Scope CurrentUser")).ToList(),
            settings.Current.KnownTenants, error);
        _cachedAt = DateTimeOffset.UtcNow;
        _cachedLanguage = Text.Language;
        return _cached;
    }
}
