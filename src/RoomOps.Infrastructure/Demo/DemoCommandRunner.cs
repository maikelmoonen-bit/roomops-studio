using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RoomOps.Application.Commands;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Infrastructure.Demo;

/// <summary>
/// Interprets catalog commands against the in-memory demo tenant. Output objects use the same property names as the real
/// cmdlets, so the gateways, planners and verification logic run unchanged in demo mode.
/// </summary>
internal sealed partial class DemoCommandRunner(ServiceKind service, DemoTenant tenant, TimeProvider time) : ICommandRunner
{
    private static int _writes;

    public ServiceKind Service => service;

    public async Task<CommandResult> RunAsync(PsCommand command, CancellationToken cancellationToken)
    {
        if (command.Service != service) throw new InvalidOperationException($"{command.Name} is not a {service} command.");
        var isWrite = command.Effect != CommandEffect.Read;
        // Small, realistic latency so loading states and progress are visible.
        await Task.Delay(isWrite ? Random.Shared.Next(140, 380) : Random.Shared.Next(25, 110), cancellationToken);

        // Every 15th write is throttled once, exercising the executor's retry path in demonstrations.
        if (isWrite && Interlocked.Increment(ref _writes) % 15 == 0)
        {
            throw Fail(command, "Server busy: request was throttled (micro delay applied). Please retry the operation.");
        }

        lock (tenant.Gate)
        {
            try
            {
                return Execute(command);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException)
            {
                throw Fail(command, $"Cannot process argument transformation on parameter: {ex.Message} (ParameterBindingValidationException)");
            }
        }
    }

    private CommandResult Execute(PsCommand command)
    {
        {
            var objects = command.Name switch
            {
                "Get-ConnectionInformation" => [new JsonObject { ["State"] = "Connected", ["TenantID"] = DemoTenant.TenantId, ["UserPrincipalName"] = DemoTenant.AdminAccount, ["TokenStatus"] = "Active" }],
                "Get-EXOMailbox" => GetMailbox(command),
                "Get-EXORecipient" => GetRecipient(command),
                "Get-EXOMailboxPermission" => MailboxPermission(command),
                "Get-EXORecipientPermission" => [new JsonObject { ["Identity"] = Find(command, Str(command, "Identity")).Name, ["Trustee"] = "NT AUTHORITY\\SELF", ["AccessRights"] = new JsonArray("SendAs"), ["IsInherited"] = false }],
                "Get-CalendarProcessing" => [PolicyJson(Find(command, Str(command, "Identity"), resource: true))],
                "Set-CalendarProcessing" => SetCalendarProcessing(command),
                "Get-MailboxCalendarConfiguration" => [HoursJson(Find(command, Str(command, "Identity"), resource: true))],
                "Set-MailboxCalendarConfiguration" => SetHours(command),
                "Get-MailboxFolderStatistics" => FolderStatistics(command),
                "Get-MailboxFolderPermission" => FolderPermissions(command),
                "Add-MailboxFolderPermission" or "Set-MailboxFolderPermission" or "Remove-MailboxFolderPermission" => ChangeFolderPermission(command),
                "Get-AcceptedDomain" =>
                [
                    new JsonObject { ["DomainName"] = DemoTenant.Domain, ["Default"] = true, ["DomainType"] = "Authoritative" },
                    new JsonObject { ["DomainName"] = "rooms." + DemoTenant.Domain, ["Default"] = false, ["DomainType"] = "Authoritative" },
                ],
                "Get-DistributionGroup" => GetGroups(command),
                "Get-DistributionGroupMember" => GroupMembers(command),
                "New-Mailbox" => NewMailbox(command),
                "Set-Mailbox" => SetMailbox(command),
                "New-DistributionGroup" => NewGroup(command),
                "Set-DistributionGroup" => SetGroup(command),
                "Add-DistributionGroupMember" or "Remove-DistributionGroupMember" => ChangeMembership(command),
                "Get-PlaceV3" => GetPlaces(command),
                "Get-PlacesSettings" =>
                [
                    new JsonObject { ["Name"] = "PlacesFinderEnabled", ["Value"] = "Default:false,OID:5e1d0010-0d3e-4a11-9c0f-000000000010@" + DemoTenant.TenantId + ":true" },
                    new JsonObject { ["Name"] = "EnableBuildings", ["Value"] = "Default:true" },
                ],
                "New-Place" => NewPlace(command),
                "Set-PlaceV3" => SetPlace(command),
                "Get-MgContext" => [new JsonObject { ["TenantId"] = DemoTenant.TenantId, ["Account"] = DemoTenant.AdminAccount, ["Scopes"] = new JsonArray("User.Read.All") }],
                "Invoke-MgGraphRequest" => Graph(command),
                _ => throw Fail(command, $"The term '{command.Name}' is not recognized in the demo tenant."),
            };
            return new CommandResult(objects, [], TimeSpan.FromMilliseconds(1));
        }
    }

