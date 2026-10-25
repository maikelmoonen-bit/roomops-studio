using RoomOps.Application.Abstractions;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Places;
using RoomOps.Domain.Validation;

namespace RoomOps.Application.Services;

public sealed record ValidationReport(
    DateTimeOffset GeneratedAt,
    int RoomsTotal,
    int RoomsWithBookingData,
    bool PlacesAvailable,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<ValidationRuleInfo> Rules,
    ValidationBaseline Baseline);

/// <summary>Runs the validation engine over the session's inventory and whatever per-room configuration has been loaded.</summary>
public sealed class ValidationService(ISessionManager sessions, InventoryService inventory, ITemplateStore templates, ISettingsStore settings, TimeProvider time)
{
    public async Task<IReadOnlyList<Finding>> GetFindingsAsync(bool refresh, CancellationToken ct) => (await RunAsync(refresh, ct)).Findings;

    public async Task<ValidationReport> RunAsync(bool refresh, CancellationToken ct)
    {
        var snapshot = await inventory.GetSnapshotAsync(refresh, ct);
        var cache = sessions.Cache;
        var tenantId = sessions.Current.TenantId ?? string.Empty;
        var assignments = (await templates.ListAssignmentsAsync(tenantId, ct)).ToDictionary(a => a.RoomId, StringComparer.OrdinalIgnoreCase);
        var templateMap = (await templates.ListAsync(ct)).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var membership = snapshot.RoomLists
            .SelectMany(l => l.Members.Select(m => (List: l.Id, Smtp: m.PrimarySmtpAddress)))
            .GroupBy(x => x.Smtp, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.List).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);

        var facts = snapshot.Rooms.Select(r => new RoomFacts
        {
            Mailbox = r,
            Booking = cache.Booking.TryGetValue(r.Id, out var b) ? b.Value : null,
            WorkingHours = cache.WorkingHours.TryGetValue(r.Id, out var w) ? w.Value : null,
            Place = snapshot.PlacesAvailable ? InventoryService.PlaceFor(r, snapshot.Places) : null,
            RoomListIds = membership.TryGetValue(r.PrimarySmtpAddress, out var ids) ? ids : [],
            TemplateAssignment = assignments.GetValueOrDefault(r.Id),
        }).ToList();

        var baseline = settings.Current.Validation;
        var findings = ValidationEngine.Run(new ValidationInput
        {
            Rooms = facts,
            RoomLists = snapshot.RoomLists,
            Baseline = baseline,
            PlacesAvailable = snapshot.PlacesAvailable,
            PlacesById = snapshot.Places.GroupBy(p => p.PlaceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            Templates = templateMap,
        });

        return new ValidationReport(time.GetUtcNow(), facts.Count, facts.Count(f => f.Booking is not null), snapshot.PlacesAvailable, findings, ValidationEngine.RuleCatalog, baseline);
    }
}
