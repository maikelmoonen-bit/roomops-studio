using RoomOps.Application.Commands;
using RoomOps.Application.Execution;
using RoomOps.Application.Gateways;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Common;
using RoomOps.Domain.RoomLists;
using RoomOps.Domain.Rooms;

namespace RoomOps.Application.Planning;

public sealed partial class ChangePlanner
{
    private static string MemberText => Text.L("Member", "Lid");
    private static string NotMemberText => Text.L("Not a member", "Geen lid");

    // ------------------------------------------------------------------------------------------------ Calendar folder permissions

    private static readonly string[] BuiltInFolderUsers = ["Default", "Anonymous"];
    private static readonly string[] FolderRoles = ["None", "AvailabilityOnly", "LimitedDetails", "Reviewer", "Author", "Editor"];
    // Default applies to everyone in the organization and Anonymous to everyone outside it, so neither may edit the calendar.
    private static readonly string[] BuiltInFolderRoles = ["None", "AvailabilityOnly", "LimitedDetails", "Reviewer"];

    internal async Task<PlanTarget> PlanFolderPermissionAsync(RoomMailbox room, SetFolderPermissionIntent intent, CancellationToken ct)
    {
        var label = intent.Action == FolderPermissionAction.Grant ? Text.L("Grant calendar folder permission", "Machtiging voor agendamap verlenen") : Text.L("Remove calendar folder permission", "Machtiging voor agendamap verwijderen");
        var folder = await exchange.GetCalendarFolderAsync(room.PrimarySmtpAddress, ct);

        // Default and Anonymous are the folder's built-in entries: they are addressed by name and always exist.
        var builtIn = BuiltInFolderUsers.FirstOrDefault(u => u.Equals(intent.User.Trim(), StringComparison.OrdinalIgnoreCase));
        string userId, userName;
        string? userDisplayName = null;
        if (builtIn is not null)
        {
            userId = userName = builtIn;
        }
        else
        {
            var user = await exchange.ResolveRecipientAsync(intent.User, ct);
            if (user.Resolution != RecipientResolution.Resolved || user.PrimarySmtpAddress is null)
            {
                return Blocked(room, "permissions.folder", label, Text.L($"'{intent.User}' could not be resolved to a single recipient.", $"'{intent.User}' verwijst niet naar één ontvanger."));
            }
            userId = user.PrimarySmtpAddress;
            userDisplayName = user.DisplayName;
            userName = user.DisplayName ?? user.PrimarySmtpAddress;
        }
        var permissions = await exchange.GetFolderPermissionsAsync(folder.Identity, ct);
        bool Matches(FolderPermission p) => builtIn is not null
            ? string.Equals(p.User, builtIn, StringComparison.OrdinalIgnoreCase)
            : (p.UserSmtp is not null && string.Equals(p.UserSmtp, userId, StringComparison.OrdinalIgnoreCase)) ||
              (userDisplayName is not null && string.Equals(p.User, userDisplayName, StringComparison.OrdinalIgnoreCase)) ||
              string.Equals(p.User, userId, StringComparison.OrdinalIgnoreCase);
        var existing = permissions.FirstOrDefault(Matches);
        var allowed = builtIn is not null ? BuiltInFolderRoles : FolderRoles;
        var issues = new List<ValidationIssue>();
        if (intent.Action == FolderPermissionAction.Grant && !allowed.Contains(intent.AccessRights, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new("accessRights", Text.L($"'{intent.AccessRights}' is not offered for {userName}. Choose one of: {string.Join(", ", allowed)}.", $"'{intent.AccessRights}' wordt niet aangeboden voor {userName}. Kies een van: {string.Join(", ", allowed)}."), IssueLevel.Error));
        }
        if (intent.Action == FolderPermissionAction.Remove && builtIn is not null)
        {
            issues.Add(new("user", Text.L($"The {builtIn} entry can't be removed. Set its access rights to None instead.", $"De vermelding {builtIn} kan niet worden verwijderd. Zet de toegangsrechten in plaats daarvan op None."), IssueLevel.Error));
        }
        if (intent.DelegateFlag && !intent.AccessRights.Equals("Editor", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("delegateFlag", Text.L("The Delegate sharing flag requires the Editor role.", "De deelmarkering Delegate vereist de rol Editor."), IssueLevel.Error));
        }

        var existingText = existing is null ? Text.L("(no entry)", "(geen vermelding)") : string.Join(", ", existing.AccessRights) + (existing.SharingPermissionFlags.Count > 0 ? $" [{string.Join(", ", existing.SharingPermissionFlags)}]" : string.Empty);
        var operations = new List<PlannedOperation>();
        string afterText;
        if (intent.Action == FolderPermissionAction.Remove)
        {
            afterText = Text.L("(no entry)", "(geen vermelding)");
            if (existing is not null)
            {
                operations.Add(new PlannedOperation
                {
                    Command = PsCommand.Create("Remove-MailboxFolderPermission", ("Identity", folder.Identity), ("User", userId), ("Confirm", new PsSwitch(false))),
                    Description = Text.L($"Remove {userName} from the calendar folder", $"{userName} uit de agendamap verwijderen"),
                });
            }
        }
        else
        {
            afterText = intent.AccessRights + (intent.DelegateFlag ? " [Delegate]" : string.Empty);
            var wasDelegate = existing is not null && existing.SharingPermissionFlags.Any(f => f.Equals("Delegate", StringComparison.OrdinalIgnoreCase));
            var sameRights = existing is not null && existing.AccessRights.Count == 1 && existing.AccessRights[0].Equals(intent.AccessRights, StringComparison.OrdinalIgnoreCase)
                && wasDelegate == intent.DelegateFlag;
            if (!sameRights)
            {
                var toEditor = intent.AccessRights.Equals("Editor", StringComparison.OrdinalIgnoreCase);
                if (wasDelegate && !intent.DelegateFlag && !toEditor)
                {
                    // Set-MailboxFolderPermission keeps the delegate status unless SharingPermissionFlags says otherwise, and the
                    // flags are only accepted together with Editor. Delegate status is therefore removed first, as its own step.
                    operations.Add(new PlannedOperation
                    {
                        Command = PsCommand.Create("Set-MailboxFolderPermission", ("Identity", folder.Identity), ("User", userId), ("AccessRights", "Editor"), ("SharingPermissionFlags", "None")),
                        Description = Text.L($"Remove calendar delegate status from {userName}", $"Agendagemachtigdenstatus van {userName} verwijderen"),
                    });
                }
                var parameters = new List<(string, object?)> { ("Identity", folder.Identity), ("User", userId), ("AccessRights", intent.AccessRights) };
                if (intent.DelegateFlag) parameters.Add(("SharingPermissionFlags", "Delegate"));
                else if (wasDelegate && toEditor) parameters.Add(("SharingPermissionFlags", "None"));
                operations.Add(new PlannedOperation
                {
                    Command = PsCommand.Create(existing is null ? "Add-MailboxFolderPermission" : "Set-MailboxFolderPermission", [.. parameters]),
                    Description = existing is null
                        ? Text.L($"Add calendar folder permission for {userName}", $"Machtiging voor agendamap toevoegen voor {userName}")
                        : Text.L($"Change calendar folder permission for {userName}", $"Machtiging voor agendamap wijzigen voor {userName}"),
                });
            }
        }
        var changes = operations.Count == 0 ? [] : new List<PropertyChange>
        {
            new($"FolderPermission:{userId}", Text.L($"Calendar permission — {userName}", $"Agendamachtiging — {userName}"), SourceSystem.ExchangeOnline, existingText, afterText, null, null),
        };

        return new PlanTarget
        {
            TargetId = room.Id,
            Kind = TargetKind.Room,
            TargetName = room.DisplayName,
            OperationType = "permissions.folder",
            OperationLabel = intent.Action == FolderPermissionAction.Grant && existing is not null ? Text.L("Change calendar folder permission", "Machtiging voor agendamap wijzigen") : label,
            Changes = changes,
            Operations = operations,
            Issues = issues,
            Notes =
            [
                Text.L($"Calendar folder: {folder.Identity} (resolved from the mailbox; localized names are supported).", $"Agendamap: {folder.Identity} (gelezen uit het postvak; gelokaliseerde namen worden ondersteund)."),
                Text.L("Folder permissions are separate from booking delegates, Full Access, Send As and Send on Behalf. No other permission is changed.", "Mapmachtigingen staan los van boekingsgemachtigden, volledige toegang, Verzenden als en Verzenden namens. Er wordt geen andere machtiging gewijzigd."),
            ],
            Fingerprint = Fingerprint.Of(existingText),
            Refingerprint = async c =>
            {
                var now = (await exchange.GetFolderPermissionsAsync(folder.Identity, c)).FirstOrDefault(Matches);
                return Fingerprint.Of(now is null ? Text.L("(no entry)", "(geen vermelding)") : string.Join(", ", now.AccessRights) + (now.SharingPermissionFlags.Count > 0 ? $" [{string.Join(", ", now.SharingPermissionFlags)}]" : string.Empty));
            },
            Verify = async c =>
            {
                var after = (await exchange.GetFolderPermissionsAsync(folder.Identity, c)).FirstOrDefault(Matches);
                var actual = after is null ? Text.L("(no entry)", "(geen vermelding)") : string.Join(", ", after.AccessRights) + (after.SharingPermissionFlags.Count > 0 ? $" [{string.Join(", ", after.SharingPermissionFlags)}]" : string.Empty);
                return VerificationResult.FromFields([new FieldVerification("FolderPermission", Text.L("Calendar permission", "Agendamachtiging"), afterText, actual, string.Equals(actual, afterText, StringComparison.OrdinalIgnoreCase))]);
            },
            RevertIntent = operations.Count == 0 ? null
                : existing is null
                    ? new SetFolderPermissionIntent(room.Id, userId, FolderPermissionAction.Remove, intent.AccessRights, false)
                    : new SetFolderPermissionIntent(room.Id, userId, FolderPermissionAction.Grant, existing.AccessRights.FirstOrDefault() ?? "Reviewer",
                        existing.SharingPermissionFlags.Any(f => f.Equals("Delegate", StringComparison.OrdinalIgnoreCase))),
        };
    }

