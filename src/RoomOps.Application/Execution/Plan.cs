using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Commands;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Common;

namespace RoomOps.Application.Execution;

/// <summary>One command in a plan. Planned operations are immutable data: what is previewed is exactly what is executed.</summary>
public sealed class PlannedOperation
{
    public required PsCommand Command { get; init; }
    public required string Description { get; init; }
    /// <summary>Retry NotFound errors for a while: the target is being provisioned by an earlier operation (eventual consistency).</summary>
    public bool AwaitsProvisioning { get; init; }
    /// <summary>When this operation keeps failing with NotFound beyond the provisioning budget, store it for later resumption instead of failing.</summary>
    public bool DeferrableUntilProvisioned { get; init; }
    /// <summary>Conflict errors mean the desired state already exists (e.g. "already a member"); treat as success.</summary>
    public bool ConflictMeansDone { get; init; }
}

public enum TargetKind
{
    Room,
    RoomList,
    Place,
    Tenant,
}

/// <summary>A per-object unit of a plan, executed and audited independently so partial failures stay isolated.</summary>
public sealed class PlanTarget
{
    public required string TargetId { get; init; }
    public required TargetKind Kind { get; init; }
    public required string TargetName { get; init; }
    public required string OperationType { get; init; }
    public required string OperationLabel { get; init; }
    public IReadOnlyList<PropertyChange> Changes { get; init; } = [];
    public IReadOnlyList<PlannedOperation> Operations { get; init; } = [];
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Hash of the relevant current state at planning time. Recomputed before execution to detect stale previews.</summary>
    public string Fingerprint { get; init; } = string.Empty;
    public Func<CancellationToken, Task<string>>? Refingerprint { get; init; }
    public Func<CancellationToken, Task<VerificationResult>>? Verify { get; init; }
    /// <summary>Intent that restores the pre-change values, when a safe compensating action exists.</summary>
    public ChangeIntent? RevertIntent { get; init; }
    public bool HasBlockingIssues => Issues.Any(i => i.Level == IssueLevel.Error);
    public bool HasChanges => Operations.Count > 0;
    /// <summary>Removes cache entries affected by this target after execution.</summary>
    public Action<TenantCache>? Invalidate { get; init; }
    /// <summary>Local follow-up after a successful (or no-change) execution, e.g. recording a template assignment.</summary>
    public Func<CancellationToken, Task>? OnSucceeded { get; set; }
}

