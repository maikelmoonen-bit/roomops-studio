using System.Text.Json;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.RoomLists;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.Templates;
using RoomOps.Domain.Validation;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Domain.Tests;

public class RecipientNamingTests
{
    [Theory]
    [InlineData("room.3.12", true)]
    [InlineData("ehv-3.12", true)]
    [InlineData(".room", false)]
    [InlineData("room..12", false)]
    [InlineData("room 12", false)]
    [InlineData("kamer(3)", false)]
    public void Alias_rules(string alias, bool valid) =>
        Assert.Equal(valid, !RecipientNaming.ValidateAlias(alias).Any(i => i.Level == IssueLevel.Error));

    [Theory]
    [InlineData("room.3.12@contoso.example", true)]
    [InlineData("room@contoso", false)]
    [InlineData("@contoso.example", false)]
    [InlineData("room@-bad.example", false)]
    public void Smtp_rules(string address, bool valid) =>
        Assert.Equal(valid, !RecipientNaming.ValidateSmtpAddress(address).Any(i => i.Level == IssueLevel.Error));

    [Fact]
    public void Alias_suggestion_strips_accents_and_separators() =>
        Assert.Equal("meeting.room.3.12.eindhoven.cafe", RecipientNaming.SuggestAlias("Meeting Room 3.12 – Eindhoven Café"));
}

public class WorkingHoursTests
{
    [Theory]
    [InlineData("Weekdays", WorkDays.Weekdays)]
    [InlineData("Monday, Tuesday", WorkDays.Monday | WorkDays.Tuesday)]
    [InlineData("Weekdays, Saturday", WorkDays.Weekdays | WorkDays.Saturday)]
    [InlineData("None", WorkDays.None)]
    [InlineData("AllDays", WorkDays.AllDays)]
    public void Work_days_parse(string text, WorkDays expected) => Assert.Equal(expected, WorkingHoursConfig.ParseWorkDays(text));

    [Fact]
    public void Work_days_format_uses_named_combinations_when_exact()
    {
        Assert.Equal("Weekdays", WorkingHoursConfig.FormatWorkDays(WorkDays.Weekdays));
        Assert.Equal("Monday,Wednesday,Friday", WorkingHoursConfig.FormatWorkDays(WorkDays.Monday | WorkDays.Wednesday | WorkDays.Friday));
    }

    [Fact]
    public void Day_descriptions_collapse_runs()
    {
        Assert.Equal("Monday–Thursday", WorkingHoursConfig.DescribeDays(WorkDays.Monday | WorkDays.Tuesday | WorkDays.Wednesday | WorkDays.Thursday));
        Assert.Equal("Monday, Wednesday", WorkingHoursConfig.DescribeDays(WorkDays.Monday | WorkDays.Wednesday));
    }

