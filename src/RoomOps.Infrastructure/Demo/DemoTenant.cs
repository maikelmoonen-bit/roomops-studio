using System.Globalization;
using System.Text.Json.Nodes;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Places;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Infrastructure.Demo;

internal sealed class DemoRecipient
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string DisplayName { get; set; }
    public required string Alias { get; init; }
    public required string PrimarySmtpAddress { get; init; }
    public required string RecipientTypeDetails { get; set; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public int? ResourceCapacity { get; set; }
    public bool IsDirSynced { get; set; }
    public DateTimeOffset WhenCreated { get; init; }
    public DateTimeOffset AvailableAt { get; init; }
    public bool AccountDisabled { get; set; } = true;
    public BookingPolicy Policy { get; set; } = BookingPolicy.ExchangeDefaults;
    public WorkingHoursConfig Hours { get; set; } = WorkingHoursConfig.ExchangeDefault;
    public string CalendarFolder { get; init; } = "Calendar";
    public List<(string User, string Smtp, string[] Rights, string[] Flags)> FolderPermissions { get; } = [];
    public List<string> FullAccess { get; } = [];
    public List<string> Members { get; } = [];
    public bool GraphAccountEnabled { get; set; }
    public List<string> Licenses { get; } = [];
    public string? PasswordPolicies { get; set; }
    public bool IsRoom => RecipientTypeDetails == "RoomMailbox";
    public bool IsGroup => RecipientTypeDetails is "RoomList" or "MailUniversalDistributionGroup" or "MailUniversalSecurityGroup" or "DynamicDistributionGroup";
}

