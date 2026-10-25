using System.Globalization;
using System.Text.Json;
using RoomOps.Domain.Common;
using RoomOps.Domain.Rooms;

namespace RoomOps.Domain.Booking;

public enum PolicyFieldKind
{
    Boolean,
    Integer,
    Enum,
    RecipientList,
    Text,
}

public enum PolicySection
{
    General,
    Scheduling,
    Conflicts,
    Permissions,
    Privacy,
    External,
    Responses,
}

/// <summary>
/// Describes one Set-CalendarProcessing parameter. The descriptor table is the single source of truth for
/// diffing, command generation, verification, template patches and editor metadata.
/// </summary>
public sealed class PolicyField
{
    public required string Name { get; init; }
    public required Phrase Label { get; init; }
    public required PolicySection Section { get; init; }
    public required PolicyFieldKind Kind { get; init; }
    public required Func<BookingPolicy, object?> Get { get; init; }
    public required Func<BookingPolicy, object?, BookingPolicy> Set { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }
    public string? Unit { get; init; }
    public IReadOnlyList<string>? EnumValues { get; init; }
    /// <summary>Documented as "used only on resource mailboxes where AutomateProcessing is AutoAccept".</summary>
    public bool AutoAcceptOnly { get; init; }
    public required Phrase Help { get; init; }

    /// <summary>Normalized value used for equality: recipient lists become sorted key sets.</summary>
    public object? Normalize(BookingPolicy policy)
    {
        var value = Get(policy);
        return value switch
        {
            IReadOnlyList<RecipientRef> list => string.Join("|", list.Select(r => r.Key).Distinct().Order(StringComparer.Ordinal)),
            string s => string.IsNullOrEmpty(s) ? null : s,
            _ => value,
        };
    }

    public bool AreEqual(BookingPolicy a, BookingPolicy b) => Equals(Normalize(a), Normalize(b));

    /// <summary>Human-readable value for previews and audit summaries.</summary>
    public string Format(BookingPolicy policy)
    {
        var value = Get(policy);
        return value switch
        {
            null => Text.L("(not set)", "(niet ingesteld)"),
            bool b => b ? Text.L("Enabled", "Ingeschakeld") : Text.L("Disabled", "Uitgeschakeld"),
            int i when Unit == "minutes" => i == 0 && Name == "MaximumDurationInMinutes" ? Text.L("Unlimited", "Onbeperkt") : DurationText.Minutes(i),
            int i when Unit == "days" => i == 1 ? Text.L("1 day", "1 dag") : Text.L($"{i} days", $"{i} dagen"),
            int i when Unit == "percent" => $"{i}%",
            int i => i.ToString(CultureInfo.InvariantCulture),
            IReadOnlyList<RecipientRef> list => list.Count == 0 ? Text.L("(none)", "(geen)") : string.Join(", ", list.Select(r => r.PrimarySmtpAddress ?? r.Identity)),
            string s => s.Length == 0 ? Text.L("(empty)", "(leeg)") : s,
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>Value bound to the PowerShell parameter. Recipient lists are passed as SMTP addresses (or the raw identity when unresolved).</summary>
    public object? ToParameterValue(BookingPolicy policy)
    {
        var value = Get(policy);
        return value switch
        {
            IReadOnlyList<RecipientRef> list => list.Select(r => r.PrimarySmtpAddress ?? r.Identity).ToArray(),
            Enum e => e.ToString(),
            _ => value,
        };
    }

    /// <summary>Applies a JSON value (from a template patch or import) to a policy, validating its type.</summary>
    public BookingPolicy ApplyJson(BookingPolicy policy, JsonElement json)
    {
        object? value = Kind switch
        {
            PolicyFieldKind.Boolean => json.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new FormatException(Text.L($"{Name} expects true or false.", $"{Name} verwacht true of false.")),
            },
            PolicyFieldKind.Integer => json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var i)
                ? i
                : throw new FormatException(Text.L($"{Name} expects a whole number.", $"{Name} verwacht een geheel getal.")),
            PolicyFieldKind.Enum => ParseEnum(json.GetString()),
            PolicyFieldKind.Text => json.ValueKind == JsonValueKind.Null ? null : json.GetString(),
            PolicyFieldKind.RecipientList => json.ValueKind == JsonValueKind.Array
                ? json.EnumerateArray().Select(e => RecipientRef.FromSmtp(e.GetString() ?? throw new FormatException(Text.L($"{Name} expects email addresses.", $"{Name} verwacht e-mailadressen.")))).ToList()
                : throw new FormatException(Text.L($"{Name} expects a list of email addresses.", $"{Name} verwacht een lijst met e-mailadressen.")),
            _ => throw new FormatException($"Unsupported field kind {Kind}."),
        };
        return Set(policy, value);
    }

