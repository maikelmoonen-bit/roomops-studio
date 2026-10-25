using System.Globalization;
using System.Text;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;

namespace RoomOps.Application.Commands;

public enum ServiceKind
{
    Exchange,
    Places,
    Graph,
}

public enum CommandEffect
{
    Read,
    Write,
    /// <summary>Removes a membership or permission. Reversible, but requires an explicit acknowledgement before execution.</summary>
    Removal,
}

/// <summary>A switch parameter. <c>new PsSwitch(false)</c> renders as <c>-Name:$false</c>.</summary>
public readonly record struct PsSwitch(bool IsPresent);

public sealed record CommandSpec(ServiceKind Service, string Name, CommandEffect Effect, IReadOnlySet<string> Parameters, string Purpose);

/// <summary>
/// The complete allowlist of PowerShell commands RoomOps may run. Nothing outside this catalog can be constructed,
/// and the worker process enforces an identical allowlist independently.
/// </summary>
public static class CommandCatalog
{
    private static IReadOnlySet<string> P(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CalendarProcessingParameters =
        ["Identity", .. BookingPolicyFields.All.Select(f => f.Name)];

    public static IReadOnlyList<CommandSpec> All { get; } =
    [
        // Exchange Online — reads
        new(ServiceKind.Exchange, "Get-ConnectionInformation", CommandEffect.Read, P(), "Session tenant and account"),
        new(ServiceKind.Exchange, "Get-EXOMailbox", CommandEffect.Read, P("Identity", "RecipientTypeDetails", "ResultSize", "Properties"), "Room mailbox inventory"),
        new(ServiceKind.Exchange, "Get-EXORecipient", CommandEffect.Read, P("Identity", "Anr", "RecipientTypeDetails", "ResultSize", "Properties"), "Recipient resolution and search"),
        new(ServiceKind.Exchange, "Get-EXOMailboxPermission", CommandEffect.Read, P("Identity", "ResultSize"), "Mailbox Full Access (read-only)"),
        new(ServiceKind.Exchange, "Get-EXORecipientPermission", CommandEffect.Read, P("Identity", "ResultSize"), "Send As (read-only)"),
        new(ServiceKind.Exchange, "Get-CalendarProcessing", CommandEffect.Read, P("Identity"), "Booking policy"),
        new(ServiceKind.Exchange, "Get-MailboxCalendarConfiguration", CommandEffect.Read, P("Identity"), "Working hours"),
        new(ServiceKind.Exchange, "Get-MailboxFolderStatistics", CommandEffect.Read, P("Identity", "FolderScope"), "Localized calendar folder name"),
        new(ServiceKind.Exchange, "Get-MailboxFolderPermission", CommandEffect.Read, P("Identity"), "Calendar folder permissions"),
        new(ServiceKind.Exchange, "Get-AcceptedDomain", CommandEffect.Read, P(), "Accepted domains"),
        new(ServiceKind.Exchange, "Get-DistributionGroup", CommandEffect.Read, P("Identity", "RecipientTypeDetails", "ResultSize", "Anr"), "Room lists and groups"),
        new(ServiceKind.Exchange, "Get-DistributionGroupMember", CommandEffect.Read, P("Identity", "ResultSize"), "Room list membership"),
        // Exchange Online — writes
        new(ServiceKind.Exchange, "Set-CalendarProcessing", CommandEffect.Write, new HashSet<string>(CalendarProcessingParameters, StringComparer.OrdinalIgnoreCase), "Booking policy"),
        new(ServiceKind.Exchange, "Set-MailboxCalendarConfiguration", CommandEffect.Write, P("Identity", "WorkDays", "WorkingHoursStartTime", "WorkingHoursEndTime", "WorkingHoursTimeZone"), "Working hours"),
        new(ServiceKind.Exchange, "Add-MailboxFolderPermission", CommandEffect.Write, P("Identity", "User", "AccessRights", "SharingPermissionFlags"), "Calendar folder permission"),
        new(ServiceKind.Exchange, "Set-MailboxFolderPermission", CommandEffect.Write, P("Identity", "User", "AccessRights", "SharingPermissionFlags"), "Calendar folder permission"),
        new(ServiceKind.Exchange, "Remove-MailboxFolderPermission", CommandEffect.Removal, P("Identity", "User", "Confirm"), "Calendar folder permission"),
        new(ServiceKind.Exchange, "New-Mailbox", CommandEffect.Write, P("Name", "Room", "Alias", "DisplayName", "PrimarySmtpAddress"), "Room mailbox creation"),
        new(ServiceKind.Exchange, "Set-Mailbox", CommandEffect.Write, P("Identity", "DisplayName", "ResourceCapacity"), "Room mailbox properties"),
        new(ServiceKind.Exchange, "New-DistributionGroup", CommandEffect.Write, P("Name", "RoomList", "Alias", "DisplayName", "PrimarySmtpAddress"), "Room list creation"),
        new(ServiceKind.Exchange, "Set-DistributionGroup", CommandEffect.Write, P("Identity", "DisplayName", "RoomList", "BypassSecurityGroupManagerCheck"), "Room list rename or conversion"),
        new(ServiceKind.Exchange, "Add-DistributionGroupMember", CommandEffect.Write, P("Identity", "Member", "BypassSecurityGroupManagerCheck"), "Room list membership"),
        new(ServiceKind.Exchange, "Remove-DistributionGroupMember", CommandEffect.Removal, P("Identity", "Member", "BypassSecurityGroupManagerCheck", "Confirm"), "Room list membership"),

        // Microsoft Places — only documented parameters (Microsoft Learn: "Do not use any parameter that is not documented").
        new(ServiceKind.Places, "Get-PlaceV3", CommandEffect.Read, P("Identity", "Type", "AncestorId"), "Places directory"),
        new(ServiceKind.Places, "Get-PlacesSettings", CommandEffect.Read, P(), "Places Finder and building settings (read-only)"),
        new(ServiceKind.Places, "New-Place", CommandEffect.Write, P("DisplayName", "Type", "ParentId", "Label", "SortOrder", "Street", "City", "State", "PostalCode", "CountryOrRegion", "Capacity", "Mailbox"), "Building, floor, section or room creation"),
        new(ServiceKind.Places, "Set-PlaceV3", CommandEffect.Write, P("Identity", "DisplayName", "ParentId", "Label", "Capacity", "IsWheelChairAccessible", "AudioDeviceName", "VideoDeviceName", "DisplayDeviceName", "MTREnabled", "Tags", "Street", "City", "State", "PostalCode", "CountryOrRegion", "SortOrder", "FloorLabel"), "Places metadata and hierarchy"),

        // Microsoft Graph — read-only, GET requests to allowlisted paths.
        new(ServiceKind.Graph, "Get-MgContext", CommandEffect.Read, P(), "Session tenant and scopes"),
        new(ServiceKind.Graph, "Invoke-MgGraphRequest", CommandEffect.Read, P("Method", "Uri", "OutputType"), "Directory account and license details"),
    ];

    private static readonly Dictionary<string, CommandSpec> ByName = All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public static CommandSpec Get(string name) =>
        ByName.TryGetValue(name, out var spec) ? spec : throw new InvalidOperationException($"Command '{name}' is not in the RoomOps command catalog.");

    public static bool IsAllowed(string name) => ByName.ContainsKey(name);

    /// <summary>Graph paths the application may request. Anything else is rejected before reaching the worker.</summary>
    public static bool IsAllowedGraphUri(string uri) =>
        System.Text.RegularExpressions.Regex.IsMatch(uri,
            @"^https://graph\.microsoft\.com/v1\.0/users/[A-Za-z0-9.@_%-]{1,256}(\?\$select=[A-Za-z,]{1,300}|/licenseDetails)$");
}

/// <summary>
/// A single, fully-bound PowerShell command. Parameters are bound by name through splatting in the worker; values are never
/// concatenated into script text. <see cref="ToScript"/> renders an equivalent, correctly quoted command for preview and copy.
/// </summary>
public sealed class PsCommand
{
    private PsCommand(CommandSpec spec, IReadOnlyList<KeyValuePair<string, object?>> parameters)
    {
        Spec = spec;
        Parameters = parameters;
    }

