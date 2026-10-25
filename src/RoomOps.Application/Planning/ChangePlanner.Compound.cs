using System.Text.Json;
using RoomOps.Application.Execution;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.Templates;

namespace RoomOps.Application.Planning;

public sealed partial class ChangePlanner
{
    // ------------------------------------------------------------------------------------------------ Templates

    internal async Task<(string, IReadOnlyList<PlanTarget>, IReadOnlyList<string>)> PlanTemplateAsync(ApplyTemplateIntent intent, CancellationToken ct)
    {
        var template = await templates.GetAsync(intent.TemplateId, ct)
            ?? throw RoomOpsException.NotFound("template.notFound", Text.L("The configuration template no longer exists.", "Het configuratiesjabloon bestaat niet meer."));
        var templateIssues = TemplateRules.Validate(template).Where(x => x.Level == IssueLevel.Error).ToList();
        var excluded = intent.ExcludedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tenantId = sessions.Current.TenantId ?? string.Empty;
        var label = Text.L($"Apply template '{template.Name}'", $"Sjabloon '{template.Name}' toepassen");
        var targets = new List<PlanTarget>();

        foreach (var roomId in intent.RoomIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var room = await inventory.ResolveRoomAsync(roomId, ct);
            var roomTargets = new List<PlanTarget>();
            if (templateIssues.Count > 0)
            {
                roomTargets.Add(Blocked(room, "template.apply", label, Text.L($"The template is invalid: {templateIssues[0].Message}", $"Het sjabloon is ongeldig: {templateIssues[0].Message}")));
            }
            else
            {
                if (template.BookingFields.Keys.Any(k => !excluded.Contains(k)))
                {
                    roomTargets.Add(await PlanBookingAsync(room, current => BookingPolicyFields.ApplyPatch(current, template.BookingFields, excluded), label, ct));
                }
                if (template.WorkingHours is { } wh && !excluded.Contains("WorkingHours"))
                {
                    roomTargets.Add(await PlanWorkingHoursAsync(room, wh, ct));
                }
            }
            if (intent.AssignTemplate && roomTargets.Count > 0)
            {
                var exclusions = intent.ExcludedFields.ToList();
                roomTargets[0].OnSucceeded = c => templates.SetAssignmentAsync(new TemplateAssignment(tenantId, room.Id, template.Id, exclusions, Now), c);
            }
            targets.AddRange(roomTargets);
        }

        var notes = new List<string>
        {
            Text.L($"Only the {template.BookingFields.Count} booking setting(s){(template.WorkingHours is not null ? " and working hours" : string.Empty)} defined in the template are compared and applied. Other room settings are not touched.", $"Alleen de {template.BookingFields.Count} boekingsinstelling(en){(template.WorkingHours is not null ? " en werktijden" : string.Empty)} uit het sjabloon worden vergeleken en toegepast. Andere instellingen van de ruimte blijven ongemoeid."),
        };
        if (excluded.Count > 0) notes.Add(Text.L($"Preserved exceptions (not applied): {string.Join(", ", excluded)}.", $"Behouden uitzonderingen (niet toegepast): {string.Join(", ", excluded)}."));
        return (intent.RoomIds.Count == 1 ? label : Text.L($"{label} to {intent.RoomIds.Count} rooms", $"{label} op {intent.RoomIds.Count} ruimtes"), targets, notes);
    }

    // ------------------------------------------------------------------------------------------------ Bulk import

