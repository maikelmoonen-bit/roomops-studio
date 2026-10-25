using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.Validation;

namespace RoomOps.Domain.Tests;

/// <summary>The interface language is process-wide, so these tests run on their own.</summary>
[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public sealed class LanguageCollection;

[Collection(nameof(LanguageCollection))]
public sealed class LocalizationTests : IDisposable
{
    public LocalizationTests() => Text.Language = "nl";

    public void Dispose() => Text.Language = "en";

    [Fact]
    public void Policy_explanation_is_written_in_dutch()
    {
        var policy = BookingPolicy.ExchangeDefaults with { BookingWindowInDays = 90, MaximumDurationInMinutes = 120 };
        var explanation = BookingPolicyExplainer.Explain(policy, "EHV 3.12");
        Assert.Equal("EHV 3.12 accepteert automatisch boekingsaanvragen van bevoegde gebruikers, mits de vergadering niet langer duurt dan 2 uur, deze binnen 90 dagen begint en er geen planningsconflict is.", explanation.Summary);
        Assert.Equal("Iedereen", explanation.ModeLabel);
    }

    [Fact]
    public void Validation_messages_and_field_labels_follow_the_language()
    {
        var policy = BookingPolicy.ExchangeDefaults with { AllBookInPolicy = false, AllRequestInPolicy = true };
        var issue = Assert.Single(BookingPolicyValidator.Validate(policy), i => i.Level == IssueLevel.Error);
        Assert.StartsWith("Aanvragen zijn ingesteld om goedkeuring", issue.Message, StringComparison.Ordinal);
        Assert.Equal("Boekingsvenster", BookingPolicyFields.Get("BookingWindowInDays").Label);

        Text.Language = "en";
        Assert.Equal("Booking window", BookingPolicyFields.Get("BookingWindowInDays").Label);
    }

    [Fact]
    public void Access_models_and_rules_are_named_in_the_current_language()
    {
        Assert.Equal("Alleen geselecteerde gebruikers – automatisch boeken", BookingPresets.Find("restricted")!.Name);
        Assert.Contains(ValidationEngine.RuleCatalog, r => r is { Id: "BOOK-003", Title: "Goedkeuring vereist maar geen gemachtigden" });

        Text.Language = "en";
        Assert.Equal("Selected users only – Automatic booking", BookingPresets.Find("restricted")!.Name);
    }

    [Fact]
    public void Recipient_validation_speaks_dutch()
    {
        var issues = RecipientNaming.ValidateSmtpAddress("not-an-address", "address");
        Assert.Contains(issues, i => i.Message == "Voer een volledig e-mailadres in (alias@domein).");
    }
}
