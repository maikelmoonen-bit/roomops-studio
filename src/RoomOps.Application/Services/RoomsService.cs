using RoomOps.Application.Abstractions;
using RoomOps.Application.Gateways;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.Validation;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Application.Services;

public sealed record RoomListRef(string Id, string DisplayName, string PrimarySmtpAddress);

public sealed record FindingCounts(int Critical, int Warning, int Recommendation, int Informational, int UnableToVerify)
{
    public static FindingCounts From(IEnumerable<Finding> findings)
    {
        var list = findings.ToList();
        return new(
            list.Count(f => f.Severity == FindingSeverity.Critical),
            list.Count(f => f.Severity == FindingSeverity.Warning),
            list.Count(f => f.Severity == FindingSeverity.Recommendation),
            list.Count(f => f.Severity == FindingSeverity.Informational),
            list.Count(f => f.Severity == FindingSeverity.UnableToVerify));
    }
}

public sealed record RoomSummary(
    string Id,
    string DisplayName,
    string PrimarySmtpAddress,
    string Alias,
    int? Capacity,
    int? PlacesCapacity,
    string? Building,
    string? Floor,
    string? Section,
    string? PlaceId,
    string? LocationPath,
    IReadOnlyList<RoomListRef> RoomLists,
    BookingMode? BookingMode,
    string? BookingModeLabel,
    int? DelegateCount,
    int? BookingWindowInDays,
    int? MaximumDurationInMinutes,
    DateTimeOffset? ConfigurationLoadedAt,
    string? TimeZone,
    FindingCounts Findings,
    bool IsDirSynced,
    bool? MtrEnabled,
    bool? WheelchairAccessible,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Devices,
    string? TemplateName,
    DateTimeOffset? WhenCreated);

public sealed record RoomInventory(
    IReadOnlyList<RoomSummary> Rooms,
    DateTimeOffset LoadedAt,
    bool PlacesAvailable,
    string? PlacesError,
    int ScannedCount,
    ScanProgress? Scan);

public sealed record TemplateAssignmentInfo(string TemplateId, string TemplateName, IReadOnlyList<string> ExcludedFields, DateTimeOffset AssignedAt, IReadOnlyList<string> DriftFields);

public sealed record RoomDetail(
    RoomSummary Summary,
    RoomMailbox Mailbox,
    Place? Place,
    IReadOnlyList<Place> PlacePath,
    IReadOnlyList<RoomListRef> RoomLists,
    BookingPolicy? Booking,
    DateTimeOffset? BookingLoadedAt,
    WorkingHoursConfig? WorkingHours,
    TimeZoneEntry? TimeZone,
    PolicyExplanation? Explanation,
    IReadOnlyList<Finding> Findings,
    TemplateAssignmentInfo? Template,
    IReadOnlyList<AuditRecord> History,
    IReadOnlyList<string> LoadErrors,
    bool PlacesAvailable);

