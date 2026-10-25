namespace RoomOps.Domain.Booking;

using RoomOps.Domain.Common;
using RoomOps.Domain.Rooms;

/// <summary>Set-CalendarProcessing -AutomateProcessing values.</summary>
public enum AutomateProcessing
{
    None,
    AutoUpdate,
    AutoAccept,
}

/// <summary>Set-CalendarProcessing -BookingType values (Exchange Online only).</summary>
public enum ResourceBookingType
{
    Standard,
    Reserved,
}

/// <summary>
/// Calendar processing configuration of a resource mailbox, mirroring Get-/Set-CalendarProcessing.
/// Defaults are the documented Exchange Online defaults (Set-CalendarProcessing reference, Microsoft Learn).
/// </summary>
public sealed record BookingPolicy
{
    // General
    public AutomateProcessing AutomateProcessing { get; init; } = AutomateProcessing.AutoAccept;
    public ResourceBookingType BookingType { get; init; } = ResourceBookingType.Standard;
    public bool AllowRecurringMeetings { get; init; } = true;

    // Scheduling constraints
    public int BookingWindowInDays { get; init; } = 180;
    public int MaximumDurationInMinutes { get; init; } = 1440;
    public int MinimumDurationInMinutes { get; init; }
    public bool ScheduleOnlyDuringWorkHours { get; init; }
    public bool EnforceSchedulingHorizon { get; init; } = true;
    public bool EnforceCapacity { get; init; }

    // Conflicts
    public bool AllowConflicts { get; init; }
    public int ConflictPercentageAllowed { get; init; }
    public int MaximumConflictInstances { get; init; }

    // Permissions
    public bool AllBookInPolicy { get; init; } = true;
    public bool AllRequestInPolicy { get; init; }
    public bool AllRequestOutOfPolicy { get; init; }
    public IReadOnlyList<RecipientRef> BookInPolicy { get; init; } = [];
    public IReadOnlyList<RecipientRef> RequestInPolicy { get; init; } = [];
    public IReadOnlyList<RecipientRef> RequestOutOfPolicy { get; init; } = [];
    public IReadOnlyList<RecipientRef> ResourceDelegates { get; init; } = [];
    public bool ForwardRequestsToDelegates { get; init; } = true;
    public bool TentativePendingApproval { get; init; } = true;

    // Privacy and content handling
    public bool AddOrganizerToSubject { get; init; } = true;
    public bool DeleteSubject { get; init; } = true;
    public bool DeleteComments { get; init; } = true;
    public bool DeleteAttachments { get; init; } = true;
    public bool DeleteNonCalendarItems { get; init; } = true;
    public bool OrganizerInfo { get; init; } = true;
    public bool RemovePrivateProperty { get; init; } = true;
    public bool RemoveCanceledMeetings { get; init; }
    public bool RemoveOldMeetingMessages { get; init; } = true;
    public bool RemoveForwardedMeetingNotifications { get; init; }
    public bool AddNewRequestsTentatively { get; init; } = true;
    public bool EnableResponseDetails { get; init; } = true;

    // External
    public bool ProcessExternalMeetingMessages { get; init; }

    // Responses
    public bool AddAdditionalResponse { get; init; }
    public string? AdditionalResponse { get; init; }

    public static BookingPolicy ExchangeDefaults { get; } = new();
}

/// <summary>Friendly classification of a policy, used in tables and filters. Derived, never stored.</summary>
public enum BookingMode
{
    Automatic,
    DelegateApproval,
    Restricted,
    Hybrid,
    Custom,
    Reserved,
    ManualAutoUpdate,
    ProcessingDisabled,
}

public static class BookingModeClassifier
{
    public static BookingMode Classify(BookingPolicy p)
    {
        if (p.AutomateProcessing == AutomateProcessing.None)
        {
            return BookingMode.ProcessingDisabled;
        }
        if (p.AutomateProcessing == AutomateProcessing.AutoUpdate)
        {
            return BookingMode.ManualAutoUpdate;
        }
        if (p.BookingType == ResourceBookingType.Reserved)
        {
            return BookingMode.Reserved;
        }
        // The booking access models are the single definition of the friendly modes; anything else is a custom configuration.
        return BookingPresets.Detect(p)?.Mode ?? BookingMode.Custom;
    }

    public static string Label(BookingMode mode) => mode switch
    {
        BookingMode.Automatic => Text.L("Everyone", "Iedereen"),
        BookingMode.DelegateApproval => Text.L("Delegate approval", "Goedkeuring door gemachtigde"),
        BookingMode.Restricted => Text.L("Selected users only", "Alleen geselecteerde gebruikers"),
        BookingMode.Hybrid => Text.L("Hybrid", "Hybride"),
        BookingMode.Custom => Text.L("Custom", "Aangepast"),
        BookingMode.Reserved => Text.L("Reserved", "Gereserveerd"),
        BookingMode.ManualAutoUpdate => Text.L("Manual (AutoUpdate)", "Handmatig (AutoUpdate)"),
        BookingMode.ProcessingDisabled => Text.L("Processing disabled", "Verwerking uitgeschakeld"),
        _ => mode.ToString(),
    };
}
