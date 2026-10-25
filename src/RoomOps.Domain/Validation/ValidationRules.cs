using RoomOps.Domain.Common;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Places;
using RoomOps.Domain.RoomLists;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.Templates;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Domain.Validation;

public enum FindingSeverity
{
    Critical,
    Warning,
    Recommendation,
    Informational,
    UnableToVerify,
}

/// <summary>
/// Technical rules describe documented Exchange/Places behavior and have fixed severities.
/// Organizational baseline rules encode local conventions; administrators can disable them or change their severity.
/// </summary>
public enum RuleCategory
{
    Technical,
    OrganizationalBaseline,
}

public sealed record ValidationRuleInfo(string Id, string Title, RuleCategory Category, FindingSeverity DefaultSeverity, string Description, string Basis);

public sealed record Finding(
    string Id,
    string RuleId,
    string Title,
    FindingSeverity Severity,
    RuleCategory Category,
    string TargetKind,
    string TargetId,
    string TargetName,
    string Explanation,
    IReadOnlyDictionary<string, string> Evidence,
    string? RemediationId);

public sealed record RuleSetting(bool Enabled, FindingSeverity? Severity);

public sealed record PrivacyBaseline(bool? DeleteSubject, bool? AddOrganizerToSubject, bool? DeleteComments, bool? RemovePrivateProperty);

public sealed record ValidationBaseline
{
    public IReadOnlyDictionary<string, RuleSetting> Rules { get; init; } = new Dictionary<string, RuleSetting>();
    public IReadOnlyList<string> ExpectedTimeZones { get; init; } = [];
    public PrivacyBaseline Privacy { get; init; } = new(null, null, null, null);
}

public sealed record RoomFacts
{
    public required RoomMailbox Mailbox { get; init; }
    public BookingPolicy? Booking { get; init; }
    public WorkingHoursConfig? WorkingHours { get; init; }
    public Place? Place { get; init; }
    public IReadOnlyList<string> RoomListIds { get; init; } = [];
    public TemplateAssignment? TemplateAssignment { get; init; }
}

public sealed record ValidationInput
{
    public required IReadOnlyList<RoomFacts> Rooms { get; init; }
    public required IReadOnlyList<RoomList> RoomLists { get; init; }
    public required ValidationBaseline Baseline { get; init; }
    public bool PlacesAvailable { get; init; }
    public IReadOnlyDictionary<string, Place> PlacesById { get; init; } = new Dictionary<string, Place>();
    public IReadOnlyDictionary<string, ConfigurationTemplate> Templates { get; init; } = new Dictionary<string, ConfigurationTemplate>();
}

public static class ValidationEngine
{
    private sealed record Rule(ValidationRuleInfo Info, Func<ValidationInput, Rule, IEnumerable<Finding>> Evaluate);

    private static Finding RoomFinding(Rule rule, RoomFacts room, string explanation, IReadOnlyDictionary<string, string>? evidence = null, string? remediation = null) =>
        new($"{rule.Info.Id}:{room.Mailbox.Id}", rule.Info.Id, rule.Info.Title, rule.Info.DefaultSeverity, rule.Info.Category, "Room",
            room.Mailbox.Id, room.Mailbox.DisplayName, explanation, evidence ?? new Dictionary<string, string>(), remediation);

    private static Finding Unverifiable(Rule rule, RoomFacts room, string why) =>
        RoomFinding(rule, room, why) with { Severity = FindingSeverity.UnableToVerify };

