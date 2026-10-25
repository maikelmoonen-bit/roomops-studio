using System.Text.Json;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Domain.Templates;

/// <summary>
/// A reusable, sparse room configuration. Only the fields present in <see cref="BookingFields"/> (and working hours, if set)
/// are applied; every other setting on the room is left untouched.
/// </summary>
public sealed record ConfigurationTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, JsonElement> BookingFields { get; init; } = new Dictionary<string, JsonElement>();
    public WorkingHoursConfig? WorkingHours { get; init; }
    public int Version { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Illustrative starting points shipped with RoomOps. They are organizational examples, not Microsoft defaults.</summary>
    public bool IsExample { get; init; }
}

public sealed record TemplateAssignment(string TenantId, string RoomId, string TemplateId, IReadOnlyList<string> ExcludedFields, DateTimeOffset AssignedAt);

public static class TemplateRules
{
    public static IReadOnlyList<ValidationIssue> Validate(ConfigurationTemplate template)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            issues.Add(new("name", Text.L("A template name is required.", "Een sjabloonnaam is verplicht."), IssueLevel.Error));
        }
        if (template.BookingFields.Count == 0 && template.WorkingHours is null)
        {
            issues.Add(new("bookingFields", Text.L("The template doesn't contain any settings.", "Het sjabloon bevat geen instellingen."), IssueLevel.Error));
        }
        var probe = BookingPolicy.ExchangeDefaults;
        foreach (var (name, value) in template.BookingFields)
        {
            var field = BookingPolicyFields.Find(name);
            if (field is null)
            {
                issues.Add(new($"bookingFields.{name}", $"'{name}' is not a supported booking setting.", IssueLevel.Error));
                continue;
            }
            if (field.Kind == PolicyFieldKind.RecipientList)
            {
                issues.Add(new($"bookingFields.{name}", $"{field.Label} references specific recipients and can't be part of a reusable template.", IssueLevel.Error));
                continue;
            }
            try
            {
                probe = field.ApplyJson(probe, value);
            }
            catch (FormatException ex)
            {
                issues.Add(new($"bookingFields.{name}", ex.Message, IssueLevel.Error));
            }
        }
        foreach (var issue in BookingPolicyValidator.Validate(probe).Where(i => i.Level == IssueLevel.Error && template.BookingFields.ContainsKey(i.Field)))
        {
            issues.Add(issue with { Field = $"bookingFields.{issue.Field}" });
        }
        if (template.WorkingHours is { } wh)
        {
            issues.AddRange(WorkingHoursValidator.Validate(wh, TimeZoneCatalog.IsKnown).Where(i => i.Level == IssueLevel.Error).Select(i => i with { Field = $"workingHours.{i.Field}" }));
        }
        return issues;
    }

    /// <summary>Fields of a room's policy that differ from the template, honoring per-room exceptions.</summary>
    public static IReadOnlyList<string> Drift(ConfigurationTemplate template, BookingPolicy current, IReadOnlySet<string> excluded)
    {
        var desired = BookingPolicyFields.ApplyPatch(current, template.BookingFields, excluded);
        return BookingPolicyFields.ChangedFields(current, desired).Select(f => f.Name).ToList();
    }

    public static IReadOnlyList<ConfigurationTemplate> Examples(DateTimeOffset now)
    {
        static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);
        return
        [
            new()
            {
                Id = "example-standard",
                Name = "Standard meeting room",
                Description = "Automatic booking, 90-day booking window, two-hour maximum duration, recurring meetings allowed.",
                BookingFields = new Dictionary<string, JsonElement>
                {
                    ["AutomateProcessing"] = J("AutoAccept"),
                    ["AllBookInPolicy"] = J(true),
                    ["BookingWindowInDays"] = J(90),
                    ["MaximumDurationInMinutes"] = J(120),
                    ["AllowRecurringMeetings"] = J(true),
                },
                UpdatedAt = now,
                IsExample = true,
            },
            new()
            {
                Id = "example-executive",
                Name = "Executive meeting room",
                Description = "Delegate approval for all requests, subjects removed and organizer shown, private flag preserved.",
                BookingFields = new Dictionary<string, JsonElement>
                {
                    ["AutomateProcessing"] = J("AutoAccept"),
                    ["AllBookInPolicy"] = J(false),
                    ["AllRequestInPolicy"] = J(true),
                    ["ForwardRequestsToDelegates"] = J(true),
                    ["TentativePendingApproval"] = J(true),
                    ["DeleteSubject"] = J(true),
                    ["AddOrganizerToSubject"] = J(true),
                    ["RemovePrivateProperty"] = J(false),
                },
                UpdatedAt = now,
                IsExample = true,
            },
            new()
            {
                Id = "example-training",
                Name = "Training room",
                Description = "Full-day sessions, longer booking window, extended working hours, recurring bookings with limited conflicts.",
                BookingFields = new Dictionary<string, JsonElement>
                {
                    ["AutomateProcessing"] = J("AutoAccept"),
                    ["BookingWindowInDays"] = J(365),
                    ["MaximumDurationInMinutes"] = J(600),
                    ["AllowRecurringMeetings"] = J(true),
                    ["ConflictPercentageAllowed"] = J(10),
                    ["MaximumConflictInstances"] = J(2),
                    ["ScheduleOnlyDuringWorkHours"] = J(true),
                },
                WorkingHours = new WorkingHoursConfig(WorkDays.Weekdays, new TimeOnly(7, 30), new TimeOnly(19, 0), "W. Europe Standard Time"),
                UpdatedAt = now,
                IsExample = true,
            },
            new()
            {
                Id = "example-collaboration",
                Name = "Shared collaboration space",
                Description = "Automatic booking for short sessions, 30-day window, recurring meetings not allowed.",
                BookingFields = new Dictionary<string, JsonElement>
                {
                    ["AutomateProcessing"] = J("AutoAccept"),
                    ["AllBookInPolicy"] = J(true),
                    ["BookingWindowInDays"] = J(30),
                    ["MaximumDurationInMinutes"] = J(240),
                    ["AllowRecurringMeetings"] = J(false),
                },
                UpdatedAt = now,
                IsExample = true,
            },
        ];
    }
}
