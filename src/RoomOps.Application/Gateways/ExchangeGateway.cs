using System.Text.Json.Nodes;
using RoomOps.Application.Commands;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.RoomLists;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Application.Gateways;

public sealed record FolderPermission(string User, string? UserSmtp, IReadOnlyList<string> AccessRights, IReadOnlyList<string> SharingPermissionFlags);

public sealed record CalendarFolder(string Name, string Identity);

/// <summary>A Full Access or Send As entry. <paramref name="DisplayName"/> and <paramref name="PrimarySmtpAddress"/> are filled when the trustee resolves.</summary>
public sealed record MailboxAccessEntry(string Trustee, string Right, bool IsInherited, string? DisplayName = null, string? PrimarySmtpAddress = null);

/// <summary>Typed reads against Exchange Online. All calls go through the session's Exchange runner (worker or demo tenant).</summary>
public sealed class ExchangeGateway(ISessionManager sessions)
{
    private static readonly string[] MailboxProperties =
        ["ResourceCapacity", "ResourceCustom", "Office", "IsDirSynced", "WhenCreated", "AccountDisabled", "HiddenFromAddressListsEnabled", "ExchangeGuid"];

    // The Minimum property set of Get-EXORecipient holds only Name, ExternalDirectoryObjectId and the recipient types;
    // display name and address must be requested explicitly.
    private static readonly string[] RecipientProperties = ["DisplayName", "PrimarySmtpAddress", "Alias"];

    private Task<CommandResult> Run(PsCommand command, CancellationToken ct) => sessions.Runner(ServiceKind.Exchange).RunAsync(command, ct);

