using System.Text.Json;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Templates;
using RoomOps.Domain.Validation;

namespace RoomOps.Application.Abstractions;

/// <summary>Pushes events (connection changes, execution progress) to the frontend.</summary>
public interface IEventSink
{
    void Publish(string topic, object payload);
}

public enum AuditOutcome
{
    Succeeded,
    Failed,
    NoChange,
    Skipped,
    Stale,
    PartiallyApplied,
    PendingProvisioning,
}

public sealed record AuditStep(string Description, string Script, string Outcome, long DurationMs, int Attempts, string? Error);

/// <summary>A command stored for resumption (e.g. after provisioning delays). Re-validated against the catalog when resumed.</summary>
public sealed record StoredCommand(string Name, IReadOnlyDictionary<string, JsonElement> Parameters, string Description);

/// <summary>
/// Application-local record of a change performed through RoomOps Studio. This is not the Microsoft 365 unified audit log and is
/// not tamper-proof; it is a local operational history stored in the user's profile.
/// </summary>
public sealed record AuditRecord
{
    public required string Id { get; init; }
    public required string ExecutionId { get; init; }
    public required string PlanId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Actor { get; init; }
    public required string TenantId { get; init; }
    public string? TenantName { get; init; }
    public required bool IsDemo { get; init; }
    public required string OperationType { get; init; }
    public required string OperationLabel { get; init; }
    public required string TargetKind { get; init; }
    public required string TargetId { get; init; }
    public required string TargetName { get; init; }
    public IReadOnlyList<PropertyChange> Changes { get; init; } = [];
    public IReadOnlyList<AuditStep> Steps { get; init; } = [];
    public required AuditOutcome Outcome { get; init; }
    public string? Error { get; init; }
    public VerificationResult? Verification { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
    public bool RevertAvailable { get; init; }
    /// <summary>Serialized compensating intent restoring the pre-change values. Re-planned (and re-diffed) when used.</summary>
    public JsonElement? RevertIntent { get; init; }
    public IReadOnlyList<StoredCommand> PendingCommands { get; init; } = [];
}

public sealed record AuditQuery(
    string? TenantId = null,
    string? TargetId = null,
    string? Search = null,
    AuditOutcome? Outcome = null,
    string? OperationType = null,
    DateTimeOffset? Since = null,
    int Limit = 200,
    int Offset = 0);

public interface IAuditStore
{
    Task AppendAsync(AuditRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken);
    Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken);
    Task<AuditRecord?> GetAsync(string id, CancellationToken cancellationToken);
    Task UpdateVerificationAsync(string id, VerificationResult verification, DateTimeOffset verifiedAt, CancellationToken cancellationToken);
    Task MarkResumedAsync(string id, CancellationToken cancellationToken);
}

public interface ITemplateStore
{
    Task<IReadOnlyList<ConfigurationTemplate>> ListAsync(CancellationToken cancellationToken);
    Task<ConfigurationTemplate?> GetAsync(string id, CancellationToken cancellationToken);
    Task SaveAsync(ConfigurationTemplate template, CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<TemplateAssignment>> ListAssignmentsAsync(string tenantId, CancellationToken cancellationToken);
    Task SetAssignmentAsync(TemplateAssignment assignment, CancellationToken cancellationToken);
    Task RemoveAssignmentAsync(string tenantId, string roomId, CancellationToken cancellationToken);
}

public enum ThemePreference
{
    System,
    Dark,
    Light,
}

public enum DensityPreference
{
    Comfortable,
    Compact,
}

public enum MotionPreference
{
    System,
    Reduced,
    Full,
}

public sealed record AppearanceSettings(ThemePreference Theme, DensityPreference Density, MotionPreference Motion, string Language);

public sealed record ConnectionSettings(string? PowerShellPath, Sessions.SignInMethod ExchangeSignIn, Sessions.SignInMethod GraphSignIn, string? DefaultAccount);

public sealed record ExecutionSettings(int PacingMilliseconds, int MaxThrottleRetries, int ProvisioningWaitSeconds);

public sealed record SavedView(string Id, string Name, string Page, JsonElement State);

public sealed record KnownTenant(string TenantId, string? DisplayName, string? PrimaryDomain, string? LastAccount, DateTimeOffset LastConnectedAt);

public sealed record AppSettings
{
    public AppearanceSettings Appearance { get; init; } = new(ThemePreference.System, DensityPreference.Comfortable, MotionPreference.System, "en");
    public ConnectionSettings Connection { get; init; } = new(null, Sessions.SignInMethod.Browser, Sessions.SignInMethod.Browser, null);
    public ExecutionSettings Execution { get; init; } = new(600, 4, 180);
    public ValidationBaseline Validation { get; init; } = new();
    public IReadOnlyList<SavedView> SavedViews { get; init; } = [];
    public IReadOnlyList<KnownTenant> KnownTenants { get; init; } = [];
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken);
}