public sealed class RoomsService(
    ISessionManager sessions,
    InventoryService inventory,
    ExchangeGateway exchange,
    ValidationService validation,
    ITemplateStore templates,
    TimeProvider time)
{
    public async Task<RoomInventory> ListAsync(bool refresh, CancellationToken ct)
    {
        var snapshot = await inventory.GetSnapshotAsync(refresh, ct);
        var report = await validation.RunAsync(false, ct);
        var findingsByRoom = report.Findings.Where(f => f.TargetKind == "Room").ToLookup(f => f.TargetId, StringComparer.OrdinalIgnoreCase);
        var tenantId = sessions.Current.TenantId ?? string.Empty;
        var assignments = (await templates.ListAssignmentsAsync(tenantId, ct)).ToDictionary(a => a.RoomId, StringComparer.OrdinalIgnoreCase);
        var templateNames = (await templates.ListAsync(ct)).ToDictionary(t => t.Id, t => t.Name, StringComparer.OrdinalIgnoreCase);
        var placesById = snapshot.Places.GroupBy(p => p.PlaceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var listsByMember = snapshot.RoomLists
            .SelectMany(l => l.Members.Select(m => (Smtp: m.PrimarySmtpAddress, Ref: new RoomListRef(l.Id, l.DisplayName, l.PrimarySmtpAddress))))
            .ToLookup(x => x.Smtp, x => x.Ref, StringComparer.OrdinalIgnoreCase);

        var summaries = snapshot.Rooms.Select(r => BuildSummary(r, snapshot, placesById, listsByMember[r.PrimarySmtpAddress].ToList(),
            findingsByRoom[r.Id], assignments.TryGetValue(r.Id, out var a) && templateNames.TryGetValue(a.TemplateId, out var tn) ? tn : null)).ToList();

        return new RoomInventory(summaries, snapshot.LoadedAt, snapshot.PlacesAvailable, snapshot.PlacesError,
            summaries.Count(s => s.ConfigurationLoadedAt is not null), sessions.Cache.LastScan);
    }

    private RoomSummary BuildSummary(RoomMailbox r, InventorySnapshot snapshot, IReadOnlyDictionary<string, Place> placesById, IReadOnlyList<RoomListRef> lists, IEnumerable<Finding> findings, string? templateName)
    {
        var cache = sessions.Cache;
        var booking = cache.Booking.TryGetValue(r.Id, out var b) ? b : null;
        var hours = cache.WorkingHours.TryGetValue(r.Id, out var w) ? w.Value : null;
        var place = snapshot.PlacesAvailable ? InventoryService.PlaceFor(r, snapshot.Places) : null;
        var ancestors = place is null ? [] : PlaceHierarchy.Ancestors(place, placesById);
        var mode = booking is null ? (BookingMode?)null : BookingModeClassifier.Classify(booking.Value);
        var devices = new[] { place?.DisplayDeviceName, place?.VideoDeviceName, place?.AudioDeviceName }.Where(d => !string.IsNullOrWhiteSpace(d)).Cast<string>().ToList();
        return new RoomSummary(
            r.Id, r.DisplayName, r.PrimarySmtpAddress, r.Alias, r.ResourceCapacity, place?.Capacity,
            ancestors.FirstOrDefault(a => a.Type == PlaceType.Building)?.DisplayName,
            ancestors.FirstOrDefault(a => a.Type == PlaceType.Floor)?.DisplayName,
            ancestors.FirstOrDefault(a => a.Type == PlaceType.Section)?.DisplayName,
            place?.PlaceId,
            ancestors.Count == 0 ? null : string.Join(" / ", ancestors.Select(a => a.DisplayName)),
            lists,
            mode,
            mode is null ? null : BookingModeClassifier.Label(mode.Value),
            booking?.Value.ResourceDelegates.Count,
            booking?.Value.BookingWindowInDays,
            booking?.Value.MaximumDurationInMinutes,
            booking?.LoadedAt,
            hours?.TimeZoneId,
            FindingCounts.From(findings),
            r.IsDirSynced,
            place?.MtrEnabled,
            place?.IsWheelChairAccessible,
            place?.Tags ?? [],
            devices,
            templateName,
            r.WhenCreated);
    }

    public async Task<RoomDetail> GetDetailAsync(string roomId, bool refresh, CancellationToken ct)
    {
        var room = await inventory.ResolveRoomAsync(roomId, ct);
        var errors = new List<string>();
        var cache = sessions.Cache;
        if (refresh || !cache.Booking.ContainsKey(room.Id))
        {
            try { cache.Booking[room.Id] = new(await exchange.GetBookingPolicyAsync(room.PrimarySmtpAddress, ct), time.GetUtcNow()); }
            catch (RoomOpsException ex) { errors.Add(Text.L($"Booking policy: {ex.Message}", $"Boekingsbeleid: {ex.Message}")); }
        }
        if (refresh || !cache.WorkingHours.ContainsKey(room.Id))
        {
            try { cache.WorkingHours[room.Id] = new(await exchange.GetWorkingHoursAsync(room.PrimarySmtpAddress, ct), time.GetUtcNow()); }
            catch (RoomOpsException ex) { errors.Add(Text.L($"Working hours: {ex.Message}", $"Werktijden: {ex.Message}")); }
        }

        var inventoryView = await ListAsync(false, ct);
        var summary = inventoryView.Rooms.FirstOrDefault(r => r.Id == room.Id)
            ?? throw RoomOpsException.NotFound("room.notFound", Text.L("The room is no longer in the inventory.", "De ruimte staat niet meer in de inventaris."));
        var snapshot = await inventory.GetSnapshotAsync(false, ct);
        var placesById = snapshot.Places.GroupBy(p => p.PlaceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var place = snapshot.PlacesAvailable ? InventoryService.PlaceFor(room, snapshot.Places) : null;
        var booking = cache.Booking.TryGetValue(room.Id, out var b) ? b : null;
        var hours = cache.WorkingHours.TryGetValue(room.Id, out var w) ? w.Value : null;
        var findings = (await validation.GetFindingsAsync(false, ct)).Where(f => f.TargetId == room.Id).ToList();

        TemplateAssignmentInfo? templateInfo = null;
        var assignment = (await templates.ListAssignmentsAsync(sessions.Current.TenantId ?? string.Empty, ct)).FirstOrDefault(a => a.RoomId == room.Id);
        if (assignment is not null && await templates.GetAsync(assignment.TemplateId, ct) is { } template)
        {
            var drift = booking is null ? [] : Domain.Templates.TemplateRules.Drift(template, booking.Value, assignment.ExcludedFields.ToHashSet(StringComparer.OrdinalIgnoreCase));
            templateInfo = new(template.Id, template.Name, assignment.ExcludedFields, assignment.AssignedAt, drift);
        }

        var history = await sessions.Audit.QueryAsync(new AuditQuery(TenantId: sessions.Current.TenantId, TargetId: room.Id, Limit: 25), ct);
        var explanation = booking is null ? null : BookingPolicyExplainer.Explain(booking.Value, room.DisplayName, hours, room.ResourceCapacity);
        return new RoomDetail(
            summary, room, place,
            place is null ? [] : PlaceHierarchy.Ancestors(place, placesById),
            summary.RoomLists, booking?.Value, booking?.LoadedAt, hours,
            hours is null ? null : TimeZoneCatalog.Find(hours.TimeZoneId),
            explanation, findings, templateInfo, history, errors, snapshot.PlacesAvailable);
    }

    public Task<IReadOnlyList<AcceptedDomain>> GetAcceptedDomainsAsync(CancellationToken ct) => exchange.GetAcceptedDomainsAsync(ct);

    public sealed record AvailabilityResult(bool AddressAvailable, bool AliasAvailable, string? SuggestedAlias, IReadOnlyList<ValidationIssue> Issues);

    /// <summary>Checks that an address and alias are free before the creation wizard reaches its review step.</summary>
    public async Task<AvailabilityResult> CheckAvailabilityAsync(string? alias, string? primarySmtpAddress, string? displayName, CancellationToken ct)
    {
        var issues = new List<ValidationIssue>();
        if (alias is not null) issues.AddRange(RecipientNaming.ValidateAlias(alias));
        if (primarySmtpAddress is not null) issues.AddRange(RecipientNaming.ValidateSmtpAddress(primarySmtpAddress));
        var addressFree = primarySmtpAddress is null || issues.Any(i => i.Field == "primarySmtpAddress") || !await exchange.RecipientExistsAsync(primarySmtpAddress, ct);
        var aliasFree = alias is null || issues.Any(i => i.Field == "alias") || !await exchange.RecipientExistsAsync(alias, ct);
        if (!addressFree) issues.Add(new("primarySmtpAddress", Text.L($"{primarySmtpAddress} is already in use.", $"{primarySmtpAddress} is al in gebruik."), IssueLevel.Error));
        if (!aliasFree) issues.Add(new("alias", Text.L($"The alias '{alias}' is already in use.", $"De alias '{alias}' is al in gebruik."), IssueLevel.Error));
        return new AvailabilityResult(addressFree, aliasFree, displayName is null ? null : RecipientNaming.SuggestAlias(displayName), issues);
    }
}