    // ------------------------------------------------------------------------------------------------ Room lists

    internal async Task<PlanTarget> PlanCreateRoomListAsync(CreateRoomListIntent intent, CancellationToken ct)
    {
        var label = Text.L("Create room list", "Ruimtelijst maken");
        var smtp = intent.PrimarySmtpAddress.Trim();
        var issues = new List<ValidationIssue>();
        issues.AddRange(RecipientNaming.ValidateName(intent.Name));
        issues.AddRange(RecipientNaming.ValidateDisplayName(intent.DisplayName));
        issues.AddRange(RecipientNaming.ValidateAlias(intent.Alias));
        issues.AddRange(RecipientNaming.ValidateSmtpAddress(smtp));
        if (issues.All(i => i.Level != IssueLevel.Error))
        {
            var domains = await exchange.GetAcceptedDomainsAsync(ct);
            var domain = smtp[(smtp.LastIndexOf('@') + 1)..];
            if (!domains.Any(d => d.DomainName.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new("primarySmtpAddress", Text.L($"'{domain}' is not an accepted domain of this tenant.", $"'{domain}' is geen geaccepteerd domein van deze tenant."), IssueLevel.Error));
            }
            if (await exchange.RecipientExistsAsync(smtp, ct))
            {
                issues.Add(new("primarySmtpAddress", Text.L($"A recipient with the address {smtp} already exists.", $"Er bestaat al een ontvanger met het adres {smtp}."), IssueLevel.Error));
            }
            if (await exchange.RecipientExistsAsync(intent.Alias, ct))
            {
                issues.Add(new("alias", Text.L($"A recipient with the alias '{intent.Alias}' already exists.", $"Er bestaat al een ontvanger met de alias '{intent.Alias}'."), IssueLevel.Error));
            }
        }

        var members = new List<RoomMailbox>();
        foreach (var id in intent.MemberRoomIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            members.Add(await inventory.ResolveRoomAsync(id, ct));
        }

        var operations = new List<PlannedOperation>
        {
            new()
            {
                Command = PsCommand.Create("New-DistributionGroup", ("Name", intent.Name.Trim()), ("RoomList", new PsSwitch(true)), ("Alias", intent.Alias.Trim()),
                    ("DisplayName", intent.DisplayName.Trim()), ("PrimarySmtpAddress", smtp)),
                Description = Text.L("Create the room list", "De ruimtelijst maken"),
            },
        };
        operations.AddRange(members.Select(m => new PlannedOperation
        {
            Command = PsCommand.Create("Add-DistributionGroupMember", ("Identity", smtp), ("Member", m.PrimarySmtpAddress), ("BypassSecurityGroupManagerCheck", new PsSwitch(true))),
            Description = Text.L($"Add {m.DisplayName}", $"{m.DisplayName} toevoegen"),
            AwaitsProvisioning = true,
            ConflictMeansDone = true,
        }));

        var changes = new List<PropertyChange>
        {
            new("DisplayName", Text.L("Display name", "Weergavenaam"), SourceSystem.ExchangeOnline, Text.L("(new)", "(nieuw)"), intent.DisplayName.Trim(), null, null),
            new("PrimarySmtpAddress", Text.L("Email address", "E-mailadres"), SourceSystem.ExchangeOnline, Text.L("(new)", "(nieuw)"), smtp, null, null),
            new("Members", Text.L("Members", "Leden"), SourceSystem.ExchangeOnline, Text.L("(none)", "(geen)"), members.Count == 0 ? Text.L("(none)", "(geen)") : string.Join(", ", members.Select(m => m.DisplayName)), null, null),
        };

        return new PlanTarget
        {
            TargetId = smtp,
            Kind = TargetKind.RoomList,
            TargetName = intent.DisplayName.Trim(),
            OperationType = "roomList.create",
            OperationLabel = label,
            Changes = changes,
            Operations = operations,
            Issues = issues,
            Notes = [Text.L("Room lists are used by Outlook Room Finder. They are independent of the Microsoft Places building and floor hierarchy.", "Ruimtelijsten worden gebruikt door Ruimtezoeker in Outlook. Ze staan los van de hiërarchie van gebouwen en verdiepingen in Microsoft Places.")],
            Fingerprint = "new",
            Refingerprint = async c => await exchange.RecipientExistsAsync(smtp, c) ? "exists" : "new",
            Verify = async c =>
            {
                var group = await exchange.GetDistributionGroupAsync(smtp, c);
                if (group is null) return new VerificationResult(VerificationOutcome.NotVerified, Text.L("The room list could not be read back yet.", "De ruimtelijst kon nog niet worden teruggelezen."), []);
                var actual = await exchange.GetGroupMembersAsync(smtp, c);
                var fields = members.Select(m => new FieldVerification($"Member:{m.PrimarySmtpAddress}", Text.L($"Member {m.DisplayName}", $"Lid {m.DisplayName}"), MemberText, actual.Any(a => a.PrimarySmtpAddress.Equals(m.PrimarySmtpAddress, StringComparison.OrdinalIgnoreCase)) ? MemberText : Text.L("Missing", "Ontbreekt"),
                    actual.Any(a => a.PrimarySmtpAddress.Equals(m.PrimarySmtpAddress, StringComparison.OrdinalIgnoreCase)))).ToList();
                fields.Insert(0, new FieldVerification("Exists", Text.L("Room list", "Ruimtelijst"), Text.L("Exists", "Bestaat"), Text.L("Exists", "Bestaat"), true));
                return VerificationResult.FromFields(fields);
            },
            Invalidate = cache => cache.RoomLists = null,
        };
    }

