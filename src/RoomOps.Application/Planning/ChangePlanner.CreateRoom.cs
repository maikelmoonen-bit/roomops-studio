using System.Text.Json;
using RoomOps.Application.Commands;
using RoomOps.Application.Execution;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.Rooms;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Application.Planning;

public sealed partial class ChangePlanner
{
    /// <summary>
    /// Plans a new room. Exactly one object-creating command is used per strategy (New-Mailbox -Room or New-Place -Type Room),
    /// so a room is never created twice. Follow-up configuration waits for the provisioned object (eventual consistency).
    /// </summary>
    internal async Task<(string Title, IReadOnlyList<PlanTarget> Targets, IReadOnlyList<string> Notes)> PlanCreateRoomAsync(CreateRoomIntent i, CancellationToken ct)
    {
        var smtp = i.PrimarySmtpAddress.Trim();
        var displayName = i.DisplayName.Trim();
        var issues = new List<ValidationIssue>();
        var notes = new List<string>();

        issues.AddRange(RecipientNaming.ValidateDisplayName(displayName));
        issues.AddRange(RecipientNaming.ValidateSmtpAddress(smtp));
        if (i.Strategy == ProvisioningStrategy.ExchangeFirst)
        {
            issues.AddRange(RecipientNaming.ValidateName(i.Name));
            issues.AddRange(RecipientNaming.ValidateAlias(i.Alias));
        }
        if (i.Capacity is < 0 or > 100000)
        {
            issues.Add(new("capacity", Text.L("Capacity must be between 0 and 100000.", "De capaciteit moet tussen 0 en 100000 liggen."), IssueLevel.Error));
        }

        if (issues.All(x => x.Level != IssueLevel.Error))
        {
            var domains = await exchange.GetAcceptedDomainsAsync(ct);
            var domain = smtp[(smtp.LastIndexOf('@') + 1)..];
            if (!domains.Any(d => d.DomainName.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new("primarySmtpAddress", Text.L($"'{domain}' is not an accepted domain of this tenant.", $"'{domain}' is geen geaccepteerd domein van deze tenant."), IssueLevel.Error));
            }
            if (await exchange.RecipientExistsAsync(smtp, ct))
            {
                issues.Add(new("primarySmtpAddress", Text.L($"A recipient with the address {smtp} already exists. RoomOps will not create a duplicate.", $"Er bestaat al een ontvanger met het adres {smtp}. RoomOps maakt geen duplicaat."), IssueLevel.Error));
            }
            if (i.Strategy == ProvisioningStrategy.ExchangeFirst && await exchange.RecipientExistsAsync(i.Alias.Trim(), ct))
            {
                issues.Add(new("alias", Text.L($"A recipient with the alias '{i.Alias.Trim()}' already exists.", $"Er bestaat al een ontvanger met de alias '{i.Alias.Trim()}'."), IssueLevel.Error));
            }
        }

        // Booking policy: preset → explicit combination, or the full custom policy from the wizard.
        var preset = BookingPresets.Find(i.BookingPresetId) ?? BookingPresets.Custom;
        var desiredBooking = i.Booking ?? BookingPolicyFields.ApplyPatch(BookingPolicy.ExchangeDefaults, preset.Fields);
        desiredBooking = await exchange.ResolveRecipientsAsync(desiredBooking, ct);
        foreach (var required in preset.RequiredInputs)
        {
            if (((IReadOnlyList<RecipientRef>)BookingPolicyFields.Get(required).Get(desiredBooking)!).Count == 0)
            {
                issues.Add(new($"booking.{required}", Text.L($"The '{preset.Name}' model requires {BookingPolicyFields.Get(required).Label.ToString().ToLowerInvariant()}.", $"Het model '{preset.Name}' vereist {BookingPolicyFields.Get(required).Label.ToString().ToLowerInvariant()}."), IssueLevel.Error));
            }
        }
        issues.AddRange(BookingPolicyValidator.Validate(desiredBooking, i.WorkingHours, i.Capacity)
            .Where(x => x.Level != IssueLevel.Info)
            .Select(x => x with { Field = $"booking.{x.Field}", Message = Text.L($"Booking: {x.Message}", $"Boeken: {x.Message}") }));
        var bookingFields = BookingPolicyFields.ChangedFields(BookingPolicy.ExchangeDefaults, desiredBooking);

        if (i.WorkingHours is { } wh)
        {
            issues.AddRange(WorkingHoursValidator.Validate(wh, TimeZoneCatalog.IsKnown).Select(x => x with { Field = $"workingHours.{x.Field}", Message = Text.L($"Working hours: {x.Message}", $"Werktijden: {x.Message}") }));
        }
        else
        {
            notes.Add(Text.L("Working hours are not set; new room mailboxes default to Pacific Standard Time until changed (Microsoft Learn, Teams Rooms resource accounts).", "Er zijn geen werktijden ingesteld; nieuwe ruimtepostvakken gebruiken standaard Pacific Standard Time totdat dit wordt gewijzigd (Microsoft Learn, resourceaccounts voor Teams Rooms)."));
        }

        // Places.
        var placesParameters = new List<KeyValuePair<string, object?>>();
        var wantsPlaces = i.Strategy == ProvisioningStrategy.PlacesFirst || i.PlacesParentId is not null || i.PlacesFields.Count > 0;
        string? parentPath = null;
        if (wantsPlaces)
        {
            issues.AddRange(PlacesSessionIssues());
            if (sessions.Current.IsConnected(ServiceKind.Places))
            {
                var hierarchy = await inventory.GetPlacesByIdAsync(ct);
                if (i.PlacesParentId is not null || i.Strategy == ProvisioningStrategy.PlacesFirst)
                {
                    issues.AddRange(ValidateParent(PlaceType.Room, i.PlacesParentId, null, hierarchy).Select(x => x with { Field = "placesParentId" }));
                    if (i.PlacesParentId is null) issues.Add(new("placesParentId", Text.L("Creating a room through Places requires a floor or section.", "Voor het maken van een ruimte via Places is een verdieping of sectie nodig."), IssueLevel.Error));
                }
                parentPath = i.PlacesParentId is null ? null : PlacePath(i.PlacesParentId, hierarchy);
            }
            var roomFields = PlaceFields.For(PlaceType.Room).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, json) in i.PlacesFields)
            {
                if (name is "DisplayName" or "ParentId" or "Capacity") continue;
                if (!roomFields.TryGetValue(name, out var field))
                {
                    issues.Add(new($"places.{name}", Text.L($"'{name}' is not a documented Set-PlaceV3 room parameter.", $"'{name}' is geen gedocumenteerde parameter van Set-PlaceV3 voor ruimtes."), IssueLevel.Error));
                    continue;
                }
                try
                {
                    var value = PlaceFields.FromJson(field, json);
                    if (value is null || value is string[] { Length: 0 }) continue;
                    placesParameters.Add(new(field.Name, value));
                }
                catch (FormatException ex)
                {
                    issues.Add(new($"places.{name}", ex.Message, IssueLevel.Error));
                }
            }
        }

        // Room list.
        string? listSmtp = null;
        string? listName = null;
        if (i.NewRoomList is { } newList)
        {
            listSmtp = newList.PrimarySmtpAddress.Trim();
            listName = newList.DisplayName.Trim();
            issues.AddRange(RecipientNaming.ValidateName(newList.Name).Select(x => x with { Field = "newRoomList.name" }));
            issues.AddRange(RecipientNaming.ValidateAlias(newList.Alias).Select(x => x with { Field = "newRoomList.alias" }));
            issues.AddRange(RecipientNaming.ValidateSmtpAddress(listSmtp, "newRoomList.primarySmtpAddress"));
            if (await exchange.RecipientExistsAsync(listSmtp, ct))
            {
                issues.Add(new("newRoomList.primarySmtpAddress", Text.L($"A recipient with the address {listSmtp} already exists.", $"Er bestaat al een ontvanger met het adres {listSmtp}."), IssueLevel.Error));
            }
        }
        else if (i.RoomListId is not null)
        {
            var list = (await inventory.GetRoomListsAsync(false, ct)).FirstOrDefault(l => l.Id.Equals(i.RoomListId, StringComparison.OrdinalIgnoreCase) || l.PrimarySmtpAddress.Equals(i.RoomListId, StringComparison.OrdinalIgnoreCase));
            if (list is null) issues.Add(new("roomListId", Text.L("The selected room list no longer exists.", "De geselecteerde ruimtelijst bestaat niet meer."), IssueLevel.Error));
            listSmtp = list?.PrimarySmtpAddress;
            listName = list?.DisplayName;
        }

        // ---------------- Operations
        var operations = new List<PlannedOperation>();
        var placesFirst = i.Strategy == ProvisioningStrategy.PlacesFirst;
        if (placesFirst)
        {
            var newPlace = new List<KeyValuePair<string, object?>> { new("DisplayName", displayName), new("Type", "Room") };
            if (i.PlacesParentId is not null) newPlace.Add(new("ParentId", i.PlacesParentId));
            if (i.Capacity is { } cap) newPlace.Add(new("Capacity", cap));
            newPlace.Add(new("Mailbox", smtp));
            operations.Add(new PlannedOperation { Command = PsCommand.Create("New-Place", newPlace), Description = "Create the Places room; Places provisions and links the room mailbox" });
            if (placesParameters.Count > 0)
            {
                operations.Add(new PlannedOperation
                {
                    Command = PsCommand.Create("Set-PlaceV3", [new("Identity", smtp), .. placesParameters]),
                    Description = Text.L("Set Places room metadata", "Places-gegevens van de ruimte instellen"),
                    AwaitsProvisioning = true,
                    DeferrableUntilProvisioned = true,
                });
            }
            notes.Add(Text.L("Places-first: New-Place provisions the mailbox automatically, which can take up to 24 hours. Exchange settings are applied once the mailbox exists; if it isn't available within the wait period, the remaining steps are saved and can be resumed from Change History.", "Eerst Places: New-Place richt het postvak automatisch in, wat tot 24 uur kan duren. Exchange-instellingen worden toegepast zodra het postvak bestaat; is het niet binnen de wachttijd beschikbaar, dan worden de resterende stappen bewaard en kun je ze hervatten vanuit de wijzigingsgeschiedenis."));
            notes.Add(Text.L("With Places-first provisioning the mailbox alias and name are chosen by the service from the address you provide.", "Bij inrichten via Places kiest de service de alias en naam van het postvak op basis van het opgegeven adres."));
        }
        else
        {
            operations.Add(new PlannedOperation
            {
                Command = PsCommand.Create("New-Mailbox", ("Name", i.Name.Trim()), ("Room", new PsSwitch(true)), ("Alias", i.Alias.Trim()), ("DisplayName", displayName), ("PrimarySmtpAddress", smtp)),
                Description = Text.L("Create the room mailbox", "Het ruimtepostvak maken"),
            });
            if (i.Capacity is { } cap)
            {
                operations.Add(new PlannedOperation
                {
                    Command = PsCommand.Create("Set-Mailbox", ("Identity", smtp), ("ResourceCapacity", cap)),
                    Description = Text.L("Set capacity", "Capaciteit instellen"),
                    AwaitsProvisioning = true,
                });
            }
        }

        if (bookingFields.Count > 0)
        {
            operations.Add(new PlannedOperation
            {
                Command = PsCommand.Create("Set-CalendarProcessing", [new("Identity", smtp), .. bookingFields.Select(f => new KeyValuePair<string, object?>(f.Name, f.ToParameterValue(desiredBooking)))]),
                Description = Text.L($"Apply booking policy ({preset.Name})", $"Boekingsbeleid toepassen ({preset.Name})"),
                AwaitsProvisioning = true,
                DeferrableUntilProvisioned = placesFirst,
            });
        }
        if (i.WorkingHours is { } hours)
        {
            operations.Add(new PlannedOperation
            {
                Command = PsCommand.Create("Set-MailboxCalendarConfiguration", ("Identity", smtp), ("WorkDays", WorkingHoursConfig.FormatWorkDays(hours.Days)),
                    ("WorkingHoursStartTime", WorkingHoursConfig.FormatTime(hours.Start)), ("WorkingHoursEndTime", WorkingHoursConfig.FormatTime(hours.End)), ("WorkingHoursTimeZone", hours.TimeZoneId)),
                Description = Text.L("Set working hours and time zone", "Werktijden en tijdzone instellen"),
                AwaitsProvisioning = true,
                DeferrableUntilProvisioned = placesFirst,
            });
        }
        if (i.NewRoomList is { } nl)
        {
            operations.Add(new PlannedOperation
            {
                Command = PsCommand.Create("New-DistributionGroup", ("Name", nl.Name.Trim()), ("RoomList", new PsSwitch(true)), ("Alias", nl.Alias.Trim()), ("DisplayName", nl.DisplayName.Trim()), ("PrimarySmtpAddress", listSmtp)),
                Description = Text.L("Create the room list", "De ruimtelijst maken"),
            });
        }
        if (listSmtp is not null)
        {
            operations.Add(new PlannedOperation
            {
                Command = PsCommand.Create("Add-DistributionGroupMember", ("Identity", listSmtp), (Text.L("Member", "Lid"), smtp), ("BypassSecurityGroupManagerCheck", new PsSwitch(true))),
                Description = Text.L($"Add to room list {listName}", $"Aan ruimtelijst {listName} toevoegen"),
                AwaitsProvisioning = true,
                DeferrableUntilProvisioned = placesFirst,
                ConflictMeansDone = true,
            });
        }
        if (!placesFirst && (i.PlacesParentId is not null || placesParameters.Count > 0))
        {
            var setPlace = new List<KeyValuePair<string, object?>> { new("Identity", smtp) };
            if (i.PlacesParentId is not null) setPlace.Add(new("ParentId", i.PlacesParentId));
            setPlace.AddRange(placesParameters);
            operations.Add(new PlannedOperation
            {
                Command = PsCommand.Create("Set-PlaceV3", setPlace),
                Description = Text.L("Link the room to its Places location and set metadata (after Places synchronizes the new mailbox)", "De ruimte aan haar Places-locatie koppelen en gegevens instellen (nadat Places het nieuwe postvak heeft gesynchroniseerd)"),
                AwaitsProvisioning = true,
                DeferrableUntilProvisioned = true,
            });
            notes.Add(Text.L("Exchange-first: Places synchronizes new room mailboxes asynchronously (up to 24 hours). If the room isn't in Places within the wait period, the Places step is saved and can be resumed from Change History.", "Eerst Exchange: Places synchroniseert nieuwe ruimtepostvakken asynchroon (tot 24 uur). Staat de ruimte niet binnen de wachttijd in Places, dan wordt de Places-stap bewaard en kun je die hervatten vanuit de wijzigingsgeschiedenis."));
        }

        // ---------------- Change summary
        var changes = new List<PropertyChange>
        {
            new("DisplayName", Text.L("Display name", "Weergavenaam"), SourceSystem.ExchangeOnline, Text.L("(new)", "(nieuw)"), displayName, null, null),
            new("PrimarySmtpAddress", Text.L("Email address", "E-mailadres"), SourceSystem.ExchangeOnline, Text.L("(new)", "(nieuw)"), smtp, null, null),
        };
        if (!placesFirst) changes.Add(new("Alias", "Alias", SourceSystem.ExchangeOnline, Text.L("(new)", "(nieuw)"), i.Alias.Trim(), null, null));
        if (i.Capacity is { } c2) changes.Add(new("Capacity", Text.L("Capacity", "Capaciteit"), placesFirst ? SourceSystem.MicrosoftPlaces : SourceSystem.ExchangeOnline, Text.L("(new)", "(nieuw)"), c2.ToString(), null, null));
        changes.Add(new("BookingPreset", Text.L("Booking policy", "Boekingsbeleid"), SourceSystem.ExchangeOnline, Text.L("Exchange defaults", "Standaardwaarden van Exchange"), Text.L($"{preset.Name} ({bookingFields.Count} setting(s) differ from defaults)", $"{preset.Name} ({bookingFields.Count} instelling(en) wijken af van de standaard)"), null, null));
        changes.AddRange(bookingFields.Select(f => new PropertyChange(f.Name, f.Label, SourceSystem.ExchangeOnline, f.Format(BookingPolicy.ExchangeDefaults), f.Format(desiredBooking), f.ToJson(BookingPolicy.ExchangeDefaults), f.ToJson(desiredBooking))));
        if (i.WorkingHours is { } w2) changes.Add(new("WorkingHours", Text.L("Working hours", "Werktijden"), SourceSystem.ExchangeOnline, Text.L("Exchange default", "Standaard van Exchange"), w2.Describe(), null, null));
        if (parentPath is not null) changes.Add(new("ParentId", Text.L("Places location", "Places-locatie"), SourceSystem.MicrosoftPlaces, Text.L("(none)", "(geen)"), parentPath, null, null));
        changes.AddRange(placesParameters.Select(p => new PropertyChange(p.Key, p.Key, SourceSystem.MicrosoftPlaces, Text.L("(not set)", "(niet ingesteld)"), PlaceFields.Format(p.Value), null, null)));
        if (listName is not null) changes.Add(new("RoomList", Text.L("Room list", "Ruimtelijst"), SourceSystem.ExchangeOnline, Text.L("(none)", "(geen)"), listName + (i.NewRoomList is not null ? Text.L(" (new)", " (nieuw)") : string.Empty), null, null));

        var target = new PlanTarget
        {
            TargetId = smtp,
            Kind = TargetKind.Room,
            TargetName = displayName,
            OperationType = "room.create",
            OperationLabel = Text.L("Create room", "Ruimte maken"),
            Changes = changes,
            Operations = operations,
            Issues = issues,
            Notes = notes,
            Fingerprint = "new",
            Refingerprint = async c => await exchange.RecipientExistsAsync(smtp, c) ? "exists" : "new",
            Verify = c => VerifyCreatedRoomAsync(smtp, displayName, i, desiredBooking, bookingFields, listSmtp, c),
            Invalidate = cache => cache.InvalidateInventory(),
        };
        return (Text.L($"Create room {displayName}", $"Ruimte {displayName} maken"), [target], []);
    }

    private async Task<VerificationResult> VerifyCreatedRoomAsync(string smtp, string displayName, CreateRoomIntent i, BookingPolicy desired, IReadOnlyList<PolicyField> bookingFields, string? listSmtp, CancellationToken ct)
    {
        var mailbox = await exchange.GetMailboxAsync(smtp, ct);
        if (mailbox is null)
        {
            return new VerificationResult(VerificationOutcome.NotVerified, Text.L("The room mailbox is not visible in Exchange yet. Provisioning may still be in progress.", "Het ruimtepostvak is nog niet zichtbaar in Exchange. De inrichting loopt mogelijk nog."), []);
        }
        var fields = new List<FieldVerification>
        {
            new("Exists", Text.L("Room mailbox", "Ruimtepostvak"), Text.L("Exists", "Bestaat"), mailbox.RecipientTypeDetails, mailbox.RecipientTypeDetails.Equals("RoomMailbox", StringComparison.OrdinalIgnoreCase)),
        };
        if (i.Strategy == ProvisioningStrategy.ExchangeFirst)
        {
            fields.Add(new("DisplayName", Text.L("Display name", "Weergavenaam"), displayName, mailbox.DisplayName, mailbox.DisplayName == displayName));
            if (i.Capacity is { } cap) fields.Add(new("ResourceCapacity", Text.L("Capacity", "Capaciteit"), cap.ToString(), mailbox.ResourceCapacity?.ToString() ?? Text.L("(not set)", "(niet ingesteld)"), mailbox.ResourceCapacity == cap));
        }
        if (bookingFields.Count > 0)
        {
            var actual = await exchange.GetBookingPolicyAsync(smtp, ct);
            fields.AddRange(bookingFields.Select(f => new FieldVerification(f.Name, f.Label, f.Format(desired), f.Format(actual), f.AreEqual(desired, actual))));
        }
        if (i.WorkingHours is { } wh)
        {
            var actual = await exchange.GetWorkingHoursAsync(smtp, ct);
            fields.Add(new("WorkingHours", Text.L("Working hours", "Werktijden"), wh.Describe(), actual.Describe(), actual == wh));
        }
        if (listSmtp is not null)
        {
            var members = await exchange.GetGroupMembersAsync(listSmtp, ct);
            var isMember = members.Any(m => m.PrimarySmtpAddress.Equals(smtp, StringComparison.OrdinalIgnoreCase));
            fields.Add(new("RoomList", Text.L("Room list membership", "Lidmaatschap van ruimtelijst"), Text.L("Member", "Lid"), isMember ? Text.L("Member", "Lid") : Text.L("Not a member", "Geen lid"), isMember));
        }

        var exchangeResult = VerificationResult.FromFields(fields);
        if (exchangeResult.Outcome == VerificationOutcome.Mismatch) return exchangeResult;

        if (i.PlacesParentId is not null && sessions.Current.IsConnected(ServiceKind.Places))
        {
            var place = await places.GetPlaceAsync(smtp, ct);
            var parentOk = place is not null && string.Equals(place.ParentId, i.PlacesParentId, StringComparison.OrdinalIgnoreCase);
            fields.Add(new("ParentId", Text.L("Places location", "Places-locatie"), i.PlacesParentId, place?.ParentId ?? Text.L("(not in Places yet)", "(nog niet in Places)"), parentOk));
            if (!parentOk)
            {
                return new VerificationResult(VerificationOutcome.PendingPropagation, Text.L("Exchange configuration verified. The Places location is not visible yet; Microsoft Places synchronization can take up to 24 hours.", "Exchange-configuratie geverifieerd. De Places-locatie is nog niet zichtbaar; synchronisatie met Microsoft Places kan tot 24 uur duren."), fields);
            }
            return new VerificationResult(VerificationOutcome.PendingPropagation, Text.L("Exchange configuration and Places location verified. Places experiences may take up to 24 hours to show the new room.", "Exchange-configuratie en Places-locatie geverifieerd. Het kan tot 24 uur duren voordat Places de nieuwe ruimte toont."), fields);
        }
        return exchangeResult;
    }
}