    /// <summary>Built on access, so titles and explanations are produced in the current interface language.</summary>
    private static List<Rule> Rules =>
    [
        // ---------------- Technical: booking policy ----------------
        new(new("BOOK-001", Text.L("Calendar processing disabled", "Agendaverwerking uitgeschakeld"), RuleCategory.Technical, FindingSeverity.Warning,
                Text.L("AutomateProcessing is None, so neither attendant processes meeting requests.", "AutomateProcessing is None, dus geen van beide attendants verwerkt vergaderverzoeken."),
                Text.L("Set-CalendarProcessing: AutomateProcessing None disables the Calendar Attendant and the Resource Booking Attendant.", "Set-CalendarProcessing: AutomateProcessing None schakelt de Calendar Attendant en de Resource Booking Attendant uit.")),
            (input, rule) => input.Rooms.Where(r => r.Booking?.AutomateProcessing == AutomateProcessing.None)
                .Select(r => RoomFinding(rule, r, Text.L("Meeting requests to this room are not accepted or declined automatically.", "Vergaderverzoeken aan deze ruimte worden niet automatisch geaccepteerd of geweigerd."), new Dictionary<string, string> { ["AutomateProcessing"] = "None" }))),

        new(new("BOOK-002", Text.L("AutoUpdate on a room mailbox", "AutoUpdate op een ruimtepostvak"), RuleCategory.Technical, FindingSeverity.Warning,
                Text.L("AutomateProcessing is AutoUpdate; on room mailboxes delegates don't get requests.", "AutomateProcessing is AutoUpdate; bij ruimtepostvakken ontvangen gemachtigden geen aanvragen."),
                "Set-CalendarProcessing: \"If you set this value on a room mailbox, delegates don't get requests.\""),
            (input, rule) => input.Rooms.Where(r => r.Booking?.AutomateProcessing == AutomateProcessing.AutoUpdate)
                .Select(r => RoomFinding(rule, r, Text.L("Requests stay tentative and are never decided automatically. Mailboxes created before November 15, 2018 may still carry this default.", "Aanvragen blijven voorlopig en worden nooit automatisch beslist. Postvakken die vóór 15 november 2018 zijn gemaakt, kunnen deze standaardwaarde nog hebben."),
                    new Dictionary<string, string> { ["AutomateProcessing"] = "AutoUpdate" }, "booking.setAutoAccept"))),

        new(new("BOOK-003", Text.L("Approval required but no delegates", "Goedkeuring vereist maar geen gemachtigden"), RuleCategory.Technical, FindingSeverity.Critical,
                Text.L("Requests are routed for delegate approval but ResourceDelegates is empty.", "Aanvragen worden ter goedkeuring naar gemachtigden gestuurd, maar ResourceDelegates is leeg."),
                Text.L("Set-CalendarProcessing: AllRequestInPolicy/RequestInPolicy and out-of-policy requests require approval by a resource mailbox delegate.", "Set-CalendarProcessing: AllRequestInPolicy/RequestInPolicy en aanvragen buiten het beleid vereisen goedkeuring door een gemachtigde van het resourcepostvak.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is { AutomateProcessing: AutomateProcessing.AutoAccept } b && b.ResourceDelegates.Count == 0 &&
                    (!b.AllBookInPolicy && (b.AllRequestInPolicy || b.RequestInPolicy.Count > 0) || b.AllRequestOutOfPolicy || b.RequestOutOfPolicy.Count > 0))
                .Select(r => RoomFinding(rule, r, Text.L("Requests that need approval can't be approved because no booking delegates are assigned.", "Aanvragen die goedkeuring nodig hebben, kunnen niet worden goedgekeurd omdat er geen boekingsgemachtigden zijn toegewezen."),
                    new Dictionary<string, string> { ["ResourceDelegates"] = "(none)", ["AllRequestInPolicy"] = r.Booking!.AllRequestInPolicy.ToString() }))),

        new(new("BOOK-004", Text.L("Unresolved recipient references", "Niet-omgezette verwijzingen naar ontvangers"), RuleCategory.Technical, FindingSeverity.Critical,
                Text.L("A delegate or booking-permission entry no longer resolves to a recipient.", "Een gemachtigde of vermelding in de boekingsmachtigingen verwijst niet meer naar een ontvanger."),
                Text.L("Recipient-valued Set-CalendarProcessing parameters must reference existing recipients; unresolved entries cause updates to fail.", "Parameters van Set-CalendarProcessing met ontvangers moeten naar bestaande ontvangers verwijzen; niet-omgezette vermeldingen laten wijzigingen mislukken.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is not null).SelectMany(r =>
            {
                var b = r.Booking!;
                var unresolved = new[] { ("ResourceDelegates", b.ResourceDelegates), ("BookInPolicy", b.BookInPolicy), ("RequestInPolicy", b.RequestInPolicy), ("RequestOutOfPolicy", b.RequestOutOfPolicy) }
                    .SelectMany(t => t.Item2.Where(x => x.Resolution is RecipientResolution.Unresolved or RecipientResolution.Ambiguous).Select(x => (Field: t.Item1, Ref: x)))
                    .ToList();
                return unresolved.Count == 0
                    ? []
                    : new[]
                    {
                        RoomFinding(rule, r, Text.L($"{unresolved.Count} reference(s) can't be resolved: {string.Join(", ", unresolved.Select(u => $"{u.Ref.Identity} ({u.Field})"))}.", $"{unresolved.Count} verwijzing(en) kunnen niet worden omgezet: {string.Join(", ", unresolved.Select(u => $"{u.Ref.Identity} ({u.Field})"))}."),
                            unresolved.GroupBy(u => u.Field).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(u => u.Ref.Identity))), "delegates.removeUnresolved"),
                    };
            })),

        new(new("BOOK-005", Text.L("Delegates not receiving requests", "Gemachtigden ontvangen geen aanvragen"), RuleCategory.Technical, FindingSeverity.Warning,
                Text.L("Approval is configured with delegates, but ForwardRequestsToDelegates is disabled.", "Goedkeuring is ingesteld met gemachtigden, maar ForwardRequestsToDelegates is uitgeschakeld."),
                Text.L("Set-CalendarProcessing: requests are forwarded to delegates for approval only when ForwardRequestsToDelegates is True.", "Set-CalendarProcessing: aanvragen worden alleen ter goedkeuring naar gemachtigden doorgestuurd als ForwardRequestsToDelegates True is.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is { AutomateProcessing: AutomateProcessing.AutoAccept, ForwardRequestsToDelegates: false } b && b.ResourceDelegates.Count > 0 &&
                    (!b.AllBookInPolicy && (b.AllRequestInPolicy || b.RequestInPolicy.Count > 0)))
                .Select(r => RoomFinding(rule, r, Text.L("Delegates must find pending requests in the room calendar themselves.", "Gemachtigden moeten openstaande aanvragen zelf in de agenda van de ruimte zoeken."), null, "booking.enableForwarding"))),

        new(new("BOOK-006", Text.L("Nobody can book in policy", "Niemand kan binnen het beleid boeken"), RuleCategory.Technical, FindingSeverity.Warning,
                Text.L("AllBookInPolicy and AllRequestInPolicy are disabled and no recipients are listed.", "AllBookInPolicy en AllRequestInPolicy zijn uitgeschakeld en er zijn geen ontvangers vermeld."),
                Text.L("Set-CalendarProcessing: with no eligible organizers, in-policy requests are declined.", "Set-CalendarProcessing: zonder bevoegde organisatoren worden aanvragen binnen het beleid geweigerd.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is { AutomateProcessing: AutomateProcessing.AutoAccept, AllBookInPolicy: false, AllRequestInPolicy: false } b &&
                    b.BookInPolicy.Count == 0 && b.RequestInPolicy.Count == 0)
                .Select(r => RoomFinding(rule, r, Text.L("Every in-policy request will be declined because no organizer is permitted to book or request the room.", "Elke aanvraag binnen het beleid wordt geweigerd, omdat geen enkele organisator de ruimte mag boeken of aanvragen.")))),

        new(new("BOOK-007", Text.L("Settings without effect", "Instellingen zonder effect"), RuleCategory.Technical, FindingSeverity.Informational,
                Text.L("AutoAccept-only settings or conflict thresholds are set but cannot take effect.", "Instellingen die alleen met AutoAccept werken of conflictdrempels zijn ingesteld, maar kunnen geen effect hebben."),
                Text.L("Set-CalendarProcessing documents several parameters as used only with AutoAccept, and conflict thresholds as ignored when AllowConflicts is True.", "Set-CalendarProcessing documenteert dat verschillende parameters alleen met AutoAccept worden gebruikt en dat conflictdrempels worden genegeerd als AllowConflicts True is.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is not null).SelectMany(r =>
            {
                var b = r.Booking!;
                var notes = new List<string>();
                if (b.AllowConflicts && (b.ConflictPercentageAllowed > 0 || b.MaximumConflictInstances > 0)) notes.Add(Text.L("conflict thresholds are ignored because conflicts are allowed", "conflictdrempels worden genegeerd omdat conflicten zijn toegestaan"));
                if (b.MinimumDurationInMinutes > 0) notes.Add(Text.L("minimum duration only works on workspace mailboxes", "minimale duur werkt alleen bij werkplekpostvakken"));
                if (b.AutomateProcessing != AutomateProcessing.AutoAccept && b.AddAdditionalResponse) notes.Add(Text.L("the additional response is only sent with AutoAccept", "het aanvullende antwoord wordt alleen met AutoAccept verzonden"));
                return notes.Count == 0 ? [] : new[] { RoomFinding(rule, r, char.ToUpperInvariant(notes[0][0]) + string.Join("; ", notes)[1..] + ".") };
            })),

        new(new("BOOK-008", Text.L("Empty custom response", "Lege eigen antwoordtekst"), RuleCategory.Technical, FindingSeverity.Recommendation,
                Text.L("AddAdditionalResponse is enabled but AdditionalResponse is empty.", "AddAdditionalResponse is ingeschakeld, maar AdditionalResponse is leeg."),
                Text.L("Set-CalendarProcessing: AdditionalResponse is the text added when AddAdditionalResponse is True.", "Set-CalendarProcessing: AdditionalResponse is de tekst die wordt toegevoegd als AddAdditionalResponse True is.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is { AddAdditionalResponse: true } b && string.IsNullOrWhiteSpace(b.AdditionalResponse))
                .Select(r => RoomFinding(rule, r, Text.L("Responses will include no additional text; disable the option or add a message.", "Antwoorden bevatten geen aanvullende tekst; schakel de optie uit of voeg een bericht toe."), null, "booking.disableEmptyResponse"))),

        new(new("BOOK-009", Text.L("Working-hours restriction without work days", "Werktijdbeperking zonder werkdagen"), RuleCategory.Technical, FindingSeverity.Critical,
                Text.L("ScheduleOnlyDuringWorkHours is enabled while the mailbox has no work days.", "ScheduleOnlyDuringWorkHours is ingeschakeld terwijl het postvak geen werkdagen heeft."),
                Text.L("Set-CalendarProcessing ScheduleOnlyDuringWorkHours uses the WorkDays configured with Set-MailboxCalendarConfiguration.", "Set-CalendarProcessing ScheduleOnlyDuringWorkHours gebruikt de WorkDays die met Set-MailboxCalendarConfiguration zijn ingesteld.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is { ScheduleOnlyDuringWorkHours: true } && r.WorkingHours is { Days: WorkDays.None })
                .Select(r => RoomFinding(rule, r, Text.L("Every request will be declined because no day is a work day.", "Elke aanvraag wordt geweigerd, omdat geen enkele dag een werkdag is.")))),

        new(new("BOOK-010", Text.L("Capacity enforcement without capacity", "Capaciteit afdwingen zonder capaciteit"), RuleCategory.Technical, FindingSeverity.Warning,
                Text.L("EnforceCapacity is enabled but the room has no capacity.", "EnforceCapacity is ingeschakeld, maar de ruimte heeft geen capaciteit."),
                Text.L("Set-CalendarProcessing EnforceCapacity limits bookings to the room's capacity.", "Set-CalendarProcessing EnforceCapacity beperkt boekingen tot de capaciteit van de ruimte.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is { EnforceCapacity: true } && r.Mailbox.ResourceCapacity is null or <= 0)
                .Select(r => RoomFinding(rule, r, Text.L("Capacity enforcement has no reference value.", "Capaciteit afdwingen heeft geen referentiewaarde.")))),

        new(new("WH-001", Text.L("Invalid working-hours range", "Ongeldig bereik van werktijden"), RuleCategory.Technical, FindingSeverity.Warning,
                Text.L("The working-hours end time is not later than the start time.", "De eindtijd van de werktijden is niet later dan de begintijd."),
                Text.L("Exchange stores a single start and end time for all work days.", "Exchange slaat één begin- en eindtijd op voor alle werkdagen.")),
            (input, rule) => input.Rooms.Where(r => r.WorkingHours is { } wh && wh.End <= wh.Start)
                .Select(r => RoomFinding(rule, r, Text.L($"Working hours are {r.WorkingHours!.Start:HH\\:mm}–{r.WorkingHours.End:HH\\:mm}.", $"De werktijden zijn {r.WorkingHours!.Start:HH\\:mm}–{r.WorkingHours.End:HH\\:mm}.")))),

        // ---------------- Technical: room lists and Places ----------------
        new(new("LIST-001", Text.L("Unexpected room list member", "Onverwacht lid van ruimtelijst"), RuleCategory.Technical, FindingSeverity.Warning,
                Text.L("A room list contains a recipient that is not a room mailbox.", "Een ruimtelijst bevat een ontvanger die geen ruimtepostvak is."),
                Text.L("Microsoft Learn: room lists are special distribution groups that contain only room mailboxes.", "Microsoft Learn: ruimtelijsten zijn speciale distributiegroepen die alleen ruimtepostvakken bevatten.")),
            (input, rule) => input.RoomLists.Where(l => l.MembersLoaded).SelectMany(l => l.Members.Where(m => !m.IsRoomMailbox).Select(m =>
                new Finding($"{rule.Info.Id}:{l.Id}:{m.Id}", rule.Info.Id, rule.Info.Title, rule.Info.DefaultSeverity, rule.Info.Category, "RoomList", l.Id, l.DisplayName,
                    Text.L($"{m.DisplayName} ({m.RecipientTypeDetails}) is a member of {l.DisplayName}.", $"{m.DisplayName} ({m.RecipientTypeDetails}) is lid van {l.DisplayName}."),
                    new Dictionary<string, string> { ["Member"] = m.PrimarySmtpAddress, ["RecipientTypeDetails"] = m.RecipientTypeDetails }, "roomList.removeMember")))),

        new(new("PLACES-001", Text.L("Room parented to a building", "Ruimte direct onder een gebouw"), RuleCategory.Technical, FindingSeverity.Warning,
                Text.L("A room's Places parent is a building instead of a floor or section.", "De bovenliggende Places-locatie van een ruimte is een gebouw in plaats van een verdieping of sectie."),
                "Set-PlaceV3: \"A room should have a parent floor or section.\""),
            (input, rule) => input.PlacesAvailable
                ? input.Rooms.Where(r => r.Place?.ParentId is { } pid && input.PlacesById.TryGetValue(pid, out var parent) && parent.Type == PlaceType.Building)
                    .Select(r => RoomFinding(rule, r, Text.L("Assign the room to a floor or section so it appears correctly in Places Finder.", "Wijs de ruimte toe aan een verdieping of sectie, zodat die goed in Places Finder verschijnt.")))
                : []),

        new(new("PLACES-002", Text.L("Room not found in Microsoft Places", "Ruimte niet gevonden in Microsoft Places"), RuleCategory.Technical, FindingSeverity.Informational,
                Text.L("The room mailbox has no matching Places room object.", "Het ruimtepostvak heeft geen bijbehorend Places-ruimteobject."),
                Text.L("Places syncs room mailboxes into the directory; new mailboxes can take up to 24 hours to appear.", "Places synchroniseert ruimtepostvakken naar de adreslijst; het kan tot 24 uur duren voordat nieuwe postvakken verschijnen.")),
            (input, rule) => input.PlacesAvailable
                ? input.Rooms.Where(r => r.Place is null).Select(r => RoomFinding(rule, r,
                    r.Mailbox.WhenCreated is { } created && DateTimeOffset.UtcNow - created < TimeSpan.FromHours(24)
                        ? Text.L("The mailbox was created less than 24 hours ago; Places synchronization may still be pending.", "Het postvak is minder dan 24 uur geleden gemaakt; de synchronisatie met Places loopt mogelijk nog.")
                        : Text.L("No Places room references this mailbox's address.", "Geen enkele Places-ruimte verwijst naar het adres van dit postvak.")))
                : []),

        // ---------------- Organizational baseline ----------------
        new(new("ORG-001", Text.L("Capacity missing", "Capaciteit ontbreekt"), RuleCategory.OrganizationalBaseline, FindingSeverity.Recommendation,
                Text.L("The room mailbox has no ResourceCapacity.", "Het ruimtepostvak heeft geen ResourceCapacity."), Text.L("Organizational baseline: capacity helps people choose a suitable room.", "Organisatiebasislijn: capaciteit helpt mensen een geschikte ruimte te kiezen.")),
            (input, rule) => input.Rooms.Where(r => r.Mailbox.ResourceCapacity is null or <= 0)
                .Select(r => RoomFinding(rule, r, Text.L("Room Finder and Places can't filter this room by size.", "Ruimtezoeker en Places kunnen deze ruimte niet op grootte filteren.")))),

        new(new("ORG-002", Text.L("Not in any room list", "In geen enkele ruimtelijst"), RuleCategory.OrganizationalBaseline, FindingSeverity.Recommendation,
                Text.L("The room isn't a member of any room list.", "De ruimte is geen lid van een ruimtelijst."), Text.L("Organizational baseline: Outlook Room Finder browses rooms by room list.", "Organisatiebasislijn: Ruimtezoeker in Outlook bladert per ruimtelijst door ruimtes.")),
            (input, rule) => input.Rooms.Where(r => r.RoomListIds.Count == 0)
                .Select(r => RoomFinding(rule, r, Text.L("The room is not discoverable through Room Finder's room-list browsing.", "De ruimte is niet vindbaar via bladeren per ruimtelijst in Ruimtezoeker.")))),

        new(new("ORG-003", Text.L("No Places location", "Geen Places-locatie"), RuleCategory.OrganizationalBaseline, FindingSeverity.Recommendation,
                Text.L("The room has no parent floor or section in Microsoft Places.", "De ruimte heeft geen bovenliggende verdieping of sectie in Microsoft Places."), Text.L("Organizational baseline: Places Finder browses rooms by building and floor.", "Organisatiebasislijn: Places Finder bladert per gebouw en verdieping door ruimtes.")),
            (input, rule) => input.PlacesAvailable
                ? input.Rooms.Where(r => r.Place is not null && string.IsNullOrEmpty(r.Place.ParentId))
                    .Select(r => RoomFinding(rule, r, Text.L("Assign a floor or section so the room appears in its building in Places Finder.", "Wijs een verdieping of sectie toe, zodat de ruimte in haar gebouw verschijnt in Places Finder.")))
                : []),

        new(new("ORG-004", Text.L("Unexpected time zone", "Onverwachte tijdzone"), RuleCategory.OrganizationalBaseline, FindingSeverity.Warning,
                Text.L("Working hours use a time zone outside the organization's expected list.", "De werktijden gebruiken een tijdzone buiten de verwachte lijst van de organisatie."), Text.L("Organizational baseline: expected time zones are configured in Settings.", "Organisatiebasislijn: verwachte tijdzones stel je in bij Instellingen.")),
            (input, rule) => input.Baseline.ExpectedTimeZones.Count == 0
                ? []
                : input.Rooms.Where(r => r.WorkingHours is { } wh && !input.Baseline.ExpectedTimeZones.Contains(wh.TimeZoneId, StringComparer.OrdinalIgnoreCase))
                    .Select(r => RoomFinding(rule, r, Text.L($"Working hours use '{r.WorkingHours!.TimeZoneId}'. New room mailboxes default to Pacific Standard Time until changed.", $"De werktijden gebruiken '{r.WorkingHours!.TimeZoneId}'. Nieuwe ruimtepostvakken gebruiken standaard Pacific Standard Time totdat dit wordt gewijzigd."),
                        new Dictionary<string, string> { ["WorkingHoursTimeZone"] = r.WorkingHours.TimeZoneId }))),

        new(new("ORG-005", Text.L("Privacy baseline deviation", "Afwijking van de privacybasislijn"), RuleCategory.OrganizationalBaseline, FindingSeverity.Recommendation,
                Text.L("Calendar privacy settings differ from the approved baseline.", "De privacyinstellingen van de agenda wijken af van de goedgekeurde basislijn."), Text.L("Organizational baseline: privacy values are configured in Settings.", "Organisatiebasislijn: privacywaarden stel je in bij Instellingen.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is not null).SelectMany(r =>
            {
                var b = r.Booking!;
                var pb = input.Baseline.Privacy;
                var diffs = new Dictionary<string, string>();
                if (pb.DeleteSubject is { } ds && b.DeleteSubject != ds) diffs["DeleteSubject"] = Text.L($"{b.DeleteSubject} (baseline {ds})", $"{b.DeleteSubject} (basislijn {ds})");
                if (pb.AddOrganizerToSubject is { } ao && b.AddOrganizerToSubject != ao) diffs["AddOrganizerToSubject"] = Text.L($"{b.AddOrganizerToSubject} (baseline {ao})", $"{b.AddOrganizerToSubject} (basislijn {ao})");
                if (pb.DeleteComments is { } dc && b.DeleteComments != dc) diffs["DeleteComments"] = Text.L($"{b.DeleteComments} (baseline {dc})", $"{b.DeleteComments} (basislijn {dc})");
                if (pb.RemovePrivateProperty is { } rp && b.RemovePrivateProperty != rp) diffs["RemovePrivateProperty"] = Text.L($"{b.RemovePrivateProperty} (baseline {rp})", $"{b.RemovePrivateProperty} (basislijn {rp})");
                return diffs.Count == 0 ? [] : new[] { RoomFinding(rule, r, Text.L($"{diffs.Count} privacy setting(s) differ from the baseline.", $"{diffs.Count} privacyinstelling(en) wijken af van de basislijn."), diffs, "booking.applyPrivacyBaseline") };
            })),

        new(new("ORG-006", Text.L("Duplicate display name", "Dubbele weergavenaam"), RuleCategory.OrganizationalBaseline, FindingSeverity.Warning,
                Text.L("Several rooms share the same display name.", "Meerdere ruimtes hebben dezelfde weergavenaam."), Text.L("Organizational baseline: unique names avoid booking the wrong room.", "Organisatiebasislijn: unieke namen voorkomen dat de verkeerde ruimte wordt geboekt.")),
            (input, rule) => input.Rooms.GroupBy(r => r.Mailbox.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(r => RoomFinding(rule, r, Text.L($"{g.Count()} rooms are named '{g.Key}'.", $"{g.Count()} ruimtes heten '{g.Key}'."),
                    new Dictionary<string, string> { ["Rooms"] = string.Join(", ", g.Select(x => x.Mailbox.PrimarySmtpAddress)) })))),

        new(new("ORG-007", Text.L("Drift from assigned template", "Afwijking van toegewezen sjabloon"), RuleCategory.OrganizationalBaseline, FindingSeverity.Recommendation,
                Text.L("The room's booking policy differs from its assigned configuration template.", "Het boekingsbeleid van de ruimte wijkt af van het toegewezen configuratiesjabloon."), Text.L("Organizational baseline: templates assigned in RoomOps Studio.", "Organisatiebasislijn: sjablonen die in RoomOps Studio zijn toegewezen.")),
            (input, rule) => input.Rooms.Where(r => r.Booking is not null && r.TemplateAssignment is not null).SelectMany(r =>
            {
                if (!input.Templates.TryGetValue(r.TemplateAssignment!.TemplateId, out var template)) return [];
                var drift = TemplateRules.Drift(template, r.Booking!, r.TemplateAssignment.ExcludedFields.ToHashSet(StringComparer.OrdinalIgnoreCase));
                return drift.Count == 0 ? [] : new[] { RoomFinding(rule, r, Text.L($"{drift.Count} setting(s) differ from '{template.Name}': {string.Join(", ", drift)}.", $"{drift.Count} instelling(en) wijken af van '{template.Name}': {string.Join(", ", drift)}."), null, "template.reapply") };
            })),

        new(new("ORG-008", Text.L("Room in several room lists", "Ruimte in meerdere ruimtelijsten"), RuleCategory.OrganizationalBaseline, FindingSeverity.Informational,
                Text.L("The room is a member of more than one room list.", "De ruimte is lid van meer dan één ruimtelijst."), Text.L("Organizational baseline: one room list per building keeps Room Finder unambiguous.", "Organisatiebasislijn: één ruimtelijst per gebouw houdt Ruimtezoeker eenduidig.")),
            (input, rule) => input.Rooms.Where(r => r.RoomListIds.Count > 1)
                .Select(r => RoomFinding(rule, r, Text.L($"Member of {r.RoomListIds.Count} room lists.", $"Lid van {r.RoomListIds.Count} ruimtelijsten."),
                    new Dictionary<string, string> { ["RoomLists"] = string.Join(", ", r.RoomListIds.Select(id => input.RoomLists.FirstOrDefault(l => l.Id == id)?.DisplayName ?? id)) }))),

        new(new("ORG-009", Text.L("Capacity differs between Exchange and Places", "Capaciteit verschilt tussen Exchange en Places"), RuleCategory.OrganizationalBaseline, FindingSeverity.Recommendation,
                Text.L("The Exchange ResourceCapacity and the Places Capacity are different.", "De ResourceCapacity in Exchange en de Capacity in Places verschillen."), Text.L("Organizational baseline: keep both systems consistent.", "Organisatiebasislijn: houd beide systemen consistent.")),
            (input, rule) => input.Rooms.Where(r => r.Place?.Capacity is { } pc && r.Mailbox.ResourceCapacity is { } ec && pc != ec)
                .Select(r => RoomFinding(rule, r, Text.L($"Exchange reports {r.Mailbox.ResourceCapacity}, Places reports {r.Place!.Capacity}.", $"Exchange meldt {r.Mailbox.ResourceCapacity}, Places meldt {r.Place!.Capacity}."),
                    new Dictionary<string, string> { ["ResourceCapacity (Exchange)"] = r.Mailbox.ResourceCapacity!.Value.ToString(), ["Capacity (Places)"] = r.Place.Capacity!.Value.ToString() }))),
    ];

    public static IReadOnlyList<ValidationRuleInfo> RuleCatalog => Rules.Select(r => r.Info).ToList();

    /// <summary>Rules that need calendar-processing data. When that data hasn't been loaded for a room, the rule reports "Unable to verify".</summary>
    private static readonly HashSet<string> BookingDependent = ["BOOK-001", "BOOK-002", "BOOK-003", "BOOK-004", "BOOK-005", "BOOK-006", "BOOK-007", "BOOK-008", "BOOK-010", "ORG-005", "ORG-007"];

    public static IReadOnlyList<Finding> Run(ValidationInput input)
    {
        var findings = new List<Finding>();
        foreach (var rule in Rules)
        {
            var setting = input.Baseline.Rules.GetValueOrDefault(rule.Info.Id);
            if (rule.Info.Category == RuleCategory.OrganizationalBaseline && setting is { Enabled: false })
            {
                continue;
            }
            foreach (var finding in rule.Evaluate(input, rule))
            {
                var severity = rule.Info.Category == RuleCategory.OrganizationalBaseline && setting?.Severity is { } overridden && finding.Severity != FindingSeverity.UnableToVerify
                    ? overridden
                    : finding.Severity;
                findings.Add(finding with { Severity = severity });
            }
        }

        // Rooms whose calendar processing hasn't been loaded: a single "unable to verify" finding instead of silence.
        var unscanned = input.Rooms.Where(r => r.Booking is null).ToList();
        if (unscanned.Count > 0)
        {
            var info = new ValidationRuleInfo("SCAN-001", Text.L("Booking configuration not loaded", "Boekingsconfiguratie niet geladen"), RuleCategory.Technical, FindingSeverity.UnableToVerify,
                Text.L("Booking-policy rules could not be evaluated because calendar processing has not been read for this room.", "Regels voor het boekingsbeleid konden niet worden beoordeeld, omdat de agendaverwerking voor deze ruimte niet is gelezen."), Text.L("Run a configuration scan to evaluate these rules.", "Voer een configuratiescan uit om deze regels te beoordelen."));
            var scanRule = new Rule(info, (_, _) => []);
            findings.AddRange(unscanned.Select(r => Unverifiable(scanRule, r,
                Text.L($"{BookingDependent.Count} booking-policy rules could not be evaluated. Run a configuration scan or open the room to load its calendar processing.", $"{BookingDependent.Count} regels voor het boekingsbeleid konden niet worden beoordeeld. Voer een configuratiescan uit of open de ruimte om de agendaverwerking te laden."))));
        }
        return findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(f => f.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
