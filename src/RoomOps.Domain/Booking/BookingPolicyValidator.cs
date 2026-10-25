using RoomOps.Domain.Common;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Domain.Booking;

/// <summary>
/// Validates a desired booking policy. Errors are values Exchange rejects or combinations that make the policy unusable;
/// warnings are valid-but-surprising combinations derived from documented parameter interactions.
/// </summary>
public static class BookingPolicyValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(BookingPolicy p, WorkingHoursConfig? workingHours = null, int? capacity = null)
    {
        var issues = new List<ValidationIssue>();

        foreach (var field in BookingPolicyFields.All.Where(f => f.Kind == PolicyFieldKind.Integer))
        {
            var value = (int)field.Get(p)!;
            if (field.Min is { } min && value < min || field.Max is { } max && value > max)
            {
                issues.Add(new(field.Name, Text.L($"{field.Label} must be between {field.Min} and {field.Max}.", $"{field.Label} moet tussen {field.Min} en {field.Max} liggen."), IssueLevel.Error));
            }
        }

        if (p.MinimumDurationInMinutes > 0 && p.MaximumDurationInMinutes > 0 && p.MinimumDurationInMinutes > p.MaximumDurationInMinutes)
        {
            issues.Add(new(nameof(p.MinimumDurationInMinutes), Text.L("The minimum duration exceeds the maximum duration, so no meeting could satisfy both.", "De minimale duur is groter dan de maximale duur, dus geen enkele vergadering kan aan beide voldoen."), IssueLevel.Error));
        }

        if (p.MinimumDurationInMinutes > 0)
        {
            issues.Add(new(nameof(p.MinimumDurationInMinutes), Text.L("Microsoft documents that the minimum duration works only on workspace mailboxes; it has no effect on room mailboxes.", "Volgens Microsoft werkt de minimale duur alleen bij werkplekpostvakken; bij ruimtepostvakken heeft die geen effect."), IssueLevel.Info));
        }

        if (p.AddAdditionalResponse && string.IsNullOrWhiteSpace(p.AdditionalResponse))
        {
            issues.Add(new(nameof(p.AdditionalResponse), Text.L("Custom response text is enabled but empty.", "Eigen antwoordtekst is ingeschakeld, maar leeg."), IssueLevel.Warning));
        }

        if (p.AutomateProcessing == AutomateProcessing.AutoAccept)
        {
            var approvalPathConfigured = !p.AllBookInPolicy && (p.AllRequestInPolicy || p.RequestInPolicy.Count > 0)
                || p.AllRequestOutOfPolicy || p.RequestOutOfPolicy.Count > 0;
            if (approvalPathConfigured && p.ResourceDelegates.Count == 0)
            {
                issues.Add(new(nameof(p.ResourceDelegates), Text.L("Requests are configured to need delegate approval, but no booking delegates are assigned. Those requests can't be approved.", "Aanvragen zijn ingesteld om goedkeuring van een gemachtigde te vereisen, maar er zijn geen boekingsgemachtigden toegewezen. Die aanvragen kunnen niet worden goedgekeurd."), IssueLevel.Error));
            }
            if (approvalPathConfigured && p.ResourceDelegates.Count > 0 && !p.ForwardRequestsToDelegates)
            {
                issues.Add(new(nameof(p.ForwardRequestsToDelegates), Text.L("Requests that need approval are not forwarded to delegates. Delegates must monitor the room calendar themselves.", "Aanvragen die goedkeuring nodig hebben, worden niet doorgestuurd naar gemachtigden. Gemachtigden moeten de agenda van de ruimte zelf in de gaten houden."), IssueLevel.Warning));
            }
            if (!p.AllBookInPolicy && p.BookInPolicy.Count == 0 && !p.AllRequestInPolicy && p.RequestInPolicy.Count == 0)
            {
                issues.Add(new(nameof(p.AllBookInPolicy), Text.L("Nobody is allowed to book or request this room in policy. In-policy requests from every organizer will be declined.", "Niemand mag deze ruimte binnen het beleid boeken of aanvragen. Aanvragen binnen het beleid van elke organisator worden geweigerd."), IssueLevel.Warning));
            }
            if (p.AllBookInPolicy && p.AllRequestInPolicy)
            {
                issues.Add(new(nameof(p.AllRequestInPolicy), Text.L("All users already book automatically, so 'All users may request in-policy' has no visible effect.", "Alle gebruikers boeken al automatisch, dus 'Alle gebruikers mogen binnen het beleid aanvragen' heeft geen zichtbaar effect."), IssueLevel.Info));
            }
        }
        else
        {
            var autoAcceptOnlyChanged = BookingPolicyFields.All
                .Where(f => f.AutoAcceptOnly && !f.AreEqual(p, BookingPolicy.ExchangeDefaults))
                .Select(f => f.Label.ToString())
                .ToList();
            if (autoAcceptOnlyChanged.Count > 0)
            {
                issues.Add(new(nameof(p.AutomateProcessing), Text.L($"These settings apply only when calendar processing is AutoAccept and currently have no effect: {string.Join(", ", autoAcceptOnlyChanged)}.", $"Deze instellingen gelden alleen als de agendaverwerking AutoAccept is en hebben nu geen effect: {string.Join(", ", autoAcceptOnlyChanged)}."), IssueLevel.Info));
            }
        }

        if (p.AutomateProcessing == AutomateProcessing.AutoUpdate && p.ResourceDelegates.Count > 0)
        {
            issues.Add(new(nameof(p.AutomateProcessing), Text.L("With AutoUpdate on a room mailbox, delegates don't get requests (documented Exchange behavior). Use AutoAccept with delegate approval instead.", "Met AutoUpdate op een ruimtepostvak ontvangen gemachtigden geen aanvragen (gedocumenteerd gedrag van Exchange). Gebruik in plaats daarvan AutoAccept met goedkeuring door een gemachtigde."), IssueLevel.Warning));
        }

        if (p.AutomateProcessing == AutomateProcessing.None)
        {
            issues.Add(new(nameof(p.AutomateProcessing), Text.L("Calendar processing is disabled: the room will neither accept nor decline requests automatically.", "Agendaverwerking is uitgeschakeld: de ruimte accepteert of weigert aanvragen niet automatisch."), IssueLevel.Warning));
        }

        if (p.AllowConflicts && (p.ConflictPercentageAllowed > 0 || p.MaximumConflictInstances > 0))
        {
            issues.Add(new(nameof(p.AllowConflicts), Text.L("Conflicts are allowed, so the conflict percentage and maximum conflict instances are ignored.", "Conflicten zijn toegestaan, dus het conflictpercentage en het maximale aantal conflicterende instanties worden genegeerd."), IssueLevel.Info));
        }

        if (!p.AllowRecurringMeetings && (p.ConflictPercentageAllowed > 0 || p.MaximumConflictInstances > 0))
        {
            issues.Add(new(nameof(p.MaximumConflictInstances), Text.L("Conflict thresholds only apply to recurring meetings, which are not allowed.", "Conflictdrempels gelden alleen voor terugkerende vergaderingen, en die zijn niet toegestaan."), IssueLevel.Info));
        }

        if (p.ScheduleOnlyDuringWorkHours && workingHours is { Days: WorkDays.None })
        {
            issues.Add(new(nameof(p.ScheduleOnlyDuringWorkHours), Text.L("Booking is limited to working hours, but the mailbox has no work days. Every request would be declined.", "Boeken is beperkt tot werktijden, maar het postvak heeft geen werkdagen. Elke aanvraag zou worden geweigerd."), IssueLevel.Error));
        }

        if (p.EnforceCapacity && capacity is null or <= 0)
        {
            issues.Add(new(nameof(p.EnforceCapacity), Text.L("Capacity enforcement is enabled but the room has no capacity configured.", "Capaciteit afdwingen is ingeschakeld, maar voor de ruimte is geen capaciteit ingesteld."), IssueLevel.Warning));
        }

        foreach (var field in BookingPolicyFields.All.Where(f => f.Kind == PolicyFieldKind.RecipientList))
        {
            var list = (IReadOnlyList<RecipientRef>)field.Get(p)!;
            foreach (var r in list.Where(r => r.Resolution is RecipientResolution.Unresolved or RecipientResolution.Ambiguous))
            {
                issues.Add(new(field.Name, Text.L($"'{r.Identity}' in {field.Label} can't be resolved to a single recipient. Exchange will reject the update until it is removed or corrected.", $"'{r.Identity}' in {field.Label} verwijst niet naar één ontvanger. Exchange weigert de wijziging totdat dit is verwijderd of gecorrigeerd."), IssueLevel.Error));
            }
            foreach (var r in list.Where(r => r.IsDynamicGroup))
            {
                issues.Add(new(field.Name, Text.L($"'{r.DisplayName ?? r.Identity}' is a dynamic distribution group. Query-based groups aren't supported in {field.Label}.", $"'{r.DisplayName ?? r.Identity}' is een dynamische distributiegroep. Groepen op basis van een query worden niet ondersteund in {field.Label}."), IssueLevel.Error));
            }
            if (list.Any(r => r.IsGroup))
            {
                issues.Add(new(field.Name, Text.L($"{field.Label} contains a group. Only non-nested group membership is honored for resource mailbox settings; members of nested groups are not.", $"{field.Label} bevat een groep. Voor instellingen van resourcepostvakken telt alleen niet-genest groepslidmaatschap; leden van geneste groepen niet."), IssueLevel.Info));
            }
            var duplicates = list.GroupBy(r => r.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                issues.Add(new(field.Name, Text.L($"{field.Label} lists the same recipient more than once: {string.Join(", ", duplicates)}.", $"{field.Label} vermeldt dezelfde ontvanger meer dan eens: {string.Join(", ", duplicates)}."), IssueLevel.Warning));
            }
        }

        return issues;
    }
}
