using System.Text.Json;
using RoomOps.Application.Abstractions;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Common;

namespace RoomOps.Infrastructure.Demo;

/// <summary>Sample activity for demo mode. Clearly flagged as demo data and only ever stored in the volatile demo audit store.</summary>
internal static class DemoHistory
{
    public static IReadOnlyList<AuditRecord> Create(DemoTenant tenant, DateTimeOffset now)
    {
        string RoomId(string alias) => tenant.Recipients.First(r => r.Alias == alias).Id;
        string ListId(string alias) => tenant.Recipients.First(r => r.Alias == alias).Id;
        static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);
        var records = new List<AuditRecord>();

        AuditRecord Record(TimeSpan ago, string op, string label, string kind, string targetId, string target, PropertyChange[] changes, AuditStep[] steps,
            AuditOutcome outcome, VerificationResult? verification, string? error = null) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ExecutionId = Guid.NewGuid().ToString("N"),
            PlanId = Guid.NewGuid().ToString("N"),
            Timestamp = now - ago,
            Actor = DemoTenant.AdminAccount,
            TenantId = DemoTenant.TenantId,
            TenantName = DemoTenant.TenantName,
            IsDemo = true,
            OperationType = op,
            OperationLabel = label,
            TargetKind = kind,
            TargetId = targetId,
            TargetName = target,
            Changes = changes,
            Steps = steps,
            Outcome = outcome,
            Verification = verification,
            VerifiedAt = verification is null ? null : now - ago + TimeSpan.FromSeconds(3),
            Error = error,
        };

        static FieldVerification Ok(string f, string l, string v) => new(f, l, v, v, true);

        records.Add(Record(TimeSpan.FromHours(2), "booking.update", "Update booking policy", "Room", RoomId("ehv-1.07"), "EHV 1.07 Huddle",
            [new("MaximumDurationInMinutes", "Maximum duration", SourceSystem.ExchangeOnline, "4 hours", "2 hours", J(240), J(120))],
            [new("Update 1 calendar-processing setting(s)", "Set-CalendarProcessing -Identity 'ehv-1.07@contoso-demo.example' -MaximumDurationInMinutes 120", "Succeeded", 412, 1, null)],
            AuditOutcome.Succeeded, VerificationResult.FromFields([Ok("MaximumDurationInMinutes", "Maximum duration", "2 hours")])));

        records.Add(Record(TimeSpan.FromHours(5), "roomList.update", "Update room list", "RoomList", ListId("rooms.teams"), "Teams Rooms",
            [new("Member:ehv-3.22@contoso-demo.example", "Add EHV 3.22 Teams Room", SourceSystem.ExchangeOnline, "Not a member", "Member", null, null)],
            [new("Add EHV 3.22 Teams Room", "Add-DistributionGroupMember -Identity 'rooms.teams@contoso-demo.example' -Member 'ehv-3.22@contoso-demo.example' -BypassSecurityGroupManagerCheck", "Succeeded", 388, 1, null)],
            AuditOutcome.Succeeded, VerificationResult.FromFields([Ok("Member:ehv-3.22@contoso-demo.example", "Add EHV 3.22 Teams Room", "Member")])));

        records.Add(Record(TimeSpan.FromHours(26), "places.room.update", "Update Places room", "Room", RoomId("ehv-3.20"), "EHV 3.20 Teams Room",
            [new("MTREnabled", "Teams Rooms system (MTR)", SourceSystem.MicrosoftPlaces, "No", "Yes", J(false), J(true))],
            [new("Update the Places room object", "Set-PlaceV3 -Identity 'ehv-3.20@contoso-demo.example' -MTREnabled $true", "Succeeded", 640, 1, null)],
            AuditOutcome.Succeeded, new VerificationResult(VerificationOutcome.PendingPropagation,
                "Configuration accepted and confirmed in the Places directory. Places experiences may take up to 24 hours to reflect it.",
                [Ok("MTREnabled", "Teams Rooms system (MTR)", "Yes")])));

        records.Add(Record(TimeSpan.FromHours(29), "workingHours.update", "Update working hours", "Room", RoomId("ehv-2.11"), "EHV 2.11 Training Room",
            [
                new("WorkingHoursStartTime", "Start time", SourceSystem.ExchangeOnline, "08:00:00", "07:30:00", J("08:00:00"), J("07:30:00")),
                new("WorkingHoursEndTime", "End time", SourceSystem.ExchangeOnline, "18:00:00", "19:00:00", J("18:00:00"), J("19:00:00")),
            ],
            [new("Update mailbox working hours", "Set-MailboxCalendarConfiguration -Identity 'ehv-2.11@contoso-demo.example' -WorkingHoursStartTime '07:30:00' -WorkingHoursEndTime '19:00:00'", "Succeeded", 505, 1, null)],
            AuditOutcome.Succeeded, VerificationResult.FromFields([Ok("WorkingHoursStartTime", "Start time", "07:30:00"), Ok("WorkingHoursEndTime", "End time", "19:00:00")])));

        records.Add(Record(TimeSpan.FromDays(2), "booking.update", "Update booking policy", "Room", RoomId("ehv-0.10"), "EHV 0.10 Auditorium",
            [new("ResourceDelegates", "Booking delegates", SourceSystem.ExchangeOnline, "sanne.devries@contoso-demo.example", "sanne.devries@contoso-demo.example, facility.desk@contoso-demo.example", null, null)],
            [new("Update 1 calendar-processing setting(s)", "Set-CalendarProcessing -Identity 'ehv-0.10@contoso-demo.example' -ResourceDelegates 'sanne.devries@contoso-demo.example','facility.desk@contoso-demo.example'", "Succeeded", 2930, 2, null)],
            AuditOutcome.Succeeded, VerificationResult.FromFields([Ok("ResourceDelegates", "Booking delegates", "sanne.devries@contoso-demo.example, facility.desk@contoso-demo.example")])));

        records.Add(Record(TimeSpan.FromDays(3), "places.create", "Create section", "Place", "new:Section:South wing", "South wing",
            [new("DisplayName", "DisplayName", SourceSystem.MicrosoftPlaces, "(new)", "South wing", null, null), new("ParentId", "Parent location", SourceSystem.MicrosoftPlaces, "(new)", "Eindhoven Headquarters / Floor 3", null, null)],
            [new("Create section", "New-Place -DisplayName 'South wing' -Type 'Section' -ParentId '5e1d0071-0d3e-4a11-9c0f-000000000071'", "Succeeded", 820, 1, null)],
            AuditOutcome.Succeeded, new VerificationResult(VerificationOutcome.Verified, "Created and read back.", [Ok("PlaceId", "Place id", "assigned")])));

        records.Add(Record(TimeSpan.FromDays(4), "booking.update", "Bulk booking update", "Room", RoomId("ams-5.10"), "Row 7 · AMS 5.10 Phone Booth",
            [new("AllowRecurringMeetings", "Allow recurring meetings", SourceSystem.ExchangeOnline, "Enabled", "Disabled", J(true), J(false))],
            [new("Update 1 calendar-processing setting(s)", "Set-CalendarProcessing -Identity 'ams-5.10@contoso-demo.example' -AllowRecurringMeetings $false", "Succeeded", 377, 1, null)],
            AuditOutcome.Succeeded, VerificationResult.FromFields([Ok("AllowRecurringMeetings", "Allow recurring meetings", "Disabled")])));

        records.Add(Record(TimeSpan.FromDays(6), "roomList.update", "Update room list", "RoomList", ListId("rooms.executive"), "Executive Rooms",
            [new("Member:ams-4.08@contoso-demo.example", "Add AMS 4.08 Client Suite", SourceSystem.ExchangeOnline, "Not a member", "Member", null, null)],
            [], AuditOutcome.Stale, null, "Configuration changed since preview."));

        records.Add(Record(TimeSpan.FromDays(8), "mailbox.update", "Update room mailbox", "Room", RoomId("ams-4.05"), "AMS 4.05 Huddle",
            [new("ResourceCapacity", "Capacity", SourceSystem.ExchangeOnline, "6", "4", J(6), J(4))],
            [new("Update mailbox properties", "Set-Mailbox -Identity 'ams-4.05@contoso-demo.example' -ResourceCapacity 4", "Failed", 290, 1,
                "The operation on mailbox \"ams-4.05\" failed because it's out of the current user's write scope (access denied).")],
            AuditOutcome.Failed, null, "The operation on mailbox \"ams-4.05\" failed because it's out of the current user's write scope (access denied)."));

        return records;
    }
}