internal sealed class DemoPlace
{
    public required string PlaceId { get; init; }
    public required PlaceType Type { get; init; }
    public required string DisplayName { get; set; }
    public string? ParentId { get; set; }
    public string? EmailAddress { get; set; }
    public string? Label { get; set; }
    public int? Capacity { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool? IsWheelChairAccessible { get; set; }
    public string? AudioDeviceName { get; set; }
    public string? VideoDeviceName { get; set; }
    public string? DisplayDeviceName { get; set; }
    public bool? MtrEnabled { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? CountryOrRegion { get; set; }
    public int? SortOrder { get; set; }
    public string? FloorLabel { get; set; }
    public DateTimeOffset AvailableAt { get; init; }
}

/// <summary>
/// Synthetic tenant for demonstration mode. All names, addresses and identifiers are fictional and use the reserved
/// ".example" domain; nothing here is ever sent to Microsoft 365.
/// </summary>
internal sealed partial class DemoTenant
{
    public const string Domain = "contoso-demo.example";
    public const string TenantName = "Contoso (Demo)";
    public const string AdminAccount = "demo.admin@contoso-demo.example";
    public static readonly string TenantId = "00000000-d3e0-4d3e-8d3e-000000000001";

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly List<DemoRecipient> _recipients = [];
    private readonly List<DemoPlace> _places = [];
    private int _seq = 100;

    public DemoTenant(TimeProvider time)
    {
        _time = time;
        Seed();
    }

    public DateTimeOffset Now => _time.GetUtcNow();
    internal object Gate => _gate;
    internal List<DemoRecipient> Recipients => _recipients;
    internal List<DemoPlace> Places => _places;

    internal string NewId() => Guid.NewGuid().ToString();

    private static string Guidish(int n) => $"5e1d{n:x4}-0d3e-4a11-9c0f-{n:x12}";

    private DemoRecipient AddUser(string first, string last, string alias, string type = "UserMailbox")
    {
        var r = new DemoRecipient
        {
            Id = Guidish(++_seq),
            Name = $"{first} {last}".Trim(),
            DisplayName = $"{first} {last}".Trim(),
            FirstName = first,
            LastName = last,
            Alias = alias,
            PrimarySmtpAddress = $"{alias}@{Domain}",
            RecipientTypeDetails = type,
            WhenCreated = Now.AddDays(-700),
            AvailableAt = DateTimeOffset.MinValue,
            AccountDisabled = false,
            GraphAccountEnabled = true,
        };
        _recipients.Add(r);
        return r;
    }

    private DemoRecipient AddGroup(string displayName, string alias, string type, params DemoRecipient[] members)
    {
        var g = new DemoRecipient
        {
            Id = Guidish(++_seq),
            Name = displayName,
            DisplayName = displayName,
            Alias = alias,
            PrimarySmtpAddress = $"{alias}@{Domain}",
            RecipientTypeDetails = type,
            WhenCreated = Now.AddDays(-500),
            AvailableAt = DateTimeOffset.MinValue,
        };
        g.Members.AddRange(members.Select(m => m.Id));
        _recipients.Add(g);
        return g;
    }

    private DemoPlace AddPlace(PlaceType type, string name, DemoPlace? parent, Action<DemoPlace>? configure = null)
    {
        var p = new DemoPlace { PlaceId = Guidish(++_seq), Type = type, DisplayName = name, ParentId = parent?.PlaceId, AvailableAt = DateTimeOffset.MinValue };
        configure?.Invoke(p);
        _places.Add(p);
        return p;
    }

    private DemoRecipient AddRoom(string displayName, string alias, int? capacity, DemoPlace? parent, string folder, Action<DemoRecipient>? configure = null, Action<DemoPlace>? place = null, bool inPlaces = true)
    {
        var room = new DemoRecipient
        {
            Id = Guidish(++_seq),
            Name = alias,
            DisplayName = displayName,
            Alias = alias,
            PrimarySmtpAddress = $"{alias}@{Domain}",
            RecipientTypeDetails = "RoomMailbox",
            ResourceCapacity = capacity,
            WhenCreated = Now.AddDays(-400 + _seq),
            AvailableAt = DateTimeOffset.MinValue,
            CalendarFolder = folder,
            Hours = new WorkingHoursConfig(WorkDays.Weekdays, new TimeOnly(8, 0), new TimeOnly(18, 0), "W. Europe Standard Time"),
        };
        room.FolderPermissions.Add(("Default", string.Empty, ["AvailabilityOnly"], []));
        room.FolderPermissions.Add(("Anonymous", string.Empty, ["None"], []));
        configure?.Invoke(room);
        _recipients.Add(room);
        if (inPlaces)
        {
            AddPlace(PlaceType.Room, displayName, parent, p =>
            {
                p.EmailAddress = room.PrimarySmtpAddress;
                p.Capacity = capacity;
                place?.Invoke(p);
            });
        }
        return room;
    }

    private static RecipientRef Ref(DemoRecipient r) => new() { Identity = r.Name, Resolution = RecipientResolution.NotChecked };

    private void Seed()
    {
        // People and groups.
        var sanne = AddUser("Sanne", "de Vries", "sanne.devries");
        var daan = AddUser("Daan", "Jansen", "daan.jansen");
        var emma = AddUser("Emma", "Bakker", "emma.bakker");
        var lucas = AddUser("Lucas", "Visser", "lucas.visser");
        var noah = AddUser("Noah", "Smit", "noah.smit");
        var julia = AddUser("Julia", "Meijer", "julia.meijer");
        var mila = AddUser("Mila", "de Boer", "mila.deboer");
        var facilityDesk = AddUser("Facility", "Desk", "facility.desk", "SharedMailbox");
        facilityDesk.Name = "Facility Desk";
        facilityDesk.DisplayName = "Facility Desk";
        var reception = AddUser("Reception", "Eindhoven", "reception.ehv", "SharedMailbox");
        // Two recipients share the name "Alex Peters" to demonstrate ambiguous identity resolution.
        AddUser("Alex", "Peters", "alex.peters");
        AddUser("Alex", "Peters", "alex.peters2");

        var execAssistants = AddGroup("Executive Assistants", "executive.assistants", "MailUniversalDistributionGroup", daan, julia);
        var facilityTeam = AddGroup("Facility Team", "facility.team", "MailUniversalSecurityGroup", sanne, emma, facilityDesk);
        var trainers = AddGroup("Training Coordinators", "training.coordinators", "MailUniversalDistributionGroup", noah, mila);
        AddGroup("All Employees (dynamic)", "all.employees", "DynamicDistributionGroup");

        // Places hierarchy.
        var ehv = AddPlace(PlaceType.Building, "Eindhoven Headquarters", null, p => { p.Street = "Lichtstraat 12"; p.City = "Eindhoven"; p.PostalCode = "5611 AB"; p.CountryOrRegion = "NL"; p.Label = "HQ"; });
        var ehv0 = AddPlace(PlaceType.Floor, "Ground floor", ehv, p => p.SortOrder = 0);
        var ehv1 = AddPlace(PlaceType.Floor, "Floor 1", ehv, p => p.SortOrder = 1);
        var ehv2 = AddPlace(PlaceType.Floor, "Floor 2", ehv, p => p.SortOrder = 2);
        var ehv3 = AddPlace(PlaceType.Floor, "Floor 3", ehv, p => p.SortOrder = 3);
        var ehv3n = AddPlace(PlaceType.Section, "North wing", ehv3);
        var ehv3s = AddPlace(PlaceType.Section, "South wing", ehv3);
        var ams = AddPlace(PlaceType.Building, "Amsterdam Zuidas", null, p => { p.Street = "Parnassusweg 81"; p.City = "Amsterdam"; p.PostalCode = "1077 DD"; p.CountryOrRegion = "NL"; });
        var ams4 = AddPlace(PlaceType.Floor, "Floor 4", ams, p => p.SortOrder = 4);
        var ams5 = AddPlace(PlaceType.Floor, "Floor 5", ams, p => p.SortOrder = 5);
        var utr = AddPlace(PlaceType.Building, "Utrecht Innovation Hub", null, p => { p.Street = "Catharijnesingel 30"; p.City = "Utrecht"; p.PostalCode = "3511 GB"; p.CountryOrRegion = "NL"; });
        var utr1 = AddPlace(PlaceType.Floor, "Floor 1", utr, p => p.SortOrder = 1);
        var utr2 = AddPlace(PlaceType.Floor, "Floor 2", utr, p => p.SortOrder = 2);
        var utrLab = AddPlace(PlaceType.Section, "Lab area", utr2);

        var standard = BookingPolicy.ExchangeDefaults with { BookingWindowInDays = 90, MaximumDurationInMinutes = 240 };

        // Eindhoven.
        var ehvVisitor = AddRoom("EHV 0.04 Visitor Room", "ehv-0.04", 4, ehv0, "Agenda", r => r.Policy = standard, p => { p.DisplayDeviceName = "65\" display"; p.IsWheelChairAccessible = true; });
        var ehvAuditorium = AddRoom("EHV 0.10 Auditorium", "ehv-0.10", 120, ehv0, "Agenda", r =>
        {
            r.Policy = standard with { AllBookInPolicy = false, AllRequestInPolicy = true, ForwardRequestsToDelegates = true, ResourceDelegates = [Ref(sanne), Ref(facilityDesk)], MaximumDurationInMinutes = 480, BookingWindowInDays = 365 };
            r.FullAccess.Add(facilityTeam.PrimarySmtpAddress);
            r.FolderPermissions.Add((sanne.DisplayName, sanne.PrimarySmtpAddress, ["Editor"], ["Delegate"]));
        }, p => { p.AudioDeviceName = "Ceiling microphone array"; p.VideoDeviceName = "PTZ camera"; p.DisplayDeviceName = "Dual laser projector"; p.IsWheelChairAccessible = true; p.Tags = ["Auditorium", "Events"]; });
        var innovationEhv = AddRoom("Innovation Space", "ehv-0.12", 10, ehv0, "Agenda", r => r.Policy = standard);
        var ehvFocus = AddRoom("EHV 1.02 Focus", "ehv-1.02", 2, ehv1, "Agenda", r => r.Policy = standard with { MaximumDurationInMinutes = 120 });
        var ehvHuddle = AddRoom("EHV 1.07 Huddle", "ehv-1.07", 4, ehv1, "Agenda", r => r.Policy = standard with { MaximumDurationInMinutes = 120 }, p => p.DisplayDeviceName = "55\" display");
        var ehvProject = AddRoom("EHV 1.15 Project Room", "ehv-1.15", 8, ehv1, "Agenda", r =>
        {
            r.Policy = standard;
            // Full Access alongside a limited folder permission: Full Access wins, which the delegates view points out.
            r.FullAccess.Add(julia.PrimarySmtpAddress);
            r.FolderPermissions.Add((julia.DisplayName, julia.PrimarySmtpAddress, ["LimitedDetails"], []));
        }, p => { p.DisplayDeviceName = "75\" display"; p.Tags = ["Whiteboard"]; });
        var ehvQuiet = AddRoom("EHV 1.20 Quiet Room", "ehv-1.20", 3, null, "Agenda", r => r.Policy = standard);
        var ehvCollab = AddRoom("EHV 2.03 Collaboration", "ehv-2.03", 6, ehv2, "Agenda", r => r.Policy = standard, p => p.DisplayDeviceName = "Interactive display");
        var ehvTraining = AddRoom("EHV 2.11 Training Room", "ehv-2.11", 24, ehv2, "Agenda", r =>
        {
            r.Policy = standard with { BookingWindowInDays = 365, MaximumDurationInMinutes = 600, ConflictPercentageAllowed = 10, MaximumConflictInstances = 2, ScheduleOnlyDuringWorkHours = true };
            r.Hours = new WorkingHoursConfig(WorkDays.Weekdays, new TimeOnly(7, 30), new TimeOnly(19, 0), "W. Europe Standard Time");
        }, p => { p.DisplayDeviceName = "Projector"; p.AudioDeviceName = "Wireless microphones"; p.IsWheelChairAccessible = true; });
        var ehvBoard = AddRoom("EHV 3.12 Boardroom", "ehv-3.12", 16, ehv3n, "Agenda", r =>
        {
            r.Policy = standard with
            {
                AllBookInPolicy = false, AllRequestInPolicy = false, ForwardRequestsToDelegates = false, BookInPolicy = [Ref(execAssistants)],
                DeleteSubject = true, AddOrganizerToSubject = true, RemovePrivateProperty = false, DeleteComments = true,
            };
            r.FolderPermissions.Add((daan.DisplayName, daan.PrimarySmtpAddress, ["Editor"], ["Delegate"]));
        }, p => { p.VideoDeviceName = "Conference camera"; p.AudioDeviceName = "Table microphones"; p.DisplayDeviceName = "Dual 86\" displays"; p.Tags = ["Executive"]; p.IsWheelChairAccessible = true; });
        var ehvExec = AddRoom("EHV 3.14 Executive Meeting", "ehv-3.14", 10, ehv3n, "Agenda", r =>
            r.Policy = standard with { AllBookInPolicy = false, AllRequestInPolicy = true, ForwardRequestsToDelegates = true, ResourceDelegates = [Ref(daan), Ref(julia)], DeleteSubject = true, AddOrganizerToSubject = true },
            p => { p.DisplayDeviceName = "75\" display"; p.Tags = ["Executive"]; });
        var ehvTeams1 = AddRoom("EHV 3.20 Teams Room", "ehv-3.20", 8, ehv3s, "Calendar", r =>
        {
            r.Policy = standard with { AddOrganizerToSubject = false, DeleteComments = false, DeleteSubject = false, RemovePrivateProperty = false, ProcessExternalMeetingMessages = true, AddAdditionalResponse = true, AdditionalResponse = "This is a Microsoft Teams Meeting room!" };
            r.AccountDisabled = false;
            r.GraphAccountEnabled = true;
            r.Licenses.Add("Microsoft_Teams_Rooms_Pro");
            r.PasswordPolicies = "DisablePasswordExpiration";
        }, p => { p.MtrEnabled = true; p.VideoDeviceName = "Teams Rooms camera bar"; p.AudioDeviceName = "Teams Rooms soundbar"; p.DisplayDeviceName = "Dual 65\" displays"; p.Tags = ["Teams Rooms"]; });
        var ehvTeams2 = AddRoom("EHV 3.22 Teams Room", "ehv-3.22", 6, ehv3s, "Calendar", r =>
        {
            r.Policy = standard with { DeleteComments = true, ProcessExternalMeetingMessages = false };
            r.AccountDisabled = false;
            r.GraphAccountEnabled = true;
            r.Hours = new WorkingHoursConfig(WorkDays.Weekdays, new TimeOnly(8, 0), new TimeOnly(17, 0), "Pacific Standard Time");
        }, p => { p.MtrEnabled = true; p.VideoDeviceName = "Teams Rooms camera bar"; p.Tags = ["Teams Rooms"]; });

        // Amsterdam.
        var amsCanal = AddRoom("AMS 4.01 Canal View", "ams-4.01", 10, ams4, "Agenda", r => r.Policy = standard, p => { p.DisplayDeviceName = "75\" display"; p.IsWheelChairAccessible = true; });
        var amsHuddle = AddRoom("AMS 4.05 Huddle", "ams-4.05", 4, ams4, "Agenda", r => r.Policy = standard with { MaximumDurationInMinutes = 90 });
        var amsClient = AddRoom("AMS 4.08 Client Suite", "ams-4.08", 14, ams4, "Agenda", r =>
            r.Policy = standard with { AllBookInPolicy = false, AllRequestInPolicy = true, ForwardRequestsToDelegates = false, ResourceDelegates = [Ref(emma)], ProcessExternalMeetingMessages = true },
            p => { p.VideoDeviceName = "Conference camera"; p.DisplayDeviceName = "86\" display"; p.Tags = ["Client facing"]; });
        var amsTeams = AddRoom("AMS 5.02 Teams Room", "ams-5.02", 8, ams5, "Calendar", r =>
        {
            r.Policy = standard with { AddOrganizerToSubject = false, DeleteComments = false, DeleteSubject = false, RemovePrivateProperty = false, ProcessExternalMeetingMessages = true };
            r.AccountDisabled = false;
            r.GraphAccountEnabled = true;
            r.Licenses.Add("Microsoft_Teams_Rooms_Basic");
            r.Licenses.Add("EXCHANGESTANDARD");
        }, p => { p.MtrEnabled = true; p.VideoDeviceName = "Teams Rooms camera bar"; p.Tags = ["Teams Rooms"]; });
        var amsWorkshop = AddRoom("AMS 5.06 Workshop", "ams-5.06", 18, ams5, "Agenda", r => r.Policy = standard with { MaximumDurationInMinutes = 480 }, p => p.Tags = ["Whiteboard", "Workshop"]);
        var amsBooth = AddRoom("AMS 5.10 Phone Booth", "ams-5.10", null, ams5, "Agenda", r => r.Policy = standard with { MaximumDurationInMinutes = 60, AllowRecurringMeetings = false });

        // Utrecht.
        var utrDemo = AddRoom("UTR 1.01 Demo Lab", "utr-1.01", 12, utr1, "Agenda", r => r.Policy = BookingPolicy.ExchangeDefaults with { AutomateProcessing = AutomateProcessing.AutoUpdate });
        var utrSprint = AddRoom("UTR 1.04 Sprint Room", "utr-1.04", 8, utr1, "Agenda", r =>
            r.Policy = standard with { AllowConflicts = true, ConflictPercentageAllowed = 25, MaximumConflictInstances = 3, AddAdditionalResponse = true, AdditionalResponse = null });
        var utrHardware = AddRoom("UTR 2.02 Hardware Lab", "utr-2.02", 6, utrLab, "Agenda", r => r.Policy = standard with { BookingType = ResourceBookingType.Reserved });
        var utrBrainstorm = AddRoom("UTR 2.05 Brainstorm", "utr-2.05", 6, utr2, "Agenda", r => r.Policy = standard, p => p.Tags = ["Whiteboard"]);
        var innovationUtr = AddRoom("Innovation Space", "utr-2.10", 12, utr, "Agenda", r => r.Policy = standard);

        // Rooms with typical gaps.
        // Synchronized from on-premises Active Directory: mailbox properties must be changed on-premises.
        var legacy = AddRoom("Legacy Conference Room B", "conf-b", 12, null, "Calendar", r =>
        {
            r.IsDirSynced = true;
            r.Policy = BookingPolicy.ExchangeDefaults with { AllBookInPolicy = false, AllRequestInPolicy = true, ResourceDelegates = [Ref(sanne), new RecipientRef { Identity = "pieter.oud", Resolution = RecipientResolution.NotChecked }] };
            r.Hours = WorkingHoursConfig.ExchangeDefault;
        }, inPlaces: false);
        var studio = AddRoom("Remote Studio (Podcast)", "studio-podcast", null, null, "Calendar", r =>
        {
            r.Policy = BookingPolicy.ExchangeDefaults with { ScheduleOnlyDuringWorkHours = true, BookInPolicy = [new RecipientRef { Identity = "Alex Peters", Resolution = RecipientResolution.NotChecked }], AllBookInPolicy = false };
            r.Hours = new WorkingHoursConfig(WorkDays.Weekdays, new TimeOnly(9, 0), new TimeOnly(17, 0), "Pacific Standard Time");
        });

        // Room lists.
        AddGroup("Eindhoven Headquarters", "rooms.eindhoven", "RoomList", ehvVisitor, ehvAuditorium, innovationEhv, ehvFocus, ehvHuddle, ehvProject, ehvCollab, ehvTraining, ehvBoard, ehvExec, ehvTeams1, ehvTeams2);
        AddGroup("Amsterdam Zuidas", "rooms.amsterdam", "RoomList", amsCanal, amsHuddle, amsClient, amsTeams, amsWorkshop, amsBooth);
        AddGroup("Utrecht Innovation Hub", "rooms.utrecht", "RoomList", utrDemo, utrSprint, utrHardware, utrBrainstorm, innovationUtr, facilityDesk);
        AddGroup("Executive Rooms", "rooms.executive", "RoomList", ehvBoard, ehvExec, amsClient);
        AddGroup("Teams Rooms", "rooms.teams", "RoomList", ehvTeams1, ehvTeams2, amsTeams);
        // Regular distribution groups: one convertible (only rooms), one not (mixed membership).
        AddGroup("AMS Project Rooms (legacy)", "ams.projectrooms", "MailUniversalDistributionGroup", amsWorkshop, amsCanal);
        AddGroup("Utrecht Facilities", "utrecht.facilities", "MailUniversalDistributionGroup", utrBrainstorm, emma);
        _ = trainers; _ = reception; _ = lucas; _ = legacy; _ = studio; _ = ehvQuiet;
    }

    internal static string F(TimeOnly t) => t.ToString("HH\\:mm\\:ss", CultureInfo.InvariantCulture);

    internal JsonObject RecipientJson(DemoRecipient r) => new()
    {
        ["Identity"] = r.Name,
        ["Name"] = r.Name,
        ["DisplayName"] = r.DisplayName,
        ["Alias"] = r.Alias,
        ["PrimarySmtpAddress"] = r.PrimarySmtpAddress,
        ["UserPrincipalName"] = r.RecipientTypeDetails.Contains("Mailbox", StringComparison.Ordinal) ? r.PrimarySmtpAddress : null,
        ["ExternalDirectoryObjectId"] = r.Id,
        ["Guid"] = r.Id,
        ["RecipientTypeDetails"] = r.RecipientTypeDetails,
        ["RecipientType"] = r.IsGroup ? "MailUniversalDistributionGroup" : "UserMailbox",
        ["EmailAddresses"] = new JsonArray($"SMTP:{r.PrimarySmtpAddress}"),
    };
}
