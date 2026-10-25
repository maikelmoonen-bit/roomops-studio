using RoomOps.Domain.Common;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Domain.Booking;

public enum OrganizerScope
{
    Internal,
    External,
}

/// <summary>How the simulated organizer relates to the recipient lists. Group membership can't be evaluated offline, so the administrator states it.</summary>
public enum ListMembership
{
    NotListed,
    ListedDirectly,
    MemberOfListedGroup,
}

public sealed record BookingScenario
{
    public OrganizerScope Organizer { get; init; } = OrganizerScope.Internal;
    public string? OrganizerAddress { get; init; }
    public ListMembership InBookInPolicy { get; init; } = ListMembership.NotListed;
    public ListMembership InRequestInPolicy { get; init; } = ListMembership.NotListed;
    public ListMembership InRequestOutOfPolicy { get; init; } = ListMembership.NotListed;
    public required DateTimeOffset RequestedAt { get; init; }
    /// <summary>Meeting start, expressed in the room's working-hours time zone.</summary>
    public required DateTime StartLocal { get; init; }
    public int DurationMinutes { get; init; } = 60;
    public bool Recurring { get; init; }
    public int Occurrences { get; init; } = 1;
    public int ConflictingOccurrences { get; init; }
    public int? Attendees { get; init; }
}

public enum SimulatedOutcome
{
    Accepted,
    AcceptedWithDeclinedOccurrences,
    ForwardedForApproval,
    Declined,
    NotProcessed,
    Indeterminate,
}

public enum StepCertainty
{
    /// <summary>Follows directly from configuration values.</summary>
    Deterministic,
    /// <summary>Depends on a fact the administrator asserted in the scenario.</summary>
    Assumption,
    /// <summary>Requires live Exchange data (free/busy, group expansion) that a configuration analysis can't see.</summary>
    RequiresLiveData,
}

public sealed record SimulationStep(string Rule, string Parameter, bool Passed, StepCertainty Certainty, string Explanation);

