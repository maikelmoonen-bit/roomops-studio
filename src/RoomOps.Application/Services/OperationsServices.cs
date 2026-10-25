using System.Text.Json;
using Microsoft.Extensions.Logging;
using RoomOps.Application.Abstractions;
using RoomOps.Application.Gateways;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.Templates;
using RoomOps.Domain.Validation;

namespace RoomOps.Application.Services;

/// <summary>
/// Reads calendar processing and working hours for every room, sequentially with pacing, so booking-dependent metrics and
/// validation rules can be evaluated. Started explicitly by the administrator; never on startup.
/// </summary>
public sealed class ScanService(ISessionManager sessions, InventoryService inventory, ExchangeGateway exchange, IEventSink events, ISettingsStore settings, TimeProvider time, ILogger<ScanService> logger) : IDisposable
{
    public void Dispose() => _cts?.Dispose();

    private CancellationTokenSource? _cts;

    public ScanProgress? Current => sessions.Cache.LastScan;

    public async Task<ScanProgress> StartAsync(CancellationToken ct)
    {
        if (sessions.Cache.LastScan is { Running: true } running) return running;
        var rooms = await inventory.GetMailboxesAsync(ct);
        var epoch = sessions.Current.Epoch;
        var cache = sessions.Cache;
        var progress = new ScanProgress(Guid.NewGuid().ToString("N"), rooms.Count, 0, 0, true, time.GetUtcNow(), null, null);
        cache.LastScan = progress;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            var failed = 0;
            var pacing = sessions.Current.Mode == SessionMode.Live ? TimeSpan.FromMilliseconds(Math.Clamp(settings.Current.Execution.PacingMilliseconds / 3, 0, 2000)) : TimeSpan.FromMilliseconds(25);
            for (var i = 0; i < rooms.Count; i++)
            {
                if (token.IsCancellationRequested || sessions.Current.Epoch != epoch) break;
                var room = rooms[i];
                cache.LastScan = progress with { Completed = i, Failed = failed, CurrentRoom = room.DisplayName };
                events.Publish("scan.progress", cache.LastScan);
                try
                {
                    cache.Booking[room.Id] = new(await exchange.GetBookingPolicyAsync(room.PrimarySmtpAddress, token), time.GetUtcNow());
                    cache.WorkingHours[room.Id] = new(await exchange.GetWorkingHoursAsync(room.PrimarySmtpAddress, token), time.GetUtcNow());
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (RoomOpsException ex)
                {
                    failed++;
                    logger.LogWarning("Scan could not read {Room}: {Message}", room.PrimarySmtpAddress, ex.Message);
                    if (ex.Error.Kind == ErrorKind.Authentication) break;
                }
                if (pacing > TimeSpan.Zero) await Task.Delay(pacing, CancellationToken.None);
            }
            if (sessions.Current.Epoch == epoch)
            {
                var completed = rooms.Count(r => cache.Booking.ContainsKey(r.Id));
                cache.LastScan = progress with { Completed = completed, Failed = failed, Running = false, FinishedAt = time.GetUtcNow(), CurrentRoom = null };
                events.Publish("scan.progress", cache.LastScan);
                events.Publish("inventory.changed", new { reason = "scan" });
            }
        }, CancellationToken.None);
        return progress;
    }

    public void Cancel() => _cts?.Cancel();
}

public sealed record Metric(int? Value, string? UnavailableReason);

public sealed record CountItem(string Key, string Label, int Count);

public sealed record DashboardSummary(
    DateTimeOffset GeneratedAt,
    Metric TotalRooms,
    Metric TotalRoomLists,
    Metric Buildings,
    Metric Floors,
    Metric MissingCapacity,
    Metric WithoutRoomList,
    Metric MissingPlacesLocation,
    Metric ManualApproval,
    Metric UnusualBooking,
    Metric TotalCapacity,
    int ScannedRooms,
    ScanProgress? Scan,
    IReadOnlyList<CountItem> BookingModes,
    IReadOnlyList<CountItem> CapacityBuckets,
    IReadOnlyList<CountItem> RoomsByBuilding,
    FindingCounts Findings,
    IReadOnlyList<CountItem> TopRules,
    IReadOnlyList<AuditRecord> RecentActivity,
    int ChangesLast7Days);

