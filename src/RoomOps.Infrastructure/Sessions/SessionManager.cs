using Microsoft.Extensions.Logging;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Commands;
using RoomOps.Application.Gateways;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Common;
using RoomOps.Infrastructure.Demo;
using RoomOps.Infrastructure.Persistence;
using RoomOps.Infrastructure.PowerShell;

namespace RoomOps.Infrastructure.Sessions;

/// <summary>
/// Single active session (demo or one live tenant). Each live service runs in its own worker process; Exchange Online anchors
/// the tenant identity and every other service must prove it belongs to the same tenant before it can be used for writes.
/// </summary>
public sealed class SessionManager : ISessionManager, IAsyncDisposable
{
    private readonly IServiceWorkerFactory _workers;
    private readonly IAuditStore _liveAudit;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionManager> _logger;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<ServiceKind, ICommandRunner> _runners = [];
    private readonly Dictionary<ServiceKind, ServiceConnection> _connections = [];
    private SessionSnapshot _current = new(SessionMode.None, 0, null, null, null, null, []);
    private TenantCache _cache = new();
    private IAuditStore _audit;
    private long _epoch;

    public SessionManager(IServiceWorkerFactory workers, IAuditStore liveAudit, ISettingsStore settings, TimeProvider time, ILogger<SessionManager> logger)
    {
        _workers = workers;
        _liveAudit = liveAudit;
        _audit = liveAudit;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    public SessionSnapshot Current { get { lock (_gate) return _current; } }
    public TenantCache Cache { get { lock (_gate) return _cache; } }
    public IAuditStore Audit { get { lock (_gate) return _audit; } }

    public event Action<SessionSnapshot>? Changed;
    public event Action<SignInPrompt>? SignInPromptRaised;

    public ICommandRunner Runner(ServiceKind service)
    {
        lock (_gate)
        {
            if (_runners.TryGetValue(service, out var runner) && _connections.TryGetValue(service, out var conn) && conn.Status == ConnectionStatus.Connected)
            {
                return runner;
            }
            var label = Label(service);
            throw _current.Mode == SessionMode.None
                ? RoomOpsException.NotConnected(Text.L("No tenant is connected. Connect to Microsoft 365 or start demo mode.", "Er is geen tenant verbonden. Verbind met Microsoft 365 of start de demomodus."))
                : _connections.TryGetValue(service, out var c) && c.Status == ConnectionStatus.Expired
                    ? RoomOpsException.NotConnected(Text.L($"{label} is not connected (the session expired). Connect {label} on the Connections page.", $"{label} is niet verbonden (de sessie is verlopen). Verbind {label} op de pagina Verbindingen."))
                    : RoomOpsException.NotConnected(Text.L($"{label} is not connected. Connect {label} on the Connections page.", $"{label} is niet verbonden. Verbind {label} op de pagina Verbindingen."));
        }
    }

    public static string Label(ServiceKind service) => service switch
    {
        ServiceKind.Exchange => "Exchange Online",
        ServiceKind.Places => "Microsoft Places",
        ServiceKind.Graph => "Microsoft Graph",
        _ => service.ToString(),
    };

    public async Task<SessionSnapshot> StartDemoAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            await TearDownAsync();
            var tenant = new DemoTenant(_time);
            var now = _time.GetUtcNow();
            lock (_gate)
            {
                foreach (var service in Enum.GetValues<ServiceKind>())
                {
                    _runners[service] = new DemoCommandRunner(service, tenant, _time);
                    _connections[service] = new ServiceConnection(service, ConnectionStatus.Connected, DemoTenant.AdminAccount, DemoTenant.TenantId,
                        TenantVerification.Verified, Text.L("Synthetic demo tenant — no Microsoft 365 connection.", "Fictieve demotenant — geen verbinding met Microsoft 365."), now, "demo", service == ServiceKind.Graph ? ["User.Read.All"] : []);
                }
                _audit = new InMemoryAuditStore(DemoHistory.Create(tenant, now));
                _cache = new TenantCache();
                _current = Snapshot(SessionMode.Demo, DemoTenant.TenantId, DemoTenant.TenantName, DemoTenant.Domain, DemoTenant.AdminAccount);
            }
            _logger.LogInformation("Demo session started (epoch {Epoch})", _current.Epoch);
        }
        finally
        {
            _transition.Release();
        }
        RaiseChanged();
        return Current;
    }

    public async Task<SessionSnapshot> ConnectAsync(ConnectRequest request, CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken);
        IServiceWorker? worker = null;
        try
        {
            var session = Current;
            if (request.Service == ServiceKind.Exchange)
            {
                if (session.Mode == SessionMode.Demo || session.Mode == SessionMode.Live && session.IsConnected(ServiceKind.Exchange))
                {
                    // Switching tenants is explicit: the previous session (and every cache) is discarded first.
                    await TearDownAsync();
                }
            }
            else
            {
                if (session.Mode != SessionMode.Live || !session.IsConnected(ServiceKind.Exchange))
                {
                    throw RoomOpsException.NotConnected(Text.L("Connect Exchange Online first. It establishes and verifies the tenant identity for the other services.", "Verbind eerst Exchange Online. Dat legt de tenantidentiteit vast en controleert die voor de andere services."));
                }
                if (session.IsConnected(request.Service))
                {
                    return session;
                }
            }

            SetConnection(new ServiceConnection(request.Service, ConnectionStatus.Connecting, null, null, TenantVerification.NotApplicable, Text.L("Waiting for sign-in", "Wachten op aanmelding"), null, null, []));
            RaiseChanged();

            worker = _workers.Create(request.Service);
            await worker.StartAsync(cancellationToken);
            var info = await worker.ConnectAsync(request, session.TenantId, prompt => SignInPromptRaised?.Invoke(prompt), cancellationToken);

            if (request.Service == ServiceKind.Exchange)
            {
                if (string.IsNullOrWhiteSpace(info.TenantId))
                {
                    throw new RoomOpsException(new DomainError("session.noTenant", Text.L("Exchange Online did not report a tenant id.", "Exchange Online heeft geen tenant-id gemeld."), ErrorKind.Authentication));
                }
                lock (_gate)
                {
                    _runners[ServiceKind.Exchange] = worker;
                    _connections[ServiceKind.Exchange] = new ServiceConnection(ServiceKind.Exchange, ConnectionStatus.Connected, info.Account, info.TenantId,
                        TenantVerification.Verified, null, _time.GetUtcNow(), info.ModuleVersion, []);
                    _audit = _liveAudit;
                    _cache = new TenantCache();
                    _current = Snapshot(SessionMode.Live, info.TenantId, null, null, info.Account);
                }
                Watch(worker, ServiceKind.Exchange);
                worker = null;
                await DescribeTenantAsync(cancellationToken);
            }
            else
            {
                var verification = request.Service == ServiceKind.Graph
                    ? string.Equals(info.TenantId, session.TenantId, StringComparison.OrdinalIgnoreCase) ? TenantVerification.Verified : TenantVerification.Mismatch
                    : await CorrelatePlacesAsync(worker, cancellationToken);
                if (verification == TenantVerification.Mismatch)
                {
                    throw RoomOpsException.TenantMismatch(Text.L($"The {Label(request.Service)} sign-in belongs to a different tenant than Exchange Online ({session.TenantName ?? session.TenantId}). The connection was closed.", $"De aanmelding bij {Label(request.Service)} hoort bij een andere tenant dan Exchange Online ({session.TenantName ?? session.TenantId}). De verbinding is gesloten."));
                }
                lock (_gate)
                {
                    _runners[request.Service] = worker;
                    _connections[request.Service] = new ServiceConnection(request.Service, ConnectionStatus.Connected, info.Account ?? _current.AdminAccount, info.TenantId ?? _current.TenantId,
                        verification,
                        verification == TenantVerification.Unverified
                            ? Text.L("Connected, but the tenant could not be confirmed (no rooms to correlate). Places writes are blocked until it can be verified.", "Verbonden, maar de tenant kon niet worden bevestigd (geen ruimtes om te vergelijken). Schrijven naar Places is geblokkeerd totdat dit kan worden geverifieerd.")
                            : verification == TenantVerification.Correlated ? Text.L("Tenant confirmed by matching room mailboxes in both services.", "Tenant bevestigd via overeenkomende ruimtepostvakken in beide services.") : null,
                        _time.GetUtcNow(), info.ModuleVersion, info.Scopes);
                    _current = _current with { Epoch = _current.Epoch, Services = [.. _connections.Values.OrderBy(c => c.Service)] };
                    _cache.InvalidateInventory();
                }
                Watch(worker, request.Service);
                worker = null;
            }
            _logger.LogInformation("{Service} connected for tenant {Tenant}", request.Service, Current.TenantId);
        }
        catch (Exception ex)
        {
            var message = ex is RoomOpsException roe ? roe.Message : Text.L($"Sign-in failed: {ex.Message}", $"Aanmelden mislukt: {ex.Message}");
            SetConnection(new ServiceConnection(request.Service, ConnectionStatus.Failed, null, null, TenantVerification.NotApplicable, PowerShellWorker.Sanitize(message), null, null, []));
            if (request.Service == ServiceKind.Exchange && Current.Mode == SessionMode.None)
            {
                lock (_gate) _current = _current with { Services = [.. _connections.Values.OrderBy(c => c.Service)] };
            }
            _logger.LogWarning("{Service} connection failed: {Message}", request.Service, PowerShellWorker.Sanitize(message));
            throw;
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            _transition.Release();
            RaiseChanged();
        }
        return Current;
    }

    /// <summary>
    /// Microsoft Places has no documented tenant-context cmdlet. The session is confirmed by checking that Places room objects carry
    /// addresses of room mailboxes in the anchored Exchange tenant.
    /// </summary>
    private async Task<TenantVerification> CorrelatePlacesAsync(IServiceWorker places, CancellationToken ct)
    {
        try
        {
            var placesRooms = await places.RunAsync(PsCommand.Create("Get-PlaceV3", ("Type", "Room")), ct);
            var addresses = placesRooms.Objects.Select(o => o.Str("Identity")).Where(s => s?.Contains('@') == true).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (addresses.Count == 0) return TenantVerification.Unverified;
            var exchange = await Runner(ServiceKind.Exchange).RunAsync(PsCommand.Create("Get-EXOMailbox", ("RecipientTypeDetails", new[] { "RoomMailbox" }), ("ResultSize", "Unlimited")), ct);
            var rooms = exchange.Objects.Select(o => o.Str("PrimarySmtpAddress")).Where(s => s is not null).Cast<string>().ToList();
            return rooms.Any(addresses.Contains) ? TenantVerification.Correlated : TenantVerification.Mismatch;
        }
        catch (RoomOpsException ex)
        {
            _logger.LogWarning("Places tenant correlation failed: {Message}", ex.Message);
            return TenantVerification.Unverified;
        }
    }

    private async Task DescribeTenantAsync(CancellationToken ct)
    {
        try
        {
            var domains = await Runner(ServiceKind.Exchange).RunAsync(PsCommand.Create("Get-AcceptedDomain"), ct);
            var primary = domains.Objects.FirstOrDefault(o => o.Bool("Default") == true)?.Str("DomainName")
                ?? domains.Objects.Select(o => o.Str("DomainName")).FirstOrDefault(d => d?.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase) == true);
            lock (_gate) _current = _current with { TenantName = primary, PrimaryDomain = primary };
        }
        catch (RoomOpsException ex)
        {
            _logger.LogWarning("Could not read accepted domains: {Message}", ex.Message);
        }
        var snapshot = Current;
        await _settings.UpdateAsync(s => s with
        {
            KnownTenants = [new KnownTenant(snapshot.TenantId!, snapshot.TenantName, snapshot.PrimaryDomain, snapshot.AdminAccount, _time.GetUtcNow()),
                .. s.KnownTenants.Where(t => !string.Equals(t.TenantId, snapshot.TenantId, StringComparison.OrdinalIgnoreCase)).Take(9)],
        }, CancellationToken.None);
    }

    private void Watch(IServiceWorker worker, ServiceKind service) =>
        worker.Exited += message =>
        {
            lock (_gate)
            {
                if (!_runners.TryGetValue(service, out var current) || !ReferenceEquals(current, worker)) return;
                _connections[service] = _connections[service] with { Status = ConnectionStatus.Failed, Detail = message + Text.L(" Reconnect to continue.", " Verbind opnieuw om door te gaan.") };
                _current = _current with { Services = [.. _connections.Values.OrderBy(c => c.Service)] };
            }
            RaiseChanged();
        };

    public async Task<SessionSnapshot> DisconnectAsync(ServiceKind service, CancellationToken cancellationToken)
    {
        if (service == ServiceKind.Exchange || Current.Mode == SessionMode.Demo)
        {
            return await EndSessionAsync(cancellationToken);
        }
        await _transition.WaitAsync(cancellationToken);
        try
        {
            ICommandRunner? runner;
            lock (_gate)
            {
                _runners.Remove(service, out runner);
                _connections.Remove(service);
                _current = _current with { Services = [.. _connections.Values.OrderBy(c => c.Service)] };
                _cache.InvalidateInventory();
            }
            if (runner is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
        finally
        {
            _transition.Release();
        }
        RaiseChanged();
        return Current;
    }

    public async Task<SessionSnapshot> EndSessionAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            await TearDownAsync();
        }
        finally
        {
            _transition.Release();
        }
        RaiseChanged();
        return Current;
    }

    public void ReportAuthenticationFailure(ServiceKind service, string message)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(service, out var conn) || _current.Mode != SessionMode.Live) return;
            _connections[service] = conn with { Status = ConnectionStatus.Expired, Detail = PowerShellWorker.Sanitize(message) };
            _current = _current with { Services = [.. _connections.Values.OrderBy(c => c.Service)] };
        }
        RaiseChanged();
    }

    /// <summary>Disposes every worker (closing privileged sessions), drops all caches and advances the epoch.</summary>
    private async Task TearDownAsync()
    {
        List<ICommandRunner> runners;
        lock (_gate)
        {
            runners = [.. _runners.Values];
            _runners.Clear();
            _connections.Clear();
            _cache = new TenantCache();
            _audit = _liveAudit;
            _current = Snapshot(SessionMode.None, null, null, null, null);
        }
        foreach (var runner in runners)
        {
            if (runner is IAsyncDisposable disposable)
            {
                try { await disposable.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error while closing a service session"); }
            }
        }
    }

    private SessionSnapshot Snapshot(SessionMode mode, string? tenantId, string? tenantName, string? domain, string? account) =>
        new(mode, Interlocked.Increment(ref _epoch), tenantId, tenantName, domain, account, [.. _connections.Values.OrderBy(c => c.Service)]);

    private void SetConnection(ServiceConnection connection)
    {
        lock (_gate)
        {
            _connections[connection.Service] = connection;
            _current = _current with { Services = [.. _connections.Values.OrderBy(c => c.Service)] };
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(Current); }
        catch (Exception ex) { _logger.LogError(ex, "Session change handler failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync();
        _transition.Dispose();
    }
}
