using RoomOps.Application.Abstractions;
using RoomOps.Application.Execution;
using RoomOps.Application.Services;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.RoomLists;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.TeamsRooms;
using RoomOps.Domain.Templates;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Bridge;

/// <summary>Every bridge method and event. The TypeScript contract is generated from this registry.</summary>
public static class BridgeContract
{
    private static readonly HashSet<string> ExternalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "learn.microsoft.com", "admin.microsoft.com", "admin.exchange.microsoft.com", "entra.microsoft.com", "places.cloud.microsoft", "admin.teams.microsoft.com",
    };

    public static BridgeRegistry Build() => new BridgeRegistry()
        // Application and host
        .Query<IHostServices, Empty, AppInfo>("app.info", (h, _, _) => Task.FromResult(h.Info))
        .Command<IHostServices, Empty, Ack>("app.ready", (h, _, _) => { h.Ready(); return Task.FromResult(new Ack()); })
        .Command<IHostServices, ThemeRequest, Ack>("app.setTheme", (h, r, _) => { h.SetTheme(r.Dark); return Task.FromResult(new Ack()); })
        .Command<IHostServices, UrlRequest, Ack>("app.openExternal", (h, r, _) =>
        {
            if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !ExternalHosts.Contains(uri.Host))
            {
                throw RoomOpsException.Validation("app.externalBlocked", Text.L("Only Microsoft documentation and admin portal links can be opened.", "Alleen koppelingen naar Microsoft-documentatie en beheerportals kunnen worden geopend."));
            }
            return Task.FromResult(new Ack(h.OpenExternal(uri)));
        })
        .Command<IHostServices, SaveFileRequest, SaveFileResult>("files.save", (h, r, ct) => h.SaveFileAsync(r, ct))
        .Command<IHostServices, OpenFileRequest, OpenFileResult>("files.open", (h, r, ct) => h.OpenFileAsync(r, ct))

        // Session and connections
        .Query<ISessionManager, Empty, SessionSnapshot>("session.get", (s, _, _) => Task.FromResult(s.Current))
        .Command<ISessionManager, Empty, SessionSnapshot>("session.startDemo", (s, _, ct) => s.StartDemoAsync(ct))
        .Command<ISessionManager, Empty, SessionSnapshot>("session.end", (s, _, ct) => s.EndSessionAsync(ct))
        .Command<LiveConnectionGate, ConnectRequest, SessionSnapshot>("connections.connect", (g, r, ct) => g.ConnectAsync(r, ct))
        .Command<ISessionManager, ServiceRequest, SessionSnapshot>("connections.disconnect", (s, r, ct) => s.DisconnectAsync(r.Service, ct))
        .Query<IEnvironmentProbe, Empty, EnvironmentInfo>("connections.environment", (p, _, ct) => p.ProbeAsync(ct))

        // Settings
        .Query<ISettingsStore, Empty, AppSettings>("settings.get", (s, _, _) => Task.FromResult(s.Current))
        .Command<ISettingsStore, AppSettings, AppSettings>("settings.update", (s, r, ct) => s.UpdateAsync(_ => Sanitize(r), ct))

        // Inventory and rooms
        .Query<DashboardService, Empty, DashboardSummary>("dashboard.get", (s, _, ct) => s.GetAsync(ct))
        .Query<RoomsService, RefreshRequest, RoomInventory>("rooms.list", (s, r, ct) => s.ListAsync(r.Refresh, ct))
        .Query<RoomsService, RoomRequest, RoomDetail>("rooms.get", (s, r, ct) => s.GetDetailAsync(r.RoomId, r.Refresh, ct))
        .Query<RoomsService, Empty, IReadOnlyList<AcceptedDomain>>("rooms.acceptedDomains", (s, _, ct) => s.GetAcceptedDomainsAsync(ct))
        .Query<RoomsService, AvailabilityRequest, RoomsService.AvailabilityResult>("rooms.checkAvailability", (s, r, ct) => s.CheckAvailabilityAsync(r.Alias, r.PrimarySmtpAddress, r.DisplayName, ct))
        .Command<ScanService, Empty, ScanProgress>("scan.start", (s, _, ct) => s.StartAsync(ct))
        .Command<ScanService, Empty, Ack>("scan.cancel", (s, _, _) => { s.Cancel(); return Task.FromResult(new Ack()); })

        // Booking policy designer
        .Query<BookingService, Empty, IReadOnlyList<PolicyFieldInfo>>("booking.fields", (s, _, _) => Task.FromResult(s.Fields()))
        .Query<BookingService, Empty, IReadOnlyList<BookingPreset>>("booking.presets", (s, _, _) => Task.FromResult(s.Presets()))
        .Query<BookingService, ApplyPresetRequest, BookingPolicy>("booking.applyPreset", (s, r, _) => Task.FromResult(s.ApplyPreset(r.Policy, r.PresetId)))
        .Query<BookingService, ExplainRequest, PolicyAnalysis>("booking.analyze", (s, r, _) => Task.FromResult(s.Analyze(r)))
        .Query<BookingService, SimulationRequest, SimulationResult>("booking.simulate", (s, r, _) => Task.FromResult(s.Simulate(r)))
        .Query<BookingService, Empty, IReadOnlyList<TimeZoneEntry>>("timeZones.list", (s, _, _) => Task.FromResult(s.TimeZones()))

        // Delegates, working hours, Teams Rooms
        .Query<DelegatesService, RecipientSearchRequest, IReadOnlyList<Recipient>>("recipients.search", (s, r, ct) => s.SearchRecipientsAsync(r.Query, r.RecipientTypeDetails, ct))
        .Query<DelegatesService, RoomRequest, DelegateOverview>("delegates.get", (s, r, ct) => s.GetAsync(r.RoomId, ct))
        .Query<WorkingHoursService, RoomRequest, WorkingHoursView>("workingHours.get", (s, r, ct) => s.GetAsync(r.RoomId, ct))
        .Query<TeamsRoomsService, RoomRequest, TeamsRoomReadiness>("teamsRooms.readiness", (s, r, ct) => s.GetAsync(r.RoomId, r.Refresh, ct))

        // Room lists
        .Query<RoomListsService, RefreshRequest, IReadOnlyList<RoomListView>>("roomLists.list", (s, r, ct) => s.ListAsync(r.Refresh, ct))
        .Query<RoomListsService, QueryRequest, IReadOnlyList<ConvertibleGroup>>("groups.search", (s, r, ct) => s.SearchGroupsAsync(r.Query, ct))
        .Query<RoomListsService, IdRequest, ConversionAssessment>("groups.assess", (s, r, ct) => s.AssessConversionAsync(r.Id, ct))

        // Microsoft Places
        .Query<PlacesService, RefreshRequest, PlacesTree>("places.tree", (s, r, ct) => s.GetTreeAsync(r.Refresh, ct))
        .Query<PlacesService, Empty, DiscoveryDiagnostics>("places.diagnostics", (s, _, ct) => s.GetDiagnosticsAsync(ct))

        // Validation, templates, bulk
        .Query<ValidationService, RefreshRequest, ValidationReport>("validation.run", (s, r, ct) => s.RunAsync(r.Refresh, ct))
        .Query<TemplateService, Empty, IReadOnlyList<ConfigurationTemplate>>("templates.list", (s, _, ct) => s.ListAsync(ct))
        .Command<TemplateService, ConfigurationTemplate, ConfigurationTemplate>("templates.save", (s, r, ct) => s.SaveAsync(r, ct))
        .Command<TemplateService, IdRequest, Ack>("templates.delete", async (s, r, ct) => { await s.DeleteAsync(r.Id, ct); return new Ack(); })
        .Query<TemplateService, IdsRequest, ExportedFile>("templates.export", (s, r, ct) => s.ExportAsync(r.Ids, ct))
        .Command<TemplateService, ImportRequest, IReadOnlyList<ConfigurationTemplate>>("templates.import", (s, r, ct) => s.ImportAsync(r.Content, ct))
        .Query<TemplateService, Empty, IReadOnlyList<TemplateAssignment>>("templates.assignments", (s, _, ct) => s.AssignmentsAsync(ct))
        .Command<TemplateService, RoomRequest, Ack>("templates.unassign", async (s, r, ct) => { await s.UnassignAsync(r.RoomId, ct); return new Ack(); })
        .Query<BulkImportService, Empty, IReadOnlyList<BulkTargetField>>("bulk.fields", (_, _, _) => Task.FromResult(BulkImportService.TargetFields))
        .Query<BulkImportService, BulkParseRequest, BulkParseResult>("bulk.parse", (s, r, ct) => s.ParseAsync(r, ct))

        // Change pipeline
        .Query<PlanService, ChangeIntent, PlanPreview>("plans.create", (s, r, ct) => s.CreateAsync(r, ct))
        .Command<PlanService, ExecuteRequest, ExecutionState>("plans.execute", (s, r, _) => Task.FromResult(s.Execute(r)))
        .Query<PlanService, ExecutionRequest, PlanPreview>("plans.retry", (s, r, ct) => s.CreateRetryAsync(r.ExecutionId, ct))
        .Query<ExecutionEngine, ExecutionRequest, ExecutionState>("executions.get", (e, r, _) => Task.FromResult(e.Get(r.ExecutionId)
            ?? throw RoomOpsException.NotFound("execution.notFound", Text.L("The execution is no longer available.", "De uitvoering is niet meer beschikbaar."))))
        .Query<ExecutionEngine, Empty, IReadOnlyList<ExecutionState>>("executions.list", (e, _, _) => Task.FromResult(e.List()))
        .Command<ExecutionEngine, ExecutionRequest, Ack>("executions.cancel", (e, r, _) => Task.FromResult(new Ack(e.Cancel(r.ExecutionId))))

        // History
        .Query<HistoryService, AuditQuery, AuditPage>("history.list", (s, r, ct) => s.ListAsync(r, ct))
        .Query<HistoryService, IdRequest, AuditRecord>("history.get", (s, r, ct) => s.GetAsync(r.Id, ct))
        .Query<HistoryService, HistoryExportRequest, ExportedFile>("history.export", (s, r, ct) => s.ExportAsync(r.Format, r.Query, r.Ids, ct))
        .Command<HistoryService, IdRequest, AuditRecord>("history.reverify", (s, r, ct) => s.ReverifyAsync(r.Id, ct))

        // Events pushed to the frontend
        .Event<SessionSnapshot>("session.changed")
        .Event<SignInPrompt>("signIn.prompt")
        .Event<ExecutionProgressEvent>("execution.progress")
        .Event<ExecutionState>("execution.completed")
        .Event<ScanProgress>("scan.progress")
        .Event<InventoryChanged>("inventory.changed");

    private static AppSettings Sanitize(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.Connection.PowerShellPath))
        {
            var name = s.Connection.PowerShellPath.Trim().Trim('"').Split('/', '\\').Last();
            if (!name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("pwsh", StringComparison.Ordinal))
            {
                throw RoomOpsException.Validation("settings.powerShellPath", Text.L("The PowerShell path must point to pwsh.exe (PowerShell 7).", "Het PowerShell-pad moet naar pwsh.exe (PowerShell 7) verwijzen."));
            }
        }
        return SanitizeValues(s);
    }

    private static AppSettings SanitizeValues(AppSettings s) => s with
    {
        Execution = new ExecutionSettings(
            Math.Clamp(s.Execution.PacingMilliseconds, 0, 10_000),
            Math.Clamp(s.Execution.MaxThrottleRetries, 0, 10),
            Math.Clamp(s.Execution.ProvisioningWaitSeconds, 0, 1800)),
        Appearance = s.Appearance with { Language = s.Appearance.Language is "en" or "nl" ? s.Appearance.Language : "en" },
        Connection = s.Connection with { PowerShellPath = string.IsNullOrWhiteSpace(s.Connection.PowerShellPath) ? null : s.Connection.PowerShellPath.Trim() },
        SavedViews = s.SavedViews.Take(100).ToList(),
        KnownTenants = s.KnownTenants.Take(10).ToList(),
    };
}

public sealed record InventoryChanged(string Reason);

/// <summary>
/// Live connections can be disabled by the host (the browser-based dev host only allows them with an explicit flag), so a
/// development server can't be used to drive a real tenant by accident.
/// </summary>
public sealed class LiveConnectionGate(ISessionManager sessions, IHostServices host)
{
    public Task<SessionSnapshot> ConnectAsync(ConnectRequest request, CancellationToken ct)
    {
        if (!host.Info.LiveConnectionsAllowed)
        {
            throw RoomOpsException.Unsupported("connections.disabled", Text.L("Live Microsoft 365 connections are disabled in this host. Use the RoomOps Studio desktop application, or start the development host with --allow-live.", "Live verbindingen met Microsoft 365 zijn uitgeschakeld in deze host. Gebruik de desktoptoepassing RoomOps Studio, of start de ontwikkelhost met --allow-live."));
        }
        return sessions.ConnectAsync(request, ct);
    }
}