    internal async Task<(string, IReadOnlyList<PlanTarget>, IReadOnlyList<string>)> PlanBulkAsync(BulkImportIntent intent, CancellationToken ct)
    {
        var targets = new List<PlanTarget>();
        var seenRooms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var listAdditions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var lists = await inventory.GetRoomListsAsync(false, ct);

        foreach (var row in intent.Rows.OrderBy(r => r.RowNumber))
        {
            var room = await inventory.TryResolveRoomAsync(row.RoomIdentity, ct);
            var rowLabel = Text.L($"Row {row.RowNumber}", $"Rij {row.RowNumber}");
            if (room is null)
            {
                targets.Add(BlockedTarget($"row:{row.RowNumber}", TargetKind.Room, $"{rowLabel} · {row.RoomIdentity}", "bulk.row", Text.L("Bulk update", "Bulkwijziging"),
                    Text.L($"No room mailbox matches '{row.RoomIdentity}' (searched by address, alias, id and unique display name).", $"Geen ruimtepostvak komt overeen met '{row.RoomIdentity}' (gezocht op adres, alias, id en unieke weergavenaam).")));
                continue;
            }
            if (seenRooms.TryGetValue(room.Id, out var firstRow))
            {
                targets.Add(BlockedTarget($"row:{row.RowNumber}", TargetKind.Room, $"{rowLabel} · {room.DisplayName}", "bulk.row", Text.L("Bulk update", "Bulkwijziging"),
                    Text.L($"Duplicate: this room is already updated by row {firstRow}. Combine the rows to avoid conflicting changes.", $"Dubbel: deze ruimte wordt al bijgewerkt door rij {firstRow}. Voeg de rijen samen om conflicterende wijzigingen te voorkomen.")));
                continue;
            }
            seenRooms[room.Id] = row.RowNumber;
            var anything = false;
            if (row.BookingFields.Count > 0)
            {
                targets.Add(Rename(await PlanBookingAsync(room, current => BookingPolicyFields.ApplyPatch(current, row.BookingFields), Text.L("Bulk booking update", "Bulkwijziging van boekingsbeleid"), ct), rowLabel));
                anything = true;
            }
            if (row.WorkingHours is { } wh)
            {
                targets.Add(Rename(await PlanWorkingHoursAsync(room, wh, ct), rowLabel));
                anything = true;
            }
            if (row.Capacity is { } cap)
            {
                targets.Add(Rename(await PlanMailboxAsync(room, new UpdateMailboxIntent(room.Id, null, cap), ct), rowLabel));
                anything = true;
            }
            if (row.PlacesFields.Count > 0)
            {
                targets.Add(Rename(await PlanPlacesRoomAsync(room, row.PlacesFields, ct), rowLabel));
                anything = true;
            }
            if (row.AddToRoomList is { Length: > 0 } listRef)
            {
                var list = lists.FirstOrDefault(l => l.PrimarySmtpAddress.Equals(listRef, StringComparison.OrdinalIgnoreCase)
                    || l.DisplayName.Equals(listRef, StringComparison.OrdinalIgnoreCase) || l.Id.Equals(listRef, StringComparison.OrdinalIgnoreCase));
                if (list is null)
                {
                    targets.Add(BlockedTarget($"row:{row.RowNumber}:list", TargetKind.RoomList, $"{rowLabel} · {listRef}", "bulk.row", Text.L("Bulk update", "Bulkwijziging"), Text.L($"Room list '{listRef}' was not found.", $"Ruimtelijst '{listRef}' is niet gevonden.")));
                }
                else
                {
                    if (!listAdditions.TryGetValue(list.Id, out var ids)) listAdditions[list.Id] = ids = [];
                    ids.Add(room.Id);
                }
                anything = true;
            }
            if (!anything)
            {
                targets.Add(BlockedTarget($"row:{row.RowNumber}", TargetKind.Room, $"{rowLabel} · {room.DisplayName}", "bulk.row", Text.L("Bulk update", "Bulkwijziging"), Text.L("The row contains no settings to apply.", "De rij bevat geen instellingen om toe te passen.")));
            }
        }

        // One membership change per room list keeps stale detection meaningful when many rows target the same list.
        foreach (var (listId, roomIds) in listAdditions)
        {
            targets.Add(await PlanUpdateRoomListAsync(new UpdateRoomListIntent(listId, null, roomIds, []), ct));
        }
        return (Text.L($"Bulk update ({intent.Rows.Count} rows)", $"Bulkwijziging ({intent.Rows.Count} rijen)"), targets, [Text.L("Each row is planned against the room's current configuration; values that already match are reported as 'No change required'.", "Elke rij wordt gepland tegen de huidige configuratie van de ruimte; waarden die al overeenkomen, worden gemeld als 'Geen wijziging nodig'.")]);
    }

    private static PlanTarget Rename(PlanTarget target, string prefix) => new()
    {
        TargetId = target.TargetId,
        Kind = target.Kind,
        TargetName = $"{prefix} · {target.TargetName}",
        OperationType = target.OperationType,
        OperationLabel = target.OperationLabel,
        Changes = target.Changes,
        Operations = target.Operations,
        Issues = target.Issues,
        Notes = target.Notes,
        Fingerprint = target.Fingerprint,
        Refingerprint = target.Refingerprint,
        Verify = target.Verify,
        RevertIntent = target.RevertIntent,
        Invalidate = target.Invalidate,
        OnSucceeded = target.OnSucceeded,
    };

    // ------------------------------------------------------------------------------------------------ Remediation

