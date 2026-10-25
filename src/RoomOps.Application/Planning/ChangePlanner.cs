using System.Text.Json;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Commands;
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

namespace RoomOps.Application.Planning;

/// <summary>
/// Turns intents into per-target plans: loads current state, validates, diffs, and produces the exact commands plus verification
/// and stale-detection callbacks. Idempotent by construction: unchanged values never produce a write.
/// </summary>
public sealed partial class ChangePlanner(
    ISessionManager sessions,
    ExchangeGateway exchange,
    PlacesGateway places,
    InventoryService inventory,
    ITemplateStore templates,
    ISettingsStore settings,
    ValidationService validation,
    TimeProvider time)
{
    private TenantCache Cache => sessions.Cache;
    private DateTimeOffset Now => time.GetUtcNow();

    public async Task<(string Title, IReadOnlyList<PlanTarget> Targets, IReadOnlyList<string> Notes)> PlanAsync(ChangeIntent intent, CancellationToken ct)
    {
        switch (intent)
        {
            case UpdateBookingPolicyIntent i:
            {
                var room = await inventory.ResolveRoomAsync(i.RoomId, ct);
                return (Text.L("Update booking policy", "Boekingsbeleid bijwerken"), [await PlanBookingAsync(room, _ => i.Policy, Text.L("Update booking policy", "Boekingsbeleid bijwerken"), ct)], []);
            }
            case PatchBookingPolicyIntent i:
            {
                var targets = new List<PlanTarget>();
                foreach (var roomId in i.RoomIds.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var room = await inventory.ResolveRoomAsync(roomId, ct);
                    targets.Add(await PlanBookingAsync(room, current => BookingPolicyFields.ApplyPatch(current, i.Fields), Text.L("Update booking settings", "Boekingsinstellingen bijwerken"), ct));
                }
                return (i.RoomIds.Count == 1 ? Text.L("Update booking settings", "Boekingsinstellingen bijwerken") : Text.L($"Update booking settings for {i.RoomIds.Count} rooms", $"Boekingsinstellingen bijwerken voor {i.RoomIds.Count} ruimtes"), targets, []);
            }
            case UpdateWorkingHoursIntent i:
            {
                var targets = new List<PlanTarget>();
                foreach (var roomId in i.RoomIds.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    targets.Add(await PlanWorkingHoursAsync(await inventory.ResolveRoomAsync(roomId, ct), i.WorkingHours, ct));
                }
                return (i.RoomIds.Count == 1 ? Text.L("Update working hours", "Werktijden bijwerken") : Text.L($"Update working hours for {i.RoomIds.Count} rooms", $"Werktijden bijwerken voor {i.RoomIds.Count} ruimtes"), targets, []);
            }
            case UpdateMailboxIntent i:
                return (Text.L("Update room mailbox", "Ruimtepostvak bijwerken"), [await PlanMailboxAsync(await inventory.ResolveRoomAsync(i.RoomId, ct), i, ct)], []);
            case UpdatePlacesRoomIntent i:
            {
                var targets = new List<PlanTarget>();
                foreach (var roomId in i.RoomIds.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    targets.Add(await PlanPlacesRoomAsync(await inventory.ResolveRoomAsync(roomId, ct), i.Fields, ct));
                }
                return (i.RoomIds.Count == 1 ? Text.L("Update Places metadata", "Places-gegevens bijwerken") : Text.L($"Update Places metadata for {i.RoomIds.Count} rooms", $"Places-gegevens bijwerken voor {i.RoomIds.Count} ruimtes"), targets, []);
            }
            case SetFolderPermissionIntent i:
            {
                var target = await PlanFolderPermissionAsync(await inventory.ResolveRoomAsync(i.RoomId, ct), i, ct);
                return (target.OperationLabel, [target], []);
            }
            case CreateRoomListIntent i:
                return (Text.L("Create room list", "Ruimtelijst maken"), [await PlanCreateRoomListAsync(i, ct)], []);
            case UpdateRoomListIntent i:
                return (Text.L("Update room list", "Ruimtelijst bijwerken"), [await PlanUpdateRoomListAsync(i, ct)], []);
            case ConvertToRoomListIntent i:
                return (Text.L("Convert distribution group to room list", "Distributiegroep omzetten naar ruimtelijst"), [await PlanConvertAsync(i, ct)], []);
            case CreatePlaceIntent i:
                return (i.Type switch
                {
                    PlaceType.Building => Text.L("Create building", "Gebouw maken"),
                    PlaceType.Floor => Text.L("Create floor", "Verdieping maken"),
                    _ => Text.L("Create section", "Sectie maken"),
                }, [await PlanCreatePlaceAsync(i, ct)], []);
            case UpdatePlaceIntent i:
                return (Text.L("Update place", "Locatie bijwerken"), [await PlanUpdatePlaceAsync(i, ct)], []);
            case CreateRoomIntent i:
                return await PlanCreateRoomAsync(i, ct);
            case ApplyTemplateIntent i:
                return await PlanTemplateAsync(i, ct);
            case BulkImportIntent i:
                return await PlanBulkAsync(i, ct);
            case RemediateIntent i:
                return await PlanRemediationAsync(i, ct);
            case RevertIntent i:
                return await PlanRevertAsync(i, ct);
            case ResumeIntent i:
                return await PlanResumeAsync(i, ct);
            case CompositeIntent i:
            {
                var all = new List<PlanTarget>();
                var notes = new List<string>();
                foreach (var sub in i.Intents)
                {
                    var (_, t, n) = await PlanAsync(sub, ct);
                    all.AddRange(t);
                    notes.AddRange(n);
                }
                return (i.Title, all, notes);
            }
            default:
                throw RoomOpsException.Unsupported("plan.unsupportedIntent", Text.L($"Intent '{intent.GetType().Name}' is not supported.", $"Intentie '{intent.GetType().Name}' wordt niet ondersteund."));
        }
    }

    // ------------------------------------------------------------------------------------------------ Booking policy

    internal async Task<PlanTarget> PlanBookingAsync(RoomMailbox room, Func<BookingPolicy, BookingPolicy> desiredFrom, string label, CancellationToken ct)
    {
        var smtp = room.PrimarySmtpAddress;
        var current = await exchange.GetBookingPolicyAsync(smtp, ct);
        Cache.Booking[room.Id] = new(current, Now);

        BookingPolicy desired;
        var issues = new List<ValidationIssue>();
        try
        {
            desired = await exchange.ResolveRecipientsAsync(desiredFrom(current), ct);
        }
        catch (FormatException ex)
        {
            return Blocked(room, "booking.update", label, ex.Message);
        }

        var changed = BookingPolicyFields.ChangedFields(current, desired);
        var changedNames = changed.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        WorkingHoursConfig? hours = null;
        if (desired.ScheduleOnlyDuringWorkHours)
        {
            hours = await TryGetWorkingHoursAsync(room, ct);
        }
        foreach (var issue in BookingPolicyValidator.Validate(desired, hours, room.ResourceCapacity))
        {
            // Errors on settings this change doesn't touch are reported, but don't block: they are not sent to Exchange.
            issues.Add(issue.Level == IssueLevel.Error && !changedNames.Contains(issue.Field)
                ? issue with { Level = IssueLevel.Warning, Message = Text.L($"Existing configuration: {issue.Message}", $"Bestaande configuratie: {issue.Message}") }
                : issue);
        }

        var changes = changed.Select(f => new PropertyChange(f.Name, f.Label, SourceSystem.ExchangeOnline, f.Format(current), f.Format(desired), f.ToJson(current), f.ToJson(desired))).ToList();
        var operations = changed.Count == 0
            ? []
            : new List<PlannedOperation>
            {
                new()
                {
                    Command = PsCommand.Create("Set-CalendarProcessing", [new("Identity", smtp), .. changed.Select(f => new KeyValuePair<string, object?>(f.Name, f.ToParameterValue(desired)))]),
                    Description = Text.L($"Update {changed.Count} calendar-processing setting(s)", $"{changed.Count} instelling(en) voor agendaverwerking bijwerken"),
                },
            };

        var revertFields = changed.ToDictionary(f => f.Name, f => f.ToJson(current));
        return new PlanTarget
        {
            TargetId = room.Id,
            Kind = TargetKind.Room,
            TargetName = room.DisplayName,
            OperationType = "booking.update",
            OperationLabel = label,
            Changes = changes,
            Operations = operations,
            Issues = issues,
            Fingerprint = BookingFingerprint(current),
            Refingerprint = async c => BookingFingerprint(await exchange.GetBookingPolicyAsync(smtp, c)),
            Verify = async c =>
            {
                var after = await exchange.GetBookingPolicyAsync(smtp, c);
                Cache.Booking[room.Id] = new(after, Now);
                return VerificationResult.FromFields(changed.Select(f => new FieldVerification(f.Name, f.Label, f.Format(desired), f.Format(after), f.AreEqual(desired, after))).ToList());
            },
            RevertIntent = changed.Count > 0 ? new PatchBookingPolicyIntent([room.Id], revertFields) : null,
            Invalidate = cache => cache.Booking.TryRemove(room.Id, out _),
        };
    }

    private static string BookingFingerprint(BookingPolicy policy) => Fingerprint.Of(BookingPolicyFields.All.Select(f => f.Normalize(policy)?.ToString()));

    private async Task<WorkingHoursConfig?> TryGetWorkingHoursAsync(RoomMailbox room, CancellationToken ct)
    {
        try
        {
            var hours = await exchange.GetWorkingHoursAsync(room.PrimarySmtpAddress, ct);
            Cache.WorkingHours[room.Id] = new(hours, Now);
            return hours;
        }
        catch (RoomOpsException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------ Working hours

    internal async Task<PlanTarget> PlanWorkingHoursAsync(RoomMailbox room, WorkingHoursConfig desired, CancellationToken ct)
    {
        var smtp = room.PrimarySmtpAddress;
        var current = await exchange.GetWorkingHoursAsync(smtp, ct);
        Cache.WorkingHours[room.Id] = new(current, Now);
        var issues = WorkingHoursValidator.Validate(desired, TimeZoneCatalog.IsKnown).ToList();

        var booking = Cache.Booking.TryGetValue(room.Id, out var b) ? b.Value : null;
        if (booking?.ScheduleOnlyDuringWorkHours == true && desired.Days == WorkDays.None)
        {
            issues.Add(new("days", Text.L("This room only accepts bookings during working hours. Without work days every request would be declined.", "Deze ruimte accepteert alleen boekingen tijdens werktijden. Zonder werkdagen zou elke aanvraag worden geweigerd."), IssueLevel.Error));
        }

        var parameters = new List<KeyValuePair<string, object?>> { new("Identity", smtp) };
        var changes = new List<PropertyChange>();
        var fields = new List<(string Name, string Label, string Before, string After)>();
        void Compare(string name, string label, string before, string after, object? value)
        {
            if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) return;
            parameters.Add(new(name, value));
            fields.Add((name, label, before, after));
            changes.Add(new PropertyChange(name, label, SourceSystem.ExchangeOnline, before, after, ChangeValues.ToJson(before), ChangeValues.ToJson(after)));
        }
        Compare("WorkDays", Text.L("Work days", "Werkdagen"), WorkingHoursConfig.DescribeDays(current.Days), WorkingHoursConfig.DescribeDays(desired.Days), WorkingHoursConfig.FormatWorkDays(desired.Days));
        Compare("WorkingHoursStartTime", Text.L("Start time", "Begintijd"), WorkingHoursConfig.FormatTime(current.Start), WorkingHoursConfig.FormatTime(desired.Start), WorkingHoursConfig.FormatTime(desired.Start));
        Compare("WorkingHoursEndTime", Text.L("End time", "Eindtijd"), WorkingHoursConfig.FormatTime(current.End), WorkingHoursConfig.FormatTime(desired.End), WorkingHoursConfig.FormatTime(desired.End));
        Compare("WorkingHoursTimeZone", Text.L("Time zone", "Tijdzone"), current.TimeZoneId, desired.TimeZoneId, desired.TimeZoneId);

        var notes = new List<string>();
        if (booking?.ScheduleOnlyDuringWorkHours == true)
        {
            notes.Add(Text.L("This room only accepts bookings during working hours (ScheduleOnlyDuringWorkHours), so these hours directly limit when it can be booked.", "Deze ruimte accepteert alleen boekingen tijdens werktijden (ScheduleOnlyDuringWorkHours), dus deze tijden bepalen direct wanneer ze kan worden geboekt."));
        }
        if (!string.Equals(current.TimeZoneId, desired.TimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(Text.L("Changing the time zone shifts the working-hours window in absolute time; existing meetings keep their times.", "Een andere tijdzone verschuift het werktijdvenster in absolute tijd; bestaande vergaderingen houden hun tijden."));
        }

        return new PlanTarget
        {
            TargetId = room.Id,
            Kind = TargetKind.Room,
            TargetName = room.DisplayName,
            OperationType = "workingHours.update",
            OperationLabel = Text.L("Update working hours", "Werktijden bijwerken"),
            Changes = changes,
            Operations = changes.Count == 0 ? [] : [new PlannedOperation { Command = PsCommand.Create("Set-MailboxCalendarConfiguration", parameters), Description = Text.L("Update mailbox working hours", "Werktijden van het postvak bijwerken") }],
            Issues = issues,
            Notes = notes,
            Fingerprint = Fingerprint.Of(current),
            Refingerprint = async c => Fingerprint.Of(await exchange.GetWorkingHoursAsync(smtp, c)),
            Verify = async c =>
            {
                var after = await exchange.GetWorkingHoursAsync(smtp, c);
                Cache.WorkingHours[room.Id] = new(after, Now);
                var actual = new Dictionary<string, string>
                {
                    ["WorkDays"] = WorkingHoursConfig.DescribeDays(after.Days),
                    ["WorkingHoursStartTime"] = WorkingHoursConfig.FormatTime(after.Start),
                    ["WorkingHoursEndTime"] = WorkingHoursConfig.FormatTime(after.End),
                    ["WorkingHoursTimeZone"] = after.TimeZoneId,
                };
                return VerificationResult.FromFields(fields.Select(f => new FieldVerification(f.Name, f.Label, f.After, actual[f.Name], string.Equals(f.After, actual[f.Name], StringComparison.OrdinalIgnoreCase))).ToList());
            },
            RevertIntent = changes.Count > 0 ? new UpdateWorkingHoursIntent([room.Id], current) : null,
            Invalidate = cache => cache.WorkingHours.TryRemove(room.Id, out _),
        };
    }

    // ------------------------------------------------------------------------------------------------ Mailbox properties

    internal async Task<PlanTarget> PlanMailboxAsync(RoomMailbox cached, UpdateMailboxIntent intent, CancellationToken ct)
    {
        var current = await exchange.GetMailboxAsync(cached.PrimarySmtpAddress, ct)
            ?? throw RoomOpsException.NotFound("room.notFound", Text.L($"Room mailbox '{cached.PrimarySmtpAddress}' no longer exists.", $"Ruimtepostvak '{cached.PrimarySmtpAddress}' bestaat niet meer."));
        var issues = new List<ValidationIssue>();
        var parameters = new List<KeyValuePair<string, object?>> { new("Identity", current.PrimarySmtpAddress) };
        var changes = new List<PropertyChange>();

        if (intent.DisplayName is { } name && !string.Equals(name.Trim(), current.DisplayName, StringComparison.Ordinal))
        {
            issues.AddRange(RecipientNaming.ValidateDisplayName(name));
            parameters.Add(new("DisplayName", name.Trim()));
            changes.Add(new("DisplayName", Text.L("Display name", "Weergavenaam"), SourceSystem.ExchangeOnline, current.DisplayName, name.Trim(), ChangeValues.ToJson(current.DisplayName), ChangeValues.ToJson(name.Trim())));
        }
        int? desiredCapacity = intent.ClearCapacity ? null : intent.ResourceCapacity ?? current.ResourceCapacity;
        if (desiredCapacity != current.ResourceCapacity)
        {
            if (desiredCapacity is < 0 or > 100000)
            {
                issues.Add(new("resourceCapacity", Text.L("Capacity must be between 0 and 100000.", "De capaciteit moet tussen 0 en 100000 liggen."), IssueLevel.Error));
            }
            parameters.Add(new("ResourceCapacity", desiredCapacity));
            changes.Add(new("ResourceCapacity", Text.L("Capacity", "Capaciteit"), SourceSystem.ExchangeOnline, current.ResourceCapacity?.ToString() ?? Text.L("(not set)", "(niet ingesteld)"), desiredCapacity?.ToString() ?? Text.L("(not set)", "(niet ingesteld)"),
                ChangeValues.ToJson(current.ResourceCapacity), ChangeValues.ToJson(desiredCapacity)));
        }
        if (current.IsDirSynced && changes.Count > 0)
        {
            issues.Add(new("identity", Text.L("This room mailbox is synchronized from on-premises Active Directory. Its display name and capacity are managed on-premises (Set-Mailbox in Exchange Server); cloud-side updates are not attempted.", "Dit ruimtepostvak wordt gesynchroniseerd vanuit on-premises Active Directory. Weergavenaam en capaciteit worden on-premises beheerd (Set-Mailbox in Exchange Server); wijzigingen in de cloud worden niet geprobeerd."), IssueLevel.Error));
        }

        return new PlanTarget
        {
            TargetId = current.Id,
            Kind = TargetKind.Room,
            TargetName = current.DisplayName,
            OperationType = "mailbox.update",
            OperationLabel = Text.L("Update room mailbox", "Ruimtepostvak bijwerken"),
            Changes = changes,
            Operations = changes.Count == 0 ? [] : [new PlannedOperation { Command = PsCommand.Create("Set-Mailbox", parameters), Description = Text.L("Update mailbox properties", "Eigenschappen van het postvak bijwerken") }],
            Issues = issues,
            Fingerprint = Fingerprint.Of(new { current.DisplayName, current.ResourceCapacity }),
            Refingerprint = async c =>
            {
                var now = await exchange.GetMailboxAsync(current.PrimarySmtpAddress, c);
                return Fingerprint.Of(new { now?.DisplayName, now?.ResourceCapacity });
            },
            Verify = async c =>
            {
                var after = await exchange.GetMailboxAsync(current.PrimarySmtpAddress, c);
                var fields = new List<FieldVerification>();
                foreach (var change in changes)
                {
                    var actual = change.Field == "DisplayName" ? after?.DisplayName ?? Text.L("(missing)", "(ontbreekt)") : after?.ResourceCapacity?.ToString() ?? Text.L("(not set)", "(niet ingesteld)");
                    fields.Add(new(change.Field, change.Label, change.After, actual, actual == change.After));
                }
                return VerificationResult.FromFields(fields);
            },
            RevertIntent = changes.Count > 0
                ? new UpdateMailboxIntent(current.Id, changes.Any(c => c.Field == "DisplayName") ? current.DisplayName : null, current.ResourceCapacity, current.ResourceCapacity is null)
                : null,
            Invalidate = cache => cache.InvalidateInventory(),
        };
    }

    // ------------------------------------------------------------------------------------------------ Helpers

    private static PlanTarget Blocked(RoomMailbox room, string operationType, string label, string message) => new()
    {
        TargetId = room.Id,
        Kind = TargetKind.Room,
        TargetName = room.DisplayName,
        OperationType = operationType,
        OperationLabel = label,
        Issues = [new ValidationIssue("target", message, IssueLevel.Error)],
    };

    private static PlanTarget BlockedTarget(string id, TargetKind kind, string name, string operationType, string label, string message) => new()
    {
        TargetId = id,
        Kind = kind,
        TargetName = name,
        OperationType = operationType,
        OperationLabel = label,
        Issues = [new ValidationIssue("target", message, IssueLevel.Error)],
    };

    private static string? JsonString(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString();
}
