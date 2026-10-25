using RoomOps.Application.Commands;
using RoomOps.Application.Gateways;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.RoomLists;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.TeamsRooms;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Application.Services;

public sealed record PolicyFieldInfo(string Name, string Label, PolicySection Section, PolicyFieldKind Kind, int? Min, int? Max, string? Unit, IReadOnlyList<string>? EnumValues, bool AutoAcceptOnly, string Help, System.Text.Json.JsonElement DefaultValue);

public sealed record SimulationRequest(string? RoomId, BookingPolicy Policy, WorkingHoursConfig? WorkingHours, int? Capacity, BookingScenario Scenario);

public sealed record ExplainRequest(string RoomName, BookingPolicy Policy, WorkingHoursConfig? WorkingHours, int? Capacity);

public sealed record PolicyAnalysis(PolicyExplanation Explanation, IReadOnlyList<ValidationIssue> Issues);

/// <summary>Booking-policy metadata, explanations and the scenario simulator. Pure configuration analysis; nothing here talks to Exchange.</summary>
public sealed class BookingService
{
    public IReadOnlyList<PolicyFieldInfo> Fields() => BookingPolicyFields.All
        .Select(f => new PolicyFieldInfo(f.Name, f.Label, f.Section, f.Kind, f.Min, f.Max == int.MaxValue ? null : f.Max, f.Unit, f.EnumValues, f.AutoAcceptOnly, f.Help, f.ToJson(BookingPolicy.ExchangeDefaults)))
        .ToList();

    public IReadOnlyList<BookingPreset> Presets() => BookingPresets.All;

    public BookingPolicy ApplyPreset(BookingPolicy current, string presetId) =>
        BookingPolicyFields.ApplyPatch(current, (BookingPresets.Find(presetId) ?? throw RoomOpsException.NotFound("preset.notFound", Text.L($"Unknown preset '{presetId}'.", $"Onbekend model '{presetId}'."))).Fields);

    public PolicyAnalysis Analyze(ExplainRequest request) => new(
        BookingPolicyExplainer.Explain(request.Policy, request.RoomName, request.WorkingHours, request.Capacity),
        BookingPolicyValidator.Validate(request.Policy, request.WorkingHours, request.Capacity));

    public SimulationResult Simulate(SimulationRequest request)
    {
        var scenario = request.Scenario;
        // Direct listing can be determined from configuration; group membership can't and stays an administrator assertion.
        if (scenario.OrganizerAddress is { Length: > 0 } organizer)
        {
            static ListMembership Detect(IReadOnlyList<RecipientRef> list, string address, ListMembership asserted) =>
                list.Any(r => string.Equals(r.PrimarySmtpAddress ?? r.Identity, address, StringComparison.OrdinalIgnoreCase)) ? ListMembership.ListedDirectly : asserted;
            scenario = scenario with
            {
                InBookInPolicy = Detect(request.Policy.BookInPolicy, organizer, scenario.InBookInPolicy),
                InRequestInPolicy = Detect(request.Policy.RequestInPolicy, organizer, scenario.InRequestInPolicy),
                InRequestOutOfPolicy = Detect(request.Policy.RequestOutOfPolicy, organizer, scenario.InRequestOutOfPolicy),
            };
        }
        return BookingSimulator.Evaluate(request.Policy, request.WorkingHours, request.Capacity, scenario);
    }

    public IReadOnlyList<TimeZoneEntry> TimeZones() => TimeZoneCatalog.All;
}

public sealed record AccessSection<T>(IReadOnlyList<T> Items, string? Error);

public sealed record DelegateOverview(
    string RoomId,
    string RoomName,
    string PrimarySmtpAddress,
    IReadOnlyList<RecipientRef> BookingDelegates,
    bool ForwardRequestsToDelegates,
    bool TentativePendingApproval,
    string ApprovalSummary,
    CalendarFolder? CalendarFolder,
    AccessSection<FolderPermission> FolderPermissions,
    AccessSection<MailboxAccessEntry> FullAccess,
    AccessSection<MailboxAccessEntry> SendAs,
    AccessSection<string> SendOnBehalf,
    int UnresolvedCount,
    BookingPolicy Policy);

