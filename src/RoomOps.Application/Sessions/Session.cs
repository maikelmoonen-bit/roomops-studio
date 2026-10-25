using System.Collections.Concurrent;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Commands;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Places;
using RoomOps.Domain.RoomLists;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.TeamsRooms;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Application.Sessions;

public enum SessionMode
{
    None,
    Demo,
    Live,
}

public enum ConnectionStatus
{
    NotConnected,
    Connecting,
    Connected,
    Failed,
    Expired,
}

/// <summary>How a service session's tenant was confirmed to be the active tenant.</summary>
public enum TenantVerification
{
    NotApplicable,
    /// <summary>The service reported the tenant id and it matches.</summary>
    Verified,
    /// <summary>The service has no tenant-context API; tenant was confirmed by resolving objects known to belong to the active tenant.</summary>
    Correlated,
    /// <summary>The tenant could not be confirmed. Writes through this service are blocked.</summary>
    Unverified,
    Mismatch,
}

public enum SignInMethod
{
    /// <summary>System browser (Connect-ExchangeOnline -DisableWAM). Robust for a background worker process.</summary>
    Browser,
    /// <summary>Web Account Manager, the module default on Windows since EXO 3.7.0.</summary>
    WindowsAccountManager,
    /// <summary>Device code flow (PowerShell 7 only).</summary>
    DeviceCode,
}

public sealed record ServiceConnection(
    ServiceKind Service,
    ConnectionStatus Status,
    string? Account,
    string? TenantId,
    TenantVerification Verification,
    string? Detail,
    DateTimeOffset? ConnectedAt,
    string? ModuleVersion,
    IReadOnlyList<string> GrantedScopes);

public sealed record SessionSnapshot(
    SessionMode Mode,
    long Epoch,
    string? TenantId,
    string? TenantName,
    string? PrimaryDomain,
    string? AdminAccount,
    IReadOnlyList<ServiceConnection> Services)
{
    public ServiceConnection For(ServiceKind service) =>
        Services.FirstOrDefault(s => s.Service == service) ??
        new ServiceConnection(service, ConnectionStatus.NotConnected, null, null, TenantVerification.NotApplicable, null, null, null, []);

    public bool IsConnected(ServiceKind service) => For(service).Status == ConnectionStatus.Connected;
}

public sealed record ConnectRequest(ServiceKind Service, string? UserPrincipalName, SignInMethod SignInMethod);

/// <summary>Device-code or browser instructions surfaced to the UI while a sign-in is in progress.</summary>
public sealed record SignInPrompt(ServiceKind Service, string Message, string? VerificationUri, string? UserCode);

/// <summary>
/// Owns the single active tenant session. A session is either a clearly labeled demo or a live tenant; switching discards every
/// cache and worker so data never crosses tenants. Each live service has its own isolated connection.
/// </summary>
public interface ISessionManager
{
    SessionSnapshot Current { get; }
    TenantCache Cache { get; }
    IAuditStore Audit { get; }

    event Action<SessionSnapshot>? Changed;
    event Action<SignInPrompt>? SignInPromptRaised;

    Task<SessionSnapshot> StartDemoAsync(CancellationToken cancellationToken);
    Task<SessionSnapshot> ConnectAsync(ConnectRequest request, CancellationToken cancellationToken);
    Task<SessionSnapshot> DisconnectAsync(ServiceKind service, CancellationToken cancellationToken);
    Task<SessionSnapshot> EndSessionAsync(CancellationToken cancellationToken);

    /// <summary>Returns the runner for a connected service, or throws a NotConnected error explaining which connection is required.</summary>
    ICommandRunner Runner(ServiceKind service);

    /// <summary>Marks a service as expired after an authentication failure so the UI can offer reconnection.</summary>
    void ReportAuthenticationFailure(ServiceKind service, string message);
}

public sealed record Timestamped<T>(T Value, DateTimeOffset LoadedAt);

public sealed record ScanProgress(string ScanId, int Total, int Completed, int Failed, bool Running, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? CurrentRoom);

/// <summary>
/// Per-session cache. A new instance is created for every session, which is how RoomOps guarantees that cached configuration
/// never mixes between tenants or between demo and live mode.
/// </summary>
public sealed class TenantCache
{
    private readonly object _gate = new();
    private Timestamped<IReadOnlyList<RoomMailbox>>? _rooms;
    private Timestamped<IReadOnlyList<RoomList>>? _roomLists;
    private Timestamped<IReadOnlyList<Place>>? _places;

    public Timestamped<IReadOnlyList<RoomMailbox>>? Rooms { get { lock (_gate) return _rooms; } set { lock (_gate) _rooms = value; } }
    public Timestamped<IReadOnlyList<RoomList>>? RoomLists { get { lock (_gate) return _roomLists; } set { lock (_gate) _roomLists = value; } }
    public Timestamped<IReadOnlyList<Place>>? Places { get { lock (_gate) return _places; } set { lock (_gate) _places = value; } }
    public IReadOnlyList<AcceptedDomain>? AcceptedDomains { get; set; }
    public ScanProgress? LastScan { get; set; }

    public ConcurrentDictionary<string, Timestamped<BookingPolicy>> Booking { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Timestamped<WorkingHoursConfig>> WorkingHours { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, RecipientRef> Recipients { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Timestamped<TeamsRoomReadiness>> Readiness { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void InvalidateRoom(string roomId)
    {
        Booking.TryRemove(roomId, out _);
        WorkingHours.TryRemove(roomId, out _);
        Readiness.TryRemove(roomId, out _);
    }

    public void InvalidateInventory()
    {
        lock (_gate)
        {
            _rooms = null;
            _roomLists = null;
            _places = null;
        }
    }
}