public sealed class DashboardService(ISessionManager sessions, InventoryService inventory, ValidationService validation, TimeProvider time)
{
    public async Task<DashboardSummary> GetAsync(CancellationToken ct)
    {
        var snapshot = await inventory.GetSnapshotAsync(false, ct);
        var report = await validation.RunAsync(false, ct);
        var cache = sessions.Cache;
        var rooms = snapshot.Rooms;
        var scanned = rooms.Where(r => cache.Booking.ContainsKey(r.Id)).Select(r => cache.Booking[r.Id].Value).ToList();
        var memberSmtps = snapshot.RoomLists.SelectMany(l => l.Members).Select(m => m.PrimarySmtpAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var placesOff = Text.L("Microsoft Places is not connected.", "Microsoft Places is niet verbonden.");
        var scanReason = scanned.Count == 0 ? Text.L("Run a configuration scan to read booking policies.", "Voer een configuratiescan uit om het boekingsbeleid te lezen.") : null;

        Metric BookingMetric(Func<BookingPolicy, bool> predicate) =>
            scanned.Count == 0 ? new Metric(null, scanReason) : new Metric(scanned.Count(predicate), scanned.Count < rooms.Count ? Text.L($"Based on {scanned.Count} of {rooms.Count} scanned rooms.", $"Gebaseerd op {scanned.Count} van {rooms.Count} gescande ruimtes.") : null);

        var modes = scanned.GroupBy(BookingModeClassifier.Classify)
            .Select(g => new CountItem(g.Key.ToString(), BookingModeClassifier.Label(g.Key), g.Count()))
            .OrderByDescending(c => c.Count).ToList();

        var buckets = new (string Key, string Label, Func<int?, bool> Match)[]
        {
            ("unknown", Text.L("Not set", "Niet ingesteld"), c => c is null or 0),
            ("1-4", "1–4", c => c is >= 1 and <= 4),
            ("5-8", "5–8", c => c is >= 5 and <= 8),
            ("9-12", "9–12", c => c is >= 9 and <= 12),
            ("13-20", "13–20", c => c is >= 13 and <= 20),
            ("21+", "21+", c => c > 20),
        };
        var capacity = buckets.Select(b => new CountItem(b.Key, b.Label, rooms.Count(r => b.Match(r.ResourceCapacity)))).ToList();

        IReadOnlyList<CountItem> byBuilding = [];
        if (snapshot.PlacesAvailable)
        {
            var byId = snapshot.Places.GroupBy(p => p.PlaceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            byBuilding = rooms
                .Select(r => InventoryService.PlaceFor(r, snapshot.Places))
                .Select(p => p is null ? null : PlaceHierarchy.Ancestors(p, byId).FirstOrDefault(a => a.Type == PlaceType.Building))
                .GroupBy(b => b?.PlaceId ?? "(none)")
                .Select(g => new CountItem(g.Key, g.First()?.DisplayName ?? Text.L("No building", "Geen gebouw"), g.Count()))
                .OrderByDescending(c => c.Count).ToList();
        }

        var history = await sessions.Audit.QueryAsync(new AuditQuery(TenantId: sessions.Current.TenantId, Limit: 8), ct);
        var weekCount = await sessions.Audit.CountAsync(new AuditQuery(TenantId: sessions.Current.TenantId, Since: time.GetUtcNow().AddDays(-7)), ct);
        var topRules = report.Findings.Where(f => f.Severity != FindingSeverity.UnableToVerify)
            .GroupBy(f => (f.RuleId, f.Title)).Select(g => new CountItem(g.Key.RuleId, g.Key.Title, g.Count()))
            .OrderByDescending(c => c.Count).Take(6).ToList();

        return new DashboardSummary(
            time.GetUtcNow(),
            new Metric(rooms.Count, null),
            new Metric(snapshot.RoomLists.Count, null),
            snapshot.PlacesAvailable ? new Metric(snapshot.Places.Count(p => p.Type == PlaceType.Building), null) : new Metric(null, placesOff),
            snapshot.PlacesAvailable ? new Metric(snapshot.Places.Count(p => p.Type == PlaceType.Floor), null) : new Metric(null, placesOff),
            new Metric(rooms.Count(r => r.ResourceCapacity is null or 0), null),
            new Metric(rooms.Count(r => !memberSmtps.Contains(r.PrimarySmtpAddress)), null),
            snapshot.PlacesAvailable
                ? new Metric(rooms.Count(r => InventoryService.PlaceFor(r, snapshot.Places) is not { ParentId: not null }), null)
                : new Metric(null, placesOff),
            BookingMetric(p => p.AutomateProcessing == AutomateProcessing.AutoAccept && !p.AllBookInPolicy && (p.AllRequestInPolicy || p.RequestInPolicy.Count > 0)),
            BookingMetric(p => BookingModeClassifier.Classify(p) is BookingMode.Custom or BookingMode.ProcessingDisabled or BookingMode.ManualAutoUpdate or BookingMode.Reserved),
            new Metric(rooms.Sum(r => r.ResourceCapacity ?? 0), null),
            scanned.Count,
            cache.LastScan,
            modes,
            capacity,
            byBuilding,
            FindingCounts.From(report.Findings),
            topRules,
            history,
            weekCount);
    }
}

public sealed record AuditPage(IReadOnlyList<AuditRecord> Records, int Total);

public sealed record ExportedFile(string FileName, string MimeType, string Content);

public sealed class HistoryService(ISessionManager sessions, ExchangeGateway exchange, PlacesGateway places, InventoryService inventory, TimeProvider time)
{
    public async Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct)
    {
        var scoped = query with { TenantId = sessions.Current.TenantId };
        return new AuditPage(await sessions.Audit.QueryAsync(scoped, ct), await sessions.Audit.CountAsync(scoped with { Limit = int.MaxValue, Offset = 0 }, ct));
    }

    public async Task<AuditRecord> GetAsync(string id, CancellationToken ct) =>
        await sessions.Audit.GetAsync(id, ct) is { } record && string.Equals(record.TenantId, sessions.Current.TenantId, StringComparison.OrdinalIgnoreCase)
            ? record
            : throw RoomOpsException.NotFound("history.notFound", Text.L("The history entry was not found for the active tenant.", "Het geschiedenisitem is niet gevonden voor de actieve tenant."));

    public async Task<ExportedFile> ExportAsync(string format, AuditQuery query, IReadOnlyList<string>? ids, CancellationToken ct)
    {
        var scoped = query with { TenantId = sessions.Current.TenantId, Limit = 5000 };
        var records = ids is { Count: > 0 }
            ? (await Task.WhenAll(ids.Select(id => sessions.Audit.GetAsync(id, ct)))).Where(r => r is not null && r.TenantId == sessions.Current.TenantId).Cast<AuditRecord>().ToList()
            : (await sessions.Audit.QueryAsync(scoped, ct)).ToList();
        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmm");
        var session = sessions.Current;
        if (format.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            return new ExportedFile($"roomops-change-report-{stamp}.html", "text/html", ReportRenderer.Render(records, session, time.GetUtcNow()));
        }
        var payload = new
        {
            generator = "RoomOps Studio",
            generatedAt = time.GetUtcNow(),
            tenantId = session.TenantId,
            tenantName = session.TenantName,
            demo = session.Mode == SessionMode.Demo,
            disclaimer = ReportRenderer.Disclaimer,
            records,
        };
        return new ExportedFile($"roomops-change-history-{stamp}.json", "application/json", JsonSerializer.Serialize(payload, new JsonSerializerOptions(Execution.RoomOpsJson.Options) { WriteIndented = true }));
    }

    /// <summary>Rereads the values a past change set and compares them again; used for Places changes that were pending propagation.</summary>
    public async Task<AuditRecord> ReverifyAsync(string id, CancellationToken ct)
    {
        var record = await GetAsync(id, ct);
        var fields = new List<FieldVerification>();
        switch (record.OperationType)
        {
            case "booking.update":
            {
                var room = await inventory.ResolveRoomAsync(record.TargetId, ct);
                var current = await exchange.GetBookingPolicyAsync(room.PrimarySmtpAddress, ct);
                foreach (var change in record.Changes.Where(c => c.AfterValue is not null))
                {
                    var field = BookingPolicyFields.Find(change.Field);
                    if (field is null) continue;
                    var desired = field.ApplyJson(current, change.AfterValue!.Value);
                    fields.Add(new(field.Name, field.Label, change.After, field.Format(current), field.AreEqual(desired, current)));
                }
                break;
            }
            case "places.room.update":
            case "places.update":
            {
                string identity;
                PlaceType type;
                if (record.OperationType == "places.room.update")
                {
                    identity = (await inventory.ResolveRoomAsync(record.TargetId, ct)).PrimarySmtpAddress;
                    type = PlaceType.Room;
                }
                else
                {
                    identity = record.TargetId;
                    type = (await places.GetPlaceAsync(identity, ct))?.Type ?? PlaceType.Building;
                }
                var place = await places.GetPlaceAsync(identity, ct)
                    ?? throw RoomOpsException.NotFound("places.notFound", Text.L("The place could not be read from Microsoft Places.", "De locatie kon niet worden gelezen uit Microsoft Places."));
                var infos = Planning.PlaceFields.For(type).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var change in record.Changes.Where(c => infos.ContainsKey(c.Field)))
                {
                    var info = infos[change.Field];
                    var desired = change.AfterValue is { } json ? Planning.PlaceFields.FromJson(info, json) : null;
                    var actual = info.Get(place);
                    fields.Add(new(info.Name, info.Label, Planning.PlaceFields.Format(desired), Planning.PlaceFields.Format(actual), Planning.PlaceFields.AreEqual(actual, desired)));
                }
                break;
            }
            default:
                throw RoomOpsException.Unsupported("history.reverifyUnsupported", Text.L("Re-verification is available for booking-policy and Microsoft Places changes.", "Opnieuw verifiëren is beschikbaar voor wijzigingen aan het boekingsbeleid en Microsoft Places."));
        }
        var verification = VerificationResult.FromFields(fields);
        var now = time.GetUtcNow();
        await sessions.Audit.UpdateVerificationAsync(id, verification, now, ct);
        return record with { Verification = verification, VerifiedAt = now };
    }
}