    public JsonElement ToJson(BookingPolicy policy)
    {
        var value = Get(policy);
        object? serializable = value switch
        {
            IReadOnlyList<RecipientRef> list => list.Select(r => r.PrimarySmtpAddress ?? r.Identity).ToArray(),
            Enum e => e.ToString(),
            _ => value,
        };
        return JsonSerializer.SerializeToElement(serializable);
    }

    private object ParseEnum(string? text)
    {
        if (Name == nameof(BookingPolicy.AutomateProcessing) && Enum.TryParse<AutomateProcessing>(text, true, out var ap))
        {
            return ap;
        }
        if (Name == nameof(BookingPolicy.BookingType) && Enum.TryParse<ResourceBookingType>(text, true, out var bt))
        {
            return bt;
        }
        throw new FormatException(Text.L($"'{text}' is not a valid value for {Name}.", $"'{text}' is geen geldige waarde voor {Name}."));
    }
}

public static class DurationText
{
    public static string Minutes(int minutes)
    {
        if (minutes <= 0)
        {
            return "0 min";
        }
        if (minutes % 60 == 0)
        {
            var h = minutes / 60;
            return h == 1 ? Text.L("1 hour", "1 uur") : Text.L($"{h} hours", $"{h} uur");
        }
        if (minutes > 60)
        {
            return Text.L($"{minutes / 60} h {minutes % 60} min", $"{minutes / 60} u {minutes % 60} min");
        }
        return $"{minutes} min";
    }
}

public static class BookingPolicyFields
{
    private static Phrase P(string en, string nl) => new(en, nl);

    private static PolicyField Bool(string name, Phrase label, PolicySection section, Func<BookingPolicy, bool> get,
        Func<BookingPolicy, bool, BookingPolicy> set, Phrase help, bool autoAcceptOnly = false) => new()
    {
        Name = name,
        Label = label,
        Section = section,
        Kind = PolicyFieldKind.Boolean,
        Get = p => get(p),
        Set = (p, v) => set(p, v as bool? ?? throw new FormatException(Text.L($"{name} expects true or false.", $"{name} verwacht true of false."))),
        Help = help,
        AutoAcceptOnly = autoAcceptOnly,
    };

    private static PolicyField Int(string name, Phrase label, PolicySection section, Func<BookingPolicy, int> get,
        Func<BookingPolicy, int, BookingPolicy> set, int min, int max, string? unit, Phrase help) => new()
    {
        Name = name,
        Label = label,
        Section = section,
        Kind = PolicyFieldKind.Integer,
        Get = p => get(p),
        Set = (p, v) => set(p, v as int? ?? throw new FormatException(Text.L($"{name} expects a whole number.", $"{name} verwacht een geheel getal."))),
        Min = min,
        Max = max,
        Unit = unit,
        Help = help,
    };

    private static PolicyField Recipients(string name, Phrase label, Func<BookingPolicy, IReadOnlyList<RecipientRef>> get,
        Func<BookingPolicy, IReadOnlyList<RecipientRef>, BookingPolicy> set, Phrase help) => new()
    {
        Name = name,
        Label = label,
        Section = PolicySection.Permissions,
        Kind = PolicyFieldKind.RecipientList,
        Get = p => get(p),
        Set = (p, v) => set(p, v as IReadOnlyList<RecipientRef> ?? []),
        Help = help,
    };

