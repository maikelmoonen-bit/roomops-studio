using RoomOps.Domain.Common;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Domain.Booking;

public enum StatementKind
{
    Access,
    Approval,
    Constraint,
    Conflict,
    Privacy,
    External,
    Response,
    Caution,
}

public sealed record PolicyStatement(StatementKind Kind, string Text);

public sealed record PolicyExplanation(string Summary, BookingMode Mode, string ModeLabel, IReadOnlyList<PolicyStatement> Statements);

/// <summary>
/// Generates plain-language descriptions from the actual policy state. Every sentence corresponds to a documented
/// Set-CalendarProcessing behavior; nothing is phrased as a guarantee of booking outcome.
/// </summary>
public static class BookingPolicyExplainer
{
    public static PolicyExplanation Explain(BookingPolicy p, string roomName, WorkingHoursConfig? workingHours = null, int? capacity = null)
    {
        var mode = BookingModeClassifier.Classify(p);
        var statements = new List<PolicyStatement>();
        var name = string.IsNullOrWhiteSpace(roomName) ? Text.L("This room", "Deze ruimte") : roomName;

        if (p.AutomateProcessing == AutomateProcessing.None)
        {
            statements.Add(new(StatementKind.Caution, Text.L("Calendar processing is disabled. Neither the Calendar Attendant nor the Resource Booking Attendant acts on meeting requests.", "Agendaverwerking is uitgeschakeld. De Calendar Attendant noch de Resource Booking Attendant reageert op vergaderverzoeken.")));
            return new(Text.L($"{name} does not process meeting requests automatically; requests stay unanswered until someone acts on them in the room calendar.", $"{name} verwerkt vergaderverzoeken niet automatisch; aanvragen blijven onbeantwoord totdat iemand ze in de agenda van de ruimte afhandelt."), mode, BookingModeClassifier.Label(mode), statements);
        }

        if (p.AutomateProcessing == AutomateProcessing.AutoUpdate)
        {
            statements.Add(new(StatementKind.Caution, Text.L("Only the Calendar Attendant runs. Requests stay tentative until a delegate decides, but on a room mailbox delegates don't receive the requests.", "Alleen de Calendar Attendant is actief. Aanvragen blijven voorlopig totdat een gemachtigde beslist, maar op een ruimtepostvak ontvangen gemachtigden de aanvragen niet.")));
            return new(Text.L($"{name} records meeting requests as tentative and never accepts or declines them automatically.", $"{name} legt vergaderverzoeken voorlopig vast en accepteert of weigert ze nooit automatisch."), mode, BookingModeClassifier.Label(mode), statements);
        }

        if (p.BookingType == ResourceBookingType.Reserved)
        {
            statements.Add(new(StatementKind.Caution, Text.L("The booking type is Reserved, so the room can't be reserved.", "Het boekingstype is Reserved, dus de ruimte kan niet worden gereserveerd.")));
            return new(Text.L($"{name} is marked as reserved and cannot be booked.", $"{name} is gemarkeerd als gereserveerd en kan niet worden geboekt."), mode, BookingModeClassifier.Label(mode), statements);
        }

        // Who can book.
        string who;
        var nobody = false;
        if (p.AllBookInPolicy)
        {
            who = Text.L("eligible users", "bevoegde gebruikers");
            statements.Add(new(StatementKind.Access, Text.L("In-policy requests from all users are accepted automatically when the room is free.", "Aanvragen binnen het beleid van alle gebruikers worden automatisch geaccepteerd als de ruimte vrij is.")));
        }
        else if (p.BookInPolicy.Count > 0)
        {
            who = p.BookInPolicy.Count == 1 ? Text.L("one listed user or group", "één vermelde gebruiker of groep") : Text.L($"{p.BookInPolicy.Count} listed users or groups", $"{p.BookInPolicy.Count} vermelde gebruikers of groepen");
            statements.Add(new(StatementKind.Access, Text.L($"Only {Names(p.BookInPolicy)} can book automatically. Their in-policy requests are accepted when the room is free.", $"Automatisch boeken is alleen toegestaan voor {Names(p.BookInPolicy)}. Hun aanvragen binnen het beleid worden geaccepteerd als de ruimte vrij is.")));
        }
        else
        {
            who = Text.L("nobody", "niemand");
            nobody = true;
            statements.Add(new(StatementKind.Caution, Text.L("No organizer is allowed to book automatically.", "Geen enkele organisator mag automatisch boeken.")));
        }

        var delegates = p.ResourceDelegates.Count > 0 ? Names(p.ResourceDelegates) : null;
        var forwardTarget = delegates is not null && p.ForwardRequestsToDelegates ? Text.L($"forwarded to {delegates} for approval", $"ter goedkeuring doorgestuurd naar {delegates}") : Text.L("held for delegate approval", "aangehouden voor goedkeuring door een gemachtigde");
        if (!p.AllBookInPolicy && p.AllRequestInPolicy)
        {
            statements.Add(new(StatementKind.Approval, Text.L($"All other users can submit in-policy requests, which are {forwardTarget}.", $"Alle andere gebruikers kunnen aanvragen binnen het beleid indienen, die worden {forwardTarget}.")));
        }
        else if (p.RequestInPolicy.Count > 0)
        {
            statements.Add(new(StatementKind.Approval, Text.L($"{Capitalize(Names(p.RequestInPolicy))} can submit in-policy requests, which are {forwardTarget}.", $"Aanvragen binnen het beleid van {Names(p.RequestInPolicy)} worden {forwardTarget}.")));
        }

        if (p.AllRequestOutOfPolicy)
        {
            statements.Add(new(StatementKind.Approval, Text.L($"Any user can submit requests that fall outside the policy; these always need delegate approval{(delegates is not null ? $" ({delegates})" : string.Empty)}.", $"Elke gebruiker kan aanvragen buiten het beleid indienen; die vereisen altijd goedkeuring van een gemachtigde{(delegates is not null ? $" ({delegates})" : string.Empty)}.")));
        }
        else if (p.RequestOutOfPolicy.Count > 0)
        {
            statements.Add(new(StatementKind.Approval, Text.L($"{Capitalize(Names(p.RequestOutOfPolicy))} can submit out-of-policy requests for delegate approval.", $"Aanvragen buiten het beleid van {Names(p.RequestOutOfPolicy)} vereisen goedkeuring door een gemachtigde.")));
        }
        else
        {
            statements.Add(new(StatementKind.Constraint, Text.L("Requests that fall outside the policy are declined.", "Aanvragen buiten het beleid worden geweigerd.")));
        }

        if (p.ResourceDelegates.Count > 0 && (p.AllRequestInPolicy || p.RequestInPolicy.Count > 0 || p.AllRequestOutOfPolicy || p.RequestOutOfPolicy.Count > 0))
        {
            statements.Add(new(StatementKind.Approval, p.TentativePendingApproval
                ? Text.L("While approval is pending, the request appears as tentative in the room calendar.", "Zolang de goedkeuring openstaat, staat de aanvraag als voorlopig in de agenda van de ruimte.")
                : Text.L("While approval is pending, the room still appears free.", "Zolang de goedkeuring openstaat, lijkt de ruimte nog vrij.")));
        }

        // Constraints.
        var window = p.BookingWindowInDays == 0 ? Text.L("for today only", "alleen voor vandaag") : Text.L($"up to {p.BookingWindowInDays} days in advance", $"tot {p.BookingWindowInDays} dagen vooruit");
        var duration = p.MaximumDurationInMinutes == 0 ? Text.L("of any length", "van elke lengte") : Text.L($"lasting no longer than {DurationText.Minutes(p.MaximumDurationInMinutes)}", $"die niet langer duren dan {DurationText.Minutes(p.MaximumDurationInMinutes)}");
        statements.Add(new(StatementKind.Constraint, Text.L($"Meetings can be booked {window}, {duration}.", $"Vergaderingen {duration} kunnen {window} worden geboekt.")));

        if (p.ScheduleOnlyDuringWorkHours)
        {
            var wh = workingHours is null ? Text.L("the mailbox working hours", "de werktijden van het postvak") : Text.L($"working hours ({workingHours.Describe()})", $"werktijden ({workingHours.Describe()})");
            statements.Add(new(StatementKind.Constraint, Text.L($"Requests outside {wh} are declined.", $"Aanvragen buiten {wh} worden geweigerd.")));
        }

        if (!p.AllowRecurringMeetings)
        {
            statements.Add(new(StatementKind.Constraint, Text.L("Recurring meetings are not allowed.", "Terugkerende vergaderingen zijn niet toegestaan.")));
        }
        else
        {
            statements.Add(new(StatementKind.Constraint, p.EnforceSchedulingHorizon
                ? Text.L("A recurring series that extends beyond the booking window is declined.", "Een terugkerende reeks die buiten het boekingsvenster doorloopt, wordt geweigerd.")
                : Text.L("A recurring series that extends beyond the booking window is accepted, with occurrences after the window removed.", "Een terugkerende reeks die buiten het boekingsvenster doorloopt, wordt geaccepteerd; instanties na het venster vervallen.")));
        }

        if (p.EnforceCapacity)
        {
            statements.Add(new(StatementKind.Constraint, capacity is > 0
                ? Text.L($"Requests with more attendees than the room capacity ({capacity}) are declined.", $"Aanvragen met meer deelnemers dan de capaciteit van de ruimte ({capacity}) worden geweigerd.")
                : Text.L("Capacity enforcement is enabled, but the room has no capacity configured.", "Capaciteit afdwingen is ingeschakeld, maar voor de ruimte is geen capaciteit ingesteld.")));
        }

        // Conflicts.
        if (p.AllowConflicts)
        {
            statements.Add(new(StatementKind.Conflict, Text.L("Conflicting requests are accepted, so the room can be double-booked.", "Conflicterende aanvragen worden geaccepteerd, dus de ruimte kan dubbel worden geboekt.")));
        }
        else
        {
            statements.Add(new(StatementKind.Conflict, Text.L("A single meeting that conflicts with an existing booking is declined.", "Eén vergadering die conflicteert met een bestaande boeking wordt geweigerd.")));
            if (p.AllowRecurringMeetings)
            {
                if (p.ConflictPercentageAllowed == 0 && p.MaximumConflictInstances == 0)
                {
                    statements.Add(new(StatementKind.Conflict, Text.L("Any conflicting occurrence declines an entire recurring series.", "Elke conflicterende instantie leidt tot weigering van de hele terugkerende reeks.")));
                }
                else
                {
                    statements.Add(new(StatementKind.Conflict,
                        Text.L($"A recurring series is declined when more than {p.MaximumConflictInstances} occurrence(s) or more than {p.ConflictPercentageAllowed}% of occurrences conflict; below those limits the series is accepted and only conflicting occurrences are declined.", $"Een terugkerende reeks wordt geweigerd als meer dan {p.MaximumConflictInstances} instantie(s) of meer dan {p.ConflictPercentageAllowed}% van de instanties conflicteren; daaronder wordt de reeks geaccepteerd en worden alleen conflicterende instanties geweigerd.")));
                }
            }
        }

        // Privacy.
        if (p.DeleteSubject) statements.Add(new(StatementKind.Privacy, Text.L("Meeting subjects are removed from the room calendar.", "Onderwerpen van vergaderingen worden uit de agenda van de ruimte verwijderd.")));
        if (p.AddOrganizerToSubject) statements.Add(new(StatementKind.Privacy, Text.L("The organizer's name is shown as the subject in the room calendar.", "De naam van de organisator wordt als onderwerp getoond in de agenda van de ruimte.")));
        if (!p.DeleteSubject && !p.AddOrganizerToSubject) statements.Add(new(StatementKind.Privacy, Text.L("Original meeting subjects stay visible to anyone who can view room details.", "Oorspronkelijke onderwerpen blijven zichtbaar voor iedereen die de details van de ruimte kan bekijken.")));
        if (p.DeleteComments) statements.Add(new(StatementKind.Privacy, Text.L("Meeting body text is removed, including any online-meeting join information in the body.", "De berichttekst van vergaderingen wordt verwijderd, inclusief eventuele deelname-informatie voor onlinevergaderingen.")));
        if (p.DeleteAttachments) statements.Add(new(StatementKind.Privacy, Text.L("Attachments are removed from meetings in the room calendar.", "Bijlagen worden verwijderd uit vergaderingen in de agenda van de ruimte.")));
        if (!p.RemovePrivateProperty) statements.Add(new(StatementKind.Privacy, Text.L("The private flag of meetings is preserved.", "De privémarkering van vergaderingen blijft behouden.")));

        // External.
        statements.Add(new(StatementKind.External, p.ProcessExternalMeetingMessages
            ? Text.L("Requests from outside the organization are processed, subject to tenant mail-flow and sharing restrictions.", "Aanvragen van buiten de organisatie worden verwerkt, binnen de beperkingen voor e-mailstroom en delen van de tenant.")
            : Text.L("Requests from outside the organization are rejected.", "Aanvragen van buiten de organisatie worden geweigerd.")));

        if (p.AddAdditionalResponse && !string.IsNullOrWhiteSpace(p.AdditionalResponse))
        {
            statements.Add(new(StatementKind.Response, Text.L("A custom message is appended to the room's responses.", "Aan de antwoorden van de ruimte wordt een eigen bericht toegevoegd.")));
        }

        // Summary sentence in the shape: "<Room> automatically accepts booking requests from <who> [during configured working hours], provided …".
        // Requests are routed to delegates for approval: the summary leads with the approval step.
        var approvalFlow = !p.AllBookInPolicy && (p.AllRequestInPolicy || p.RequestInPolicy.Count > 0) && p.ResourceDelegates.Count > 0 && p.ForwardRequestsToDelegates;
        string summary;
        if (nobody && !approvalFlow)
        {
            summary = Text.L($"{name} does not accept requests automatically from anyone; {(p.AllRequestInPolicy || p.RequestInPolicy.Count > 0 ? "eligible requests wait for delegate approval" : "in-policy requests are declined")}.", $"{name} accepteert van niemand automatisch aanvragen; {(p.AllRequestInPolicy || p.RequestInPolicy.Count > 0 ? "geldige aanvragen wachten op goedkeuring door een gemachtigde" : "aanvragen binnen het beleid worden geweigerd")}.");
        }
        else if (approvalFlow)
        {
            summary = Text.L($"{name} sends in-policy requests to {(delegates ?? "its delegates")} for approval{(p.BookInPolicy.Count > 0 ? $", except from {Names(p.BookInPolicy)}, which are accepted automatically" : string.Empty)}.", $"{name} stuurt aanvragen binnen het beleid ter goedkeuring naar {(delegates ?? "de gemachtigden")}{(p.BookInPolicy.Count > 0 ? $", behalve die van {Names(p.BookInPolicy)}; die worden automatisch geaccepteerd" : string.Empty)}.");
        }
        else
        {
            var conditions = new List<string>();
            if (p.MaximumDurationInMinutes > 0) conditions.Add(Text.L($"the meeting does not exceed {DurationText.Minutes(p.MaximumDurationInMinutes)}", $"de vergadering niet langer duurt dan {DurationText.Minutes(p.MaximumDurationInMinutes)}"));
            conditions.Add(p.BookingWindowInDays == 0 ? Text.L("it takes place today", "deze vandaag plaatsvindt") : Text.L($"it starts within {p.BookingWindowInDays} days", $"deze binnen {p.BookingWindowInDays} dagen begint"));
            if (!p.AllowConflicts) conditions.Add(Text.L("there is no scheduling conflict", "er geen planningsconflict is"));
            summary = Text.L($"{name} automatically accepts booking requests from {who}{(p.ScheduleOnlyDuringWorkHours ? " during configured working hours" : string.Empty)}, provided {JoinWithAnd(conditions)}.", $"{name} accepteert automatisch boekingsaanvragen van {who}{(p.ScheduleOnlyDuringWorkHours ? " tijdens de ingestelde werktijden" : string.Empty)}, mits {JoinWithAnd(conditions)}.");
        }

        return new(summary, mode, BookingModeClassifier.Label(mode), statements);
    }

    private static string Names(IReadOnlyList<Rooms.RecipientRef> list)
    {
        var names = list.Select(r => r.DisplayName ?? r.PrimarySmtpAddress ?? r.Identity).ToList();
        return names.Count switch
        {
            0 => Text.L("nobody", "niemand"),
            1 => names[0],
            2 => Text.L($"{names[0]} and {names[1]}", $"{names[0]} en {names[1]}"),
            3 => Text.L($"{names[0]}, {names[1]} and {names[2]}", $"{names[0]}, {names[1]} en {names[2]}"),
            _ => Text.L($"{names[0]}, {names[1]} and {names.Count - 2} others", $"{names[0]}, {names[1]} en {names.Count - 2} anderen"),
        };
    }

    private static string JoinWithAnd(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => string.Empty,
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + Text.L(" and ", " en ") + parts[^1],
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
