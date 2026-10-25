using System.Text.Json;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Execution;
using RoomOps.Application.Gateways;
using RoomOps.Application.Services;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Infrastructure.Tests;

public class ChangePipelineTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Dictionary<string, JsonElement> Patch(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value));

    [Fact]
    public async Task Booking_change_is_previewed_executed_verified_and_audited()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.07");
        var current = await backend.Get<ExchangeGateway>().GetBookingPolicyAsync(room.PrimarySmtpAddress, None);

        var plan = await backend.PlanAsync(new UpdateBookingPolicyIntent(room.Id, current with { BookingWindowInDays = 45, AllowConflicts = false }));
        var target = Assert.Single(plan.Targets);
        Assert.Equal(TargetPlanStatus.Changes, target.Status);
        var change = Assert.Single(target.Changes);
        Assert.Equal("BookingWindowInDays", change.Field);
        Assert.Equal("Set-CalendarProcessing -Identity 'ehv-1.07@contoso-demo.example' -BookingWindowInDays 45", Assert.Single(target.Operations).Script);
        Assert.True(plan.IsDemo);

        var result = await backend.ExecuteAsync(plan);
        var executed = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionStatus.Succeeded, executed.Status);
        Assert.Equal(VerificationOutcome.Verified, executed.Verification!.Outcome);
        Assert.Equal(45, (await backend.Get<ExchangeGateway>().GetBookingPolicyAsync(room.PrimarySmtpAddress, None)).BookingWindowInDays);

        var record = await backend.Sessions.Audit.GetAsync(executed.AuditId!, None);
        Assert.NotNull(record);
        Assert.True(record.IsDemo);
        Assert.Equal(AuditOutcome.Succeeded, record.Outcome);
        Assert.True(record.RevertAvailable);
        Assert.Equal("90 days", Assert.Single(record.Changes).Before);
    }

    [Fact]
    public async Task Unchanged_values_produce_no_write()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.07");
        var plan = await backend.PlanAsync(new PatchBookingPolicyIntent([room.Id], Patch(("BookingWindowInDays", 90))));
        var target = Assert.Single(plan.Targets);
        Assert.Equal(TargetPlanStatus.NoChange, target.Status);
        Assert.Empty(target.Operations);
        Assert.Equal(0, plan.Totals.Operations);
    }

    [Fact]
    public async Task Stale_previews_are_detected_before_writing()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.15");
        var first = await backend.PlanAsync(new PatchBookingPolicyIntent([room.Id], Patch(("BookingWindowInDays", 60))));
        var second = await backend.PlanAsync(new PatchBookingPolicyIntent([room.Id], Patch(("BookingWindowInDays", 30))));

        Assert.Equal(TargetExecutionStatus.Succeeded, Assert.Single((await backend.ExecuteAsync(second)).Targets).Status);
        var stale = Assert.Single((await backend.ExecuteAsync(first)).Targets);
        Assert.Equal(TargetExecutionStatus.Stale, stale.Status);
        Assert.Equal(30, (await backend.Get<ExchangeGateway>().GetBookingPolicyAsync(room.PrimarySmtpAddress, None)).BookingWindowInDays);
    }

    [Fact]
    public async Task Plans_are_bound_to_the_tenant_session_they_were_created_in()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.15");
        var plan = await backend.PlanAsync(new PatchBookingPolicyIntent([room.Id], Patch(("BookingWindowInDays", 60))));

        var wrongTenant = Assert.Throws<RoomOpsException>(() => backend.Get<PlanService>().Execute(new ExecuteRequest(plan.PlanId, "00000000-0000-0000-0000-00000000beef", false)));
        Assert.Equal(ErrorKind.TenantMismatch, wrongTenant.Error.Kind);

        await backend.Sessions.StartDemoAsync(None); // a new session: new epoch, new caches
        var switched = Assert.Throws<RoomOpsException>(() => backend.Get<PlanService>().Execute(new ExecuteRequest(plan.PlanId, plan.TenantId, false)));
        Assert.Equal(ErrorKind.TenantMismatch, switched.Error.Kind);
    }

    [Fact]
    public async Task A_plan_executes_at_most_once()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.15");
        var plan = await backend.PlanAsync(new PatchBookingPolicyIntent([room.Id], Patch(("BookingWindowInDays", 61))));
        await backend.ExecuteAsync(plan);
        Assert.Throws<RoomOpsException>(() => backend.Get<PlanService>().Execute(new ExecuteRequest(plan.PlanId, plan.TenantId, false)));
    }

    [Fact]
    public async Task Invalid_desired_state_is_blocked_and_never_executed()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.15");
        var plan = await backend.PlanAsync(new PatchBookingPolicyIntent([room.Id], Patch(("BookingWindowInDays", 5000))));
        Assert.Equal(TargetPlanStatus.Blocked, Assert.Single(plan.Targets).Status);
        Assert.Throws<RoomOpsException>(() => backend.Get<PlanService>().Execute(new ExecuteRequest(plan.PlanId, plan.TenantId, false)));
    }

    [Fact]
    public async Task Removals_require_explicit_acknowledgement()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var lists = await backend.Get<InventoryService>().GetRoomListsAsync(false, None);
        var executive = lists.Single(l => l.DisplayName == "Executive Rooms");
        var plan = await backend.PlanAsync(new UpdateRoomListIntent(executive.Id, null, [], ["ams-4.08@contoso-demo.example"]));
        Assert.True(plan.RequiresRemovalAcknowledgement);
        var error = Assert.Throws<RoomOpsException>(() => backend.Get<PlanService>().Execute(new ExecuteRequest(plan.PlanId, plan.TenantId, false)));
        Assert.Equal(ErrorKind.Validation, error.Error.Kind);

        var result = await backend.ExecuteAsync(plan, acknowledgeRemovals: true);
        Assert.Equal(VerificationOutcome.Verified, Assert.Single(result.Targets).Verification!.Outcome);
        var members = await backend.Get<ExchangeGateway>().GetGroupMembersAsync(executive.PrimarySmtpAddress, None);
        Assert.DoesNotContain(members, m => m.PrimarySmtpAddress == "ams-4.08@contoso-demo.example");
    }

    [Fact]
    public async Task Room_list_conversion_requires_room_only_membership()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var blocked = await backend.PlanAsync(new ConvertToRoomListIntent("utrecht.facilities@contoso-demo.example"));
        Assert.Equal(TargetPlanStatus.Blocked, Assert.Single(blocked.Targets).Status);

        var result = await backend.PlanAndExecuteAsync(new ConvertToRoomListIntent("ams.projectrooms@contoso-demo.example"));
        Assert.Equal(TargetExecutionStatus.Succeeded, Assert.Single(result.Targets).Status);
        Assert.Contains((await backend.Get<InventoryService>().GetRoomListsAsync(true, None)), l => l.PrimarySmtpAddress == "ams.projectrooms@contoso-demo.example");
    }

    [Fact]
    public async Task Exchange_first_room_creation_waits_for_provisioning_and_reports_places_propagation()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var floor = (await backend.Get<InventoryService>().GetPlacesByIdAsync(None)).Values.First(p => p.Type == PlaceType.Floor && p.DisplayName == "Floor 2");
        var lists = await backend.Get<InventoryService>().GetRoomListsAsync(false, None);
        var intent = new CreateRoomIntent
        {
            Strategy = ProvisioningStrategy.ExchangeFirst,
            Name = "ehv-2.20",
            DisplayName = "EHV 2.20 Design Studio",
            Alias = "ehv-2.20",
            PrimarySmtpAddress = "ehv-2.20@contoso-demo.example",
            Capacity = 10,
            PlacesParentId = floor.PlaceId,
            PlacesFields = Patch(("IsWheelChairAccessible", true), ("Tags", new[] { "Design", "Whiteboard" })),
            BookingPresetId = "automatic",
            Booking = BookingPolicy.ExchangeDefaults with { BookingWindowInDays = 90, MaximumDurationInMinutes = 240 },
            WorkingHours = new WorkingHoursConfig(WorkDays.Weekdays, new TimeOnly(8, 0), new TimeOnly(18, 0), "W. Europe Standard Time"),
            RoomListId = lists.Single(l => l.DisplayName == "Eindhoven Headquarters").Id,
        };
        var plan = await backend.PlanAsync(intent);
        var target = Assert.Single(plan.Targets);
        Assert.Equal(TargetPlanStatus.Changes, target.Status);
        Assert.Equal(1, target.Operations.Count(o => o.Command is "New-Mailbox" or "New-Place"));
        Assert.Equal("New-Mailbox", target.Operations[0].Command);

        var result = Assert.Single((await backend.ExecuteAsync(plan)).Targets);
        Assert.All(result.Steps, s => Assert.Contains(s.Status, new[] { "Succeeded", "AlreadyInPlace" }));
        Assert.Contains(result.Steps, s => s.Attempts > 1); // waited for the mailbox / Places sync
        Assert.Equal(TargetExecutionStatus.PendingPropagation, result.Status);
        Assert.Equal(VerificationOutcome.PendingPropagation, result.Verification!.Outcome);
        Assert.All(result.Verification.Fields, f => Assert.True(f.Matches, f.Label));

        var duplicate = await backend.PlanAsync(intent);
        Assert.Equal(TargetPlanStatus.Blocked, Assert.Single(duplicate.Targets).Status);
    }

    [Fact]
    public async Task Places_first_creation_defers_exchange_steps_and_can_resume()
    {
        await using var backend = await TestBackend.StartDemoAsync(s => s with { Execution = s.Execution with { ProvisioningWaitSeconds = 0 } });
        var section = (await backend.Get<InventoryService>().GetPlacesByIdAsync(None)).Values.First(p => p.Type == PlaceType.Section && p.DisplayName == "North wing");
        var intent = new CreateRoomIntent
        {
            Strategy = ProvisioningStrategy.PlacesFirst,
            Name = "ehv-3.30",
            DisplayName = "EHV 3.30 Quiet Room",
            Alias = "ehv-3.30",
            PrimarySmtpAddress = "ehv-3.30@contoso-demo.example",
            Capacity = 4,
            PlacesParentId = section.PlaceId,
            BookingPresetId = "automatic",
            Booking = BookingPolicy.ExchangeDefaults with { MaximumDurationInMinutes = 120 },
        };
        var result = Assert.Single((await backend.PlanAndExecuteAsync(intent)).Targets);
        Assert.Equal("New-Place", (await backend.Sessions.Audit.GetAsync(result.AuditId!, None))!.Steps[0].Script.Split(' ')[0]);
        Assert.Equal(TargetExecutionStatus.PendingProvisioning, result.Status);
        var record = await backend.Sessions.Audit.GetAsync(result.AuditId!, None);
        Assert.Equal(AuditOutcome.PendingProvisioning, record!.Outcome);
        Assert.Contains(record.PendingCommands, c => c.Name == "Set-CalendarProcessing");

        backend.Time.Advance(TimeSpan.FromSeconds(30)); // mailbox provisioned
        var resumed = Assert.Single((await backend.PlanAndExecuteAsync(new ResumeIntent(record.Id))).Targets);
        Assert.True(resumed.Status == TargetExecutionStatus.Succeeded, $"{resumed.Status}: {resumed.Message} | {string.Join(" / ", resumed.Steps.Select(s => s.Status + " " + s.Script + " " + s.Error))}");
        Assert.Equal(120, (await backend.Get<ExchangeGateway>().GetBookingPolicyAsync("ehv-3.30@contoso-demo.example", None)).MaximumDurationInMinutes);
        Assert.Empty((await backend.Sessions.Audit.GetAsync(record.Id, None))!.PendingCommands);
    }

    [Fact]
    public async Task Templates_apply_only_their_fields_honor_exceptions_and_record_assignment()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ams-4.01");
        var templates = backend.Get<TemplateService>();
        var template = await templates.SaveAsync(new Domain.Templates.ConfigurationTemplate
        {
            Id = string.Empty,
            Name = "Short meetings",
            BookingFields = Patch(("BookingWindowInDays", 14), ("MaximumDurationInMinutes", 60), ("AllowConflicts", true)),
        }, None);

        var before = await backend.Get<ExchangeGateway>().GetBookingPolicyAsync(room.PrimarySmtpAddress, None);
        var plan = await backend.PlanAsync(new ApplyTemplateIntent(template.Id, [room.Id], ["AllowConflicts"], true));
        Assert.Equal(["BookingWindowInDays", "MaximumDurationInMinutes"], Assert.Single(plan.Targets).Changes.Select(c => c.Field).Order());
        await backend.ExecuteAsync(plan);

        var after = await backend.Get<ExchangeGateway>().GetBookingPolicyAsync(room.PrimarySmtpAddress, None);
        Assert.Equal(14, after.BookingWindowInDays);
        Assert.Equal(before.AllowConflicts, after.AllowConflicts);
        Assert.Equal(before.DeleteSubject, after.DeleteSubject);
        var assignment = Assert.Single(await templates.AssignmentsAsync(None), a => a.RoomId == room.Id);
        Assert.Equal(["AllowConflicts"], assignment.ExcludedFields);
    }

    [Fact]
    public async Task Revert_restores_recorded_values()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-2.03");
        var change = Assert.Single((await backend.PlanAndExecuteAsync(new PatchBookingPolicyIntent([room.Id], Patch(("MaximumDurationInMinutes", 30), ("DeleteSubject", false))))).Targets);
        var revertPlan = await backend.PlanAsync(new RevertIntent(change.AuditId!));
        Assert.Equal(2, Assert.Single(revertPlan.Targets).Changes.Count);
        await backend.ExecuteAsync(revertPlan);
        var policy = await backend.Get<ExchangeGateway>().GetBookingPolicyAsync(room.PrimarySmtpAddress, None);
        Assert.Equal(240, policy.MaximumDurationInMinutes);
        Assert.True(policy.DeleteSubject);
    }

    [Fact]
    public async Task Unresolved_and_ambiguous_references_are_detected_and_remediated()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var legacy = await backend.Room("conf-b");
        var studio = await backend.Room("studio-podcast");
        var rooms = backend.Get<RoomsService>();
        await rooms.GetDetailAsync(legacy.Id, false, None);
        var studioDetail = await rooms.GetDetailAsync(studio.Id, false, None);
        Assert.Equal(RecipientResolution.Ambiguous, Assert.Single(studioDetail.Booking!.BookInPolicy).Resolution);

        var finding = Assert.Single(await backend.Get<ValidationService>().GetFindingsAsync(false, None), f => f.RuleId == "BOOK-004" && f.TargetId == legacy.Id);
        var result = Assert.Single((await backend.PlanAndExecuteAsync(new RemediateIntent([finding.Id]))).Targets);
        Assert.Equal(TargetExecutionStatus.Succeeded, result.Status);
        var delegates = (await backend.Get<ExchangeGateway>().GetBookingPolicyAsync(legacy.PrimarySmtpAddress, None)).ResourceDelegates;
        Assert.All(delegates, d => Assert.Equal(RecipientResolution.Resolved, d.Resolution));
        Assert.DoesNotContain(await backend.Get<ValidationService>().GetFindingsAsync(false, None), f => f.RuleId == "BOOK-004" && f.TargetId == legacy.Id);
    }

    [Fact]
    public async Task Folder_permissions_target_the_localized_calendar_folder()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.07");
        var plan = await backend.PlanAsync(new SetFolderPermissionIntent(room.Id, "sanne.devries@contoso-demo.example", FolderPermissionAction.Grant, "Editor", true));
        var op = Assert.Single(Assert.Single(plan.Targets).Operations);
        Assert.Equal("Add-MailboxFolderPermission -Identity 'ehv-1.07@contoso-demo.example:\\Agenda' -User 'sanne.devries@contoso-demo.example' -AccessRights 'Editor' -SharingPermissionFlags 'Delegate'", op.Script);
        Assert.Equal(VerificationOutcome.Verified, Assert.Single((await backend.ExecuteAsync(plan)).Targets).Verification!.Outcome);

        var overview = await backend.Get<DelegatesService>().GetAsync(room.Id, None);
        Assert.Equal("ehv-1.07@contoso-demo.example:\\Agenda", overview.CalendarFolder!.Identity);
        Assert.Empty(overview.FullAccess.Items); // granting folder permission never grants Full Access
    }

    [Fact]
    public async Task Recipient_search_finds_people_by_name()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        // Uses Get-Recipient -Anr: on a live tenant, Get-EXORecipient ignored -Anr and returned the first 50 recipients by name.
        var found = Assert.Single(await backend.Get<DelegatesService>().SearchRecipientsAsync("julia", null, None));
        Assert.Equal("Julia Meijer", found.DisplayName);
        Assert.Equal("julia.meijer@contoso-demo.example", found.PrimarySmtpAddress);
    }

    [Fact]
    public async Task Configuration_scan_reads_every_room_in_chunks_and_keeps_each_result_with_its_room()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var rooms = await backend.Get<InventoryService>().GetMailboxesAsync(None);
        var scan = backend.Get<ScanService>();
        await scan.StartAsync(None);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (scan.Current is { Running: true } && DateTime.UtcNow < deadline) await Task.Delay(100);

        var done = scan.Current!;
        Assert.False(done.Running);
        Assert.Equal(rooms.Count, done.Completed);
        Assert.Equal(0, done.Failed);
        var cache = backend.Get<ISessionManager>().Cache;
        Assert.All(rooms, r => Assert.True(cache.Booking.ContainsKey(r.Id) && cache.WorkingHours.ContainsKey(r.Id)));
        var training = rooms.Single(r => r.Alias == "ehv-2.11"); // the only room whose working hours start at 07:30
        Assert.Equal(365, cache.Booking[training.Id].Value.BookingWindowInDays);
        Assert.Equal(new TimeOnly(7, 30), cache.WorkingHours[training.Id].Value.Start);
    }

    [Fact]
    public async Task Folder_permission_role_changes_in_place_and_drops_delegate_status_first()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-0.10"); // Sanne de Vries: Editor [Delegate]
        var plan = await backend.PlanAsync(new SetFolderPermissionIntent(room.Id, "Sanne de Vries", FolderPermissionAction.Grant, "LimitedDetails", false));
        var target = Assert.Single(plan.Targets);
        Assert.Equal("Change calendar folder permission", target.OperationLabel);
        Assert.Equal(
        [
            "Set-MailboxFolderPermission -Identity 'ehv-0.10@contoso-demo.example:\\Agenda' -User 'sanne.devries@contoso-demo.example' -AccessRights 'Editor' -SharingPermissionFlags 'None'",
            "Set-MailboxFolderPermission -Identity 'ehv-0.10@contoso-demo.example:\\Agenda' -User 'sanne.devries@contoso-demo.example' -AccessRights 'LimitedDetails'",
        ], target.Operations.Select(o => o.Script));
        Assert.Equal(VerificationOutcome.Verified, Assert.Single((await backend.ExecuteAsync(plan)).Targets).Verification!.Outcome);
    }

    [Fact]
    public async Task Built_in_folder_entries_change_role_but_cannot_be_removed_or_given_edit_rights()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.07");
        var plan = await backend.PlanAsync(new SetFolderPermissionIntent(room.Id, "Default", FolderPermissionAction.Grant, "LimitedDetails", false));
        Assert.Equal("Set-MailboxFolderPermission -Identity 'ehv-1.07@contoso-demo.example:\\Agenda' -User 'Default' -AccessRights 'LimitedDetails'", Assert.Single(Assert.Single(plan.Targets).Operations).Script);
        Assert.Equal(VerificationOutcome.Verified, Assert.Single((await backend.ExecuteAsync(plan)).Targets).Verification!.Outcome);

        Assert.Equal(TargetPlanStatus.Blocked, Assert.Single((await backend.PlanAsync(new SetFolderPermissionIntent(room.Id, "Default", FolderPermissionAction.Grant, "Editor", false))).Targets).Status);
        Assert.Equal(TargetPlanStatus.Blocked, Assert.Single((await backend.PlanAsync(new SetFolderPermissionIntent(room.Id, "Anonymous", FolderPermissionAction.Remove, "None", false))).Targets).Status);
    }

    [Fact]
    public async Task Delegates_overview_names_full_access_trustees()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.15");
        var overview = await backend.Get<DelegatesService>().GetAsync(room.Id, None);
        var entry = Assert.Single(overview.FullAccess.Items);
        Assert.Equal("Julia Meijer", entry.DisplayName);
        Assert.Contains(overview.FolderPermissions.Items, p => p.User == entry.DisplayName && p.AccessRights.SequenceEqual(["LimitedDetails"]));
    }

    [Fact]
    public async Task Places_reparenting_follows_hierarchy_rules_and_reports_propagation()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-1.20");
        var places = (await backend.Get<InventoryService>().GetPlacesByIdAsync(None)).Values.ToList();
        var building = places.First(p => p.Type == PlaceType.Building);
        var floor = places.First(p => p.Type == PlaceType.Floor && p.ParentId == building.PlaceId && p.DisplayName == "Floor 1");

        var invalid = await backend.PlanAsync(new UpdatePlacesRoomIntent([room.Id], Patch(("ParentId", building.PlaceId))));
        Assert.Equal(TargetPlanStatus.Blocked, Assert.Single(invalid.Targets).Status);

        var result = Assert.Single((await backend.PlanAndExecuteAsync(new UpdatePlacesRoomIntent([room.Id], Patch(("ParentId", floor.PlaceId))))).Targets);
        Assert.Equal(TargetExecutionStatus.PendingPropagation, result.Status);
        Assert.Contains("24 hours", result.Verification!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synchronized_mailbox_properties_are_not_changed_in_the_cloud()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var synced = await backend.Room("conf-b");
        Assert.True(synced.IsDirSynced);
        var plan = await backend.PlanAsync(new UpdateMailboxIntent(synced.Id, "Conference Room B", 14));
        var target = Assert.Single(plan.Targets);
        Assert.Equal(TargetPlanStatus.Blocked, target.Status);
        Assert.Contains(target.Issues, i => i.Message.Contains("on-premises", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bulk_import_validates_rows_and_plans_per_object()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        const string csv = "Room;BookingWindowInDays;AllowRecurringMeetings;Capacity\n" +
            "ehv-1.02@contoso-demo.example;30;yes;3\n" +
            "ehv-1.07;45;no;\n" +
            "unknown.room@contoso-demo.example;30;yes;\n" +
            "ehv-1.02@contoso-demo.example;60;yes;\n" +
            "ams-4.05@contoso-demo.example;20;maybe;\n";
        var parsed = await backend.Get<BulkImportService>().ParseAsync(new BulkParseRequest("csv", csv, null), None);
        Assert.Empty(parsed.Errors);
        Assert.Equal("Room", parsed.Mapping["Room"]);
        Assert.Equal(5, parsed.Rows.Count);
        Assert.Equal(2, parsed.ValidRows);
        Assert.Contains(parsed.Rows[2].Issues, i => i.Message.Contains("No room mailbox", StringComparison.Ordinal));
        Assert.Contains(parsed.Rows[3].Issues, i => i.Message.Contains("Duplicate of row 2", StringComparison.Ordinal));
        Assert.Contains(parsed.Rows[4].Issues, i => i.Field == "AllowRecurringMeetings");

        var plan = await backend.PlanAsync(parsed.Intent!);
        Assert.Equal(3, plan.Targets.Count); // two booking targets + one capacity target
        var result = await backend.ExecuteAsync(plan);
        Assert.Equal(0, result.Counts.Failed);
        Assert.Equal(3, (await backend.Get<ExchangeGateway>().GetMailboxAsync("ehv-1.02@contoso-demo.example", None))!.ResourceCapacity);
    }

    [Fact]
    public async Task Working_hours_are_validated_and_verified()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var room = await backend.Room("ehv-2.03");
        var invalid = await backend.PlanAsync(new UpdateWorkingHoursIntent([room.Id], new WorkingHoursConfig(WorkDays.Weekdays, new TimeOnly(18, 0), new TimeOnly(9, 0), "W. Europe Standard Time")));
        Assert.Equal(TargetPlanStatus.Blocked, Assert.Single(invalid.Targets).Status);
        var result = await backend.PlanAndExecuteAsync(new UpdateWorkingHoursIntent([room.Id], new WorkingHoursConfig(WorkDays.Monday | WorkDays.Tuesday | WorkDays.Wednesday | WorkDays.Thursday, new TimeOnly(7, 0), new TimeOnly(16, 30), "GMT Standard Time")));
        Assert.Equal(VerificationOutcome.Verified, Assert.Single(result.Targets).Verification!.Outcome);
    }

    [Fact]
    public async Task Retry_replans_only_items_that_did_not_complete()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        var a = await backend.Room("ehv-1.02");
        var b = await backend.Room("ehv-1.15");
        var stalePlan = await backend.PlanAsync(new PatchBookingPolicyIntent([a.Id, b.Id], Patch(("BookingWindowInDays", 33))));
        await backend.PlanAndExecuteAsync(new PatchBookingPolicyIntent([b.Id], Patch(("BookingWindowInDays", 34)))); // makes b stale
        var first = await backend.ExecuteAsync(stalePlan);
        Assert.Equal(1, first.Counts.Failed);

        var retry = await backend.Get<PlanService>().CreateRetryAsync(first.ExecutionId, None);
        var target = Assert.Single(retry.Targets);
        Assert.Equal(b.Id, target.TargetId);
        Assert.Equal("34 days", Assert.Single(target.Changes).Before);
    }

    [Fact]
    public async Task Live_services_other_than_exchange_require_an_exchange_anchor()
    {
        await using var backend = await TestBackend.StartDemoAsync();
        await backend.Sessions.EndSessionAsync(None);
        var error = await Assert.ThrowsAsync<RoomOpsException>(() => backend.Sessions.ConnectAsync(new Application.Sessions.ConnectRequest(Application.Commands.ServiceKind.Places, null, Application.Sessions.SignInMethod.Browser), None));
        Assert.Equal(ErrorKind.NotConnected, error.Error.Kind);
    }
}