    public static IReadOnlyList<PolicyField> All { get; } =
    [
        new PolicyField
        {
            Name = nameof(BookingPolicy.AutomateProcessing),
            Label = P("Calendar processing", "Agendaverwerking"),
            Section = PolicySection.General,
            Kind = PolicyFieldKind.Enum,
            EnumValues = Enum.GetNames<AutomateProcessing>(),
            Get = p => p.AutomateProcessing,
            Set = (p, v) => p with { AutomateProcessing = (AutomateProcessing)v! },
            Help = P("AutoAccept enables both the Calendar Attendant and the Resource Booking Attendant. AutoUpdate leaves requests tentative and, on a room mailbox, delegates don't get requests. None disables both attendants.", "AutoAccept schakelt zowel de Calendar Attendant als de Resource Booking Attendant in. AutoUpdate laat aanvragen voorlopig staan en op een ruimtepostvak ontvangen gemachtigden geen aanvragen. None schakelt beide uit."),
        },
        new PolicyField
        {
            Name = nameof(BookingPolicy.BookingType),
            Label = P("Booking type", "Boekingstype"),
            Section = PolicySection.General,
            Kind = PolicyFieldKind.Enum,
            EnumValues = Enum.GetNames<ResourceBookingType>(),
            Get = p => p.BookingType,
            Set = (p, v) => p with { BookingType = (ResourceBookingType)v! },
            Help = P("Standard: the room is reservable according to the other settings. Reserved: the room can't be reserved. Exchange Online only.", "Standard: de ruimte is te reserveren volgens de andere instellingen. Reserved: de ruimte kan niet worden gereserveerd. Alleen Exchange Online."),
        },
        Bool(nameof(BookingPolicy.AllowRecurringMeetings), P("Allow recurring meetings", "Terugkerende vergaderingen toestaan"), PolicySection.General, p => p.AllowRecurringMeetings, (p, v) => p with { AllowRecurringMeetings = v },
            P("When disabled, recurring meeting requests are not allowed.", "Als dit is uitgeschakeld, zijn aanvragen voor terugkerende vergaderingen niet toegestaan.")),

        Int(nameof(BookingPolicy.BookingWindowInDays), P("Booking window", "Boekingsvenster"), PolicySection.Scheduling, p => p.BookingWindowInDays, (p, v) => p with { BookingWindowInDays = v }, 0, 1080, "days",
            P("How far in advance the room can be booked. 0 means today only. Valid range 0–1080 days.", "Hoe ver vooruit de ruimte kan worden geboekt. 0 betekent alleen vandaag. Geldig bereik 0–1080 dagen.")),
        Int(nameof(BookingPolicy.MaximumDurationInMinutes), P("Maximum duration", "Maximale duur"), PolicySection.Scheduling, p => p.MaximumDurationInMinutes, (p, v) => p with { MaximumDurationInMinutes = v }, 0, int.MaxValue, "minutes",
            P("Longest meeting the room accepts; for recurring meetings it applies to each instance. 0 means unlimited.", "Langste vergadering die de ruimte accepteert; bij terugkerende vergaderingen geldt dit per instantie. 0 betekent onbeperkt.")),
        Int(nameof(BookingPolicy.MinimumDurationInMinutes), P("Minimum duration", "Minimale duur"), PolicySection.Scheduling, p => p.MinimumDurationInMinutes, (p, v) => p with { MinimumDurationInMinutes = v }, 0, int.MaxValue, "minutes",
            P("Shortest meeting accepted. Microsoft documents that this works only on workspace mailboxes.", "Kortste vergadering die wordt geaccepteerd. Volgens Microsoft werkt dit alleen bij werkplekpostvakken.")),
        Bool(nameof(BookingPolicy.ScheduleOnlyDuringWorkHours), P("Only during working hours", "Alleen tijdens werktijden"), PolicySection.Scheduling, p => p.ScheduleOnlyDuringWorkHours, (p, v) => p with { ScheduleOnlyDuringWorkHours = v },
            P("Requests outside the mailbox working hours (Set-MailboxCalendarConfiguration) are declined automatically.", "Aanvragen buiten de werktijden van het postvak (Set-MailboxCalendarConfiguration) worden automatisch geweigerd.")),
        Bool(nameof(BookingPolicy.EnforceSchedulingHorizon), P("Enforce scheduling horizon", "Planningshorizon afdwingen"), PolicySection.Scheduling, p => p.EnforceSchedulingHorizon, (p, v) => p with { EnforceSchedulingHorizon = v },
            P("Recurring series that start inside the booking window but extend beyond it are declined when enabled; when disabled they are accepted with occurrences truncated at the window end.", "Terugkerende reeksen die binnen het boekingsvenster beginnen maar erbuiten doorlopen, worden geweigerd als dit is ingeschakeld; als het is uitgeschakeld worden ze geaccepteerd en afgekapt aan het einde van het venster.")),
        Bool(nameof(BookingPolicy.EnforceCapacity), P("Enforce capacity", "Capaciteit afdwingen"), PolicySection.Scheduling, p => p.EnforceCapacity, (p, v) => p with { EnforceCapacity = v },
            P("Limits bookings to the room's capacity. Exchange Online only.", "Beperkt boekingen tot de capaciteit van de ruimte. Alleen Exchange Online.")),

        Bool(nameof(BookingPolicy.AllowConflicts), P("Allow conflicts", "Conflicten toestaan"), PolicySection.Conflicts, p => p.AllowConflicts, (p, v) => p with { AllowConflicts = v },
            P("When enabled, a series is accepted regardless of conflicts, and the conflict thresholds are ignored.", "Als dit is ingeschakeld, wordt een reeks geaccepteerd ongeacht conflicten en worden de conflictdrempels genegeerd.")),
        Int(nameof(BookingPolicy.ConflictPercentageAllowed), P("Conflict percentage allowed", "Toegestaan conflictpercentage"), PolicySection.Conflicts, p => p.ConflictPercentageAllowed, (p, v) => p with { ConflictPercentageAllowed = v }, 0, 100, "percent",
            P("For new recurring requests: the series is declined when the share of conflicting occurrences exceeds this percentage.", "Voor nieuwe terugkerende aanvragen: de reeks wordt geweigerd als het aandeel conflicterende instanties dit percentage overschrijdt.")),
        Int(nameof(BookingPolicy.MaximumConflictInstances), P("Maximum conflict instances", "Maximaal aantal conflicterende instanties"), PolicySection.Conflicts, p => p.MaximumConflictInstances, (p, v) => p with { MaximumConflictInstances = v }, 0, int.MaxValue, null,
            P("For recurring requests: the series is declined when more occurrences than this conflict.", "Voor terugkerende aanvragen: de reeks wordt geweigerd als meer instanties dan dit aantal conflicteren.")),

        Bool(nameof(BookingPolicy.AllBookInPolicy), P("All users book in-policy automatically", "Alle gebruikers boeken binnen het beleid automatisch"), PolicySection.Permissions, p => p.AllBookInPolicy, (p, v) => p with { AllBookInPolicy = v },
            P("In-policy requests from all users are approved automatically. When disabled, only BookInPolicy recipients are approved automatically.", "Aanvragen binnen het beleid van alle gebruikers worden automatisch goedgekeurd. Als dit is uitgeschakeld, worden alleen ontvangers in BookInPolicy automatisch goedgekeurd.")),
        Bool(nameof(BookingPolicy.AllRequestInPolicy), P("All users may request in-policy", "Alle gebruikers mogen binnen het beleid aanvragen"), PolicySection.Permissions, p => p.AllRequestInPolicy, (p, v) => p with { AllRequestInPolicy = v },
            P("All users can submit in-policy requests; they require delegate approval when automatic booking is not granted.", "Alle gebruikers kunnen aanvragen binnen het beleid indienen; die vereisen goedkeuring van een gemachtigde als automatisch boeken niet is toegestaan.")),
        Bool(nameof(BookingPolicy.AllRequestOutOfPolicy), P("All users may request out-of-policy", "Alle gebruikers mogen buiten het beleid aanvragen"), PolicySection.Permissions, p => p.AllRequestOutOfPolicy, (p, v) => p with { AllRequestOutOfPolicy = v },
            P("All users can submit out-of-policy requests, which always require delegate approval.", "Alle gebruikers kunnen aanvragen buiten het beleid indienen; die vereisen altijd goedkeuring van een gemachtigde.")),
        Recipients(nameof(BookingPolicy.BookInPolicy), P("Automatic-booking list", "Lijst voor automatisch boeken"), p => p.BookInPolicy, (p, v) => p with { BookInPolicy = v },
            P("Users or groups whose in-policy requests are approved automatically. Query-based (dynamic) groups aren't supported; nested groups don't work.", "Gebruikers of groepen van wie aanvragen binnen het beleid automatisch worden goedgekeurd. Dynamische groepen worden niet ondersteund; geneste groepen werken niet.")),
        Recipients(nameof(BookingPolicy.RequestInPolicy), P("In-policy request list", "Aanvraaglijst binnen het beleid"), p => p.RequestInPolicy, (p, v) => p with { RequestInPolicy = v },
            P("Users who can submit in-policy requests that require delegate approval.", "Gebruikers die aanvragen binnen het beleid kunnen indienen die goedkeuring van een gemachtigde vereisen.")),
        Recipients(nameof(BookingPolicy.RequestOutOfPolicy), P("Out-of-policy request list", "Aanvraaglijst buiten het beleid"), p => p.RequestOutOfPolicy, (p, v) => p with { RequestOutOfPolicy = v },
            P("Users who can submit out-of-policy requests that require delegate approval.", "Gebruikers die aanvragen buiten het beleid kunnen indienen die goedkeuring van een gemachtigde vereisen.")),
        Recipients(nameof(BookingPolicy.ResourceDelegates), P("Booking delegates", "Boekingsgemachtigden"), p => p.ResourceDelegates, (p, v) => p with { ResourceDelegates = v },
            P("Users who can approve or reject requests sent to the room. This is not mailbox Full Access, Send As or folder permission.", "Gebruikers die aanvragen aan de ruimte kunnen goedkeuren of weigeren. Dit is geen volledige toegang tot het postvak, Verzenden als of mapmachtiging.")),
        Bool(nameof(BookingPolicy.ForwardRequestsToDelegates), P("Forward requests to delegates", "Aanvragen doorsturen naar gemachtigden"), PolicySection.Permissions, p => p.ForwardRequestsToDelegates, (p, v) => p with { ForwardRequestsToDelegates = v },
            P("Requests that need approval are forwarded to the booking delegates.", "Aanvragen die goedkeuring nodig hebben, worden doorgestuurd naar de boekingsgemachtigden.")),
        Bool(nameof(BookingPolicy.TentativePendingApproval), P("Tentative while pending approval", "Voorlopig zolang goedkeuring openstaat"), PolicySection.Permissions, p => p.TentativePendingApproval, (p, v) => p with { TentativePendingApproval = v },
            P("Pending requests show as tentative in the room calendar. When disabled they show as free.", "Openstaande aanvragen worden als voorlopig getoond in de agenda van de ruimte. Als dit is uitgeschakeld, worden ze als vrij getoond.")),

        Bool(nameof(BookingPolicy.AddOrganizerToSubject), P("Add organizer to subject", "Organisator aan onderwerp toevoegen"), PolicySection.Privacy, p => p.AddOrganizerToSubject, (p, v) => p with { AddOrganizerToSubject = v },
            P("The organizer's name is added to the subject of the meeting in the room calendar.", "De naam van de organisator wordt toegevoegd aan het onderwerp van de vergadering in de agenda van de ruimte."), autoAcceptOnly: true),
        Bool(nameof(BookingPolicy.DeleteSubject), P("Delete subject", "Onderwerp verwijderen"), PolicySection.Privacy, p => p.DeleteSubject, (p, v) => p with { DeleteSubject = v },
            P("The subject of incoming requests is removed in the room calendar.", "Het onderwerp van binnenkomende aanvragen wordt verwijderd in de agenda van de ruimte."), autoAcceptOnly: true),
        Bool(nameof(BookingPolicy.DeleteComments), P("Delete body text", "Berichttekst verwijderen"), PolicySection.Privacy, p => p.DeleteComments, (p, v) => p with { DeleteComments = v },
            P("Body text of incoming requests is removed. Teams Rooms guidance keeps body text so join information remains available.", "De berichttekst van binnenkomende aanvragen wordt verwijderd. Volgens de richtlijnen voor Teams Rooms blijft de tekst staan, zodat deelname-informatie beschikbaar blijft."), autoAcceptOnly: true),
        Bool(nameof(BookingPolicy.DeleteAttachments), P("Delete attachments", "Bijlagen verwijderen"), PolicySection.Privacy, p => p.DeleteAttachments, (p, v) => p with { DeleteAttachments = v },
            P("Attachments on incoming requests are removed.", "Bijlagen bij binnenkomende aanvragen worden verwijderd."), autoAcceptOnly: true),
        Bool(nameof(BookingPolicy.RemovePrivateProperty), P("Clear private flag", "Privémarkering wissen"), PolicySection.Privacy, p => p.RemovePrivateProperty, (p, v) => p with { RemovePrivateProperty = v },
            P("The private flag of incoming meetings is cleared.", "De privémarkering van binnenkomende vergaderingen wordt gewist.")),
        Bool(nameof(BookingPolicy.OrganizerInfo), P("Send organizer info on conflicts", "Organisatorgegevens sturen bij conflicten"), PolicySection.Privacy, p => p.OrganizerInfo, (p, v) => p with { OrganizerInfo = v },
            P("When a request is declined because of conflicts, the response includes organizer information of the conflicting meeting.", "Als een aanvraag wordt geweigerd vanwege conflicten, bevat het antwoord gegevens van de organisator van de conflicterende vergadering.")),
        Bool(nameof(BookingPolicy.DeleteNonCalendarItems), P("Delete non-calendar items", "Niet-agenda-items verwijderen"), PolicySection.Privacy, p => p.DeleteNonCalendarItems, (p, v) => p with { DeleteNonCalendarItems = v },
            P("Non-calendar messages sent to the room are deleted.", "Berichten aan de ruimte die geen agenda-item zijn, worden verwijderd.")),
        Bool(nameof(BookingPolicy.RemoveCanceledMeetings), P("Remove canceled meetings", "Geannuleerde vergaderingen verwijderen"), PolicySection.Privacy, p => p.RemoveCanceledMeetings, (p, v) => p with { RemoveCanceledMeetings = v },
            P("Canceled meetings are deleted from the room calendar. Exchange Online only.", "Geannuleerde vergaderingen worden uit de agenda van de ruimte verwijderd. Alleen Exchange Online.")),
        Bool(nameof(BookingPolicy.RemoveOldMeetingMessages), P("Remove old meeting messages", "Oude vergaderberichten verwijderen"), PolicySection.Privacy, p => p.RemoveOldMeetingMessages, (p, v) => p with { RemoveOldMeetingMessages = v },
            P("Outdated and redundant updates and responses are deleted.", "Verouderde en overbodige updates en antwoorden worden verwijderd.")),
        Bool(nameof(BookingPolicy.RemoveForwardedMeetingNotifications), P("Remove forwarded notifications", "Doorgestuurde meldingen verwijderen"), PolicySection.Privacy, p => p.RemoveForwardedMeetingNotifications, (p, v) => p with { RemoveForwardedMeetingNotifications = v },
            P("Processed forwarded meeting notifications are moved to Deleted Items.", "Verwerkte doorgestuurde vergadermeldingen worden naar Verwijderde items verplaatst.")),
        Bool(nameof(BookingPolicy.AddNewRequestsTentatively), P("Add new requests tentatively", "Nieuwe aanvragen voorlopig toevoegen"), PolicySection.Privacy, p => p.AddNewRequestsTentatively, (p, v) => p with { AddNewRequestsTentatively = v },
            P("New requests are added to the calendar as tentative. When disabled, only existing items are updated by the Calendar Attendant.", "Nieuwe aanvragen worden als voorlopig aan de agenda toegevoegd. Als dit is uitgeschakeld, werkt de Calendar Attendant alleen bestaande items bij.")),
        Bool(nameof(BookingPolicy.EnableResponseDetails), P("Include response details", "Details in antwoord opnemen"), PolicySection.Responses, p => p.EnableResponseDetails, (p, v) => p with { EnableResponseDetails = v },
            P("Reasons for accepting or declining are included in the response message.", "Redenen voor accepteren of weigeren worden in het antwoordbericht opgenomen.")),

        Bool(nameof(BookingPolicy.ProcessExternalMeetingMessages), P("Process external requests", "Externe aanvragen verwerken"), PolicySection.External, p => p.ProcessExternalMeetingMessages, (p, v) => p with { ProcessExternalMeetingMessages = v },
            P("When disabled, requests from outside the organization are rejected. Enabling it does not override mail flow, connector or other tenant restrictions.", "Als dit is uitgeschakeld, worden aanvragen van buiten de organisatie geweigerd. Inschakelen heft regels voor e-mailstroom, connectors of andere tenantbeperkingen niet op.")),

        Bool(nameof(BookingPolicy.AddAdditionalResponse), P("Add custom response text", "Eigen antwoordtekst toevoegen"), PolicySection.Responses, p => p.AddAdditionalResponse, (p, v) => p with { AddAdditionalResponse = v },
            P("Appends the additional response text to the room's reply.", "Voegt de aanvullende antwoordtekst toe aan het antwoord van de ruimte."), autoAcceptOnly: true),
        new PolicyField
        {
            Name = nameof(BookingPolicy.AdditionalResponse),
            Label = P("Additional response text", "Aanvullende antwoordtekst"),
            Section = PolicySection.Responses,
            Kind = PolicyFieldKind.Text,
            Get = p => p.AdditionalResponse,
            Set = (p, v) => p with { AdditionalResponse = v as string },
            Help = P("Text appended to responses when 'Add custom response text' is enabled.", "Tekst die aan antwoorden wordt toegevoegd als 'Eigen antwoordtekst toevoegen' is ingeschakeld."),
        },
    ];

    private static readonly Dictionary<string, PolicyField> ByName =
        All.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    public static PolicyField? Find(string name) => ByName.GetValueOrDefault(name);

    public static PolicyField Get(string name) =>
        ByName.TryGetValue(name, out var field) ? field : throw new KeyNotFoundException(Text.L($"Unknown booking policy field '{name}'.", $"Onbekend veld van het boekingsbeleid '{name}'."));

    /// <summary>Applies a sparse patch (field name → JSON value) to a policy.</summary>
    public static BookingPolicy ApplyPatch(BookingPolicy policy, IReadOnlyDictionary<string, JsonElement> patch, IReadOnlySet<string>? excludedFields = null)
    {
        var result = policy;
        foreach (var (name, value) in patch)
        {
            if (excludedFields is not null && excludedFields.Contains(name))
            {
                continue;
            }
            result = Get(name).ApplyJson(result, value);
        }
        return result;
    }

    public static IReadOnlyList<PolicyField> ChangedFields(BookingPolicy current, BookingPolicy desired) =>
        All.Where(f => !f.AreEqual(current, desired)).ToList();
}
