using System.Text.Json;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Domain.Tests;

public class BookingPolicyFieldsTests
{
    private static RecipientRef R(string smtp) => RecipientRef.FromSmtp(smtp);

    [Fact]
    public void Descriptor_table_covers_every_policy_property()
    {
        var properties = typeof(BookingPolicy).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(p => p.Name).Where(n => n != "EqualityContract").Order();
        Assert.Equal(properties, BookingPolicyFields.All.Select(f => f.Name).Order());
    }

    [Fact]
    public void Changed_fields_reports_only_differences()
    {
        var current = BookingPolicy.ExchangeDefaults;
        var desired = current with { BookingWindowInDays = 90, MaximumDurationInMinutes = 120 };
        Assert.Equal(["BookingWindowInDays", "MaximumDurationInMinutes"], BookingPolicyFields.ChangedFields(current, desired).Select(f => f.Name).Order());
    }

    [Fact]
    public void Recipient_lists_compare_by_address_regardless_of_order_and_case()
    {
        var a = BookingPolicy.ExchangeDefaults with { ResourceDelegates = [R("a@x.example"), R("B@x.example")] };
        var b = BookingPolicy.ExchangeDefaults with { ResourceDelegates = [R("b@x.example"), R("a@X.example")] };
        Assert.Empty(BookingPolicyFields.ChangedFields(a, b));
    }

    [Fact]
    public void Empty_additional_response_equals_null()
    {
        var a = BookingPolicy.ExchangeDefaults with { AdditionalResponse = "" };
        Assert.Empty(BookingPolicyFields.ChangedFields(a, BookingPolicy.ExchangeDefaults));
    }

    [Fact]
    public void Patch_applies_typed_values_and_honors_exclusions()
    {
        var patch = new Dictionary<string, JsonElement>
        {
            ["BookingWindowInDays"] = JsonSerializer.SerializeToElement(30),
            ["AutomateProcessing"] = JsonSerializer.SerializeToElement("AutoUpdate"),
            ["DeleteSubject"] = JsonSerializer.SerializeToElement(false),
        };
        var result = BookingPolicyFields.ApplyPatch(BookingPolicy.ExchangeDefaults, patch, new HashSet<string> { "DeleteSubject" });
        Assert.Equal(30, result.BookingWindowInDays);
        Assert.Equal(AutomateProcessing.AutoUpdate, result.AutomateProcessing);
        Assert.True(result.DeleteSubject);
    }

    [Theory]
    [InlineData("BookingWindowInDays", "\"ninety\"")]
    [InlineData("AllowConflicts", "1")]
    [InlineData("AutomateProcessing", "\"Sometimes\"")]
    public void Patch_rejects_wrongly_typed_values(string field, string json)
    {
        var patch = new Dictionary<string, JsonElement> { [field] = JsonDocument.Parse(json).RootElement };
        Assert.Throws<FormatException>(() => BookingPolicyFields.ApplyPatch(BookingPolicy.ExchangeDefaults, patch));
    }

    [Fact]
    public void Parameter_values_are_primitive_and_enum_names()
    {
        var policy = BookingPolicy.ExchangeDefaults with { AutomateProcessing = AutomateProcessing.AutoAccept, ResourceDelegates = [R("d@x.example")] };
        Assert.Equal("AutoAccept", BookingPolicyFields.Get("AutomateProcessing").ToParameterValue(policy));
        Assert.Equal(new[] { "d@x.example" }, BookingPolicyFields.Get("ResourceDelegates").ToParameterValue(policy));
    }

    [Fact]
    public void Duration_formatting_is_human_friendly()
    {
        Assert.Equal("2 hours", DurationText.Minutes(120));
        Assert.Equal("1 h 30 min", DurationText.Minutes(90));
        Assert.Equal("45 min", DurationText.Minutes(45));
        Assert.Equal("Unlimited", BookingPolicyFields.Get("MaximumDurationInMinutes").Format(BookingPolicy.ExchangeDefaults with { MaximumDurationInMinutes = 0 }));
    }
}

public class BookingPolicyValidatorTests
{
    private static RecipientRef R(string smtp) => RecipientRef.FromSmtp(smtp);