    public CommandSpec Spec { get; }
    public string Name => Spec.Name;
    public ServiceKind Service => Spec.Service;
    public CommandEffect Effect => Spec.Effect;
    public IReadOnlyList<KeyValuePair<string, object?>> Parameters { get; }

    public static PsCommand Create(string name, params (string Name, object? Value)[] parameters) =>
        Create(name, parameters.Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)));

    public static PsCommand Create(string name, IEnumerable<KeyValuePair<string, object?>> parameters)
    {
        var spec = CommandCatalog.Get(name);
        var list = new List<KeyValuePair<string, object?>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, raw) in parameters)
        {
            if (!spec.Parameters.Contains(key))
            {
                throw new InvalidOperationException($"Parameter '{key}' is not allowed for {name}.");
            }
            if (!seen.Add(key))
            {
                throw new InvalidOperationException($"Parameter '{key}' was specified twice for {name}.");
            }
            list.Add(new(key, NormalizeValue(name, key, raw)));
        }
        if (name.Equals("Invoke-MgGraphRequest", StringComparison.OrdinalIgnoreCase))
        {
            var method = list.FirstOrDefault(p => p.Key.Equals("Method", StringComparison.OrdinalIgnoreCase)).Value as string;
            var uri = list.FirstOrDefault(p => p.Key.Equals("Uri", StringComparison.OrdinalIgnoreCase)).Value as string;
            if (!"GET".Equals(method, StringComparison.Ordinal) || uri is null || !CommandCatalog.IsAllowedGraphUri(uri))
            {
                throw new InvalidOperationException("Only allowlisted Graph GET requests are permitted.");
            }
        }
        return new PsCommand(spec, list);
    }

    private static object? NormalizeValue(string command, string parameter, object? value) => value switch
    {
        null => null,
        string or bool or int or long or PsSwitch => value,
        string[] arr => arr,
        IEnumerable<string> seq => seq.ToArray(),
        Enum e => e.ToString(),
        _ => throw new InvalidOperationException($"Unsupported value type {value.GetType().Name} for {command} -{parameter}."),
    };

    public object? Get(string parameter) =>
        Parameters.FirstOrDefault(p => p.Key.Equals(parameter, StringComparison.OrdinalIgnoreCase)).Value;

    public bool Has(string parameter) => Parameters.Any(p => p.Key.Equals(parameter, StringComparison.OrdinalIgnoreCase));

    public string? Identity => Get("Identity") as string;

    /// <summary>Renders the command as PowerShell with safe single-quote escaping.</summary>
    public string ToScript()
    {
        var sb = new StringBuilder(Name);
        foreach (var (key, value) in Parameters)
        {
            sb.Append(' ');
            switch (value)
            {
                case PsSwitch sw:
                    sb.Append('-').Append(key);
                    if (!sw.IsPresent) sb.Append(":$false");
                    break;
                default:
                    sb.Append('-').Append(key).Append(' ').Append(Literal(value));
                    break;
            }
        }
        return sb.ToString();
    }

    public static string Literal(object? value) => value switch
    {
        null => "$null",
        bool b => b ? "$true" : "$false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        string s => Quote(s),
        string[] { Length: 0 } => "@()",
        string[] arr => string.Join(",", arr.Select(Quote)),
        _ => Quote(value.ToString() ?? string.Empty),
    };

    /// <summary>PowerShell single-quoted string: only the quote characters need escaping (including typographic quotes PowerShell treats as quotes).</summary>
    public static string Quote(string s)
    {
        var escaped = new StringBuilder(s.Length + 2);
        foreach (var ch in s)
        {
            if (ch is '\'' or '‘' or '’' or '‚' or '‛')
            {
                escaped.Append(ch).Append(ch);
            }
            else
            {
                escaped.Append(ch);
            }
        }
        return $"'{escaped}'";
    }

    public override string ToString() => ToScript();
}

