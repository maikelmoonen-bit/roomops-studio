using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Commands;
using RoomOps.Application.Planning;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Common;

namespace RoomOps.Application.Execution;

/// <summary>Holds previews until they are executed or expire. A plan can be executed at most once.</summary>
public sealed class PlanRegistry(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, ChangePlan> _plans = new(StringComparer.Ordinal);

    public void Add(ChangePlan plan)
    {
        foreach (var expired in _plans.Values.Where(p => p.ExpiresAt < time.GetUtcNow()).ToList())
        {
            _plans.TryRemove(expired.PlanId, out _);
        }
        _plans[plan.PlanId] = plan;
    }

    public ChangePlan? Get(string planId) => _plans.GetValueOrDefault(planId);

    public bool TryTake(string planId, out ChangePlan? plan) => _plans.TryRemove(planId, out plan);

    public void Clear() => _plans.Clear();
}

/// <summary>
/// Entry point for every mutation: create a preview (stages 1–7), then execute it after explicit confirmation (stages 8–12).
/// </summary>
public sealed class PlanService(ISessionManager sessions, ChangePlanner planner, PlanRegistry registry, ExecutionEngine engine, TimeProvider time)
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);

    public async Task<PlanPreview> CreateAsync(ChangeIntent intent, CancellationToken ct)
    {
        var session = sessions.Current;
        if (session.Mode == SessionMode.None || session.TenantId is null)
        {
            throw RoomOpsException.NotConnected(Text.L("Connect to a Microsoft 365 tenant or start demo mode before planning changes.", "Verbind met een Microsoft 365-tenant of start de demomodus voordat je wijzigingen plant."));
        }
        if (session.Mode == SessionMode.Live && !session.IsConnected(ServiceKind.Exchange))
        {
            throw RoomOpsException.NotConnected(Text.L("Exchange Online is not connected. Changes are planned against the live Exchange configuration.", "Exchange Online is niet verbonden. Wijzigingen worden gepland tegen de live configuratie van Exchange."));
        }

        var (title, targets, notes) = await planner.PlanAsync(intent, ct);
        if (sessions.Current.Epoch != session.Epoch)
        {
            throw RoomOpsException.TenantMismatch(Text.L("The tenant session changed while the preview was being prepared. Nothing was planned.", "De tenantsessie is gewijzigd terwijl het voorbeeld werd voorbereid. Er is niets gepland."));
        }

        var now = time.GetUtcNow();
        var plan = new ChangePlan
        {
            PlanId = Guid.NewGuid().ToString("N"),
            Title = title,
            TenantId = session.TenantId,
            TenantName = session.TenantName,
            SessionEpoch = session.Epoch,
            Mode = session.Mode,
            CreatedAt = now,
            ExpiresAt = now + PreviewLifetime,
            Intent = intent,
            Targets = targets,
            Notes = notes,
        };
        registry.Add(plan);
        return PlanPreviewBuilder.Build(plan);
    }

    /// <summary>Re-plans the original intent against current state and keeps only targets that did not complete. Previously applied changes are never re-sent blindly.</summary>
    public async Task<PlanPreview> CreateRetryAsync(string executionId, CancellationToken ct)
    {
        var info = engine.GetRetryInfo(executionId)
            ?? throw RoomOpsException.NotFound("execution.notFound", Text.L("The execution is unknown or still running.", "De uitvoering is onbekend of nog bezig."));
        if (info.Keys.Count == 0)
        {
            throw RoomOpsException.Validation("execution.nothingToRetry", Text.L("Every item in this execution completed; there is nothing to retry.", "Elk item in deze uitvoering is voltooid; er is niets opnieuw te proberen."));
        }
        var preview = await CreateAsync(info.Intent, ct);
        var plan = registry.Get(preview.PlanId)!;
        var filtered = new ChangePlan
        {
            PlanId = plan.PlanId,
            Title = Text.L($"Retry: {plan.Title}", $"Opnieuw: {plan.Title}"),
            TenantId = plan.TenantId,
            TenantName = plan.TenantName,
            SessionEpoch = plan.SessionEpoch,
            Mode = plan.Mode,
            CreatedAt = plan.CreatedAt,
            ExpiresAt = plan.ExpiresAt,
            Intent = plan.Intent,
            Targets = plan.Targets.Where(t => info.Keys.Contains($"{t.OperationType}|{t.TargetId}")).ToList(),
            Notes = [.. plan.Notes, Text.L("Only items that failed, went stale or were canceled are included. They were re-planned against the current configuration.", "Alleen items die zijn mislukt, verouderd of geannuleerd zijn opgenomen. Ze zijn opnieuw gepland tegen de huidige configuratie.")],
        };
        registry.Add(filtered);
        return PlanPreviewBuilder.Build(filtered);
    }

    public ExecutionState Execute(ExecuteRequest request)
    {
        var plan = registry.Get(request.PlanId)
            ?? throw new RoomOpsException(new DomainError("plan.notFound", Text.L("This preview is no longer available. Create a new preview.", "Dit voorbeeld is niet meer beschikbaar. Maak een nieuw voorbeeld."), ErrorKind.Stale));
        var session = sessions.Current;
        if (plan.ExpiresAt < time.GetUtcNow())
        {
            registry.TryTake(plan.PlanId, out _);
            throw new RoomOpsException(new DomainError("plan.expired", Text.L("This preview has expired. Create a new preview so the current configuration is compared again.", "Dit voorbeeld is verlopen. Maak een nieuw voorbeeld zodat de huidige configuratie opnieuw wordt vergeleken."), ErrorKind.Stale));
        }
        if (plan.SessionEpoch != session.Epoch || !string.Equals(plan.TenantId, session.TenantId, StringComparison.OrdinalIgnoreCase) || plan.Mode != session.Mode)
        {
            registry.TryTake(plan.PlanId, out _);
            throw RoomOpsException.TenantMismatch(Text.L("The active tenant session changed after this preview was created. Nothing was executed.", "De actieve tenantsessie is gewijzigd nadat dit voorbeeld is gemaakt. Er is niets uitgevoerd."));
        }
        if (!string.Equals(request.ConfirmTenantId, plan.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw RoomOpsException.TenantMismatch(Text.L("The confirmed tenant does not match the tenant this preview was created for. Nothing was executed.", "De bevestigde tenant komt niet overeen met de tenant waarvoor dit voorbeeld is gemaakt. Er is niets uitgevoerd."));
        }
        var preview = PlanPreviewBuilder.Build(plan);
        if (preview.RequiresRemovalAcknowledgement && !request.AcknowledgeRemovals)
        {
            throw RoomOpsException.Validation("plan.acknowledgementRequired", Text.L($"This plan removes {preview.Totals.Removals} membership(s) or permission(s). Confirm the removals to continue.", $"Dit plan verwijdert {preview.Totals.Removals} lidmaatschap(pen) of machtiging(en). Bevestig de verwijderingen om door te gaan."));
        }
        if (preview.Totals.WithChanges == 0 && plan.Targets.All(t => t.OnSucceeded is null))
        {
            throw RoomOpsException.Validation("plan.nothingToDo", Text.L("The preview contains no executable changes.", "Het voorbeeld bevat geen uitvoerbare wijzigingen."));
        }
        if (!registry.TryTake(plan.PlanId, out _))
        {
            throw new RoomOpsException(new DomainError("plan.alreadyExecuted", Text.L("This preview has already been executed.", "Dit voorbeeld is al uitgevoerd."), ErrorKind.Conflict));
        }
        return engine.Start(plan);
    }
}