    [Fact]
    public void Out_of_range_booking_window_is_an_error()
    {
        var issues = BookingPolicyValidator.Validate(BookingPolicy.ExchangeDefaults with { BookingWindowInDays = 1081 });
        Assert.Contains(issues, i => i is { Field: "BookingWindowInDays", Level: IssueLevel.Error });
    }

    [Fact]
    public void Conflict_percentage_above_100_is_an_error()
    {
        Assert.Contains(BookingPolicyValidator.Validate(BookingPolicy.ExchangeDefaults with { ConflictPercentageAllowed = 101 }), i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Minimum_above_maximum_is_an_error()
    {
        var issues = BookingPolicyValidator.Validate(BookingPolicy.ExchangeDefaults with { MinimumDurationInMinutes = 120, MaximumDurationInMinutes = 60 });
        Assert.Contains(issues, i => i is { Field: "MinimumDurationInMinutes", Level: IssueLevel.Error });
    }

    [Fact]
    public void Delegate_approval_without_delegates_is_an_error()
    {
        var policy = BookingPolicy.ExchangeDefaults with { AllBookInPolicy = false, AllRequestInPolicy = true };
        Assert.Contains(BookingPolicyValidator.Validate(policy), i => i is { Field: "ResourceDelegates", Level: IssueLevel.Error });
    }

    [Fact]
    public void Delegate_approval_with_delegates_is_valid()
    {
        var policy = BookingPolicy.ExchangeDefaults with { AllBookInPolicy = false, AllRequestInPolicy = true, ResourceDelegates = [R("d@x.example")] };
        Assert.DoesNotContain(BookingPolicyValidator.Validate(policy), i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Dynamic_groups_are_rejected_in_recipient_lists()
    {
        var dynamic = RecipientRef.FromSmtp("all@x.example", "All", "DynamicDistributionGroup");
        var policy = BookingPolicy.ExchangeDefaults with { AllBookInPolicy = false, BookInPolicy = [dynamic] };
        Assert.Contains(BookingPolicyValidator.Validate(policy), i => i is { Field: "BookInPolicy", Level: IssueLevel.Error });
    }

    [Fact]
    public void Unresolved_recipients_are_errors()
    {
        var policy = BookingPolicy.ExchangeDefaults with { ResourceDelegates = [new RecipientRef { Identity = "gone", Resolution = RecipientResolution.Unresolved }] };
        Assert.Contains(BookingPolicyValidator.Validate(policy), i => i is { Field: "ResourceDelegates", Level: IssueLevel.Error });
    }

    [Fact]
    public void Working_hours_restriction_without_work_days_is_an_error()
    {
        var hours = new WorkingHoursConfig(WorkDays.None, new TimeOnly(8, 0), new TimeOnly(17, 0), "UTC");
        var issues = BookingPolicyValidator.Validate(BookingPolicy.ExchangeDefaults with { ScheduleOnlyDuringWorkHours = true }, hours);
        Assert.Contains(issues, i => i is { Field: "ScheduleOnlyDuringWorkHours", Level: IssueLevel.Error });
    }

    [Fact]
    public void AutoUpdate_with_delegates_warns_about_documented_behavior()
    {
        var policy = BookingPolicy.ExchangeDefaults with { AutomateProcessing = AutomateProcessing.AutoUpdate, ResourceDelegates = [R("d@x.example")] };
        Assert.Contains(BookingPolicyValidator.Validate(policy), i => i is { Field: "AutomateProcessing", Level: IssueLevel.Warning });
    }
}

public class BookingPresetTests
{
    private static RecipientRef R(string smtp) => RecipientRef.FromSmtp(smtp);

    [Fact]
    public void Presets_produce_their_documented_mode()
    {
        BookingPolicy Apply(string id, BookingPolicy start) => BookingPolicyFields.ApplyPatch(start, BookingPresets.Find(id)!.Fields);
        var fromRestricted = BookingPolicy.ExchangeDefaults with { AllBookInPolicy = false, BookInPolicy = [R("g@x.example")] };

        Assert.Equal(BookingMode.Automatic, BookingModeClassifier.Classify(Apply("automatic", fromRestricted)));
        Assert.Equal(BookingMode.DelegateApproval, BookingModeClassifier.Classify(Apply("delegate-approval", BookingPolicy.ExchangeDefaults with { ResourceDelegates = [R("d@x.example")] })));
        Assert.Equal(BookingMode.Restricted, BookingModeClassifier.Classify(Apply("restricted", BookingPolicy.ExchangeDefaults with { BookInPolicy = [R("g@x.example")] })));
    }

    [Fact]
    public void Presets_set_parameters_explicitly_rather_than_a_single_flag()
    {
        Assert.All(BookingPresets.All, p => Assert.True(p.Fields.Count >= 3));
        Assert.Contains("ResourceDelegates", BookingPresets.Find("delegate-approval")!.RequiredInputs);
        Assert.Equal(["BookInPolicy", "ResourceDelegates"], BookingPresets.Find("hybrid")!.RequiredInputs);
        Assert.Equal(["automatic", "restricted", "delegate-approval", "hybrid"], BookingPresets.All.Select(p => p.Id));
    }

    private static BookingPolicy Apply(string id, BookingPolicy start) => BookingPolicyFields.ApplyPatch(start, BookingPresets.Find(id)!.Fields);

    /// <summary>A room configured with every permission list filled and a non-default booking limit.</summary>
    private static readonly BookingPolicy Busy = BookingPolicy.ExchangeDefaults with
    {
        BookingWindowInDays = 365,
        MaximumDurationInMinutes = 120,
        DeleteSubject = false,
        AllBookInPolicy = false,
        AllRequestOutOfPolicy = true,
        BookInPolicy = [R("old-booker@x.example")],
        RequestInPolicy = [R("requester@x.example")],
        RequestOutOfPolicy = [R("vip@x.example")],
        ResourceDelegates = [R("delegate@x.example")],
        ForwardRequestsToDelegates = false,
        TentativePendingApproval = false,
    };

    [Theory]
    [InlineData("automatic")]
    [InlineData("restricted")]
    [InlineData("delegate-approval")]
    [InlineData("hybrid")]
    public void Every_model_is_recognized_again_after_it_is_applied(string id)
    {
        var applied = Apply(id, Busy);
        Assert.Equal(id, BookingPresets.Detect(applied)?.Id);
        Assert.Equal(BookingPresets.Find(id)!.Mode, BookingModeClassifier.Classify(applied));
    }

    [Fact]
    public void Lists_a_model_does_not_use_are_cleared_and_unrelated_settings_are_kept()
    {
        var everyone = Apply("automatic", Busy);
        Assert.Empty(everyone.BookInPolicy);
        Assert.Empty(everyone.RequestInPolicy);
        Assert.Empty(everyone.RequestOutOfPolicy);
        Assert.Empty(everyone.ResourceDelegates);
        Assert.False(everyone.AllRequestOutOfPolicy);

        var approval = Apply("delegate-approval", Busy);
        Assert.Empty(approval.BookInPolicy);
        Assert.Equal(["delegate@x.example"], approval.ResourceDelegates.Select(r => r.PrimarySmtpAddress));
        Assert.True(approval.ForwardRequestsToDelegates);
        Assert.True(approval.TentativePendingApproval);

        foreach (var preset in BookingPresets.All)
        {
            var result = Apply(preset.Id, Busy);
            Assert.Equal(365, result.BookingWindowInDays);
            Assert.Equal(120, result.MaximumDurationInMinutes);
            Assert.False(result.DeleteSubject);
        }
    }

    [Fact]
    public void Only_five_selected_users_can_book_and_everyone_else_is_denied()
    {
        var five = Enumerable.Range(1, 5).Select(i => R($"user{i}@x.example")).ToList();
        var policy = Apply("restricted", BookingPolicy.ExchangeDefaults with { ResourceDelegates = [R("delegate@x.example")] }) with { BookInPolicy = five };

        Assert.Equal("restricted", BookingPresets.Detect(policy)?.Id);
        Assert.Empty(policy.ResourceDelegates);
        Assert.DoesNotContain(BookingPolicyValidator.Validate(policy), i => i.Level != IssueLevel.Info);

        var request = new BookingScenario { RequestedAt = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero), StartLocal = new DateTime(2026, 10, 20, 10, 0, 0) };
        Assert.Equal(SimulatedOutcome.Accepted, BookingSimulator.Evaluate(policy, null, null, request with { InBookInPolicy = ListMembership.ListedDirectly }).Outcome);
        Assert.Equal(SimulatedOutcome.Declined, BookingSimulator.Evaluate(policy, null, null, request).Outcome);
    }

    [Fact]
    public void Exchange_defaults_are_the_everyone_model()
    {
        Assert.Equal("automatic", BookingPresets.Detect(BookingPolicy.ExchangeDefaults)?.Id);
    }

    [Theory]
    [InlineData("RequestOutOfPolicy")]
    [InlineData("ResourceDelegates")]
    [InlineData("Reserved")]
    public void Deviating_configurations_are_custom(string deviation)
    {
        var policy = deviation switch
        {
            "RequestOutOfPolicy" => Apply("restricted", BookingPolicy.ExchangeDefaults with { BookInPolicy = [R("a@x.example")] }) with { RequestOutOfPolicy = [R("b@x.example")] },
            "ResourceDelegates" => BookingPolicy.ExchangeDefaults with { ResourceDelegates = [R("d@x.example")] },
            _ => BookingPolicy.ExchangeDefaults with { BookingType = ResourceBookingType.Reserved },
        };
        Assert.Null(BookingPresets.Detect(policy));
    }

    [Fact]
    public void Approval_models_need_requests_routed_to_delegates()
    {
        var approval = Apply("delegate-approval", BookingPolicy.ExchangeDefaults with { ResourceDelegates = [R("d@x.example")] });
        Assert.Null(BookingPresets.Detect(approval with { ForwardRequestsToDelegates = false }));
        Assert.Null(BookingPresets.Detect(approval with { ResourceDelegates = [] }));
    }
}

public class ExplainerTests
{
    [Fact]
    public void Summary_reflects_window_duration_and_working_hours()
    {
        var policy = BookingPolicy.ExchangeDefaults with { BookingWindowInDays = 90, MaximumDurationInMinutes = 120, ScheduleOnlyDuringWorkHours = true };
        var explanation = BookingPolicyExplainer.Explain(policy, "Meeting Room Eindhoven 3.12");
        Assert.Equal("Meeting Room Eindhoven 3.12 automatically accepts booking requests from eligible users during configured working hours, provided the meeting does not exceed 2 hours, it starts within 90 days and there is no scheduling conflict.", explanation.Summary);
        Assert.Equal(BookingMode.Automatic, explanation.Mode);
    }

    [Fact]
    public void Disabled_processing_is_explained_without_claims_of_acceptance()
    {
        var explanation = BookingPolicyExplainer.Explain(BookingPolicy.ExchangeDefaults with { AutomateProcessing = AutomateProcessing.None }, "Room");
        Assert.DoesNotContain("accepts", explanation.Summary, StringComparison.Ordinal);
        Assert.Equal(BookingMode.ProcessingDisabled, explanation.Mode);
    }

    [Fact]
    public void Delegate_approval_summary_names_delegates()
    {
        var policy = BookingPolicy.ExchangeDefaults with
        {
            AllBookInPolicy = false, AllRequestInPolicy = true,
            ResourceDelegates = [RecipientRef.FromSmtp("sanne@x.example", "Sanne de Vries")],
        };
        Assert.Contains("Sanne de Vries", BookingPolicyExplainer.Explain(policy, "Auditorium").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Privacy_statements_follow_settings()
    {
        var policy = BookingPolicy.ExchangeDefaults with { DeleteSubject = false, AddOrganizerToSubject = false };
        Assert.Contains(BookingPolicyExplainer.Explain(policy, "Room").Statements, s => s.Kind == StatementKind.Privacy && s.Text.Contains("subjects stay visible", StringComparison.Ordinal));
    }
}

public class SimulatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero); // Monday
    private static readonly WorkingHoursConfig Hours = new(WorkDays.Weekdays, new TimeOnly(8, 0), new TimeOnly(18, 0), "W. Europe Standard Time");

    private static BookingScenario Scenario(int daysAhead = 1, int hour = 10, int minutes = 60) => new()
    {
        RequestedAt = Now,
        StartLocal = Now.Date.AddDays(daysAhead).AddHours(hour),
        DurationMinutes = minutes,
    };

    [Fact]
    public void Automatic_room_accepts_in_policy_request()
    {
        var result = BookingSimulator.Evaluate(BookingPolicy.ExchangeDefaults, Hours, 8, Scenario());
        Assert.Equal(SimulatedOutcome.Accepted, result.Outcome);
        Assert.NotEmpty(result.Limitations);
    }

    [Fact]
    public void External_requests_are_declined_unless_processed()
    {
        var external = Scenario() with { Organizer = OrganizerScope.External };
        Assert.Equal(SimulatedOutcome.Declined, BookingSimulator.Evaluate(BookingPolicy.ExchangeDefaults, Hours, 8, external).Outcome);
        Assert.Equal(SimulatedOutcome.Accepted, BookingSimulator.Evaluate(BookingPolicy.ExchangeDefaults with { ProcessExternalMeetingMessages = true }, Hours, 8, external).Outcome);
    }

    [Fact]
    public void Requests_beyond_the_window_are_out_of_policy()
    {
        var policy = BookingPolicy.ExchangeDefaults with { BookingWindowInDays = 30 };
        var result = BookingSimulator.Evaluate(policy, Hours, 8, Scenario(daysAhead: 45));
        Assert.Equal(SimulatedOutcome.Declined, result.Outcome);
        Assert.Contains(result.Steps, s => s is { Parameter: "BookingWindowInDays", Passed: false, Certainty: StepCertainty.Deterministic });
    }

    [Fact]
    public void Out_of_policy_requests_are_forwarded_when_allowed()
    {
        var policy = BookingPolicy.ExchangeDefaults with
        {
            MaximumDurationInMinutes = 60, AllRequestOutOfPolicy = true, ResourceDelegates = [RecipientRef.FromSmtp("d@x.example")],
        };
        Assert.Equal(SimulatedOutcome.ForwardedForApproval, BookingSimulator.Evaluate(policy, Hours, 8, Scenario(minutes: 180)).Outcome);
    }

    [Fact]
    public void Requests_outside_working_hours_are_declined_when_restricted()
    {
        var policy = BookingPolicy.ExchangeDefaults with { ScheduleOnlyDuringWorkHours = true };
        Assert.Equal(SimulatedOutcome.Declined, BookingSimulator.Evaluate(policy, Hours, 8, Scenario(hour: 19)).Outcome);
        Assert.Equal(SimulatedOutcome.Accepted, BookingSimulator.Evaluate(policy, Hours, 8, Scenario(hour: 9)).Outcome);
    }

    [Theory]
    [InlineData(10, 1, 0, 0, SimulatedOutcome.Declined)]
    [InlineData(10, 1, 2, 20, SimulatedOutcome.AcceptedWithDeclinedOccurrences)]
    [InlineData(10, 3, 2, 50, SimulatedOutcome.Declined)]
    public void Recurring_conflicts_follow_thresholds(int occurrences, int conflicts, int maxInstances, int percentage, SimulatedOutcome expected)
    {
        var policy = BookingPolicy.ExchangeDefaults with { MaximumConflictInstances = maxInstances, ConflictPercentageAllowed = percentage };
        var scenario = Scenario() with { Recurring = true, Occurrences = occurrences, ConflictingOccurrences = conflicts };
        Assert.Equal(expected, BookingSimulator.Evaluate(policy, Hours, 8, scenario).Outcome);
    }

    [Fact]
    public void Restricted_room_declines_unlisted_and_accepts_listed_organizers()
    {
        var policy = BookingPolicy.ExchangeDefaults with { AllBookInPolicy = false, BookInPolicy = [RecipientRef.FromSmtp("ea@x.example")] };
        Assert.Equal(SimulatedOutcome.Declined, BookingSimulator.Evaluate(policy, Hours, 8, Scenario()).Outcome);
        var listed = BookingSimulator.Evaluate(policy, Hours, 8, Scenario() with { InBookInPolicy = ListMembership.MemberOfListedGroup });
        Assert.Equal(SimulatedOutcome.Accepted, listed.Outcome);
        Assert.Contains(listed.Assumptions, a => a.Contains("Nested group", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_processing_is_not_processed()
    {
        Assert.Equal(SimulatedOutcome.NotProcessed, BookingSimulator.Evaluate(BookingPolicy.ExchangeDefaults with { AutomateProcessing = AutomateProcessing.None }, Hours, 8, Scenario()).Outcome);
    }
}