    internal async Task<(string, IReadOnlyList<PlanTarget>, IReadOnlyList<string>)> PlanRemediationAsync(RemediateIntent intent, CancellationToken ct)
    {
        var findings = (await validation.GetFindingsAsync(false, ct)).ToDictionary(f => f.Id, StringComparer.Ordinal);
        var selected = intent.FindingIds.Select(id => findings.GetValueOrDefault(id)).Where(f => f is not null).Cast<Domain.Validation.Finding>().ToList();
        var notes = new List<string>();
        var missing = intent.FindingIds.Count - selected.Count;
        if (missing > 0) notes.Add(Text.L($"{missing} finding(s) no longer apply and were skipped.", $"{missing} bevinding(en) zijn niet meer van toepassing en zijn overgeslagen."));
        var unsupported = selected.Where(f => f.RemediationId is null).ToList();
        if (unsupported.Count > 0) notes.Add(Text.L($"{unsupported.Count} finding(s) have no automatic remediation and need a manual decision: {string.Join(", ", unsupported.Select(f => f.Title).Distinct())}.", $"{unsupported.Count} bevinding(en) hebben geen automatisch herstel en vragen een handmatige beslissing: {string.Join(", ", unsupported.Select(f => f.Title).Distinct())}."));

        var targets = new List<PlanTarget>();
        var baseline = settings.Current.Validation.Privacy;
        foreach (var group in selected.Where(f => f.RemediationId is not null).GroupBy(f => f.RemediationId!))
        {
            var roomIds = group.Where(f => f.TargetKind == "Room").Select(f => f.TargetId).Distinct().ToList();
            switch (group.Key)
            {
                case "booking.setAutoAccept":
                    targets.AddRange((await PlanAsync(new PatchBookingPolicyIntent(roomIds, Patch(("AutomateProcessing", "AutoAccept"))), ct)).Targets);
                    break;
                case "booking.enableForwarding":
                    targets.AddRange((await PlanAsync(new PatchBookingPolicyIntent(roomIds, Patch(("ForwardRequestsToDelegates", true))), ct)).Targets);
                    break;
                case "booking.disableEmptyResponse":
                    targets.AddRange((await PlanAsync(new PatchBookingPolicyIntent(roomIds, Patch(("AddAdditionalResponse", false))), ct)).Targets);
                    break;
                case "booking.applyPrivacyBaseline":
                {
                    var fields = new List<(string, object)>();
                    if (baseline.DeleteSubject is { } a) fields.Add(("DeleteSubject", a));
                    if (baseline.AddOrganizerToSubject is { } b) fields.Add(("AddOrganizerToSubject", b));
                    if (baseline.DeleteComments is { } c) fields.Add(("DeleteComments", c));
                    if (baseline.RemovePrivateProperty is { } d) fields.Add(("RemovePrivateProperty", d));
                    targets.AddRange((await PlanAsync(new PatchBookingPolicyIntent(roomIds, Patch([.. fields])), ct)).Targets);
                    break;
                }
                case "delegates.removeUnresolved":
                    foreach (var roomId in roomIds)
                    {
                        var room = await inventory.ResolveRoomAsync(roomId, ct);
                        static IReadOnlyList<RecipientRef> Keep(IReadOnlyList<RecipientRef> list) => list.Where(r => r.Resolution == RecipientResolution.Resolved).ToList();
                        targets.Add(await PlanBookingAsync(room, p => p with
                        {
                            ResourceDelegates = Keep(p.ResourceDelegates),
                            BookInPolicy = Keep(p.BookInPolicy),
                            RequestInPolicy = Keep(p.RequestInPolicy),
                            RequestOutOfPolicy = Keep(p.RequestOutOfPolicy),
                        }, Text.L("Remove unresolved recipients", "Niet-omgezette ontvangers verwijderen"), ct));
                    }
                    break;
                case "template.reapply":
                {
                    var assignments = await templates.ListAssignmentsAsync(sessions.Current.TenantId ?? string.Empty, ct);
                    foreach (var roomId in roomIds)
                    {
                        var assignment = assignments.FirstOrDefault(a => a.RoomId.Equals(roomId, StringComparison.OrdinalIgnoreCase));
                        if (assignment is null) continue;
                        targets.AddRange((await PlanTemplateAsync(new ApplyTemplateIntent(assignment.TemplateId, [roomId], assignment.ExcludedFields, false), ct)).Item2);
                    }
                    break;
                }
                case "roomList.removeMember":
                    foreach (var byList in group.GroupBy(f => f.TargetId))
                    {
                        var members = byList.Select(f => f.Evidence.GetValueOrDefault("Member")).Where(m => m is not null).Cast<string>().ToList();
                        targets.Add(await PlanUpdateRoomListAsync(new UpdateRoomListIntent(byList.Key, null, [], members), ct));
                    }
                    break;
                default:
                    notes.Add(Text.L($"Remediation '{group.Key}' is not available.", $"Herstel '{group.Key}' is niet beschikbaar."));
                    break;
            }
        }
        return (Text.L($"Remediate {selected.Count} finding(s)", $"{selected.Count} bevinding(en) herstellen"), targets, notes);
    }