    internal async Task<PlanTarget> PlanUpdateRoomListAsync(UpdateRoomListIntent intent, CancellationToken ct)
    {
        var label = Text.L("Update room list", "Ruimtelijst bijwerken");
        // Room lists are addressed by Entra object id in the UI; resolve to the SMTP address Exchange accepts as -Identity.
        var cachedList = (await inventory.GetRoomListsAsync(false, ct)).FirstOrDefault(l =>
            l.Id.Equals(intent.RoomListId, StringComparison.OrdinalIgnoreCase) || l.PrimarySmtpAddress.Equals(intent.RoomListId, StringComparison.OrdinalIgnoreCase));
        var list = await exchange.GetDistributionGroupAsync(cachedList?.PrimarySmtpAddress ?? intent.RoomListId, ct);
        if (list is null)
        {
            return BlockedTarget(intent.RoomListId, TargetKind.RoomList, intent.RoomListId, "roomList.update", label, Text.L("The room list was not found.", "De ruimtelijst is niet gevonden."));
        }
        var members = await exchange.GetGroupMembersAsync(list.PrimarySmtpAddress, ct);
        var issues = new List<ValidationIssue>();
        var operations = new List<PlannedOperation>();
        var changes = new List<PropertyChange>();
        if (list.IsDirSynced)
        {
            issues.Add(new("identity", Text.L("This room list is synchronized from on-premises Active Directory and must be managed there.", "Deze ruimtelijst wordt gesynchroniseerd vanuit on-premises Active Directory en moet daar worden beheerd."), IssueLevel.Error));
        }

        if (intent.DisplayName is { } newName && !string.Equals(newName.Trim(), list.DisplayName, StringComparison.Ordinal))
        {
            issues.AddRange(RecipientNaming.ValidateDisplayName(newName));
            operations.Add(new PlannedOperation
            {
                Command = PsCommand.Create("Set-DistributionGroup", ("Identity", list.PrimarySmtpAddress), ("DisplayName", newName.Trim()), ("BypassSecurityGroupManagerCheck", new PsSwitch(true))),
                Description = Text.L("Rename the room list", "De ruimtelijst hernoemen"),
            });
            changes.Add(new("DisplayName", Text.L("Display name", "Weergavenaam"), SourceSystem.ExchangeOnline, list.DisplayName, newName.Trim(), null, null));
        }

        var added = new List<RoomMailbox>();
        foreach (var roomId in intent.AddRoomIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var room = await inventory.ResolveRoomAsync(roomId, ct);
            if (members.Any(m => m.PrimarySmtpAddress.Equals(room.PrimarySmtpAddress, StringComparison.OrdinalIgnoreCase))) continue;
            added.Add(room);
            operations.Add(new PlannedOperation
            {
                Command = PsCommand.Create("Add-DistributionGroupMember", ("Identity", list.PrimarySmtpAddress), ("Member", room.PrimarySmtpAddress), ("BypassSecurityGroupManagerCheck", new PsSwitch(true))),
                Description = Text.L($"Add {room.DisplayName}", $"{room.DisplayName} toevoegen"),
                ConflictMeansDone = true,
            });
        }
        var removed = new List<RoomListMember>();
        foreach (var address in intent.RemoveMemberAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var member = members.FirstOrDefault(m => m.PrimarySmtpAddress.Equals(address, StringComparison.OrdinalIgnoreCase) || m.Id.Equals(address, StringComparison.OrdinalIgnoreCase));
            if (member is null) continue;
            removed.Add(member);
            operations.Add(new PlannedOperation
            {
                Command = PsCommand.Create("Remove-DistributionGroupMember", ("Identity", list.PrimarySmtpAddress), ("Member", member.PrimarySmtpAddress), ("BypassSecurityGroupManagerCheck", new PsSwitch(true)), ("Confirm", new PsSwitch(false))),
                Description = Text.L($"Remove {member.DisplayName}", $"{member.DisplayName} verwijderen"),
            });
        }
        if (added.Count > 0 || removed.Count > 0)
        {
            var before = members.Select(m => m.DisplayName).ToList();
            var after = before.Except(removed.Select(r => r.DisplayName)).Concat(added.Select(a => a.DisplayName)).ToList();
            changes.Add(new("Members", Text.L("Members", "Leden"), SourceSystem.ExchangeOnline,
                Text.L($"{before.Count} member(s)", $"{before.Count} lid/leden"), Text.L($"{after.Count} member(s): +{added.Count} / −{removed.Count}", $"{after.Count} lid/leden: +{added.Count} / −{removed.Count}"), null, null));
            changes.AddRange(added.Select(a => new PropertyChange($"Member:{a.PrimarySmtpAddress}", Text.L($"Add {a.DisplayName}", $"{a.DisplayName} toevoegen"), SourceSystem.ExchangeOnline, NotMemberText, MemberText, null, null)));
            changes.AddRange(removed.Select(r => new PropertyChange($"Member:{r.PrimarySmtpAddress}", Text.L($"Remove {r.DisplayName}", $"{r.DisplayName} verwijderen"), SourceSystem.ExchangeOnline, MemberText, NotMemberText, null, null)));
        }

        var memberFingerprint = Fingerprint.Of(new { list.DisplayName, Members = members.Select(m => m.PrimarySmtpAddress.ToLowerInvariant()).Order() });
        return new PlanTarget
        {
            TargetId = list.Id,
            Kind = TargetKind.RoomList,
            TargetName = list.DisplayName,
            OperationType = "roomList.update",
            OperationLabel = label,
            Changes = changes,
            Operations = operations,
            Issues = issues,
            Notes = removed.Count > 0 ? [Text.L("Removing a room from a room list only affects Room Finder browsing. The room mailbox and its Places location are not changed.", "Een ruimte uit een ruimtelijst verwijderen heeft alleen invloed op bladeren in Ruimtezoeker. Het ruimtepostvak en de Places-locatie veranderen niet.")] : [],
            Fingerprint = memberFingerprint,
            Refingerprint = async c =>
            {
                var g = await exchange.GetDistributionGroupAsync(list.PrimarySmtpAddress, c);
                var m = await exchange.GetGroupMembersAsync(list.PrimarySmtpAddress, c);
                return Fingerprint.Of(new { g?.DisplayName, Members = m.Select(x => x.PrimarySmtpAddress.ToLowerInvariant()).Order() });
            },
            Verify = async c =>
            {
                var g = await exchange.GetDistributionGroupAsync(list.PrimarySmtpAddress, c);
                var m = await exchange.GetGroupMembersAsync(list.PrimarySmtpAddress, c);
                bool IsMember(string smtp) => m.Any(x => x.PrimarySmtpAddress.Equals(smtp, StringComparison.OrdinalIgnoreCase));
                var fields = new List<FieldVerification>();
                if (intent.DisplayName is { } n && changes.Any(x => x.Field == "DisplayName"))
                {
                    fields.Add(new("DisplayName", Text.L("Display name", "Weergavenaam"), n.Trim(), g?.DisplayName ?? Text.L("(missing)", "(ontbreekt)"), string.Equals(g?.DisplayName, n.Trim(), StringComparison.Ordinal)));
                }
                fields.AddRange(added.Select(a => new FieldVerification($"Member:{a.PrimarySmtpAddress}", Text.L($"Add {a.DisplayName}", $"{a.DisplayName} toevoegen"), MemberText, IsMember(a.PrimarySmtpAddress) ? MemberText : NotMemberText, IsMember(a.PrimarySmtpAddress))));
                fields.AddRange(removed.Select(r => new FieldVerification($"Member:{r.PrimarySmtpAddress}", Text.L($"Remove {r.DisplayName}", $"{r.DisplayName} verwijderen"), NotMemberText, IsMember(r.PrimarySmtpAddress) ? MemberText : NotMemberText, !IsMember(r.PrimarySmtpAddress))));
                return VerificationResult.FromFields(fields);
            },
            RevertIntent = operations.Count > 0
                ? new UpdateRoomListIntent(list.Id, changes.Any(x => x.Field == "DisplayName") ? list.DisplayName : null, removed.Select(r => r.PrimarySmtpAddress).ToList(), added.Select(a => a.PrimarySmtpAddress).ToList())
                : null,
            Invalidate = cache => cache.RoomLists = null,
        };
    }