/// <summary>Reads the five distinct permission mechanisms side by side. Only booking delegates and calendar-folder permissions are editable.</summary>
public sealed class DelegatesService(InventoryService inventory, ExchangeGateway exchange, ISessionManager sessions, TimeProvider time)
{
    public async Task<DelegateOverview> GetAsync(string roomId, CancellationToken ct)
    {
        var room = await inventory.ResolveRoomAsync(roomId, ct);
        var policy = await exchange.GetBookingPolicyAsync(room.PrimarySmtpAddress, ct);
        sessions.Cache.Booking[room.Id] = new(policy, time.GetUtcNow());

        CalendarFolder? folder = null;
        AccessSection<FolderPermission> folderPermissions;
        try
        {
            folder = await exchange.GetCalendarFolderAsync(room.PrimarySmtpAddress, ct);
            folderPermissions = new(await exchange.GetFolderPermissionsAsync(folder.Identity, ct), null);
        }
        catch (RoomOpsException ex)
        {
            folderPermissions = new([], ex.Message);
        }

        async Task<AccessSection<T>> Section<T>(Func<Task<IReadOnlyList<T>>> load)
        {
            try { return new(await load(), null); }
            catch (RoomOpsException ex) { return new([], ex.Message); }
        }

        // Trustees are reported as UPNs while folder permissions use display names; resolving them lets the overview show
        // when one person holds both. Resolution is cached per session.
        async Task<IReadOnlyList<MailboxAccessEntry>> WithNames(IReadOnlyList<MailboxAccessEntry> entries)
        {
            var named = new List<MailboxAccessEntry>(entries.Count);
            foreach (var e in entries)
            {
                RecipientRef? r = null;
                try { r = await exchange.ResolveRecipientAsync(e.Trustee, ct); }
                catch (RoomOpsException) { }
                named.Add(r?.Resolution == RecipientResolution.Resolved ? e with { DisplayName = r.DisplayName, PrimarySmtpAddress = r.PrimarySmtpAddress } : e);
            }
            return named;
        }

        var fullAccess = await Section(async () => await WithNames(await exchange.GetFullAccessAsync(room.PrimarySmtpAddress, ct)));
        var sendAs = await Section(async () => await WithNames(await exchange.GetSendAsAsync(room.PrimarySmtpAddress, ct)));
        var sendOnBehalf = await Section(() => exchange.GetSendOnBehalfAsync(room.PrimarySmtpAddress, ct));
        var explanation = BookingPolicyExplainer.Explain(policy, room.DisplayName);
        var approval = explanation.Statements.Where(s => s.Kind == StatementKind.Approval).Select(s => s.Text).ToList();

        return new DelegateOverview(
            room.Id, room.DisplayName, room.PrimarySmtpAddress, policy.ResourceDelegates, policy.ForwardRequestsToDelegates, policy.TentativePendingApproval,
            approval.Count > 0 ? string.Join(" ", approval) : Text.L("No approval workflow is configured; delegates are informational only for this room.", "Er is geen goedkeuringsproces ingesteld; gemachtigden zijn voor deze ruimte alleen informatief."),
            folder, folderPermissions, fullAccess, sendAs, sendOnBehalf,
            policy.ResourceDelegates.Count(d => d.Resolution != RecipientResolution.Resolved), policy);
    }

    public Task<IReadOnlyList<Recipient>> SearchRecipientsAsync(string query, IReadOnlyList<string>? types, CancellationToken ct) =>
        exchange.SearchRecipientsAsync(query, types, ct);
}

public sealed record WorkingHoursView(string RoomId, string RoomName, WorkingHoursConfig WorkingHours, TimeZoneEntry? TimeZone, bool ScheduleOnlyDuringWorkHours, bool BookingLoaded, IReadOnlyList<ValidationIssue> Issues);