/// <summary>
/// Executes plans target by target with pacing, bounded retries for throttling, provisioning waits, stale-state detection,
/// post-execution verification and audit logging. Targets are isolated: one failure never rolls back or blocks unrelated targets.
/// </summary>
public sealed class ExecutionEngine(ISessionManager sessions, IEventSink events, ISettingsStore settings, TimeProvider time, ILogger<ExecutionEngine> logger)
{
    private readonly ConcurrentDictionary<string, ExecutionJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>Delay hook so tests can run without real waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

    public ExecutionState Start(ChangePlan plan)
    {
        var job = new ExecutionJob(plan, time.GetUtcNow());
        _jobs[job.ExecutionId] = job;
        foreach (var stale in _jobs.Values.Where(j => !j.Running && j.FinishedAt < time.GetUtcNow().AddHours(-6)).ToList())
        {
            _jobs.TryRemove(stale.ExecutionId, out _);
        }
        job.Task = Task.Run(() => RunAsync(job));
        return job.Snapshot();
    }

    public ExecutionState? Get(string executionId) => _jobs.TryGetValue(executionId, out var job) ? job.Snapshot() : null;

    public IReadOnlyList<ExecutionState> List() => _jobs.Values.OrderByDescending(j => j.StartedAt).Select(j => j.Snapshot()).ToList();