public sealed record SimulationResult(
    SimulatedOutcome Outcome,
    string Headline,
    IReadOnlyList<SimulationStep> Steps,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Configuration analysis of how the Resource Booking Attendant is expected to treat a request. This is not a booking
/// transaction: it reads the policy, applies documented rules in order, and labels every input it can't verify.
/// </summary>
public static class BookingSimulator
{
    public static SimulationResult Evaluate(BookingPolicy p, WorkingHoursConfig? hours, int? capacity, BookingScenario s)
    {
        var steps = new List<SimulationStep>();
        var assumptions = new List<string>();
        var limitations = new List<string>
        {
            Text.L("This is a configuration analysis, not an Exchange booking transaction. Actual processing depends on live free/busy data and service-side evaluation.", "Dit is een configuratieanalyse, geen boekingstransactie in Exchange. De werkelijke verwerking hangt af van live beschikbaarheidsgegevens en de beoordeling door de service."),
        };

        SimulationResult Done(SimulatedOutcome outcome, string headline) => new(outcome, headline, steps, assumptions, limitations);

        if (p.AutomateProcessing == AutomateProcessing.None)
        {
            steps.Add(new(Text.L("Calendar processing", "Agendaverwerking"), "AutomateProcessing", false, StepCertainty.Deterministic, Text.L("AutomateProcessing is None: neither attendant processes the request.", "AutomateProcessing is None: geen van beide attendants verwerkt de aanvraag.")));
            return Done(SimulatedOutcome.NotProcessed, Text.L("The request would not be processed automatically.", "De aanvraag zou niet automatisch worden verwerkt."));
        }
        if (p.AutomateProcessing == AutomateProcessing.AutoUpdate)
        {
            steps.Add(new(Text.L("Calendar processing", "Agendaverwerking"), "AutomateProcessing", false, StepCertainty.Deterministic, Text.L("AutoUpdate keeps the request tentative; delegates of a room mailbox don't receive it.", "AutoUpdate houdt de aanvraag voorlopig; gemachtigden van een ruimtepostvak ontvangen die niet.")));
            return Done(SimulatedOutcome.NotProcessed, Text.L("The request would remain tentative without an automatic decision.", "De aanvraag zou voorlopig blijven zonder automatische beslissing."));
        }
        steps.Add(new(Text.L("Calendar processing", "Agendaverwerking"), "AutomateProcessing", true, StepCertainty.Deterministic, Text.L("AutoAccept: the Resource Booking Attendant evaluates the request.", "AutoAccept: de Resource Booking Attendant beoordeelt de aanvraag.")));

        if (s.Organizer == OrganizerScope.External)
        {
            if (!p.ProcessExternalMeetingMessages)
            {
                steps.Add(new(Text.L("External organizer", "Externe organisator"), "ProcessExternalMeetingMessages", false, StepCertainty.Deterministic, Text.L("Requests from outside the organization are rejected.", "Aanvragen van buiten de organisatie worden geweigerd.")));
                return Done(SimulatedOutcome.Declined, Text.L("The request would be declined because external requests are not processed.", "De aanvraag zou worden geweigerd omdat externe aanvragen niet worden verwerkt."));
            }
            steps.Add(new(Text.L("External organizer", "Externe organisator"), "ProcessExternalMeetingMessages", true, StepCertainty.Deterministic, Text.L("External requests are processed by the room.", "Externe aanvragen worden door de ruimte verwerkt.")));
            limitations.Add(Text.L("Delivery of external requests also depends on mail flow, connectors and tenant sharing settings, which this analysis does not evaluate.", "De bezorging van externe aanvragen hangt ook af van e-mailstroom, connectors en instellingen voor delen van de tenant; die worden in deze analyse niet beoordeeld."));
        }

        if (p.BookingType == ResourceBookingType.Reserved)
        {
            steps.Add(new(Text.L("Booking type", "Boekingstype"), "BookingType", false, StepCertainty.Deterministic, Text.L("The room is Reserved and can't be booked.", "De ruimte is Reserved en kan niet worden geboekt.")));
            return Done(SimulatedOutcome.Declined, Text.L("The request would be declined because the room is reserved.", "De aanvraag zou worden geweigerd omdat de ruimte is gereserveerd."));
        }

        // ---- Policy checks: a failed check makes the request out-of-policy (not an immediate decline). ----
        var inPolicy = true;

        if (s.Recurring && !p.AllowRecurringMeetings)
        {
            inPolicy = false;
            steps.Add(new(Text.L("Recurring meetings", "Terugkerende vergaderingen"), "AllowRecurringMeetings", false, StepCertainty.Deterministic, Text.L("Recurring meetings are not allowed.", "Terugkerende vergaderingen zijn niet toegestaan.")));
        }

        var daysAhead = (s.StartLocal.Date - s.RequestedAt.Date).TotalDays;
        if (daysAhead < 0)
        {
            steps.Add(new(Text.L("Start date", "Begindatum"), "BookingWindowInDays", false, StepCertainty.Deterministic, Text.L("The meeting starts in the past relative to the request date.", "De vergadering begint in het verleden ten opzichte van de aanvraagdatum.")));
            inPolicy = false;
        }
        else if (daysAhead > p.BookingWindowInDays)
        {
            inPolicy = false;
            steps.Add(new(Text.L("Booking window", "Boekingsvenster"), "BookingWindowInDays", false, StepCertainty.Deterministic, Text.L($"The meeting starts {daysAhead:0} days ahead; the window is {p.BookingWindowInDays} days.", $"De vergadering begint over {daysAhead:0} dagen; het venster is {p.BookingWindowInDays} dagen.")));
        }
        else
        {
            steps.Add(new(Text.L("Booking window", "Boekingsvenster"), "BookingWindowInDays", true, StepCertainty.Deterministic, Text.L($"Starts {daysAhead:0} days ahead, within the {p.BookingWindowInDays}-day window.", $"Begint over {daysAhead:0} dagen, binnen het venster van {p.BookingWindowInDays} dagen.")));
        }

        if (s.Recurring && s.Occurrences > 1 && inPolicy)
        {
            steps.Add(new(Text.L("Scheduling horizon", "Planningshorizon"), "EnforceSchedulingHorizon", true, StepCertainty.Assumption,
                p.EnforceSchedulingHorizon
                    ? Text.L("If later occurrences fall beyond the booking window, the whole series is declined. The simulator does not expand the recurrence pattern.", "Als latere instanties buiten het boekingsvenster vallen, wordt de hele reeks geweigerd. De simulator werkt het herhalingspatroon niet uit.")
                    : Text.L("Occurrences beyond the booking window would be removed and the remaining series accepted.", "Instanties buiten het boekingsvenster zouden vervallen en de rest van de reeks zou worden geaccepteerd.")));
            assumptions.Add(Text.L("The recurrence pattern is not expanded; all occurrences are assumed to fall inside the booking window.", "Het herhalingspatroon wordt niet uitgewerkt; aangenomen wordt dat alle instanties binnen het boekingsvenster vallen."));
        }

        if (p.MaximumDurationInMinutes > 0 && s.DurationMinutes > p.MaximumDurationInMinutes)
        {
            inPolicy = false;
            steps.Add(new(Text.L("Maximum duration", "Maximale duur"), "MaximumDurationInMinutes", false, StepCertainty.Deterministic, Text.L($"{DurationText.Minutes(s.DurationMinutes)} exceeds the maximum of {DurationText.Minutes(p.MaximumDurationInMinutes)}.", $"{DurationText.Minutes(s.DurationMinutes)} is meer dan het maximum van {DurationText.Minutes(p.MaximumDurationInMinutes)}.")));
        }
        else
        {
            steps.Add(new(Text.L("Maximum duration", "Maximale duur"), "MaximumDurationInMinutes", true, StepCertainty.Deterministic, p.MaximumDurationInMinutes == 0 ? Text.L("No maximum duration.", "Geen maximale duur.") : Text.L($"{DurationText.Minutes(s.DurationMinutes)} is within {DurationText.Minutes(p.MaximumDurationInMinutes)}.", $"{DurationText.Minutes(s.DurationMinutes)} valt binnen {DurationText.Minutes(p.MaximumDurationInMinutes)}.")));
        }

        if (p.ScheduleOnlyDuringWorkHours)
        {
            if (hours is null)
            {
                steps.Add(new(Text.L("Working hours", "Werktijden"), "ScheduleOnlyDuringWorkHours", true, StepCertainty.RequiresLiveData, Text.L("Working hours were not loaded, so this check can't be evaluated.", "De werktijden zijn niet geladen, dus deze controle kan niet worden beoordeeld.")));
                limitations.Add(Text.L("Mailbox working hours were not available for this analysis.", "De werktijden van het postvak waren niet beschikbaar voor deze analyse."));
            }
            else
            {
                var end = s.StartLocal.AddMinutes(s.DurationMinutes);
                var dayOk = hours.Includes(s.StartLocal.DayOfWeek);
                var timeOk = TimeOnly.FromDateTime(s.StartLocal) >= hours.Start && end.Date == s.StartLocal.Date && TimeOnly.FromDateTime(end) <= hours.End;
                if (!dayOk || !timeOk)
                {
                    inPolicy = false;
                    steps.Add(new(Text.L("Working hours", "Werktijden"), "ScheduleOnlyDuringWorkHours", false, StepCertainty.Deterministic, Text.L($"The meeting is outside working hours ({hours.Describe()}).", $"De vergadering valt buiten de werktijden ({hours.Describe()}).")));
                }
                else
                {
                    steps.Add(new(Text.L("Working hours", "Werktijden"), "ScheduleOnlyDuringWorkHours", true, StepCertainty.Deterministic, Text.L($"Within working hours ({hours.Describe()}).", $"Binnen de werktijden ({hours.Describe()}).")));
                }
            }
        }

        if (p.EnforceCapacity)
        {
            if (s.Attendees is null || capacity is null or <= 0)
            {
                steps.Add(new(Text.L("Capacity", "Capaciteit"), "EnforceCapacity", true, StepCertainty.Assumption, Text.L("Attendee count or room capacity unknown; capacity enforcement not evaluated.", "Aantal deelnemers of capaciteit van de ruimte onbekend; capaciteit afdwingen is niet beoordeeld.")));
            }
            else if (s.Attendees > capacity)
            {
                inPolicy = false;
                steps.Add(new(Text.L("Capacity", "Capaciteit"), "EnforceCapacity", false, StepCertainty.Deterministic, Text.L($"{s.Attendees} attendees exceed the capacity of {capacity}.", $"{s.Attendees} deelnemers is meer dan de capaciteit van {capacity}.")));
            }
            else
            {
                steps.Add(new(Text.L("Capacity", "Capaciteit"), "EnforceCapacity", true, StepCertainty.Deterministic, Text.L($"{s.Attendees} attendees fit the capacity of {capacity}.", $"{s.Attendees} deelnemers passen binnen de capaciteit van {capacity}.")));
            }
        }

        // ---- Conflicts (availability). Busy = decline, independent of policy eligibility. ----
        var conflictResult = EvaluateConflicts(p, s, steps, assumptions);
        if (conflictResult == ConflictVerdict.SeriesDeclined)
        {
            return Done(SimulatedOutcome.Declined, Text.L("The request would be declined because of scheduling conflicts.", "De aanvraag zou worden geweigerd vanwege planningsconflicten."));
        }

        // ---- Eligibility. ----
        var delegatesAvailable = p.ResourceDelegates.Count > 0;
        if (inPolicy)
        {
            var canBook = p.AllBookInPolicy || Listed(s.InBookInPolicy, "BookInPolicy", assumptions);
            if (canBook)
            {
                steps.Add(new(Text.L("Automatic booking", "Automatisch boeken"), p.AllBookInPolicy ? "AllBookInPolicy" : "BookInPolicy", true, Certainty(p.AllBookInPolicy, s.InBookInPolicy),
                    p.AllBookInPolicy ? Text.L("All users may book in-policy requests automatically.", "Alle gebruikers mogen aanvragen binnen het beleid automatisch boeken.") : Text.L("The organizer is eligible for automatic booking.", "De organisator komt in aanmerking voor automatisch boeken.")));
                return Done(conflictResult == ConflictVerdict.SomeOccurrencesDeclined ? SimulatedOutcome.AcceptedWithDeclinedOccurrences : SimulatedOutcome.Accepted,
                    conflictResult == ConflictVerdict.SomeOccurrencesDeclined
                        ? Text.L("The series would likely be accepted, with conflicting occurrences declined.", "De reeks zou waarschijnlijk worden geaccepteerd, met weigering van conflicterende instanties.")
                        : Text.L("The request would likely be accepted automatically.", "De aanvraag zou waarschijnlijk automatisch worden geaccepteerd."));
            }
            steps.Add(new(Text.L("Automatic booking", "Automatisch boeken"), "AllBookInPolicy / BookInPolicy", false, Certainty(false, s.InBookInPolicy), Text.L("The organizer is not eligible for automatic booking.", "De organisator komt niet in aanmerking voor automatisch boeken.")));

            var canRequest = p.AllRequestInPolicy || Listed(s.InRequestInPolicy, "RequestInPolicy", assumptions);
            if (canRequest)
            {
                return Forward(p, steps, delegatesAvailable, Text.L("In-policy request", "Aanvraag binnen het beleid"), p.AllRequestInPolicy ? "AllRequestInPolicy" : "RequestInPolicy", Certainty(p.AllRequestInPolicy, s.InRequestInPolicy), assumptions, limitations);
            }
            steps.Add(new(Text.L("In-policy request", "Aanvraag binnen het beleid"), "AllRequestInPolicy / RequestInPolicy", false, Certainty(false, s.InRequestInPolicy), Text.L("The organizer may not submit in-policy requests for approval.", "De organisator mag geen aanvragen binnen het beleid ter goedkeuring indienen.")));
            return Done(SimulatedOutcome.Declined, Text.L("The request would be declined: the organizer is not permitted to book or request this room.", "De aanvraag zou worden geweigerd: de organisator mag deze ruimte niet boeken of aanvragen."));
        }

        var canRequestOut = p.AllRequestOutOfPolicy || Listed(s.InRequestOutOfPolicy, "RequestOutOfPolicy", assumptions);
        if (canRequestOut)
        {
            return Forward(p, steps, delegatesAvailable, Text.L("Out-of-policy request", "Aanvraag buiten het beleid"), p.AllRequestOutOfPolicy ? "AllRequestOutOfPolicy" : "RequestOutOfPolicy", Certainty(p.AllRequestOutOfPolicy, s.InRequestOutOfPolicy), assumptions, limitations);
        }
        steps.Add(new(Text.L("Out-of-policy request", "Aanvraag buiten het beleid"), "AllRequestOutOfPolicy / RequestOutOfPolicy", false, Certainty(false, s.InRequestOutOfPolicy), Text.L("The request is out of policy and the organizer may not submit out-of-policy requests.", "De aanvraag valt buiten het beleid en de organisator mag geen aanvragen buiten het beleid indienen.")));
        return Done(SimulatedOutcome.Declined, Text.L("The request would be declined because it is outside the booking policy.", "De aanvraag zou worden geweigerd omdat die buiten het boekingsbeleid valt."));
    }

    private enum ConflictVerdict
    {
        None,
        SomeOccurrencesDeclined,
        SeriesDeclined,
    }

    private static ConflictVerdict EvaluateConflicts(BookingPolicy p, BookingScenario s, List<SimulationStep> steps, List<string> assumptions)
    {
        assumptions.Add(s.ConflictingOccurrences > 0
            ? Text.L($"The administrator stated that {s.ConflictingOccurrences} occurrence(s) conflict with existing bookings.", $"De beheerder heeft aangegeven dat {s.ConflictingOccurrences} instantie(s) conflicteren met bestaande boekingen.")
            : Text.L("The administrator stated that the room is free for the requested time.", "De beheerder heeft aangegeven dat de ruimte op het gevraagde tijdstip vrij is."));
        if (s.ConflictingOccurrences <= 0)
        {
            steps.Add(new(Text.L("Availability", "Beschikbaarheid"), "AllowConflicts", true, StepCertainty.Assumption, Text.L("No conflict in the scenario.", "Geen conflict in het scenario.")));
            return ConflictVerdict.None;
        }
        if (p.AllowConflicts)
        {
            steps.Add(new(Text.L("Availability", "Beschikbaarheid"), "AllowConflicts", true, StepCertainty.Assumption, Text.L("Conflicts are allowed; the request is not declined for conflicts.", "Conflicten zijn toegestaan; de aanvraag wordt niet vanwege conflicten geweigerd.")));
            return ConflictVerdict.None;
        }
        if (!s.Recurring || s.Occurrences <= 1)
        {
            steps.Add(new(Text.L("Availability", "Beschikbaarheid"), "AllowConflicts", false, StepCertainty.Assumption, Text.L("A single meeting that conflicts is declined.", "Eén vergadering die conflicteert, wordt geweigerd.")));
            return ConflictVerdict.SeriesDeclined;
        }
        var conflicts = Math.Min(s.ConflictingOccurrences, s.Occurrences);
        var percentage = conflicts * 100.0 / s.Occurrences;
        var overCount = conflicts > p.MaximumConflictInstances;
        var overPct = percentage > p.ConflictPercentageAllowed;
        if (overCount || overPct)
        {
            steps.Add(new(Text.L("Recurring conflicts", "Terugkerende conflicten"), overCount ? "MaximumConflictInstances" : "ConflictPercentageAllowed", false, StepCertainty.Assumption,
                Text.L($"{conflicts} of {s.Occurrences} occurrences conflict ({percentage:0.#}%), above the limits ({p.MaximumConflictInstances} instances / {p.ConflictPercentageAllowed}%). The series is declined.", $"{conflicts} van {s.Occurrences} instanties conflicteren ({percentage:0.#}%), boven de limieten ({p.MaximumConflictInstances} instanties / {p.ConflictPercentageAllowed}%). De reeks wordt geweigerd.")));
            return ConflictVerdict.SeriesDeclined;
        }
        steps.Add(new(Text.L("Recurring conflicts", "Terugkerende conflicten"), "ConflictPercentageAllowed / MaximumConflictInstances", true, StepCertainty.Assumption,
            Text.L($"{conflicts} of {s.Occurrences} occurrences conflict ({percentage:0.#}%), within the limits. Conflicting occurrences are declined.", $"{conflicts} van {s.Occurrences} instanties conflicteren ({percentage:0.#}%), binnen de limieten. Conflicterende instanties worden geweigerd.")));
        return ConflictVerdict.SomeOccurrencesDeclined;
    }

    private static SimulationResult Forward(BookingPolicy p, List<SimulationStep> steps, bool delegatesAvailable, string rule, string parameter, StepCertainty certainty, List<string> assumptions, List<string> limitations)
    {
        steps.Add(new(rule, parameter, true, certainty, Text.L("The organizer may submit this request for delegate approval.", "De organisator mag deze aanvraag ter goedkeuring door een gemachtigde indienen.")));
        if (!delegatesAvailable)
        {
            steps.Add(new(Text.L("Delegates", "Gemachtigden"), "ResourceDelegates", false, StepCertainty.Deterministic, Text.L("No booking delegates are configured, so nobody can approve the request.", "Er zijn geen boekingsgemachtigden ingesteld, dus niemand kan de aanvraag goedkeuren.")));
            return new(SimulatedOutcome.Indeterminate, Text.L("The request would need approval, but no delegates are configured.", "De aanvraag zou goedkeuring nodig hebben, maar er zijn geen gemachtigden ingesteld."), steps, assumptions, limitations);
        }
        steps.Add(new(Text.L("Delegates", "Gemachtigden"), "ForwardRequestsToDelegates", p.ForwardRequestsToDelegates, StepCertainty.Deterministic,
            p.ForwardRequestsToDelegates ? Text.L($"The request is forwarded to {p.ResourceDelegates.Count} delegate(s).", $"De aanvraag wordt doorgestuurd naar {p.ResourceDelegates.Count} gemachtigde(n).") : Text.L("Requests are not forwarded; delegates must act from the room calendar.", "Aanvragen worden niet doorgestuurd; gemachtigden moeten vanuit de agenda van de ruimte handelen.")));
        steps.Add(new(Text.L("Pending state", "Status in afwachting"), "TentativePendingApproval", true, StepCertainty.Deterministic,
            p.TentativePendingApproval ? Text.L("Shown as tentative in the room calendar while pending.", "Wordt als voorlopig getoond in de agenda van de ruimte zolang de aanvraag openstaat.") : Text.L("The room appears free while pending.", "De ruimte lijkt vrij zolang de aanvraag openstaat.")));
        return new(SimulatedOutcome.ForwardedForApproval, Text.L("The request would wait for delegate approval.", "De aanvraag zou wachten op goedkeuring door een gemachtigde."), steps, assumptions, limitations);
    }

    private static bool Listed(ListMembership membership, string list, List<string> assumptions)
    {
        if (membership == ListMembership.MemberOfListedGroup)
        {
            assumptions.Add(Text.L($"The organizer is assumed to be a direct member of a group listed in {list}. Nested group membership is not honored by Exchange.", $"Aangenomen wordt dat de organisator direct lid is van een groep in {list}. Lidmaatschap van geneste groepen wordt door Exchange niet meegenomen."));
        }
        return membership != ListMembership.NotListed;
    }

    private static StepCertainty Certainty(bool fromAllFlag, ListMembership membership) =>
        fromAllFlag || membership == ListMembership.ListedDirectly ? StepCertainty.Deterministic : StepCertainty.Assumption;
}