public sealed record TemplateExport(string Format, int Version, IReadOnlyList<ConfigurationTemplate> Templates);

public sealed class TemplateService(ITemplateStore store, ISessionManager sessions, TimeProvider time)
{
    public Task<IReadOnlyList<ConfigurationTemplate>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public async Task<ConfigurationTemplate> SaveAsync(ConfigurationTemplate template, CancellationToken ct)
    {
        var issues = TemplateRules.Validate(template);
        if (issues.Any(i => i.Level == IssueLevel.Error))
        {
            throw RoomOpsException.Validation("template.invalid", string.Join(" ", issues.Where(i => i.Level == IssueLevel.Error).Select(i => i.Message)));
        }
        var existing = string.IsNullOrEmpty(template.Id) ? null : await store.GetAsync(template.Id, ct);
        var saved = template with
        {
            Id = string.IsNullOrEmpty(template.Id) ? Guid.NewGuid().ToString("N") : template.Id,
            Version = (existing?.Version ?? 0) + 1,
            UpdatedAt = time.GetUtcNow(),
            IsExample = false,
        };
        await store.SaveAsync(saved, ct);
        return saved;
    }

    public Task DeleteAsync(string id, CancellationToken ct) => store.DeleteAsync(id, ct);

    public async Task<ExportedFile> ExportAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        var selected = ids.Count == 0 ? all : all.Where(t => ids.Contains(t.Id)).ToList();
        var json = JsonSerializer.Serialize(new TemplateExport("roomops.templates", 1, selected), new JsonSerializerOptions(Execution.RoomOpsJson.Options) { WriteIndented = true });
        return new ExportedFile($"roomops-templates-{time.GetUtcNow():yyyyMMdd}.json", "application/json", json);
    }

    /// <summary>Imports templates from JSON. Every template is validated; imported templates get new ids so nothing existing is overwritten.</summary>
    public async Task<IReadOnlyList<ConfigurationTemplate>> ImportAsync(string json, CancellationToken ct)
    {
        TemplateExport? export;
        try
        {
            export = JsonSerializer.Deserialize<TemplateExport>(json, Execution.RoomOpsJson.Options);
        }
        catch (JsonException ex)
        {
            throw RoomOpsException.Validation("template.importInvalid", Text.L($"The file is not a valid RoomOps template export: {ex.Message}", $"Het bestand is geen geldige sjabloonexport van RoomOps: {ex.Message}"));
        }
        if (export is null || export.Format != "roomops.templates")
        {
            throw RoomOpsException.Validation("template.importInvalid", Text.L("The file is not a RoomOps template export.", "Het bestand is geen sjabloonexport van RoomOps."));
        }
        var imported = new List<ConfigurationTemplate>();
        foreach (var template in export.Templates)
        {
            imported.Add(await SaveAsync(template with { Id = string.Empty, Name = template.Name }, ct));
        }
        return imported;
    }

    public async Task<IReadOnlyList<TemplateAssignment>> AssignmentsAsync(CancellationToken ct) =>
        await store.ListAssignmentsAsync(sessions.Current.TenantId ?? string.Empty, ct);

    public Task UnassignAsync(string roomId, CancellationToken ct) => store.RemoveAssignmentAsync(sessions.Current.TenantId ?? string.Empty, roomId, ct);
}