    /// <summary>Intent and keys (operation type + target id) of targets that failed, went stale or were canceled.</summary>
    public (ChangeIntent Intent, IReadOnlySet<string> Keys)? GetRetryInfo(string executionId)
    {
        if (!_jobs.TryGetValue(executionId, out var job) || job.Running) return null;
        var snapshot = job.Snapshot();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < snapshot.Targets.Count; i++)
        {
            if (snapshot.Targets[i].Status is TargetExecutionStatus.Failed or TargetExecutionStatus.Stale or TargetExecutionStatus.Canceled or TargetExecutionStatus.VerificationMismatch)
            {
                keys.Add($"{job.Plan.Targets[i].OperationType}|{job.Plan.Targets[i].TargetId}");
            }
        }
        return (job.Plan.Intent, keys);
    }

    public bool Cancel(string executionId)
    {
        if (!_jobs.TryGetValue(executionId, out var job) || !job.Running) return false;
        job.CancelRequested = true;
        Publish(job, null);
        return true;
    }

    /// <summary>Waits for a job to finish (used by tests and by synchronous callers).</summary>
    public async Task<ExecutionState> WaitAsync(string executionId)
    {
        if (_jobs.TryGetValue(executionId, out var job) && job.Task is { } task) await task;
        return Get(executionId)!;
    }

    private async Task RunAsync(ExecutionJob job)
    {
        try
        {
            var pacing = TimeSpan.FromMilliseconds(Math.Clamp(settings.Current.Execution.PacingMilliseconds, 0, 10_000));
            for (var i = 0; i < job.Plan.Targets.Count; i++)
            {
                if (job.CancelRequested)
                {
                    for (var j = i; j < job.Plan.Targets.Count; j++)
                    {
                        job.Update(j, s => s with { Status = TargetExecutionStatus.Canceled, Stage = ExecutionStage.Completed, Message = Text.L("Canceled before it started.", "Geannuleerd voordat het begon.") });
                    }
                    break;
                }
                var target = job.Plan.Targets[i];
                if (i > 0 && target.HasChanges && !target.HasBlockingIssues && job.Plan.Mode == SessionMode.Live)
                {
                    await Delay(pacing, CancellationToken.None);
                }
                try
                {
                    await ExecuteTargetAsync(job, i, target);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected failure executing target {Target}", target.TargetName);
                    job.Update(i, s => s with { Status = TargetExecutionStatus.Failed, Stage = ExecutionStage.Completed, Message = Text.L($"Unexpected error: {ex.Message}", $"Onverwachte fout: {ex.Message}") });
                }
                Publish(job, i);
            }
        }
        finally
        {
            job.Finish(time.GetUtcNow());
            events.Publish("execution.completed", job.Snapshot());
        }
    }

    private void Publish(ExecutionJob job, int? index)
    {
        var snapshot = job.Snapshot();
        events.Publish("execution.progress", new ExecutionProgressEvent(snapshot.ExecutionId, index is { } i ? snapshot.Targets[i] : null, snapshot.Counts, snapshot.Running, snapshot.CancelRequested));
    }

    private async Task ExecuteTargetAsync(ExecutionJob job, int index, PlanTarget target)
    {
        var ct = CancellationToken.None;
        var plan = job.Plan;
        void Set(Func<TargetExecutionState, TargetExecutionState> update)
        {
            job.Update(index, update);
            Publish(job, index);
        }

        if (target.HasBlockingIssues)
        {
            Set(s => s with { Status = TargetExecutionStatus.Skipped, Stage = ExecutionStage.Completed, Message = target.Issues.First(x => x.Level == IssueLevel.Error).Message });
            return;
        }

        if (!target.HasChanges)
        {
            if (target.OnSucceeded is { } hook) await hook(ct);
            Set(s => s with { Status = TargetExecutionStatus.NoChange, Stage = ExecutionStage.Completed, Message = Text.L("No change required: the current configuration already matches.", "Geen wijziging nodig: de huidige configuratie komt al overeen.") });
            await AuditAsync(job, index, target, AuditOutcome.NoChange, [], null, null, []);
            return;
        }

        // Stage 2: revalidate tenant and target state immediately before writing.
        var session = sessions.Current;
        if (session.Epoch != plan.SessionEpoch || !string.Equals(session.TenantId, plan.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            Set(s => s with { Status = TargetExecutionStatus.Failed, Stage = ExecutionStage.Completed, Message = Text.L("The tenant session changed during execution. Remaining changes were not applied.", "De tenantsessie is tijdens de uitvoering gewijzigd. De resterende wijzigingen zijn niet toegepast.") });
            job.CancelRequested = true;
            return;
        }
        Set(s => s with { Status = TargetExecutionStatus.Running, Stage = ExecutionStage.Revalidating, Message = Text.L("Rereading current configuration", "Huidige configuratie opnieuw lezen") });
        if (target.Refingerprint is { } refingerprint)
        {
            string current;
            try
            {
                current = await refingerprint(ct);
            }
            catch (RoomOpsException ex)
            {
                Set(s => s with { Status = TargetExecutionStatus.Failed, Stage = ExecutionStage.Completed, Message = Text.L($"Could not reread the current configuration: {ex.Message}", $"De huidige configuratie kon niet opnieuw worden gelezen: {ex.Message}"), Retryable = IsRetryable(ex) });
                await AuditAsync(job, index, target, AuditOutcome.Failed, [], ex.Message, null, []);
                return;
            }
            if (!string.Equals(current, target.Fingerprint, StringComparison.Ordinal))
            {
                Set(s => s with { Status = TargetExecutionStatus.Stale, Stage = ExecutionStage.Completed, Message = Text.L("The configuration changed after the preview was created. Nothing was applied; create a new preview.", "De configuratie is gewijzigd nadat het voorbeeld is gemaakt. Er is niets toegepast; maak een nieuw voorbeeld."), Retryable = true });
                await AuditAsync(job, index, target, AuditOutcome.Stale, [], Text.L("Configuration changed since preview.", "Configuratie gewijzigd sinds het voorbeeld."), null, []);
                return;
            }
        }

        // Stages 9: execute operations.
        var steps = new List<StepState>();
        var budget = TimeSpan.FromSeconds(Math.Clamp(settings.Current.Execution.ProvisioningWaitSeconds, 0, 1800));
        var maxRetries = Math.Clamp(settings.Current.Execution.MaxThrottleRetries, 0, 10);
        for (var opIndex = 0; opIndex < target.Operations.Count; opIndex++)
        {
            var op = target.Operations[opIndex];
            var script = op.Command.ToScript();
            var attempts = 0;
            var waited = TimeSpan.Zero;
            var sw = Stopwatch.StartNew();
            Set(s => s with { Stage = ExecutionStage.Applying, Message = op.Description, Steps = [.. steps, new StepState(op.Description, script, "Running", 1, 0, null)] });
            while (true)
            {
                attempts++;
                try
                {
                    await sessions.Runner(op.Command.Service).RunAsync(op.Command, ct);
                    steps.Add(new StepState(op.Description, script, "Succeeded", attempts, sw.ElapsedMilliseconds, null));
                    break;
                }
                catch (CommandFailedException ex) when (ex.Kind == ErrorKind.Conflict && op.ConflictMeansDone)
                {
                    steps.Add(new StepState(op.Description, script, "AlreadyInPlace", attempts, sw.ElapsedMilliseconds, null));
                    break;
                }
                catch (CommandFailedException ex) when (ex.Kind == ErrorKind.NotFound && op.AwaitsProvisioning && waited < budget)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(5 * attempts, 30));
                    waited += delay;
                    Set(s => s with { Stage = ExecutionStage.WaitingForProvisioning, Message = Text.L($"Waiting for the object to be provisioned ({(int)waited.TotalSeconds}s of up to {(int)budget.TotalSeconds}s)", $"Wachten op de inrichting van het object ({(int)waited.TotalSeconds}s van maximaal {(int)budget.TotalSeconds}s)") });
                    await Delay(delay, ct);
                }
                catch (CommandFailedException ex) when (ex.IsRetryable && attempts <= maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempts));
                    Set(s => s with { Message = ex.Kind == ErrorKind.Throttled ? Text.L($"Throttled by the service; retrying in {delay.TotalSeconds:0}s (attempt {attempts + 1})", $"Afgeremd door de service; nieuwe poging over {delay.TotalSeconds:0}s (poging {attempts + 1})") : Text.L($"Temporary failure; retrying in {delay.TotalSeconds:0}s", $"Tijdelijke fout; nieuwe poging over {delay.TotalSeconds:0}s") });
                    await Delay(delay, ct);
                }
                catch (RoomOpsException ex)
                {
                    var kind = ex.Error.Kind;
                    if (kind == ErrorKind.NotFound && op.DeferrableUntilProvisioned)
                    {
                        var pending = target.Operations.Skip(opIndex).Select(CommandStorage.ToStored).ToList();
                        steps.Add(new StepState(op.Description, script, "Deferred", attempts, sw.ElapsedMilliseconds, Text.L("Object not provisioned yet", "Object nog niet ingericht")));
                        steps.AddRange(target.Operations.Skip(opIndex + 1).Select(o => new StepState(o.Description, o.Command.ToScript(), "Deferred", 0, 0, null)));
                        Set(s => s with
                        {
                            Status = TargetExecutionStatus.PendingProvisioning,
                            Stage = ExecutionStage.Completed,
                            Steps = steps.ToList(),
                            Message = Text.L($"Accepted by Microsoft 365. {pending.Count} remaining step(s) are waiting for provisioning and were saved; resume them from Change History.", $"Geaccepteerd door Microsoft 365. {pending.Count} resterende stap(pen) wachten op inrichting en zijn bewaard; hervat ze vanuit de wijzigingsgeschiedenis."),
                        });
                        target.Invalidate?.Invoke(sessions.Cache);
                        await AuditAsync(job, index, target, AuditOutcome.PendingProvisioning, steps, null, null, pending);
                        return;
                    }
                    if (kind == ErrorKind.Authentication)
                    {
                        sessions.ReportAuthenticationFailure(op.Command.Service, ex.Message);
                    }
                    steps.Add(new StepState(op.Description, script, "Failed", attempts, sw.ElapsedMilliseconds, ex.Message));
                    steps.AddRange(target.Operations.Skip(opIndex + 1).Select(o => new StepState(o.Description, o.Command.ToScript(), "NotRun", 0, 0, null)));
                    var partial = opIndex > 0;
                    var message = partial
                        ? Text.L($"{opIndex} of {target.Operations.Count} step(s) were applied before a failure: {ex.Message}", $"{opIndex} van {target.Operations.Count} stap(pen) waren toegepast vóór een fout: {ex.Message}")
                        : ex.Message;
                    Set(s => s with { Status = TargetExecutionStatus.Failed, Stage = ExecutionStage.Completed, Steps = steps.ToList(), Message = message, Retryable = IsRetryable(ex) });
                    if (partial) target.Invalidate?.Invoke(sessions.Cache);
                    await AuditAsync(job, index, target, partial ? AuditOutcome.PartiallyApplied : AuditOutcome.Failed, steps, ex.Message, null, []);
                    return;
                }
            }
            Set(s => s with { Steps = steps.ToList() });
        }

        // Stages 10–11: reread and compare.
        target.Invalidate?.Invoke(sessions.Cache);
        Set(s => s with { Stage = ExecutionStage.Verifying, Message = Text.L("Reading back the resulting configuration", "De resulterende configuratie teruglezen") });
        VerificationResult verification;
        try
        {
            verification = target.Verify is { } verify
                ? await verify(ct)
                : VerificationResult.NotApplicable(Text.L("No automated verification is defined for this change.", "Voor deze wijziging is geen automatische verificatie gedefinieerd."));
        }
        catch (RoomOpsException ex)
        {
            verification = new VerificationResult(VerificationOutcome.NotVerified, Text.L($"The change was applied, but the result could not be read back: {ex.Message}", $"De wijziging is toegepast, maar het resultaat kon niet worden teruggelezen: {ex.Message}"), []);
        }

        var status = verification.Outcome switch
        {
            VerificationOutcome.Verified or VerificationOutcome.NotApplicable => TargetExecutionStatus.Succeeded,
            VerificationOutcome.PendingPropagation or VerificationOutcome.NotVerified => TargetExecutionStatus.PendingPropagation,
            _ => TargetExecutionStatus.VerificationMismatch,
        };
        if (target.OnSucceeded is { } onSucceeded) await onSucceeded(ct);
        Set(s => s with { Status = status, Stage = ExecutionStage.Completed, Steps = steps.ToList(), Verification = verification, Message = verification.Message });

        // Stage 12: audit.
        await AuditAsync(job, index, target, AuditOutcome.Succeeded, steps, null, verification, []);
    }

    private static bool IsRetryable(RoomOpsException ex) => ex.Error.Kind is ErrorKind.Throttled or ErrorKind.Transient or ErrorKind.Authentication or ErrorKind.Stale;

    private async Task AuditAsync(ExecutionJob job, int index, PlanTarget target, AuditOutcome outcome, IReadOnlyList<StepState> steps, string? error, VerificationResult? verification, IReadOnlyList<StoredCommand> pending)
    {
        var session = sessions.Current;
        var revertable = target.RevertIntent is not null && outcome is AuditOutcome.Succeeded or AuditOutcome.PartiallyApplied;
        var record = new AuditRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ExecutionId = job.ExecutionId,
            PlanId = job.Plan.PlanId,
            Timestamp = time.GetUtcNow(),
            Actor = session.AdminAccount ?? (job.Plan.Mode == SessionMode.Demo ? "demo.admin@contoso-demo.example" : "unknown"),
            TenantId = job.Plan.TenantId,
            TenantName = job.Plan.TenantName,
            IsDemo = job.Plan.Mode == SessionMode.Demo,
            OperationType = target.OperationType,
            OperationLabel = target.OperationLabel,
            TargetKind = target.Kind.ToString(),
            TargetId = target.TargetId,
            TargetName = target.TargetName,
            Changes = target.Changes,
            Steps = steps.Select(s => new AuditStep(s.Description, s.Script, s.Status, s.DurationMs, s.Attempts, s.Error)).ToList(),
            Outcome = outcome,
            Error = error,
            Verification = verification,
            VerifiedAt = verification is null ? null : time.GetUtcNow(),
            RevertAvailable = revertable,
            RevertIntent = revertable ? JsonSerializer.SerializeToElement<ChangeIntent>(target.RevertIntent!, RoomOpsJson.Options) : null,
            PendingCommands = pending,
        };
        try
        {
            await sessions.Audit.AppendAsync(record, CancellationToken.None);
            job.Update(index, s => s with { AuditId = record.Id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit record for {Target}", target.TargetName);
        }
    }

    private sealed class ExecutionJob
    {
        private readonly object _gate = new();
        private readonly TargetExecutionState[] _targets;

        public ExecutionJob(ChangePlan plan, DateTimeOffset startedAt)
        {
            Plan = plan;
            StartedAt = startedAt;
            ExecutionId = Guid.NewGuid().ToString("N");
            _targets = plan.Targets.Select(t => new TargetExecutionState(t.TargetId, t.TargetName, t.OperationLabel, TargetExecutionStatus.Pending, ExecutionStage.Pending,
                t.Operations.Select(o => new StepState(o.Description, o.Command.ToScript(), "Pending", 0, 0, null)).ToList(), null, false, null, null)).ToArray();
        }

        public ChangePlan Plan { get; }
        public string ExecutionId { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset? FinishedAt { get; private set; }
        public bool Running { get; private set; } = true;
        public volatile bool CancelRequested;
        public Task? Task { get; set; }

        public void Update(int index, Func<TargetExecutionState, TargetExecutionState> update)
        {
            lock (_gate) _targets[index] = update(_targets[index]);
        }

        public void Finish(DateTimeOffset at)
        {
            lock (_gate)
            {
                FinishedAt = at;
                Running = false;
            }
        }

        public ExecutionState Snapshot()
        {
            lock (_gate)
            {
                var targets = _targets.ToList();
                var completed = targets.Count(t => t.Stage == ExecutionStage.Completed);
                var counts = new ExecutionCounts(
                    targets.Count,
                    completed,
                    targets.Count(t => t.Status is TargetExecutionStatus.Succeeded or TargetExecutionStatus.PendingPropagation or TargetExecutionStatus.PendingProvisioning),
                    targets.Count(t => t.Status is TargetExecutionStatus.Failed or TargetExecutionStatus.VerificationMismatch or TargetExecutionStatus.Stale),
                    targets.Count(t => t.Status is TargetExecutionStatus.Skipped or TargetExecutionStatus.Canceled),
                    targets.Count(t => t.Status is TargetExecutionStatus.Pending or TargetExecutionStatus.Running),
                    targets.Count(t => t.Retryable),
                    targets.Count(t => t.Status == TargetExecutionStatus.NoChange));
                return new ExecutionState(ExecutionId, Plan.PlanId, Plan.Title, Plan.TenantId, Plan.TenantName, Plan.Mode == SessionMode.Demo, StartedAt, FinishedAt, Running, CancelRequested, targets, counts);
            }
        }
    }
}

public sealed record ExecutionProgressEvent(string ExecutionId, TargetExecutionState? Target, ExecutionCounts Counts, bool Running, bool CancelRequested);