    public async Task<IReadOnlyList<RoomMailbox>> GetRoomMailboxesAsync(CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-EXOMailbox",
            ("RecipientTypeDetails", new[] { "RoomMailbox" }),
            ("ResultSize", "Unlimited"),
            ("Properties", MailboxProperties)), ct);
        return result.Objects.Select(MapMailbox).OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<RoomMailbox?> GetMailboxAsync(string identity, CancellationToken ct)
    {
        try
        {
            var result = await Run(PsCommand.Create("Get-EXOMailbox", ("Identity", identity), ("Properties", MailboxProperties)), ct);
            return result.Objects.Count == 0 ? null : MapMailbox(result.Objects[0]);
        }
        catch (CommandFailedException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            return null;
        }
    }

    public static RoomMailbox MapMailbox(JsonObject o)
    {
        var smtp = o.Str("PrimarySmtpAddress") ?? throw new InvalidOperationException("Mailbox without PrimarySmtpAddress.");
        return new RoomMailbox
        {
            Id = o.Str("ExternalDirectoryObjectId") ?? o.Str("ExchangeGuid") ?? o.Str("Guid") ?? smtp,
            ExchangeGuid = o.Str("ExchangeGuid"),
            DisplayName = o.Str("DisplayName") ?? smtp,
            Alias = o.Str("Alias") ?? smtp.Split('@')[0],
            PrimarySmtpAddress = smtp,
            UserPrincipalName = o.Str("UserPrincipalName"),
            Name = o.Str("Name"),
            ResourceCapacity = o.Int("ResourceCapacity"),
            Office = o.Str("Office"),
            ResourceCustom = o.StrList("ResourceCustom"),
            IsDirSynced = o.Bool("IsDirSynced") ?? false,
            AccountDisabled = o.Bool("AccountDisabled"),
            HiddenFromAddressLists = o.Bool("HiddenFromAddressListsEnabled") ?? false,
            RecipientTypeDetails = o.Str("RecipientTypeDetails") ?? "RoomMailbox",
            WhenCreated = o.Date("WhenCreated") ?? o.Date("WhenCreatedUTC"),
        };
    }

    /// <summary>Reads calendar processing. Recipient-valued properties are resolved to SMTP addresses (cached per session).</summary>
    public async Task<BookingPolicy> GetBookingPolicyAsync(string identity, CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-CalendarProcessing", ("Identity", identity)), ct);
        if (result.Objects.Count == 0)
        {
            throw RoomOpsException.NotFound("booking.notFound", Text.L($"Calendar processing for '{identity}' was not returned.", $"De agendaverwerking voor '{identity}' is niet geretourneerd."));
        }
        var policy = MapBookingPolicy(result.Objects[0]);
        return await ResolveRecipientsAsync(policy, ct);
    }

    public static BookingPolicy MapBookingPolicy(JsonObject o)
    {
        var policy = BookingPolicy.ExchangeDefaults;
        foreach (var field in BookingPolicyFields.All)
        {
            if (!o.ContainsKey(field.Name)) continue;
            object? value = field.Kind switch
            {
                PolicyFieldKind.Boolean => o.Bool(field.Name),
                PolicyFieldKind.Integer => o.Int(field.Name),
                PolicyFieldKind.Enum => field.Name == nameof(BookingPolicy.AutomateProcessing)
                    ? Enum.TryParse<AutomateProcessing>(o.Str(field.Name), true, out var ap) ? ap : null
                    : Enum.TryParse<ResourceBookingType>(o.Str(field.Name), true, out var bt) ? bt : null,
                PolicyFieldKind.RecipientList => o.StrList(field.Name).Select(s => new RecipientRef { Identity = s, Resolution = RecipientResolution.NotChecked }).ToList(),
                PolicyFieldKind.Text => o.Str(field.Name),
                _ => null,
            };
            if (value is not null || field.Kind == PolicyFieldKind.Text)
            {
                policy = field.Set(policy, value);
            }
        }
        return policy;
    }

    public async Task<BookingPolicy> ResolveRecipientsAsync(BookingPolicy policy, CancellationToken ct)
    {
        async Task<IReadOnlyList<RecipientRef>> ResolveAll(IReadOnlyList<RecipientRef> list)
        {
            var resolved = new List<RecipientRef>(list.Count);
            foreach (var r in list)
            {
                resolved.Add(r.Resolution == RecipientResolution.Resolved && r.PrimarySmtpAddress is not null ? r : await ResolveRecipientAsync(r.Identity, ct));
            }
            return resolved;
        }

        return policy with
        {
            ResourceDelegates = await ResolveAll(policy.ResourceDelegates),
            BookInPolicy = await ResolveAll(policy.BookInPolicy),
            RequestInPolicy = await ResolveAll(policy.RequestInPolicy),
            RequestOutOfPolicy = await ResolveAll(policy.RequestOutOfPolicy),
        };
    }

    public async Task<RecipientRef> ResolveRecipientAsync(string identity, CancellationToken ct)
    {
        if (sessions.Cache.Recipients.TryGetValue(identity, out var cached)) return cached;
        RecipientRef resolved;
        try
        {
            var result = await Run(PsCommand.Create("Get-EXORecipient", ("Identity", identity), ("Properties", RecipientProperties)), ct);
            resolved = result.Objects.Count switch
            {
                0 => await ResolveByDisplayNameAsync(identity, ct),
                1 => new RecipientRef
                {
                    Identity = identity,
                    DisplayName = result.Objects[0].Str("DisplayName"),
                    PrimarySmtpAddress = result.Objects[0].Str("PrimarySmtpAddress"),
                    RecipientTypeDetails = result.Objects[0].Str("RecipientTypeDetails"),
                    Resolution = RecipientResolution.Resolved,
                },
                _ => new RecipientRef { Identity = identity, Resolution = RecipientResolution.Ambiguous },
            };
        }
        catch (CommandFailedException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            resolved = await ResolveByDisplayNameAsync(identity, ct);
        }
        catch (CommandFailedException ex) when (ex.Message.Contains("multiple", StringComparison.OrdinalIgnoreCase))
        {
            resolved = new RecipientRef { Identity = identity, Resolution = RecipientResolution.Ambiguous };
        }
        sessions.Cache.Recipients[identity] = resolved;
        return resolved;
    }

    /// <summary>
    /// Calendar folder permissions name users by display name, which -Identity does not accept. An ANR search finds them;
    /// only an exact display-name match counts, and more than one is ambiguous.
    /// </summary>
    private async Task<RecipientRef> ResolveByDisplayNameAsync(string displayName, CancellationToken ct)
    {
        var matches = (await SearchRecipientsAsync(displayName, null, ct))
            .Where(r => string.Equals(r.DisplayName, displayName.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            0 => new RecipientRef { Identity = displayName, Resolution = RecipientResolution.Unresolved },
            1 => new RecipientRef
            {
                Identity = displayName,
                DisplayName = matches[0].DisplayName,
                PrimarySmtpAddress = matches[0].PrimarySmtpAddress,
                RecipientTypeDetails = matches[0].RecipientTypeDetails,
                Resolution = RecipientResolution.Resolved,
            },
            _ => new RecipientRef { Identity = displayName, Resolution = RecipientResolution.Ambiguous },
        };
    }

    /// <summary>Ambiguous name resolution search, used by recipient pickers. The query is bound to -Anr, never interpolated into a filter.</summary>
    public async Task<IReadOnlyList<Recipient>> SearchRecipientsAsync(string query, IReadOnlyList<string>? recipientTypeDetails, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return [];
        var parameters = new List<(string, object?)> { ("Anr", query.Trim()), ("ResultSize", "50"), ("Properties", RecipientProperties) };
        if (recipientTypeDetails is { Count: > 0 }) parameters.Add(("RecipientTypeDetails", recipientTypeDetails.ToArray()));
        var result = await Run(PsCommand.Create("Get-EXORecipient", [.. parameters]), ct);
        return result.Objects
            .Select(o => new Recipient(
                o.Str("ExternalDirectoryObjectId") ?? o.Str("Guid") ?? o.StrOr("PrimarySmtpAddress", string.Empty),
                o.Str("DisplayName") ?? o.StrOr("PrimarySmtpAddress", string.Empty),
                o.StrOr("PrimarySmtpAddress", string.Empty),
                o.StrOr("RecipientTypeDetails", "Unknown"),
                o.Str("Alias")))
            .Where(r => r.PrimarySmtpAddress.Length > 0)
            .ToList();
    }

    public async Task<bool> RecipientExistsAsync(string identity, CancellationToken ct)
    {
        try
        {
            var result = await Run(PsCommand.Create("Get-EXORecipient", ("Identity", identity)), ct);
            return result.Objects.Count > 0;
        }
        catch (CommandFailedException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            return false;
        }
    }

    public async Task<WorkingHoursConfig> GetWorkingHoursAsync(string identity, CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-MailboxCalendarConfiguration", ("Identity", identity)), ct);
        if (result.Objects.Count == 0)
        {
            throw RoomOpsException.NotFound("workingHours.notFound", Text.L($"Calendar configuration for '{identity}' was not returned.", $"De agendaconfiguratie voor '{identity}' is niet geretourneerd."));
        }
        return MapWorkingHours(result.Objects[0]);
    }

    public static WorkingHoursConfig MapWorkingHours(JsonObject o) => new(
        WorkingHoursConfig.ParseWorkDays(o.Str("WorkDays")),
        WorkingHoursConfig.ParseTime(o.Str("WorkingHoursStartTime")),
        WorkingHoursConfig.ParseTime(o.Str("WorkingHoursEndTime")),
        o.Str("WorkingHoursTimeZone") ?? "UTC");

    public async Task<IReadOnlyList<AcceptedDomain>> GetAcceptedDomainsAsync(CancellationToken ct)
    {
        if (sessions.Cache.AcceptedDomains is { } cached) return cached;
        var result = await Run(PsCommand.Create("Get-AcceptedDomain"), ct);
        var domains = result.Objects
            .Select(o => new AcceptedDomain(o.Str("DomainName") ?? o.StrOr("Name", string.Empty), o.Bool("Default") ?? false, o.Str("DomainType") ?? "Authoritative"))
            .Where(d => d.DomainName.Length > 0)
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.DomainName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        sessions.Cache.AcceptedDomains = domains;
        return domains;
    }

    public async Task<IReadOnlyList<RoomList>> GetRoomListsAsync(bool includeMembers, CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-DistributionGroup", ("RecipientTypeDetails", new[] { "RoomList" }), ("ResultSize", "Unlimited")), ct);
        var lists = new List<RoomList>();
        foreach (var o in result.Objects)
        {
            var list = MapGroup(o);
            if (includeMembers)
            {
                list = list with { Members = await GetGroupMembersAsync(list.PrimarySmtpAddress, ct), MembersLoaded = true };
            }
            lists.Add(list);
        }
        return lists.OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<RoomList?> GetDistributionGroupAsync(string identity, CancellationToken ct)
    {
        try
        {
            var result = await Run(PsCommand.Create("Get-DistributionGroup", ("Identity", identity)), ct);
            return result.Objects.Count == 0 ? null : MapGroup(result.Objects[0]);
        }
        catch (CommandFailedException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ConvertibleGroup>> SearchDistributionGroupsAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return [];
        var result = await Run(PsCommand.Create("Get-DistributionGroup", ("Anr", query.Trim()), ("ResultSize", "50")), ct);
        return result.Objects
            .Where(o => !string.Equals(o.Str("RecipientTypeDetails"), "RoomList", StringComparison.OrdinalIgnoreCase))
            .Select(o => new ConvertibleGroup(
                o.Str("ExternalDirectoryObjectId") ?? o.Str("Guid") ?? o.StrOr("PrimarySmtpAddress", string.Empty),
                o.StrOr("DisplayName", string.Empty),
                o.StrOr("PrimarySmtpAddress", string.Empty),
                o.StrOr("RecipientTypeDetails", "Unknown"),
                -1))
            .ToList();
    }

    /// <summary>Group type and identity, used to assess room-list conversion.</summary>
    public async Task<ConvertibleGroup?> GetGroupInfoAsync(string identity, CancellationToken ct)
    {
        try
        {
            var result = await Run(PsCommand.Create("Get-DistributionGroup", ("Identity", identity)), ct);
            if (result.Objects.Count == 0) return null;
            var o = result.Objects[0];
            return new ConvertibleGroup(
                o.Str("ExternalDirectoryObjectId") ?? o.Str("Guid") ?? o.StrOr("PrimarySmtpAddress", identity),
                o.StrOr("DisplayName", identity),
                o.StrOr("PrimarySmtpAddress", identity),
                o.StrOr("RecipientTypeDetails", "Unknown"),
                -1);
        }
        catch (CommandFailedException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            return null;
        }
    }

    public static RoomList MapGroup(JsonObject o)
    {
        var smtp = o.Str("PrimarySmtpAddress") ?? throw new InvalidOperationException("Group without PrimarySmtpAddress.");
        return new RoomList
        {
            Id = o.Str("ExternalDirectoryObjectId") ?? o.Str("Guid") ?? smtp,
            Name = o.Str("Name") ?? smtp,
            DisplayName = o.Str("DisplayName") ?? o.Str("Name") ?? smtp,
            Alias = o.Str("Alias"),
            PrimarySmtpAddress = smtp,
            IsDirSynced = o.Bool("IsDirSynced") ?? false,
        };
    }

    public async Task<IReadOnlyList<RoomListMember>> GetGroupMembersAsync(string identity, CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-DistributionGroupMember", ("Identity", identity), ("ResultSize", "Unlimited")), ct);
        return result.Objects.Select(m => new RoomListMember(
                m.Str("ExternalDirectoryObjectId") ?? m.Str("Guid") ?? m.StrOr("PrimarySmtpAddress", string.Empty),
                m.Str("DisplayName") ?? m.StrOr("Name", string.Empty),
                m.StrOr("PrimarySmtpAddress", string.Empty),
                m.StrOr("RecipientTypeDetails", "Unknown")))
            .ToList();
    }

    /// <summary>
    /// Resolves the calendar folder's real name. The default calendar is localized (e.g. "Agenda", "Kalender"), so the folder path
    /// is read with Get-MailboxFolderStatistics -FolderScope Calendar rather than assuming "Calendar".
    /// </summary>
    public async Task<CalendarFolder> GetCalendarFolderAsync(string mailbox, CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-MailboxFolderStatistics", ("Identity", mailbox), ("FolderScope", "Calendar")), ct);
        var calendar = result.Objects.FirstOrDefault(o => string.Equals(o.Str("FolderType"), "Calendar", StringComparison.OrdinalIgnoreCase))
            ?? (result.Objects.Count > 0 ? result.Objects[0] : null);
        if (calendar is null)
        {
            throw RoomOpsException.NotFound("calendar.folderNotFound", Text.L($"The calendar folder of '{mailbox}' could not be determined.", $"De agendamap van '{mailbox}' kon niet worden bepaald."));
        }
        var path = calendar.Str("FolderPath") ?? $"/{calendar.Str("Name")}";
        var name = path.TrimStart('/').Replace('/', '\\');
        return new CalendarFolder(name, $"{mailbox}:\\{name}");
    }

    public async Task<IReadOnlyList<FolderPermission>> GetFolderPermissionsAsync(string folderIdentity, CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-MailboxFolderPermission", ("Identity", folderIdentity)), ct);
        return result.Objects.Select(o => new FolderPermission(
                o.Str("User") ?? "(unknown)",
                o.Str("UserSmtp") ?? o.Str("PrimarySmtpAddress"),
                o.StrList("AccessRights"),
                o.StrList("SharingPermissionFlags")))
            .ToList();
    }

    public async Task<IReadOnlyList<MailboxAccessEntry>> GetFullAccessAsync(string mailbox, CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-EXOMailboxPermission", ("Identity", mailbox)), ct);
        return result.Objects
            .Where(o => o.StrList("AccessRights").Any(r => r.Contains("FullAccess", StringComparison.OrdinalIgnoreCase)))
            .Where(o => !(o.Str("User") ?? string.Empty).StartsWith("NT AUTHORITY", StringComparison.OrdinalIgnoreCase))
            .Select(o => new MailboxAccessEntry(o.StrOr("User", "(unknown)"), "FullAccess", o.Bool("IsInherited") ?? false))
            .ToList();
    }

    public async Task<IReadOnlyList<MailboxAccessEntry>> GetSendAsAsync(string mailbox, CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-EXORecipientPermission", ("Identity", mailbox)), ct);
        return result.Objects
            .Where(o => o.StrList("AccessRights").Any(r => r.Contains("SendAs", StringComparison.OrdinalIgnoreCase)))
            .Where(o => !(o.Str("Trustee") ?? string.Empty).StartsWith("NT AUTHORITY", StringComparison.OrdinalIgnoreCase))
            .Select(o => new MailboxAccessEntry(o.StrOr("Trustee", "(unknown)"), "SendAs", o.Bool("IsInherited") ?? false))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetSendOnBehalfAsync(string mailbox, CancellationToken ct)
    {
        var result = await Run(PsCommand.Create("Get-EXOMailbox", ("Identity", mailbox), ("Properties", new[] { "GrantSendOnBehalfTo" })), ct);
        return result.Objects.Count == 0 ? [] : result.Objects[0].StrList("GrantSendOnBehalfTo");
    }
}