public sealed class WorkingHoursService(InventoryService inventory, ExchangeGateway exchange, ISessionManager sessions, TimeProvider time)
{
    public async Task<WorkingHoursView> GetAsync(string roomId, CancellationToken ct)
    {
        var room = await inventory.ResolveRoomAsync(roomId, ct);
        var hours = await exchange.GetWorkingHoursAsync(room.PrimarySmtpAddress, ct);
        sessions.Cache.WorkingHours[room.Id] = new(hours, time.GetUtcNow());
        var booking = sessions.Cache.Booking.TryGetValue(room.Id, out var b) ? b.Value : null;
        if (booking is null)
        {
            try
            {
                booking = await exchange.GetBookingPolicyAsync(room.PrimarySmtpAddress, ct);
                sessions.Cache.Booking[room.Id] = new(booking, time.GetUtcNow());
            }
            catch (RoomOpsException)
            {
                // Working hours remain viewable when calendar processing can't be read.
            }
        }
        return new WorkingHoursView(room.Id, room.DisplayName, hours, TimeZoneCatalog.Find(hours.TimeZoneId), booking?.ScheduleOnlyDuringWorkHours ?? false, booking is not null,
            WorkingHoursValidator.Validate(hours, TimeZoneCatalog.IsKnown));
    }
}

public sealed record RoomListView(
    string Id,
    string DisplayName,
    string Name,
    string? Alias,
    string PrimarySmtpAddress,
    bool IsDirSynced,
    IReadOnlyList<RoomListMember> Members,
    int RoomCount,
    int UnexpectedMemberCount,
    IReadOnlyList<string> Buildings,
    int FindingCount);