public sealed record CommandResult(IReadOnlyList<System.Text.Json.Nodes.JsonObject> Objects, IReadOnlyList<string> Warnings, TimeSpan Duration);

/// <summary>Executes catalog commands for one service. Live sessions use an isolated PowerShell worker; demo sessions use the in-memory tenant.</summary>
public interface ICommandRunner
{
    ServiceKind Service { get; }
    Task<CommandResult> RunAsync(PsCommand command, CancellationToken cancellationToken);
}

/// <summary>A command failed in the service. <see cref="Kind"/> drives retry and the explanation shown to the administrator.</summary>
public sealed class CommandFailedException : RoomOpsException
{
    public CommandFailedException(string command, ErrorKind kind, string message, string? errorId = null, Exception? inner = null)
        : base(new DomainError($"command.{kind.ToString().ToLowerInvariant()}", message, kind, errorId), inner)
    {
        Command = command;
        ErrorId = errorId;
    }

    public string Command { get; }
    public string? ErrorId { get; }
    public ErrorKind Kind => Error.Kind;

    public bool IsRetryable => Kind is ErrorKind.Throttled or ErrorKind.Transient;
}

public static class ErrorClassifier
{
    /// <summary>Maps Exchange/Places/Graph error text to an <see cref="ErrorKind"/>. Conservative: unknown errors are not retried.</summary>
    public static ErrorKind Classify(string? message, string? errorId = null, string? category = null)
    {
        var text = $"{message} {errorId} {category}";
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("throttl") || Has("TooManyRequests") || Has("429") || Has("micro delay") || Has("server busy") || Has("ServerBusy")) return ErrorKind.Throttled;
        if (Has("couldn't be found") || Has("could not be found") || Has("ManagementObjectNotFound") || Has("ObjectNotFound") || Has("doesn't exist") || Has("Request_ResourceNotFound") || Has("404")) return ErrorKind.NotFound;
        if (Has("access denied") || Has("AccessDenied") || Has("Authorization_RequestDenied") || Has("Insufficient privileges") || Has("isn't within your current write scopes") || Has("permission") && Has("denied") || Has("403")) return ErrorKind.PermissionDenied;
        if (Has("token") && (Has("expired") || Has("invalid")) || Has("AADSTS") || Has("unauthorized") || Has("401") || Has("not connected") || Has("Connect-ExchangeOnline")) return ErrorKind.Authentication;
        if (Has("already exists") || Has("is already being used") || Has("proxy address") && Has("already") || Has("already a member") || Has("MemberAlreadyExists")) return ErrorKind.Conflict;
        if (Has("timed out") || Has("timeout") || Has("temporarily") || Has("503") || Has("502") || Has("connection was closed") || Has("network")) return ErrorKind.Transient;
        if (Has("cannot bind parameter") || Has("is not valid") || Has("isn't valid") || Has("ParameterBinding") || Has("ValidationError") || Has("InvalidArgument")) return ErrorKind.Validation;
        return ErrorKind.Internal;
    }
}