    internal async Task<PlanTarget> PlanConvertAsync(ConvertToRoomListIntent intent, CancellationToken ct)
    {
        var label = Text.L("Convert to room list", "Omzetten naar ruimtelijst");
        var info = await exchange.GetGroupInfoAsync(intent.GroupIdentity, ct);
        if (info is null)
        {
            return BlockedTarget(intent.GroupIdentity, TargetKind.RoomList, intent.GroupIdentity, "roomList.convert", label, Text.L("The distribution group was not found.", "De distributiegroep is niet gevonden."));
        }
        var members = await exchange.GetGroupMembersAsync(info.PrimarySmtpAddress, ct);
        var assessment = RoomListRules.AssessConversion(info with { MemberCount = members.Count }, members);
        var issues = assessment.Reasons.Select(r => new ValidationIssue("group", r, IssueLevel.Error)).ToList();
        return new PlanTarget
        {
            TargetId = info.Id,
            Kind = TargetKind.RoomList,
            TargetName = info.DisplayName,
            OperationType = "roomList.convert",
            OperationLabel = label,
            Changes = [new("RecipientTypeDetails", Text.L("Group type", "Groepstype"), SourceSystem.ExchangeOnline, info.RecipientTypeDetails, "RoomList", null, null)],
            Operations = [new PlannedOperation { Command = PsCommand.Create("Set-DistributionGroup", ("Identity", info.PrimarySmtpAddress), ("RoomList", new PsSwitch(true)), ("BypassSecurityGroupManagerCheck", new PsSwitch(true))), Description = "Convert the group to a room list" }],
            Issues = issues,
            Notes =
            [
                Text.L($"All {members.Count} member(s) are room mailboxes.", $"Alle {members.Count} leden zijn ruimtepostvakken."),
                Text.L("Microsoft documents conversion of distribution groups that contain room mailboxes. RoomOps does not offer converting a room list back to a regular group.", "Microsoft documenteert het omzetten van distributiegroepen met ruimtepostvakken. RoomOps biedt geen terugzetten van een ruimtelijst naar een gewone groep."),
            ],
            Fingerprint = Fingerprint.Of(new { info.RecipientTypeDetails, Members = members.Select(m => m.PrimarySmtpAddress.ToLowerInvariant()).Order() }),
            Refingerprint = async c =>
            {
                var now = await exchange.GetGroupInfoAsync(info.PrimarySmtpAddress, c);
                var m = await exchange.GetGroupMembersAsync(info.PrimarySmtpAddress, c);
                return Fingerprint.Of(new { now?.RecipientTypeDetails, Members = m.Select(x => x.PrimarySmtpAddress.ToLowerInvariant()).Order() });
            },
            Verify = async c =>
            {
                var now = await exchange.GetGroupInfoAsync(info.PrimarySmtpAddress, c);
                var actual = now?.RecipientTypeDetails ?? Text.L("(missing)", "(ontbreekt)");
                return VerificationResult.FromFields([new FieldVerification("RecipientTypeDetails", Text.L("Group type", "Groepstype"), "RoomList", actual, actual.Equals("RoomList", StringComparison.OrdinalIgnoreCase))]);
            },
            Invalidate = cache => cache.RoomLists = null,
        };
    }
}