public sealed class RoomListsService(InventoryService inventory, ExchangeGateway exchange, ValidationService validation)
{
    public async Task<IReadOnlyList<RoomListView>> ListAsync(bool refresh, CancellationToken ct)
    {
        var snapshot = await inventory.GetSnapshotAsync(false, ct);
        var lists = refresh ? await inventory.GetRoomListsAsync(true, ct) : snapshot.RoomLists;
        var findings = (await validation.GetFindingsAsync(false, ct)).Where(f => f.TargetKind == "RoomList").ToLookup(f => f.TargetId);
        var placesById = snapshot.Places.GroupBy(p => p.PlaceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return lists.Select(l =>
        {
            // "Related building": the Places buildings that the list's rooms belong to, most common first.
            var buildings = l.Members
                .Select(m => snapshot.Places.FirstOrDefault(p => p.Type == PlaceType.Room && string.Equals(p.EmailAddress, m.PrimarySmtpAddress, StringComparison.OrdinalIgnoreCase)))
                .Where(p => p is not null)
                .Select(p => PlaceHierarchy.Ancestors(p!, placesById).FirstOrDefault(a => a.Type == PlaceType.Building)?.DisplayName)
                .Where(n => n is not null)
                .GroupBy(n => n!)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .ToList();
            return new RoomListView(l.Id, l.DisplayName, l.Name, l.Alias, l.PrimarySmtpAddress, l.IsDirSynced, l.Members, l.Members.Count(m => m.IsRoomMailbox),
                l.Members.Count(m => !m.IsRoomMailbox), buildings, findings[l.Id].Count());
        }).ToList();
    }

    public Task<IReadOnlyList<ConvertibleGroup>> SearchGroupsAsync(string query, CancellationToken ct) => exchange.SearchDistributionGroupsAsync(query, ct);

    public async Task<ConversionAssessment> AssessConversionAsync(string identity, CancellationToken ct)
    {
        var info = await exchange.GetGroupInfoAsync(identity, ct) ?? throw RoomOpsException.NotFound("group.notFound", Text.L("The distribution group was not found.", "De distributiegroep is niet gevonden."));
        var members = await exchange.GetGroupMembersAsync(info.PrimarySmtpAddress, ct);
        return RoomListRules.AssessConversion(info with { MemberCount = members.Count }, members);
    }
}

public sealed record PlaceNode(Place Place, int ChildCount, int RoomCount, bool HasRoomList, IReadOnlyList<string> Issues);

public sealed record PlacesTree(bool Available, string? Error, IReadOnlyList<PlaceNode> Nodes, IReadOnlyList<RoomSummaryRef> UnplacedRooms, DateTimeOffset? LoadedAt);

public sealed record RoomSummaryRef(string Id, string DisplayName, string PrimarySmtpAddress, int? Capacity);

public sealed record DiscoveryDiagnostics(
    bool PlacesAvailable,
    PlacesDiscoverySettings? Settings,
    string? SettingsError,
    int RoomCount,
    int RoomsInRoomLists,
    int RoomsWithPlacesParent,
    int RoomsInBoth,
    int RoomsInNeither,
    IReadOnlyList<BuildingListMatch> Buildings);

public sealed record BuildingListMatch(string BuildingId, string BuildingName, int RoomCount, string? MatchingRoomList, bool NameMatchesExactly);

public sealed class PlacesService(InventoryService inventory, PlacesGateway places)
{
    public async Task<PlacesTree> GetTreeAsync(bool refresh, CancellationToken ct)
    {
        var snapshot = await inventory.GetSnapshotAsync(refresh, ct);
        if (!places.IsAvailable)
        {
            return new PlacesTree(false, Text.L("Microsoft Places is not connected.", "Microsoft Places is niet verbonden."), [], [], null);
        }
        var children = snapshot.Places.Where(p => p.ParentId is not null).ToLookup(p => p.ParentId!, StringComparer.OrdinalIgnoreCase);
        int Rooms(Place p) => children[p.PlaceId].Sum(c => c.Type == PlaceType.Room ? 1 : Rooms(c));
        var listMembers = snapshot.RoomLists.SelectMany(l => l.Members).Select(m => m.PrimarySmtpAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodes = snapshot.Places.Select(p =>
        {
            var issues = new List<string>();
            if (p.Type is PlaceType.Floor or PlaceType.Section && p.ParentId is null) issues.Add(PlaceHierarchy.Rule(p.Type));
            if (p.Type == PlaceType.Room && p.ParentId is null) issues.Add(Text.L("No parent floor or section.", "Geen bovenliggende verdieping of sectie."));
            return new PlaceNode(p, children[p.PlaceId].Count(), p.Type == PlaceType.Room ? 0 : Rooms(p), p.EmailAddress is not null && listMembers.Contains(p.EmailAddress), issues);
        }).ToList();
        var placedRooms = snapshot.Places.Where(p => p.Type == PlaceType.Room && p.ParentId is not null && p.EmailAddress is not null).Select(p => p.EmailAddress!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unplaced = snapshot.Rooms.Where(r => !placedRooms.Contains(r.PrimarySmtpAddress)).Select(r => new RoomSummaryRef(r.Id, r.DisplayName, r.PrimarySmtpAddress, r.ResourceCapacity)).ToList();
        return new PlacesTree(snapshot.PlacesAvailable, snapshot.PlacesError, nodes, unplaced, snapshot.LoadedAt);
    }

    public async Task<DiscoveryDiagnostics> GetDiagnosticsAsync(CancellationToken ct)
    {
        var snapshot = await inventory.GetSnapshotAsync(false, ct);
        var inLists = snapshot.RoomLists.SelectMany(l => l.Members).Where(m => m.IsRoomMailbox).Select(m => m.PrimarySmtpAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var withParent = snapshot.Places.Where(p => p.Type == PlaceType.Room && p.ParentId is not null && p.EmailAddress is not null).Select(p => p.EmailAddress!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rooms = snapshot.Rooms.Select(r => r.PrimarySmtpAddress).ToList();

        PlacesDiscoverySettings? settings = null;
        string? settingsError = null;
        if (places.IsAvailable)
        {
            try { settings = await places.GetSettingsAsync(ct); }
            catch (RoomOpsException ex) { settingsError = ex.Message; }
        }

        var placesById = snapshot.Places.GroupBy(p => p.PlaceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var buildings = snapshot.Places.Where(p => p.Type == PlaceType.Building).Select(b =>
        {
            var roomCount = snapshot.Places.Count(p => p.Type == PlaceType.Room && PlaceHierarchy.Ancestors(p, placesById).Any(a => a.PlaceId == b.PlaceId));
            var exact = snapshot.RoomLists.FirstOrDefault(l => l.DisplayName.Equals(b.DisplayName, StringComparison.OrdinalIgnoreCase));
            var partial = exact ?? snapshot.RoomLists.FirstOrDefault(l => l.DisplayName.Contains(b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return new BuildingListMatch(b.PlaceId, b.DisplayName, roomCount, partial?.DisplayName, exact is not null);
        }).OrderBy(b => b.BuildingName, StringComparer.OrdinalIgnoreCase).ToList();

        return new DiscoveryDiagnostics(snapshot.PlacesAvailable, settings, settingsError, rooms.Count,
            rooms.Count(inLists.Contains), rooms.Count(withParent.Contains), rooms.Count(r => inLists.Contains(r) && withParent.Contains(r)),
            rooms.Count(r => !inLists.Contains(r) && !withParent.Contains(r)), buildings);
    }
}

/// <summary>
/// Teams Rooms readiness signals. A room mailbox is not automatically a Teams Rooms resource account; licensing and account
/// signals come from Microsoft Graph and are reported as unverifiable when Graph isn't connected or permission is missing.
/// RoomOps never changes passwords, licenses, Conditional Access or device settings.
/// </summary>
public sealed class TeamsRoomsService(InventoryService inventory, ExchangeGateway exchange, GraphGateway graph, ISessionManager sessions, TimeProvider time)
{
    public async Task<TeamsRoomReadiness> GetAsync(string roomId, bool refresh, CancellationToken ct)
    {
        var room = await inventory.ResolveRoomAsync(roomId, ct);
        if (!refresh && sessions.Cache.Readiness.TryGetValue(room.Id, out var cached)) return cached.Value;

        var signals = new List<ReadinessSignal>
        {
            new("mailboxType", Text.L("Mailbox type", "Postvaktype"), room.RecipientTypeDetails == "RoomMailbox" ? SignalStatus.Ready : SignalStatus.Attention, room.RecipientTypeDetails,
                Text.L("Teams Rooms devices sign in with a resource account backed by a room mailbox.", "Teams Rooms-apparaten melden zich aan met een resourceaccount dat bij een ruimtepostvak hoort."), "Exchange Online"),
        };
        var upnMatches = string.Equals(room.UserPrincipalName, room.PrimarySmtpAddress, StringComparison.OrdinalIgnoreCase);
        signals.Add(new("upn", "UPN matches SMTP address", upnMatches ? SignalStatus.Ready : SignalStatus.Attention, room.UserPrincipalName ?? "(unknown)",
            Text.L("Microsoft states the resource account's UPN must match its SMTP address.", "Volgens Microsoft moet de UPN van het resourceaccount overeenkomen met het SMTP-adres."), "Exchange Online"));

        DirectoryAccount? account = null;
        string? graphError = null;
        if (graph.IsAvailable && room.UserPrincipalName is not null)
        {
            try { account = await graph.GetAccountAsync(room.UserPrincipalName, ct); }
            catch (RoomOpsException ex) { graphError = ex.Error.Kind == ErrorKind.PermissionDenied ? Text.L("The signed-in account can't read users or license details (User.Read.All).", "Het aangemelde account kan geen gebruikers of licentiegegevens lezen (User.Read.All).") : ex.Message; }
        }
        else if (!graph.IsAvailable)
        {
            graphError = Text.L("Microsoft Graph is not connected.", "Microsoft Graph is niet verbonden.");
        }

        if (account is not null)
        {
            signals.Add(new("accountEnabled", Text.L("Sign-in enabled", "Aanmelden ingeschakeld"), account.AccountEnabled == true ? SignalStatus.Ready : SignalStatus.Attention,
                account.AccountEnabled == true ? Text.L("Enabled", "Ingeschakeld") : Text.L("Disabled", "Uitgeschakeld"), Text.L("A device can only sign in when the account is enabled (EnableRoomMailboxAccount).", "Een apparaat kan zich alleen aanmelden als het account is ingeschakeld (EnableRoomMailboxAccount)."), "Microsoft Graph"));
            var mtr = account.LicenseSkuPartNumbers.Where(TeamsRoomsLicensing.IsTeamsRoomsSku).ToList();
            signals.Add(new("license", Text.L("Teams Rooms license", "Teams Rooms-licentie"), mtr.Count > 0 ? SignalStatus.Ready : SignalStatus.Attention,
                mtr.Count > 0 ? string.Join(", ", mtr) : account.LicenseSkuPartNumbers.Count == 0 ? Text.L("No licenses", "Geen licenties") : Text.L($"No Teams Rooms SKU ({account.LicenseSkuPartNumbers.Count} other)", $"Geen Teams Rooms-SKU ({account.LicenseSkuPartNumbers.Count} andere)"),
                Text.L("Each resource account used by a Teams Rooms device needs a Teams Rooms Basic or Pro license. Detected by SKU part number.", "Elk resourceaccount dat door een Teams Rooms-apparaat wordt gebruikt, heeft een Teams Rooms Basic- of Pro-licentie nodig. Herkend aan het SKU-onderdeelnummer."), "Microsoft Graph"));
            var neverExpires = account.PasswordPolicies?.Contains("DisablePasswordExpiration", StringComparison.OrdinalIgnoreCase) == true;
            signals.Add(new("passwordExpiry", Text.L("Password never expires", "Wachtwoord verloopt nooit"), neverExpires ? SignalStatus.Ready : SignalStatus.Attention, neverExpires ? Text.L("Set", "Ingesteld") : Text.L("Not set", "Niet ingesteld"),
                Text.L("Microsoft lists 'Password never expires' as a requirement for shared Teams devices.", "Microsoft noemt 'Wachtwoord verloopt nooit' als vereiste voor gedeelde Teams-apparaten."), "Microsoft Graph"));
            signals.Add(new("cloudOnly", Text.L("Cloud-only account", "Account alleen in de cloud"), account.OnPremisesSyncEnabled == true ? SignalStatus.Attention : SignalStatus.Ready,
                account.OnPremisesSyncEnabled == true ? Text.L("Synchronized from on-premises", "Gesynchroniseerd vanuit on-premises") : Text.L("Cloud only", "Alleen cloud"), Text.L("Microsoft recommends creating resource accounts as cloud-only.", "Microsoft raadt aan resourceaccounts alleen in de cloud te maken."), "Microsoft Graph"));
        }
        else
        {
            foreach (var (key, label) in new[] { ("accountEnabled", Text.L("Sign-in enabled", "Aanmelden ingeschakeld")), ("license", Text.L("Teams Rooms license", "Teams Rooms-licentie")), ("passwordExpiry", Text.L("Password never expires", "Wachtwoord verloopt nooit")) })
            {
                signals.Add(new(key, label, SignalStatus.Unverifiable, Text.L("Unable to verify", "Niet te controleren"), graphError ?? Text.L("The directory account was not found.", "Het adreslijstaccount is niet gevonden."), "Microsoft Graph"));
            }
        }

        try
        {
            var policy = sessions.Cache.Booking.TryGetValue(room.Id, out var b) ? b.Value : await exchange.GetBookingPolicyAsync(room.PrimarySmtpAddress, ct);
            var deviations = new List<string>();
            if (policy.AutomateProcessing != AutomateProcessing.AutoAccept) deviations.Add(Text.L("AutomateProcessing is not AutoAccept", "AutomateProcessing is niet AutoAccept"));
            if (policy.AddOrganizerToSubject) deviations.Add(Text.L("AddOrganizerToSubject is enabled", "AddOrganizerToSubject is ingeschakeld"));
            if (policy.DeleteComments) deviations.Add(Text.L("DeleteComments is enabled (join information in the body is removed)", "DeleteComments is ingeschakeld (deelname-informatie in de tekst wordt verwijderd)"));
            if (policy.DeleteSubject) deviations.Add(Text.L("DeleteSubject is enabled", "DeleteSubject is ingeschakeld"));
            if (policy.RemovePrivateProperty) deviations.Add(Text.L("RemovePrivateProperty is enabled", "RemovePrivateProperty is ingeschakeld"));
            if (!policy.ProcessExternalMeetingMessages) deviations.Add(Text.L("ProcessExternalMeetingMessages is disabled", "ProcessExternalMeetingMessages is uitgeschakeld"));
            signals.Add(new("calendarProcessing", Text.L("Calendar processing", "Agendaverwerking"), deviations.Count == 0 ? SignalStatus.Ready : SignalStatus.Attention,
                deviations.Count == 0 ? Text.L("Matches Microsoft guidance", "Komt overeen met de richtlijnen van Microsoft") : Text.L($"{deviations.Count} difference(s)", $"{deviations.Count} verschil(len)"),
                deviations.Count == 0 ? Text.L("Matches the Set-CalendarProcessing example in Microsoft's Teams Rooms resource account guidance.", "Komt overeen met het Set-CalendarProcessing-voorbeeld in de richtlijnen van Microsoft voor resourceaccounts van Teams Rooms.") : Text.L("Compared with Microsoft's Teams Rooms guidance: ", "Vergeleken met de richtlijnen van Microsoft voor Teams Rooms: ") + string.Join("; ", deviations) + ".",
                "Exchange Online"));
        }
        catch (RoomOpsException ex)
        {
            signals.Add(new("calendarProcessing", Text.L("Calendar processing", "Agendaverwerking"), SignalStatus.Unverifiable, Text.L("Unable to verify", "Niet te controleren"), ex.Message, "Exchange Online"));
        }

        try
        {
            var hours = sessions.Cache.WorkingHours.TryGetValue(room.Id, out var w) ? w.Value : await exchange.GetWorkingHoursAsync(room.PrimarySmtpAddress, ct);
            var isDefault = hours.TimeZoneId.Equals("Pacific Standard Time", StringComparison.OrdinalIgnoreCase);
            signals.Add(new("timeZone", Text.L("Time zone", "Tijdzone"), isDefault ? SignalStatus.Attention : SignalStatus.Ready, hours.TimeZoneId,
                isDefault ? Text.L("Pacific Standard Time is the default for new resource accounts; confirm it matches the room's location.", "Pacific Standard Time is de standaard voor nieuwe resourceaccounts; controleer of die past bij de locatie van de ruimte.") : Text.L("Working-hours time zone is set.", "De tijdzone van de werktijden is ingesteld."), "Exchange Online"));
        }
        catch (RoomOpsException ex)
        {
            signals.Add(new("timeZone", Text.L("Time zone", "Tijdzone"), SignalStatus.Unverifiable, Text.L("Unable to verify", "Niet te controleren"), ex.Message, "Exchange Online"));
        }

        var licenses = account?.LicenseSkuPartNumbers.Where(TeamsRoomsLicensing.IsTeamsRoomsSku).ToList() ?? [];
        var attention = signals.Count(s => s.Status == SignalStatus.Attention);
        var unverifiable = signals.Count(s => s.Status == SignalStatus.Unverifiable);
        var summary = attention == 0 && unverifiable == 0
            ? Text.L("All readable signals match Microsoft's Teams Rooms resource account guidance. Device sign-in itself is not tested by RoomOps.", "Alle leesbare signalen komen overeen met de richtlijnen van Microsoft voor resourceaccounts van Teams Rooms. Het aanmelden op het apparaat zelf test RoomOps niet.")
            : Text.L($"{attention} signal(s) need attention and {unverifiable} could not be verified. A room mailbox alone is not a ready Teams Rooms resource account.", $"{attention} signaal/signalen vragen aandacht en {unverifiable} konden niet worden gecontroleerd. Een ruimtepostvak alleen is geen gereed resourceaccount voor Teams Rooms.");
        var readiness = new TeamsRoomReadiness(room.Id, room.PrimarySmtpAddress, account is not null, signals, licenses, summary);
        sessions.Cache.Readiness[room.Id] = new(readiness, time.GetUtcNow());
        return readiness;
    }
}
