using RoomOps.Application.Abstractions;
using RoomOps.Application.Commands;
using RoomOps.Domain.Booking;

namespace RoomOps.Bridge;

// Request and response records used only at the bridge boundary. Service result types are reused directly.

public sealed record Empty;

public sealed record Ack(bool Ok = true, string? Message = null);

public sealed record RefreshRequest(bool Refresh = false);

public sealed record RoomRequest(string RoomId, bool Refresh = false);

public sealed record IdRequest(string Id);

public sealed record IdsRequest(IReadOnlyList<string> Ids);

public sealed record QueryRequest(string Query);

public sealed record ServiceRequest(ServiceKind Service);

public sealed record ExecutionRequest(string ExecutionId);

public sealed record RecipientSearchRequest(string Query, IReadOnlyList<string>? RecipientTypeDetails);

public sealed record AvailabilityRequest(string? Alias, string? PrimarySmtpAddress, string? DisplayName);

public sealed record ApplyPresetRequest(BookingPolicy Policy, string PresetId);

public sealed record ImportRequest(string Content);

public sealed record HistoryExportRequest(string Format, AuditQuery Query, IReadOnlyList<string>? Ids);

public sealed record SaveFileRequest(string FileName, string MimeType, string Content);

public sealed record SaveFileResult(bool Saved, string? Path, bool UseBrowserDownload);

public sealed record OpenFileRequest(IReadOnlyList<string> Extensions);

public sealed record OpenFileResult(bool Opened, string? FileName, string? Content, bool UseBrowserPicker);

public sealed record ThemeRequest(bool Dark);

public sealed record UrlRequest(string Url);

public sealed record HostCapabilities(bool NativeFileDialogs, bool NativeTitleBar);

public sealed record AppInfo(string Product, string Version, string Host, string Platform, string Runtime, HostCapabilities Capabilities, string DataDirectory, bool LiveConnectionsAllowed);

public sealed record ModuleStatus(string Name, string Purpose, string MinimumVersion, string? InstalledVersion, string InstallCommand);

public sealed record EnvironmentInfo(string? PowerShellPath, string? PowerShellVersion, IReadOnlyList<ModuleStatus> Modules, IReadOnlyList<KnownTenant> KnownTenants, string? ProbeError);

/// <summary>Host-specific capabilities (native dialogs, window chrome). Implemented by the desktop shell and the dev host.</summary>
public interface IHostServices
{
    AppInfo Info { get; }
    Task<SaveFileResult> SaveFileAsync(SaveFileRequest request, CancellationToken cancellationToken);
    Task<OpenFileResult> OpenFileAsync(OpenFileRequest request, CancellationToken cancellationToken);
    void Ready();
    void SetTheme(bool dark);
    bool OpenExternal(Uri uri);
}

public interface IEnvironmentProbe
{
    Task<EnvironmentInfo> ProbeAsync(CancellationToken cancellationToken);
}