    [Fact]
    public void End_before_start_is_rejected()
    {
        var issues = WorkingHoursValidator.Validate(new WorkingHoursConfig(WorkDays.Weekdays, new TimeOnly(18, 0), new TimeOnly(8, 0), "UTC"), _ => true);
        Assert.Contains(issues, i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Unknown_time_zone_is_rejected()
    {
        var issues = WorkingHoursValidator.Validate(new WorkingHoursConfig(WorkDays.Weekdays, new TimeOnly(8, 0), new TimeOnly(17, 0), "Mars Standard Time"), TimeZoneCatalog.IsKnown);
        Assert.Contains(issues, i => i is { Field: "timeZone", Level: IssueLevel.Error });
    }

    [Fact]
    public void Catalog_uses_windows_time_zone_names_on_every_platform()
    {
        Assert.True(TimeZoneCatalog.IsKnown("W. Europe Standard Time"));
        Assert.True(TimeZoneCatalog.IsKnown("Pacific Standard Time"));
        var europe = TimeZoneCatalog.Find("W. Europe Standard Time")!;
        Assert.Equal(60, europe.BaseOffsetMinutes);
        Assert.True(europe.SupportsDaylightSaving);
        Assert.NotNull(europe.NextTransitionUtc);
    }
}

public class PlaceHierarchyTests
{
    [Theory]
    [InlineData(PlaceType.Floor, PlaceType.Building, true)]
    [InlineData(PlaceType.Section, PlaceType.Floor, true)]
    [InlineData(PlaceType.Room, PlaceType.Floor, true)]
    [InlineData(PlaceType.Room, PlaceType.Section, true)]
    [InlineData(PlaceType.Room, PlaceType.Building, false)]
    [InlineData(PlaceType.Section, PlaceType.Building, false)]
    [InlineData(PlaceType.Desk, PlaceType.Floor, false)]
    [InlineData(PlaceType.Workspace, PlaceType.Section, true)]
    public void Documented_parent_rules(PlaceType child, PlaceType parent, bool allowed) =>
        Assert.Equal(allowed, PlaceHierarchy.CanParent(child, parent));

    [Fact]
    public void Ancestors_and_cycles()
    {
        var building = new Place { PlaceId = "b", Type = PlaceType.Building, DisplayName = "HQ" };
        var floor = new Place { PlaceId = "f", Type = PlaceType.Floor, DisplayName = "Floor 3", ParentId = "b" };
        var section = new Place { PlaceId = "s", Type = PlaceType.Section, DisplayName = "North", ParentId = "f" };
        var byId = new[] { building, floor, section }.ToDictionary(p => p.PlaceId);
        Assert.Equal(["HQ", "Floor 3"], PlaceHierarchy.Ancestors(section, byId).Select(p => p.DisplayName));
        Assert.True(PlaceHierarchy.WouldCreateCycle("f", "s", byId));
        Assert.False(PlaceHierarchy.WouldCreateCycle("s", "f", byId));
    }
}

public class RoomListRulesTests
{
    private static ConvertibleGroup Group(string type = "MailUniversalDistributionGroup") => new("g", "Group", "g@x.example", type, 2);

    [Fact]
    public void Group_with_only_rooms_can_be_converted()
    {
        var members = new[] { new RoomListMember("1", "A", "a@x.example", "RoomMailbox"), new RoomListMember("2", "B", "b@x.example", "RoomMailbox") };
        Assert.True(RoomListRules.AssessConversion(Group(), members).CanConvert);
    }

    [Fact]
    public void Mixed_or_security_groups_are_not_offered_for_conversion()
    {
        var mixed = new[] { new RoomListMember("1", "A", "a@x.example", "RoomMailbox"), new RoomListMember("2", "User", "u@x.example", "UserMailbox") };
        var assessment = RoomListRules.AssessConversion(Group(), mixed);
        Assert.False(assessment.CanConvert);
        Assert.Single(assessment.NonRoomMembers);
        Assert.False(RoomListRules.AssessConversion(Group("MailUniversalSecurityGroup"), [mixed[0]]).CanConvert);
        Assert.False(RoomListRules.AssessConversion(Group(), []).CanConvert);
    }
}

public class TemplateTests
{
    private static ConfigurationTemplate Template(params (string, object)[] fields) => new()
    {
        Id = "t",
        Name = "Test",
        BookingFields = fields.ToDictionary(f => f.Item1, f => JsonSerializer.SerializeToElement(f.Item2)),
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Templates_may_not_contain_recipient_lists_or_unknown_fields()
    {
        Assert.Contains(TemplateRules.Validate(Template(("ResourceDelegates", new[] { "a@x.example" }))), i => i.Level == IssueLevel.Error);
        Assert.Contains(TemplateRules.Validate(Template(("NotARealSetting", true))), i => i.Level == IssueLevel.Error);
        Assert.Contains(TemplateRules.Validate(Template(("BookingWindowInDays", 5000))), i => i.Level == IssueLevel.Error);
        Assert.DoesNotContain(TemplateRules.Validate(Template(("BookingWindowInDays", 90))), i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Drift_respects_exceptions()
    {
        var template = Template(("BookingWindowInDays", 90), ("MaximumDurationInMinutes", 120));
        var current = BookingPolicy.ExchangeDefaults with { BookingWindowInDays = 180, MaximumDurationInMinutes = 120 };
        Assert.Equal(["BookingWindowInDays"], TemplateRules.Drift(template, current, new HashSet<string>()));
        Assert.Empty(TemplateRules.Drift(template, current, new HashSet<string> { "BookingWindowInDays" }));
    }

    [Fact]
    public void Example_templates_are_valid() =>
        Assert.All(TemplateRules.Examples(DateTimeOffset.UtcNow), t => Assert.DoesNotContain(TemplateRules.Validate(t), i => i.Level == IssueLevel.Error));
}

public class ValidationEngineTests
{
    private static RoomMailbox Room(string id, int? capacity = 8) => new() { Id = id, DisplayName = $"Room {id}", Alias = id, PrimarySmtpAddress = $"{id}@x.example", ResourceCapacity = capacity };

    private static ValidationInput Input(params RoomFacts[] rooms) => new() { Rooms = rooms, RoomLists = [], Baseline = new ValidationBaseline() };

    [Fact]
    public void Approval_without_delegates_is_critical()
    {
        var facts = new RoomFacts { Mailbox = Room("a"), Booking = BookingPolicy.ExchangeDefaults with { AllBookInPolicy = false, AllRequestInPolicy = true }, RoomListIds = ["l"] };
        var finding = Assert.Single(ValidationEngine.Run(Input(facts)), f => f.RuleId == "BOOK-003");
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Equal(RuleCategory.Technical, finding.Category);
    }

    [Fact]
    public void Rooms_without_loaded_booking_data_are_unable_to_verify()
    {
        var findings = ValidationEngine.Run(Input(new RoomFacts { Mailbox = Room("a"), RoomListIds = ["l"] }));
        Assert.Contains(findings, f => f is { RuleId: "SCAN-001", Severity: FindingSeverity.UnableToVerify });
    }

    [Fact]
    public void Baseline_rules_can_be_disabled_or_reclassified_but_technical_rules_cannot()
    {
        var facts = new RoomFacts { Mailbox = Room("a", capacity: null), Booking = BookingPolicy.ExchangeDefaults with { AutomateProcessing = AutomateProcessing.None } };
        var baseline = new ValidationBaseline
        {
            Rules = new Dictionary<string, RuleSetting>
            {
                ["ORG-002"] = new(false, null),
                ["ORG-001"] = new(true, FindingSeverity.Warning),
                ["BOOK-001"] = new(false, FindingSeverity.Informational),
            },
        };
        var findings = ValidationEngine.Run(Input(facts) with { Baseline = baseline });
        Assert.DoesNotContain(findings, f => f.RuleId == "ORG-002");
        Assert.Equal(FindingSeverity.Warning, findings.Single(f => f.RuleId == "ORG-001").Severity);
        Assert.Equal(FindingSeverity.Warning, findings.Single(f => f.RuleId == "BOOK-001").Severity);
    }

    [Fact]
    public void Duplicate_names_and_unexpected_room_list_members_are_found()
    {
        var a = new RoomFacts { Mailbox = Room("a") with { DisplayName = "Huddle" }, Booking = BookingPolicy.ExchangeDefaults };
        var b = new RoomFacts { Mailbox = Room("b") with { DisplayName = "huddle " }, Booking = BookingPolicy.ExchangeDefaults };
        var list = new RoomList
        {
            Id = "l", Name = "l", DisplayName = "List", PrimarySmtpAddress = "l@x.example", MembersLoaded = true,
            Members = [new RoomListMember("u", "User", "u@x.example", "UserMailbox")],
        };
        var findings = ValidationEngine.Run(Input(a, b) with { RoomLists = [list] });
        Assert.Equal(2, findings.Count(f => f.RuleId == "ORG-006"));
        Assert.Contains(findings, f => f.RuleId == "LIST-001" && f.RemediationId == "roomList.removeMember");
    }
}