    // ------------------------------------------------------------------------------------------------ Helpers

    private DateTimeOffset Now => time.GetUtcNow();

    private static CommandFailedException Fail(PsCommand command, string message) =>
        new(command.Name, ErrorClassifier.Classify(message), message);

    private static string Str(PsCommand c, string name) => c.Get(name) as string ?? throw Fail(c, $"Cannot bind argument to parameter '{name}' because it is null.");

    private static string? OptStr(PsCommand c, string name) => c.Get(name) as string;

    private IEnumerable<DemoRecipient> Visible => tenant.Recipients.Where(r => r.AvailableAt <= Now);

    private DemoRecipient Find(PsCommand c, string identity, bool resource = false)
    {
        var mailbox = identity.Split(":\\", 2)[0];
        var matches = Visible.Where(r =>
            r.Id.Equals(mailbox, StringComparison.OrdinalIgnoreCase) ||
            r.PrimarySmtpAddress.Equals(mailbox, StringComparison.OrdinalIgnoreCase) ||
            r.Alias.Equals(mailbox, StringComparison.OrdinalIgnoreCase) ||
            r.Name.Equals(mailbox, StringComparison.OrdinalIgnoreCase) ||
            r.DisplayName.Equals(mailbox, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw Fail(c, $"The operation couldn't be performed because '{mailbox}' matches multiple entries.");
        if (matches.Count == 0) throw Fail(c, $"The operation couldn't be performed because object '{mailbox}' couldn't be found on '{DemoTenant.Domain}'.");
        if (resource && !matches[0].IsRoom) throw Fail(c, $"'{mailbox}' is not a resource mailbox. This cmdlet is effective only on resource mailboxes.");
        return matches[0];
    }

    private JsonObject MailboxJson(DemoRecipient r, PsCommand c)
    {
        var o = tenant.RecipientJson(r);
        o["ExchangeGuid"] = r.Id;
        o["UserPrincipalName"] = r.PrimarySmtpAddress;
        var props = c.Get("Properties") as string[] ?? [];
        if (props.Contains("ResourceCapacity")) o["ResourceCapacity"] = r.ResourceCapacity;
        if (props.Contains("ResourceCustom")) o["ResourceCustom"] = new JsonArray();
        if (props.Contains("Office")) o["Office"] = null;
        if (props.Contains("IsDirSynced")) o["IsDirSynced"] = r.IsDirSynced;
        if (props.Contains("WhenCreated")) o["WhenCreated"] = r.WhenCreated.ToString("o");
        if (props.Contains("AccountDisabled")) o["AccountDisabled"] = r.AccountDisabled;
        if (props.Contains("HiddenFromAddressListsEnabled")) o["HiddenFromAddressListsEnabled"] = false;
        if (props.Contains("GrantSendOnBehalfTo")) o["GrantSendOnBehalfTo"] = new JsonArray();
        return o;
    }

    private List<JsonObject> GetMailbox(PsCommand c)
    {
        if (OptStr(c, "Identity") is { } identity)
        {
            var r = Find(c, identity);
            if (!r.RecipientTypeDetails.Contains("Mailbox", StringComparison.Ordinal)) throw Fail(c, $"The specified mailbox Identity:\"{identity}\" doesn't exist.");
            return [MailboxJson(r, c)];
        }
        var types = c.Get("RecipientTypeDetails") as string[] ?? [];
        return Visible.Where(r => r.RecipientTypeDetails.Contains("Mailbox", StringComparison.Ordinal) && (types.Length == 0 || types.Contains(r.RecipientTypeDetails)))
            .Select(r => MailboxJson(r, c)).ToList();
    }

    // Like Exchange Online, Get-EXORecipient returns only its Minimum property set unless -Properties asks for more.
    private static readonly string[] RecipientMinimumProperties = ["ExternalDirectoryObjectId", "Name", "RecipientType", "RecipientTypeDetails"];

    private List<JsonObject> GetRecipient(PsCommand c)
    {
        var properties = RecipientMinimumProperties.Concat(c.Get("Properties") as string[] ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        JsonObject Project(DemoRecipient r)
        {
            var full = tenant.RecipientJson(r);
            var o = new JsonObject();
            foreach (var (key, value) in full)
            {
                if (properties.Contains(key)) o[key] = value?.DeepClone();
            }
            return o;
        }
        return GetRecipients(c).Select(Project).ToList();
    }

    private IEnumerable<DemoRecipient> GetRecipients(PsCommand c)
    {
        if (OptStr(c, "Identity") is { } identity) return [Find(c, identity)];
        var anr = OptStr(c, "Anr") ?? string.Empty;
        var types = c.Get("RecipientTypeDetails") as string[] ?? [];
        bool Matches(DemoRecipient r) =>
            r.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(anr, StringComparison.OrdinalIgnoreCase)) ||
            r.DisplayName.StartsWith(anr, StringComparison.OrdinalIgnoreCase) ||
            r.Alias.StartsWith(anr, StringComparison.OrdinalIgnoreCase) ||
            r.PrimarySmtpAddress.StartsWith(anr, StringComparison.OrdinalIgnoreCase);
        return Visible.Where(r => Matches(r) && (types.Length == 0 || types.Contains(r.RecipientTypeDetails))).Take(50);
    }

    private List<JsonObject> MailboxPermission(PsCommand c)
    {
        var r = Find(c, Str(c, "Identity"));
        var list = new List<JsonObject> { new() { ["Identity"] = r.Name, ["User"] = "NT AUTHORITY\\SELF", ["AccessRights"] = new JsonArray("FullAccess", "ReadPermission"), ["IsInherited"] = false } };
        list.AddRange(r.FullAccess.Select(u => new JsonObject { ["Identity"] = r.Name, ["User"] = u, ["AccessRights"] = new JsonArray("FullAccess"), ["IsInherited"] = false }));
        return list;
    }

    private JsonObject PolicyJson(DemoRecipient r)
    {
        var o = new JsonObject { ["Identity"] = r.Name };
        foreach (var field in BookingPolicyFields.All)
        {
            var value = field.Get(r.Policy);
            o[field.Name] = value switch
            {
                null => null,
                bool b => b,
                int i => i,
                Enum e => e.ToString(),
                string s => s,
                IReadOnlyList<RecipientRef> refs => new JsonArray(refs.Select(x => (JsonNode?)JsonValue.Create(x.Identity)).ToArray()),
                _ => value.ToString(),
            };
        }
        return o;
    }

    private List<JsonObject> SetCalendarProcessing(PsCommand c)
    {
        var room = Find(c, Str(c, "Identity"), resource: true);
        var policy = room.Policy;
        foreach (var (key, value) in c.Parameters.Where(p => p.Key != "Identity"))
        {
            var field = BookingPolicyFields.Get(key);
            object? typed = field.Kind switch
            {
                PolicyFieldKind.RecipientList => ((string[]?)value ?? []).Select(id => new RecipientRef { Identity = Find(c, id).Name, Resolution = RecipientResolution.NotChecked }).ToList(),
                PolicyFieldKind.Enum when key == nameof(BookingPolicy.AutomateProcessing) => Enum.Parse<AutomateProcessing>((string)value!, true),
                PolicyFieldKind.Enum => Enum.Parse<ResourceBookingType>((string)value!, true),
                PolicyFieldKind.Integer when value is int n && (field.Min is { } min && n < min || field.Max is { } max && n > max) =>
                    throw Fail(c, $"Cannot validate argument on parameter '{key}'. The {n} argument is greater than the maximum allowed range of {field.Max}."),
                _ => value,
            };
            policy = field.Set(policy, typed);
        }
        room.Policy = policy;
        return [];
    }

    private static JsonObject HoursJson(DemoRecipient r) => new()
    {
        ["Identity"] = r.Name,
        ["WorkDays"] = WorkingHoursConfig.FormatWorkDays(r.Hours.Days).Replace(",", ", ", StringComparison.Ordinal),
        ["WorkingHoursStartTime"] = DemoTenant.F(r.Hours.Start),
        ["WorkingHoursEndTime"] = DemoTenant.F(r.Hours.End),
        ["WorkingHoursTimeZone"] = r.Hours.TimeZoneId,
    };

    private List<JsonObject> SetHours(PsCommand c)
    {
        var room = Find(c, Str(c, "Identity"), resource: true);
        var hours = room.Hours;
        try
        {
            if (OptStr(c, "WorkDays") is { } days) hours = hours with { Days = WorkingHoursConfig.ParseWorkDays(days) };
            if (OptStr(c, "WorkingHoursStartTime") is { } start) hours = hours with { Start = WorkingHoursConfig.ParseTime(start) };
            if (OptStr(c, "WorkingHoursEndTime") is { } end) hours = hours with { End = WorkingHoursConfig.ParseTime(end) };
        }
        catch (FormatException ex)
        {
            throw Fail(c, $"Cannot process argument transformation: {ex.Message} (ParameterBindingValidationException)");
        }
        if (OptStr(c, "WorkingHoursTimeZone") is { } tz)
        {
            if (!TimeZoneCatalog.IsKnown(tz)) throw Fail(c, $"'{tz}' is not a valid time zone value. (InvalidArgument)");
            hours = hours with { TimeZoneId = tz };
        }
        room.Hours = hours;
        return [];
    }

    private List<JsonObject> FolderStatistics(PsCommand c)
    {
        var r = Find(c, Str(c, "Identity"));
        var birthday = r.CalendarFolder == "Agenda" ? "Verjaardagen" : "Birthdays";
        return
        [
            new JsonObject { ["Name"] = r.CalendarFolder, ["FolderPath"] = "/" + r.CalendarFolder, ["FolderType"] = "Calendar" },
            new JsonObject { ["Name"] = birthday, ["FolderPath"] = $"/{r.CalendarFolder}/{birthday}", ["FolderType"] = "Birthdays" },
        ];
    }

    private DemoRecipient FindFolderOwner(PsCommand c)
    {
        var identity = Str(c, "Identity");
        var parts = identity.Split(":\\", 2);
        var r = Find(c, parts[0]);
        if (parts.Length < 2 || !parts[1].Equals(r.CalendarFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(c, $"The operation couldn't be performed because '{identity}' couldn't be found.");
        }
        return r;
    }

    private List<JsonObject> FolderPermissions(PsCommand c)
    {
        var r = FindFolderOwner(c);
        return r.FolderPermissions.Select(p => new JsonObject
        {
            ["FolderName"] = r.CalendarFolder,
            ["User"] = p.User,
            ["UserSmtp"] = p.Smtp.Length > 0 ? p.Smtp : null,
            ["AccessRights"] = new JsonArray(p.Rights.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["SharingPermissionFlags"] = new JsonArray(p.Flags.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        }).ToList();
    }

    private List<JsonObject> ChangeFolderPermission(PsCommand c)
    {
        var r = FindFolderOwner(c);
        var userId = Str(c, "User");
        var builtIn = userId is "Default" or "Anonymous";
        string name = userId, smtp = string.Empty;
        if (!builtIn)
        {
            var user = Find(c, userId);
            (name, smtp) = (user.DisplayName, user.PrimarySmtpAddress);
        }
        var index = r.FolderPermissions.FindIndex(p => builtIn ? p.User == name : p.Smtp.Equals(smtp, StringComparison.OrdinalIgnoreCase) || p.User == name);
        var rights = c.Name == "Remove-MailboxFolderPermission" ? [] : new[] { Str(c, "AccessRights") };
        var flags = OptStr(c, "SharingPermissionFlags");
        if (flags is not null && !rights[0].Equals("Editor", StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(c, "SharingPermissionFlags can only be used when AccessRights is Editor.");
        }
        string[] FlagsFrom(string[] current) => flags switch
        {
            null => rights[0].Equals("Editor", StringComparison.OrdinalIgnoreCase) ? current : [], // an omitted value keeps the delegate status
            "None" => [],
            _ => [flags],
        };
        switch (c.Name)
        {
            case "Add-MailboxFolderPermission":
                if (index >= 0) throw Fail(c, $"An existing permission entry was found for user: {name}.");
                r.FolderPermissions.Add((name, smtp, rights, FlagsFrom([])));
                break;
            case "Set-MailboxFolderPermission":
                if (index < 0) throw Fail(c, $"There is no existing permission entry found for user: {name}.");
                r.FolderPermissions[index] = (name, smtp, rights, FlagsFrom(r.FolderPermissions[index].Flags));
                break;
            default:
                if (index < 0) throw Fail(c, $"There is no existing permission entry found for user: {name}.");
                r.FolderPermissions.RemoveAt(index);
                break;
        }
        return [];
    }

    private List<JsonObject> GetGroups(PsCommand c)
    {
        if (OptStr(c, "Identity") is { } identity)
        {
            var g = Find(c, identity);
            if (!g.IsGroup) throw Fail(c, $"The operation couldn't be performed because object '{identity}' couldn't be found on '{DemoTenant.Domain}'.");
            return [tenant.RecipientJson(g)];
        }
        var types = c.Get("RecipientTypeDetails") as string[] ?? [];
        var anr = OptStr(c, "Anr");
        return Visible.Where(r => r.IsGroup && r.RecipientTypeDetails != "DynamicDistributionGroup")
            .Where(r => types.Length == 0 || types.Contains(r.RecipientTypeDetails))
            .Where(r => anr is null || r.DisplayName.Contains(anr, StringComparison.OrdinalIgnoreCase) || r.Alias.StartsWith(anr, StringComparison.OrdinalIgnoreCase))
            .Select(tenant.RecipientJson).ToList();
    }

    private List<JsonObject> GroupMembers(PsCommand c)
    {
        var g = Find(c, Str(c, "Identity"));
        return g.Members.Select(id => tenant.Recipients.First(r => r.Id == id)).Where(r => r.AvailableAt <= Now).Select(tenant.RecipientJson).ToList();
    }

    private List<JsonObject> NewMailbox(PsCommand c)
    {
        var smtp = Str(c, "PrimarySmtpAddress");
        var alias = Str(c, "Alias");
        if (tenant.Recipients.Any(r => r.PrimarySmtpAddress.Equals(smtp, StringComparison.OrdinalIgnoreCase)))
        {
            throw Fail(c, $"The proxy address \"SMTP:{smtp}\" is already being used by the proxy addresses or LegacyExchangeDN. Please choose another proxy address.");
        }
        if (tenant.Recipients.Any(r => r.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
        {
            throw Fail(c, $"The alias \"{alias}\" is already being used. Please choose another alias.");
        }
        var room = CreateRoom(Str(c, "DisplayName"), alias, smtp, Str(c, "Name"), mailboxDelay: TimeSpan.FromSeconds(3));
        // Places synchronizes new room mailboxes asynchronously; the demo uses a short delay to show the pending state.
        tenant.Places.Add(new DemoPlace { PlaceId = tenant.NewId(), Type = PlaceType.Room, DisplayName = room.DisplayName, EmailAddress = smtp, AvailableAt = Now.AddSeconds(15) });
        return [tenant.RecipientJson(room)];
    }

    private DemoRecipient CreateRoom(string displayName, string alias, string smtp, string name, TimeSpan mailboxDelay)
    {
        var room = new DemoRecipient
        {
            Id = tenant.NewId(),
            Name = name,
            DisplayName = displayName,
            Alias = alias,
            PrimarySmtpAddress = smtp,
            RecipientTypeDetails = "RoomMailbox",
            WhenCreated = Now,
            AvailableAt = Now + mailboxDelay,
            Hours = WorkingHoursConfig.ExchangeDefault,
        };
        room.FolderPermissions.Add(("Default", string.Empty, ["AvailabilityOnly"], []));
        room.FolderPermissions.Add(("Anonymous", string.Empty, ["None"], []));
        tenant.Recipients.Add(room);
        return room;
    }

    private List<JsonObject> SetMailbox(PsCommand c)
    {
        var r = Find(c, Str(c, "Identity"));
        if (c.Has("DisplayName")) r.DisplayName = Str(c, "DisplayName");
        if (c.Has("ResourceCapacity")) r.ResourceCapacity = c.Get("ResourceCapacity") as int?;
        return [];
    }

    private List<JsonObject> NewGroup(PsCommand c)
    {
        var smtp = Str(c, "PrimarySmtpAddress");
        if (tenant.Recipients.Any(r => r.PrimarySmtpAddress.Equals(smtp, StringComparison.OrdinalIgnoreCase)))
        {
            throw Fail(c, $"The proxy address \"SMTP:{smtp}\" is already being used by the proxy addresses or LegacyExchangeDN.");
        }
        var group = new DemoRecipient
        {
            Id = tenant.NewId(),
            Name = Str(c, "Name"),
            DisplayName = OptStr(c, "DisplayName") ?? Str(c, "Name"),
            Alias = OptStr(c, "Alias") ?? smtp.Split('@')[0],
            PrimarySmtpAddress = smtp,
            RecipientTypeDetails = c.Get("RoomList") is PsSwitch { IsPresent: true } ? "RoomList" : "MailUniversalDistributionGroup",
            WhenCreated = Now,
            AvailableAt = Now,
        };
        tenant.Recipients.Add(group);
        return [tenant.RecipientJson(group)];
    }

    private List<JsonObject> SetGroup(PsCommand c)
    {
        var g = Find(c, Str(c, "Identity"));
        if (!g.IsGroup) throw Fail(c, "The object isn't a distribution group.");
        if (c.Has("DisplayName")) g.DisplayName = Str(c, "DisplayName");
        if (c.Get("RoomList") is PsSwitch { IsPresent: true })
        {
            if (g.Members.Select(id => tenant.Recipients.First(r => r.Id == id)).Any(m => !m.IsRoom))
            {
                throw Fail(c, "The distribution group can't be converted to a room list because it contains members that aren't room mailboxes. (InvalidArgument)");
            }
            g.RecipientTypeDetails = "RoomList";
        }
        return [];
    }

    private List<JsonObject> ChangeMembership(PsCommand c)
    {
        var g = Find(c, Str(c, "Identity"));
        var member = Find(c, Str(c, "Member"));
        var isMember = g.Members.Contains(member.Id);
        if (c.Name == "Add-DistributionGroupMember")
        {
            if (isMember) throw Fail(c, $"The recipient \"{member.PrimarySmtpAddress}\" is already a member of the group \"{g.DisplayName}\".");
            g.Members.Add(member.Id);
        }
        else
        {
            if (!isMember) throw Fail(c, $"The recipient \"{member.PrimarySmtpAddress}\" isn't a member of the group \"{g.DisplayName}\".");
            g.Members.Remove(member.Id);
        }
        return [];
    }

    // ------------------------------------------------------------------------------------------------ Places

    private IEnumerable<DemoPlace> VisiblePlaces => tenant.Places.Where(p => p.AvailableAt <= Now);

    private JsonObject PlaceJson(DemoPlace p) => new()
    {
        ["PlaceId"] = p.PlaceId,
        ["DisplayName"] = p.DisplayName,
        ["Type"] = p.Type.ToString(),
        ["Identity"] = p.EmailAddress ?? $"{p.DisplayName}_{p.PlaceId}",
        ["ParentId"] = p.ParentId,
        ["Label"] = p.Label,
        ["Capacity"] = p.Capacity,
        ["Tags"] = new JsonArray(p.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
        ["IsWheelChairAccessible"] = p.IsWheelChairAccessible,
        ["AudioDeviceName"] = p.AudioDeviceName,
        ["VideoDeviceName"] = p.VideoDeviceName,
        ["DisplayDeviceName"] = p.DisplayDeviceName,
        ["MTREnabled"] = p.MtrEnabled,
        ["Street"] = p.Street,
        ["City"] = p.City,
        ["State"] = p.State,
        ["PostalCode"] = p.PostalCode,
        ["CountryOrRegion"] = p.CountryOrRegion,
        ["SortOrder"] = p.SortOrder,
        ["FloorLabel"] = p.FloorLabel,
        ["IsManaged"] = true,
        ["IsAutoGenerated"] = false,
    };

    private DemoPlace FindPlace(PsCommand c, string identity)
    {
        var id = identity.Contains('_', StringComparison.Ordinal) && !identity.Contains('@', StringComparison.Ordinal) ? identity[(identity.LastIndexOf('_') + 1)..] : identity;
        return VisiblePlaces.FirstOrDefault(p => p.PlaceId.Equals(id, StringComparison.OrdinalIgnoreCase) || (p.EmailAddress?.Equals(id, StringComparison.OrdinalIgnoreCase) ?? false))
            ?? throw Fail(c, $"Place '{identity}' couldn't be found.");
    }

    private List<JsonObject> GetPlaces(PsCommand c)
    {
        if (OptStr(c, "Identity") is { } identity) return [PlaceJson(FindPlace(c, identity))];
        if (OptStr(c, "AncestorId") is { } ancestor)
        {
            var result = new List<DemoPlace>();
            var frontier = new Queue<string>([ancestor]);
            while (frontier.Count > 0)
            {
                var parent = frontier.Dequeue();
                foreach (var child in VisiblePlaces.Where(p => p.ParentId == parent)) { result.Add(child); frontier.Enqueue(child.PlaceId); }
            }
            return result.Select(PlaceJson).ToList();
        }
        var type = OptStr(c, "Type");
        return VisiblePlaces.Where(p => type is null || p.Type.ToString().Equals(type, StringComparison.OrdinalIgnoreCase)).Select(PlaceJson).ToList();
    }

    private void ValidateParent(PsCommand c, PlaceType type, string? parentId)
    {
        if (parentId is null) return;
        var parent = VisiblePlaces.FirstOrDefault(p => p.PlaceId == parentId) ?? throw Fail(c, $"Parent place '{parentId}' couldn't be found.");
        if (!PlaceHierarchy.CanParent(type, parent.Type)) throw Fail(c, $"A {type} can't be placed under a {parent.Type}. (InvalidArgument)");
    }

    private List<JsonObject> NewPlace(PsCommand c)
    {
        var type = Enum.Parse<PlaceType>(Str(c, "Type"), true);
        var parent = OptStr(c, "ParentId");
        ValidateParent(c, type, parent);
        var place = new DemoPlace
        {
            PlaceId = tenant.NewId(),
            Type = type,
            DisplayName = Str(c, "DisplayName"),
            ParentId = parent,
            Label = OptStr(c, "Label"),
            SortOrder = c.Get("SortOrder") as int?,
            Street = OptStr(c, "Street"),
            City = OptStr(c, "City"),
            State = OptStr(c, "State"),
            PostalCode = OptStr(c, "PostalCode"),
            CountryOrRegion = OptStr(c, "CountryOrRegion"),
            Capacity = c.Get("Capacity") as int?,
            AvailableAt = Now,
        };
        if (type == PlaceType.Room)
        {
            var smtp = OptStr(c, "Mailbox") ?? $"room-{place.PlaceId[..8]}@{DemoTenant.Domain}";
            if (tenant.Recipients.Any(r => r.PrimarySmtpAddress.Equals(smtp, StringComparison.OrdinalIgnoreCase)))
            {
                throw Fail(c, $"A mailbox with the address {smtp} already exists. (Conflict: already exists)");
            }
            place.EmailAddress = smtp;
            // New-Place provisions the linked room mailbox asynchronously.
            var room = CreateRoom(place.DisplayName, smtp.Split('@')[0], smtp, smtp.Split('@')[0], mailboxDelay: TimeSpan.FromSeconds(12));
            room.ResourceCapacity = place.Capacity;
        }
        tenant.Places.Add(place);
        return [PlaceJson(place)];
    }

    private List<JsonObject> SetPlace(PsCommand c)
    {
        var place = FindPlace(c, Str(c, "Identity"));
        foreach (var (key, value) in c.Parameters.Where(p => p.Key != "Identity"))
        {
            switch (key)
            {
                case "DisplayName": place.DisplayName = (string?)value ?? throw Fail(c, "DisplayName can't be empty."); break;
                case "ParentId":
                    ValidateParent(c, place.Type, (string?)value);
                    place.ParentId = (string?)value;
                    break;
                case "Label": place.Label = (string?)value; break;
                case "Capacity": place.Capacity = value as int?; break;
                case "IsWheelChairAccessible": place.IsWheelChairAccessible = value as bool?; break;
                case "AudioDeviceName": place.AudioDeviceName = (string?)value; break;
                case "VideoDeviceName": place.VideoDeviceName = (string?)value; break;
                case "DisplayDeviceName": place.DisplayDeviceName = (string?)value; break;
                case "MTREnabled": place.MtrEnabled = value as bool?; break;
                case "Tags": place.Tags = [.. (string[]?)value ?? []]; break;
                case "Street": place.Street = (string?)value; break;
                case "City": place.City = (string?)value; break;
                case "State": place.State = (string?)value; break;
                case "PostalCode": place.PostalCode = (string?)value; break;
                case "CountryOrRegion": place.CountryOrRegion = (string?)value; break;
                case "SortOrder": place.SortOrder = value as int?; break;
                case "FloorLabel": place.FloorLabel = (string?)value; break;
                default: throw Fail(c, $"A parameter cannot be found that matches parameter name '{key}'.");
            }
        }
        return [];
    }

    // ------------------------------------------------------------------------------------------------ Graph

    [GeneratedRegex(@"^https://graph\.microsoft\.com/v1\.0/users/(?<id>[^?/]+)(?<rest>.*)$")]
    private static partial Regex GraphUser();

    private List<JsonObject> Graph(PsCommand c)
    {
        var match = GraphUser().Match(Str(c, "Uri"));
        if (!match.Success) throw Fail(c, "Request_BadRequest: unsupported request.");
        var id = Uri.UnescapeDataString(match.Groups["id"].Value);
        var user = Visible.FirstOrDefault(r => r.PrimarySmtpAddress.Equals(id, StringComparison.OrdinalIgnoreCase) || r.Id == id)
            ?? throw Fail(c, "Request_ResourceNotFound: Resource '" + id + "' does not exist or one of its queried reference-property objects are not present. (404)");
        if (match.Groups["rest"].Value == "/licenseDetails")
        {
            return [new JsonObject { ["value"] = new JsonArray(user.Licenses.Select(l => (JsonNode?)new JsonObject { ["skuPartNumber"] = l, ["skuId"] = Guid.NewGuid().ToString() }).ToArray()) }];
        }
        return
        [
            new JsonObject
            {
                ["id"] = user.Id,
                ["userPrincipalName"] = user.PrimarySmtpAddress,
                ["mail"] = user.PrimarySmtpAddress,
                ["accountEnabled"] = user.IsRoom ? user.GraphAccountEnabled : true,
                ["passwordPolicies"] = user.PasswordPolicies,
                ["onPremisesSyncEnabled"] = user.IsDirSynced ? true : null,
            },
        ];
    }
}