    private static Dictionary<string, JsonElement> Patch(params (string Key, object Value)[] values) =>
        values.ToDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value));

    // ------------------------------------------------------------------------------------------------ Revert and resume

    internal async Task<(string, IReadOnlyList<PlanTarget>, IReadOnlyList<string>)> PlanRevertAsync(RevertIntent intent, CancellationToken ct)
    {
        var record = await sessions.Audit.GetAsync(intent.AuditId, ct)
            ?? throw RoomOpsException.NotFound("history.notFound", Text.L("The history entry was not found.", "Het geschiedenisitem is niet gevonden."));
        if (!string.Equals(record.TenantId, sessions.Current.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw RoomOpsException.TenantMismatch(Text.L($"This change was made in tenant {record.TenantName ?? record.TenantId}. Connect to that tenant to revert it.", $"Deze wijziging is gemaakt in tenant {record.TenantName ?? record.TenantId}. Verbind met die tenant om haar terug te draaien."));
        }
        if (record.RevertIntent is not { } stored)
        {
            throw RoomOpsException.Unsupported("history.noRevert", Text.L("No safe compensating action is available for this change. Objects that were created are never deleted automatically.", "Voor deze wijziging is geen veilige herstelactie beschikbaar. Aangemaakte objecten worden nooit automatisch verwijderd."));
        }
        var revert = stored.Deserialize<ChangeIntent>(RoomOpsJson.Options)
            ?? throw RoomOpsException.Unsupported("history.noRevert", Text.L("The stored compensating action could not be read.", "De opgeslagen herstelactie kon niet worden gelezen."));
        var (_, targets, notes) = await PlanAsync(revert, ct);
        return (Text.L($"Revert: {record.OperationLabel} — {record.TargetName}", $"Terugdraaien: {record.OperationLabel} — {record.TargetName}"), targets,
            [.. notes, Text.L($"Restores the values recorded before the change on {record.Timestamp:yyyy-MM-dd HH:mm} UTC. Values are re-compared with the current configuration; anything already restored shows 'No change required'.", $"Herstelt de waarden die vóór de wijziging op {record.Timestamp:yyyy-MM-dd HH:mm} UTC zijn vastgelegd. Waarden worden opnieuw vergeleken met de huidige configuratie; wat al is hersteld, toont 'Geen wijziging nodig'.")]);
    }

    internal async Task<(string, IReadOnlyList<PlanTarget>, IReadOnlyList<string>)> PlanResumeAsync(ResumeIntent intent, CancellationToken ct)
    {
        var record = await sessions.Audit.GetAsync(intent.AuditId, ct)
            ?? throw RoomOpsException.NotFound("history.notFound", Text.L("The history entry was not found.", "Het geschiedenisitem is niet gevonden."));
        if (!string.Equals(record.TenantId, sessions.Current.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw RoomOpsException.TenantMismatch(Text.L($"These steps belong to tenant {record.TenantName ?? record.TenantId}. Connect to that tenant to resume them.", $"Deze stappen horen bij tenant {record.TenantName ?? record.TenantId}. Verbind met die tenant om ze te hervatten."));
        }
        if (record.PendingCommands.Count == 0)
        {
            throw RoomOpsException.Validation("history.nothingPending", Text.L("This entry has no pending steps.", "Dit item heeft geen openstaande stappen."));
        }
        var operations = record.PendingCommands.Select(s => new PlannedOperation
        {
            Command = CommandStorage.FromStored(s),
            Description = s.Description,
            AwaitsProvisioning = true,
            DeferrableUntilProvisioned = true,
            ConflictMeansDone = s.Name.Equals("Add-DistributionGroupMember", StringComparison.OrdinalIgnoreCase),
        }).ToList();
        var target = new PlanTarget
        {
            TargetId = record.TargetId,
            Kind = Enum.TryParse<TargetKind>(record.TargetKind, out var kind) ? kind : TargetKind.Room,
            TargetName = record.TargetName,
            OperationType = record.OperationType + ".resume",
            OperationLabel = Text.L($"Resume: {record.OperationLabel}", $"Hervatten: {record.OperationLabel}"),
            Operations = operations,
            Notes = [Text.L("These steps were saved because the object was still provisioning. They are re-validated against the command allowlist before running.", "Deze stappen zijn bewaard omdat het object nog werd ingericht. Ze worden vóór uitvoering opnieuw gevalideerd tegen de lijst met toegestane opdrachten.")],
            Fingerprint = "resume",
            Refingerprint = _ => Task.FromResult("resume"),
            Verify = _ => Task.FromResult(new Domain.Changes.VerificationResult(Domain.Changes.VerificationOutcome.NotApplicable,
                Text.L("Resumed steps completed. Open the room to review its configuration.", "Hervatte stappen voltooid. Open de ruimte om de configuratie te controleren."), [])),
            OnSucceeded = c => sessions.Audit.MarkResumedAsync(record.Id, c),
            Invalidate = cache => cache.InvalidateInventory(),
        };
        return (Text.L($"Resume pending steps — {record.TargetName}", $"Openstaande stappen hervatten — {record.TargetName}"), [target], []);
    }
}