public sealed class ChangePlan
{
    public required string PlanId { get; init; }
    public required string Title { get; init; }
    public required string TenantId { get; init; }
    public string? TenantName { get; init; }
    public required long SessionEpoch { get; init; }
    public required SessionMode Mode { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required ChangeIntent Intent { get; init; }
    public required IReadOnlyList<PlanTarget> Targets { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string? AuditIdBeingResumed { get; init; }
}

public static class Fingerprint
{
    public static string Of(object? state)
    {
        var json = JsonSerializer.Serialize(state, FingerprintOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..24];
    }

    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

// ---------------------------------------------------------------- DTOs sent to the frontend

public sealed record OperationPreview(ServiceKind Service, string Command, string Script, CommandEffect Effect, string Description, bool AwaitsProvisioning);

public enum TargetPlanStatus
{
    Changes,
    NoChange,
    Blocked,
}

public sealed record TargetPreview(
    string TargetId,
    TargetKind Kind,
    string TargetName,
    string OperationLabel,
    TargetPlanStatus Status,
    IReadOnlyList<PropertyChange> Changes,
    IReadOnlyList<OperationPreview> Operations,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<string> Notes,
    bool RevertAvailable);

public sealed record PlanTotals(int Targets, int WithChanges, int NoChange, int Blocked, int Operations, int Removals);

public sealed record PlanPreview(
    string PlanId,
    string Title,
    string TenantId,
    string? TenantName,
    bool IsDemo,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<TargetPreview> Targets,
    PlanTotals Totals,
    bool RequiresRemovalAcknowledgement,
    string Script,
    IReadOnlyList<string> ScriptNotes,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ServiceKind> ServicesUsed);

public static class PlanPreviewBuilder
{
    public static PlanPreview Build(ChangePlan plan)
    {
        var targets = plan.Targets.Select(t => new TargetPreview(
            t.TargetId, t.Kind, t.TargetName, t.OperationLabel,
            t.HasBlockingIssues ? TargetPlanStatus.Blocked : t.HasChanges ? TargetPlanStatus.Changes : TargetPlanStatus.NoChange,
            t.Changes,
            t.Operations.Select(o => new OperationPreview(o.Command.Service, o.Command.Name, o.Command.ToScript(), o.Command.Effect, o.Description, o.AwaitsProvisioning)).ToList(),
            t.Issues,
            t.Notes,
            t.RevertIntent is not null)).ToList();

        var executable = plan.Targets.Where(t => !t.HasBlockingIssues && t.HasChanges).ToList();
        var removals = executable.SelectMany(t => t.Operations).Count(o => o.Command.Effect == CommandEffect.Removal);
        var services = executable.SelectMany(t => t.Operations).Select(o => o.Command.Service).Distinct().Order().ToList();

        var script = new StringBuilder();
        var notes = new List<string>();
        script.AppendLine($"# RoomOps Studio — {plan.Title}");
        script.AppendLine($"# Tenant: {plan.TenantName ?? plan.TenantId} ({plan.TenantId})");
        script.AppendLine($"# Generated {plan.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC. Review before running; RoomOps verifies results itself when it executes the plan.");
        if (plan.Mode == SessionMode.Demo)
        {
            script.AppendLine("# DEMO MODE — these commands were planned against synthetic demo data and are never sent to Microsoft 365.");
        }
        if (services.Contains(ServiceKind.Exchange)) script.AppendLine("# Requires: Connect-ExchangeOnline (ExchangeOnlineManagement module)");
        if (services.Contains(ServiceKind.Places)) script.AppendLine("# Requires: Connect-MicrosoftPlaces (MicrosoftPlaces module 2.0.0 or later)");
        foreach (var target in executable)
        {
            script.AppendLine();
            script.AppendLine($"# {target.OperationLabel}: {target.TargetName}");
            foreach (var op in target.Operations)
            {
                if (op.AwaitsProvisioning)
                {
                    script.AppendLine("# (runs after the object above has been provisioned; RoomOps retries automatically while it propagates)");
                }
                script.AppendLine(op.Command.ToScript());
            }
        }
        if (executable.Any(t => t.Operations.Any(o => o.AwaitsProvisioning)))
        {
            notes.Add(Text.L("Some commands depend on objects created earlier in the script. Microsoft 365 provisions these asynchronously; when running manually, wait until each object exists before continuing.", "Sommige opdrachten zijn afhankelijk van objecten die eerder in het script worden gemaakt. Microsoft 365 richt deze asynchroon in; wacht bij handmatig uitvoeren tot elk object bestaat voordat je verdergaat."));
        }
        if (removals > 0)
        {
            notes.Add(Text.L("Removal commands include -Confirm:$false because RoomOps collects the confirmation in its own review dialog.", "Verwijderopdrachten bevatten -Confirm:$false, omdat RoomOps de bevestiging in een eigen controlevenster vraagt."));
        }
        notes.Add(Text.L("The script contains no credentials or tokens. Run it in a session you have connected yourself.", "Het script bevat geen aanmeldgegevens of tokens. Voer het uit in een sessie die je zelf hebt verbonden."));

        return new PlanPreview(
            plan.PlanId, plan.Title, plan.TenantId, plan.TenantName, plan.Mode == SessionMode.Demo, plan.CreatedAt, plan.ExpiresAt, targets,
            new PlanTotals(targets.Count, targets.Count(t => t.Status == TargetPlanStatus.Changes), targets.Count(t => t.Status == TargetPlanStatus.NoChange),
                targets.Count(t => t.Status == TargetPlanStatus.Blocked), executable.Sum(t => t.Operations.Count), removals),
            removals > 0, script.ToString(), notes, plan.Notes, services);
    }
}

// ---------------------------------------------------------------- Execution state

public enum ExecutionStage
{
    Pending,
    Revalidating,
    Applying,
    WaitingForProvisioning,
    Verifying,
    Completed,
}

public enum TargetExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    PendingPropagation,
    PendingProvisioning,
    VerificationMismatch,
    NoChange,
    Skipped,
    Stale,
    Failed,
    Canceled,
}

public sealed record StepState(string Description, string Script, string Status, int Attempts, long DurationMs, string? Error);

public sealed record TargetExecutionState(
    string TargetId,
    string TargetName,
    string OperationLabel,
    TargetExecutionStatus Status,
    ExecutionStage Stage,
    IReadOnlyList<StepState> Steps,
    string? Message,
    bool Retryable,
    VerificationResult? Verification,
    string? AuditId);

public sealed record ExecutionCounts(int Total, int Completed, int Succeeded, int Failed, int Skipped, int Pending, int Retryable, int NoChange);

public sealed record ExecutionState(
    string ExecutionId,
    string PlanId,
    string Title,
    string TenantId,
    string? TenantName,
    bool IsDemo,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool Running,
    bool CancelRequested,
    IReadOnlyList<TargetExecutionState> Targets,
    ExecutionCounts Counts);

public sealed record ExecuteRequest(string PlanId, string ConfirmTenantId, bool AcknowledgeRemovals);
