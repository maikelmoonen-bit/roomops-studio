using System.Text.Json;
using RoomOps.Application.Commands;
using RoomOps.Application.Execution;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Changes;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.Rooms;

namespace RoomOps.Application.Planning;

public enum PlaceValueKind
{
    Text,
    Integer,
    Boolean,
    List,
}

public sealed record PlaceFieldInfo(string Name, string Label, PlaceValueKind Kind, int? MaxLength, Func<Place, object?> Get);

public static class PlaceFields
{
    internal static string PlaceTypeName(PlaceType type) => type switch
    {
        PlaceType.Building => Text.L("building", "gebouw"),
        PlaceType.Floor => Text.L("floor", "verdieping"),
        PlaceType.Section => Text.L("section", "sectie"),
        PlaceType.Room => Text.L("room", "ruimte"),
        PlaceType.Workspace => Text.L("workspace", "werkplek"),
        PlaceType.Desk => Text.L("desk", "bureau"),
        _ => type.ToString().ToLowerInvariant(),
    };

    private static PlaceFieldInfo DisplayName => new("DisplayName", Text.L("Display name", "Weergavenaam"), PlaceValueKind.Text, 200, p => p.DisplayName);
    private static PlaceFieldInfo Label => new("Label", "Label", PlaceValueKind.Text, 200, p => p.Label);
    private static PlaceFieldInfo ParentId => new("ParentId", Text.L("Parent location", "Bovenliggende locatie"), PlaceValueKind.Text, null, p => p.ParentId);

    /// <summary>Set-PlaceV3 parameters documented for each place type (Microsoft Learn syntax blocks).</summary>
    public static IReadOnlyList<PlaceFieldInfo> For(PlaceType type) => type switch
    {
        PlaceType.Room =>
        [
            DisplayName, Label, ParentId,
            new("Capacity", Text.L("Capacity", "Capaciteit"), PlaceValueKind.Integer, null, p => p.Capacity),
            new("IsWheelChairAccessible", Text.L("Wheelchair accessible", "Rolstoeltoegankelijk"), PlaceValueKind.Boolean, null, p => p.IsWheelChairAccessible),
            new("AudioDeviceName", Text.L("Audio device", "Audioapparaat"), PlaceValueKind.Text, null, p => p.AudioDeviceName),
            new("VideoDeviceName", Text.L("Video device", "Videoapparaat"), PlaceValueKind.Text, null, p => p.VideoDeviceName),
            new("DisplayDeviceName", Text.L("Display device", "Beeldscherm"), PlaceValueKind.Text, null, p => p.DisplayDeviceName),
            new("MTREnabled", Text.L("Teams Rooms system (MTR)", "Teams Rooms-systeem (MTR)"), PlaceValueKind.Boolean, null, p => p.MtrEnabled),
            new("Tags", "Tags", PlaceValueKind.List, null, p => p.Tags),
            new("FloorLabel", Text.L("Floor label (Room Finder)", "Verdiepingslabel (Ruimtezoeker)"), PlaceValueKind.Text, null, p => p.FloorLabel),
        ],
        PlaceType.Building =>
        [
            DisplayName, Label,
            new("Street", Text.L("Street", "Straat"), PlaceValueKind.Text, 200, p => p.Street),
            new("City", Text.L("City", "Plaats"), PlaceValueKind.Text, 200, p => p.City),
            new("State", Text.L("State or province", "Staat of provincie"), PlaceValueKind.Text, 200, p => p.State),
            new("PostalCode", Text.L("Postal code", "Postcode"), PlaceValueKind.Text, 200, p => p.PostalCode),
            new("CountryOrRegion", Text.L("Country or region", "Land of regio"), PlaceValueKind.Text, null, p => p.CountryOrRegion),
            new("IsWheelChairAccessible", Text.L("Wheelchair accessible", "Rolstoeltoegankelijk"), PlaceValueKind.Boolean, null, p => p.IsWheelChairAccessible),
        ],
        PlaceType.Floor => [DisplayName, Label, ParentId, new("SortOrder", Text.L("Sort order", "Sorteervolgorde"), PlaceValueKind.Integer, null, p => p.SortOrder)],
        PlaceType.Section => [DisplayName, Label, ParentId],
        _ => [],
    };

    public static object? FromJson(PlaceFieldInfo field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        return field.Kind switch
        {
            PlaceValueKind.Text => value.ValueKind == JsonValueKind.String ? NullIfEmpty(value.GetString()) : throw new FormatException(Text.L($"{field.Label} expects text.", $"{field.Label} verwacht tekst.")),
            PlaceValueKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : throw new FormatException(Text.L($"{field.Label} expects a whole number.", $"{field.Label} verwacht een geheel getal.")),
            PlaceValueKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new FormatException(Text.L($"{field.Label} expects true or false.", $"{field.Label} verwacht true of false.")),
            PlaceValueKind.List => value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Select(e => e.GetString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : throw new FormatException(Text.L($"{field.Label} expects a list.", $"{field.Label} verwacht een lijst.")),
            _ => null,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string Format(object? value) => value switch
    {
        null => Text.L("(not set)", "(niet ingesteld)"),
        bool b => b ? Text.L("Yes", "Ja") : Text.L("No", "Nee"),
        IEnumerable<string> list => list.Any() ? string.Join(", ", list) : Text.L("(none)", "(geen)"),
        _ => value.ToString() ?? string.Empty,
    };

    public static bool AreEqual(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (IEnumerable<string> x, IEnumerable<string> y) => x.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(y.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
        (IEnumerable<string> x, null) => !x.Any(),
        (null, IEnumerable<string> y) => !y.Any(),
        (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
        _ => Equals(a, b),
    };
}

public sealed partial class ChangePlanner
{
    private IReadOnlyList<ValidationIssue> PlacesSessionIssues()
    {
        var conn = sessions.Current.For(ServiceKind.Places);
        if (conn.Status != ConnectionStatus.Connected)
        {
            return [new("places", Text.L("Microsoft Places is not connected. Connect Places on the Connections page to change Places data.", "Microsoft Places is niet verbonden. Verbind Places op de pagina Verbindingen om Places-gegevens te wijzigen."), IssueLevel.Error)];
        }
        if (conn.Verification is TenantVerification.Unverified or TenantVerification.Mismatch)
        {
            return [new("places", Text.L("The Microsoft Places session could not be confirmed to belong to the active tenant. Places writes are blocked until it is verified.", "Niet bevestigd kon worden dat de Microsoft Places-sessie bij de actieve tenant hoort. Schrijven naar Places is geblokkeerd totdat dit is geverifieerd."), IssueLevel.Error)];
        }
        return [];
    }

    private IReadOnlyList<ValidationIssue> ValidateParent(PlaceType child, string? parentId, string? selfId, IReadOnlyDictionary<string, Place> byId)
    {
        var issues = new List<ValidationIssue>();
        if (parentId is null)
        {
            if (child is PlaceType.Floor or PlaceType.Section)
            {
                issues.Add(new("ParentId", PlaceHierarchy.Rule(child), IssueLevel.Error));
            }
            return issues;
        }
        if (!byId.TryGetValue(parentId, out var parent))
        {
            issues.Add(new("ParentId", Text.L($"Parent '{parentId}' was not found in Microsoft Places.", $"Bovenliggende locatie '{parentId}' is niet gevonden in Microsoft Places."), IssueLevel.Error));
            return issues;
        }
        if (!PlaceHierarchy.CanParent(child, parent.Type))
        {
            issues.Add(new("ParentId", Text.L($"{PlaceHierarchy.Rule(child)} '{parent.DisplayName}' is a {parent.Type.ToString().ToLowerInvariant()}.", $"{PlaceHierarchy.Rule(child)} '{parent.DisplayName}' is een {PlaceFields.PlaceTypeName(parent.Type)}."), IssueLevel.Error));
        }
        if (selfId is not null && PlaceHierarchy.WouldCreateCycle(selfId, parentId, byId))
        {
            issues.Add(new("ParentId", Text.L("This move would place the item inside itself.", "Door deze verplaatsing zou het item in zichzelf komen te staan."), IssueLevel.Error));
        }
        return issues;
    }

    private string PlacePath(string? parentId, IReadOnlyDictionary<string, Place> byId)
    {
        if (parentId is null || !byId.TryGetValue(parentId, out var parent)) return parentId ?? Text.L("(none)", "(geen)");
        return string.Join(" / ", PlaceHierarchy.Ancestors(parent, byId).Append(parent).Select(p => p.DisplayName));
    }

    // ------------------------------------------------------------------------------------------------ Places room metadata

    internal async Task<PlanTarget> PlanPlacesRoomAsync(RoomMailbox room, IReadOnlyDictionary<string, JsonElement> requested, CancellationToken ct)
    {
        const string op = "places.room.update";
        var label = Text.L("Update Places room", "Places-ruimte bijwerken");
        var sessionIssues = PlacesSessionIssues();
        if (sessionIssues.Count > 0) return Blocked(room, op, label, sessionIssues[0].Message);

        var smtp = room.PrimarySmtpAddress;
        var current = await places.GetPlaceAsync(smtp, ct);
        if (current is null)
        {
            return Blocked(room, op, label, Text.L("This room is not present in Microsoft Places yet. Places synchronizes room mailboxes asynchronously (up to 24 hours).", "Deze ruimte staat nog niet in Microsoft Places. Places synchroniseert ruimtepostvakken asynchroon (tot 24 uur)."));
        }
        var hierarchy = await inventory.GetPlacesByIdAsync(ct);
        var fields = PlaceFields.For(PlaceType.Room).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var issues = new List<ValidationIssue>();
        var parameters = new List<KeyValuePair<string, object?>> { new("Identity", smtp) };
        var changes = new List<PropertyChange>();
        var verifications = new List<(PlaceFieldInfo Field, object? Desired)>();
        var revert = new Dictionary<string, JsonElement>();

        foreach (var (name, json) in requested)
        {
            if (!fields.TryGetValue(name, out var field))
            {
                issues.Add(new(name, Text.L($"'{name}' is not a documented Set-PlaceV3 parameter for rooms.", $"'{name}' is geen gedocumenteerde parameter van Set-PlaceV3 voor ruimtes."), IssueLevel.Error));
                continue;
            }
            object? desired;
            try { desired = PlaceFields.FromJson(field, json); }
            catch (FormatException ex) { issues.Add(new(name, ex.Message, IssueLevel.Error)); continue; }

            var before = field.Get(current);
            if (PlaceFields.AreEqual(before, desired)) continue;
            if (field.MaxLength is { } max && desired is string s && s.Length > max)
            {
                issues.Add(new(name, Text.L($"{field.Label} can contain at most {max} characters.", $"{field.Label} mag maximaal {max} tekens bevatten."), IssueLevel.Error));
            }
            if (field.Kind == PlaceValueKind.Integer && desired is int n && n < 0)
            {
                issues.Add(new(name, Text.L($"{field.Label} can't be negative.", $"{field.Label} kan niet negatief zijn."), IssueLevel.Error));
            }
            if (name == "ParentId")
            {
                issues.AddRange(ValidateParent(PlaceType.Room, desired as string, current.PlaceId, hierarchy));
            }
            if (room.IsDirSynced && name is "DisplayName" or "Capacity")
            {
                issues.Add(new(name, Text.L("Updating the name or capacity of a synchronized room through Places requires the Places Administrator or Global Administrator role.", "Voor het wijzigen van naam of capaciteit van een gesynchroniseerde ruimte via Places is de rol Places-beheerder of Globale beheerder nodig."), IssueLevel.Warning));
            }
            parameters.Add(new(field.Name, desired));
            verifications.Add((field, desired));
            revert[field.Name] = JsonSerializer.SerializeToElement(before);
            var beforeText = name == "ParentId" ? PlacePath(before as string, hierarchy) : PlaceFields.Format(before);
            var afterText = name == "ParentId" ? PlacePath(desired as string, hierarchy) : PlaceFields.Format(desired);
            changes.Add(new(field.Name, field.Label, SourceSystem.MicrosoftPlaces, beforeText, afterText, ChangeValues.ToJson(before), ChangeValues.ToJson(desired)));
        }

        var notes = new List<string>
        {
            Text.L("Microsoft Places can take up to 24 hours to show room updates in Places Finder and Outlook, even after the directory accepts them.", "Het kan tot 24 uur duren voordat Microsoft Places wijzigingen aan ruimtes toont in Places Finder en Outlook, ook nadat de adreslijst ze heeft geaccepteerd."),
        };
        if (changes.Any(c => c.Field == "ParentId"))
        {
            notes.Add(Text.L("For Outlook Room Finder, Microsoft recommends also updating the Floor label and keeping the room in a room list; Places hierarchy and room lists are separate discovery models.", "Voor Ruimtezoeker in Outlook raadt Microsoft aan ook het verdiepingslabel bij te werken en de ruimte in een ruimtelijst te houden; de Places-hiërarchie en ruimtelijsten zijn aparte vindmodellen."));
        }

        return new PlanTarget
        {
            TargetId = room.Id,
            Kind = TargetKind.Room,
            TargetName = room.DisplayName,
            OperationType = op,
            OperationLabel = label,
            Changes = changes,
            Operations = changes.Count == 0 ? [] : [new PlannedOperation { Command = PsCommand.Create("Set-PlaceV3", parameters), Description = "Update the Places room object" }],
            Issues = issues,
            Notes = notes,
            Fingerprint = PlaceFingerprint(current),
            Refingerprint = async c => PlaceFingerprint(await places.GetPlaceAsync(smtp, c)),
            Verify = c => VerifyPlaceAsync(smtp, verifications, c),
            RevertIntent = changes.Count > 0 ? new UpdatePlacesRoomIntent([room.Id], revert) : null,
            Invalidate = cache => cache.Places = null,
        };
    }

    private static string PlaceFingerprint(Place? place) => Fingerprint.Of(place);

    /// <summary>
    /// Rereads the place. A value that doesn't match yet is reported as pending propagation, never as an immediate failure:
    /// Microsoft documents delays of up to 24 hours for room and workspace updates.
    /// </summary>
    private async Task<VerificationResult> VerifyPlaceAsync(string identity, IReadOnlyList<(PlaceFieldInfo Field, object? Desired)> expected, CancellationToken ct)
    {
        var after = await places.GetPlaceAsync(identity, ct);
        if (after is null)
        {
            return new VerificationResult(VerificationOutcome.PendingPropagation, Text.L("Places accepted the change, but the object could not be read back yet. Re-verify later.", "Places heeft de wijziging geaccepteerd, maar het object kon nog niet worden teruggelezen. Verifieer later opnieuw."), []);
        }
        var fields = expected.Select(e => new FieldVerification(e.Field.Name, e.Field.Label, PlaceFields.Format(e.Desired), PlaceFields.Format(e.Field.Get(after)), PlaceFields.AreEqual(e.Field.Get(after), e.Desired))).ToList();
        var mismatched = fields.Count(f => !f.Matches);
        return mismatched == 0
            ? new VerificationResult(VerificationOutcome.PendingPropagation, Text.L("Configuration accepted and confirmed in the Places directory. Places experiences may take up to 24 hours to reflect it.", "Configuratie geaccepteerd en bevestigd in de Places-adreslijst. Het kan tot 24 uur duren voordat Places dit overal toont."), fields)
            : new VerificationResult(VerificationOutcome.PendingPropagation, Text.L($"Configuration accepted. {mismatched} value(s) don't show the new value yet; Microsoft Places propagation may still be pending. Re-verify later.", $"Configuratie geaccepteerd. {mismatched} waarde(n) tonen de nieuwe waarde nog niet; doorvoering in Microsoft Places loopt mogelijk nog. Verifieer later opnieuw."), fields);
    }

    // ------------------------------------------------------------------------------------------------ Buildings, floors, sections

    internal async Task<PlanTarget> PlanCreatePlaceAsync(CreatePlaceIntent intent, CancellationToken ct)
    {
        var label = intent.Type switch
        {
            PlaceType.Building => Text.L("Create building", "Gebouw maken"),
            PlaceType.Floor => Text.L("Create floor", "Verdieping maken"),
            _ => Text.L("Create section", "Sectie maken"),
        };
        var id = $"new:{intent.Type}:{intent.DisplayName}";
        if (intent.Type is not (PlaceType.Building or PlaceType.Floor or PlaceType.Section))
        {
            return BlockedTarget(id, TargetKind.Place, intent.DisplayName, "places.create", label, Text.L("Only buildings, floors and sections can be created here. Rooms are created with the room wizard.", "Hier kunnen alleen gebouwen, verdiepingen en secties worden gemaakt. Ruimtes maak je met de wizard voor ruimtes."));
        }
        var sessionIssues = PlacesSessionIssues();
        if (sessionIssues.Count > 0) return BlockedTarget(id, TargetKind.Place, intent.DisplayName, "places.create", label, sessionIssues[0].Message);

        var hierarchy = await inventory.GetPlacesByIdAsync(ct);
        var issues = new List<ValidationIssue>();
        var name = intent.DisplayName?.Trim() ?? string.Empty;
        if (name.Length == 0) issues.Add(new("DisplayName", Text.L("A name is required.", "Een naam is verplicht."), IssueLevel.Error));
        if (name.Length > 200) issues.Add(new("DisplayName", Text.L("The name can contain at most 200 characters.", "De naam mag maximaal 200 tekens bevatten."), IssueLevel.Error));
        issues.AddRange(ValidateParent(intent.Type, intent.ParentId, null, hierarchy));
        if (hierarchy.Values.Any(p => p.Type == intent.Type && string.Equals(p.ParentId, intent.ParentId, StringComparison.OrdinalIgnoreCase) && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new("DisplayName", Text.L($"A {intent.Type.ToString().ToLowerInvariant()} named '{name}' already exists at this location. Creating another would produce a duplicate.", $"Op deze locatie bestaat al een {PlaceFields.PlaceTypeName(intent.Type)} met de naam '{name}'. Nog een maken zou een duplicaat opleveren."), IssueLevel.Error));
        }
        if (intent.CountryOrRegion is { Length: > 0 } country && country.Length != 2)
        {
            issues.Add(new("CountryOrRegion", Text.L("Use a two-letter ISO 3166-1 country code (for example NL).", "Gebruik een landcode van twee letters volgens ISO 3166-1 (bijvoorbeeld NL)."), IssueLevel.Warning));
        }

        var parameters = new List<KeyValuePair<string, object?>> { new("DisplayName", name), new("Type", intent.Type.ToString()) };
        if (intent.ParentId is not null) parameters.Add(new("ParentId", intent.ParentId));
        if (!string.IsNullOrWhiteSpace(intent.Label)) parameters.Add(new("Label", intent.Label.Trim()));
        if (intent.Type == PlaceType.Floor && intent.SortOrder is { } sort) parameters.Add(new("SortOrder", sort));
        if (intent.Type == PlaceType.Building)
        {
            void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) parameters.Add(new(key, value.Trim())); }
            Add("Street", intent.Street);
            Add("City", intent.City);
            Add("State", intent.State);
            Add("PostalCode", intent.PostalCode);
            Add("CountryOrRegion", intent.CountryOrRegion?.ToUpperInvariant());
        }
        var changes = parameters.Where(p => p.Key != "Type").Select(p => new PropertyChange(p.Key, p.Key == "ParentId" ? Text.L("Parent location", "Bovenliggende locatie") : p.Key, SourceSystem.MicrosoftPlaces, Text.L("(new)", "(nieuw)"),
            p.Key == "ParentId" ? PlacePath(p.Value as string, hierarchy) : PlaceFields.Format(p.Value), null, ChangeValues.ToJson(p.Value))).ToList();

        var notes = new List<string>();
        if (intent.Type == PlaceType.Building)
        {
            notes.Add(Text.L("Buildings are not visible across all Places experiences until building experiences are enabled for the tenant (Set-PlacesSettings -EnableBuildings). RoomOps does not change tenant-wide settings.", "Gebouwen zijn niet in alle Places-onderdelen zichtbaar totdat gebouwen voor de tenant zijn ingeschakeld (Set-PlacesSettings -EnableBuildings). RoomOps wijzigt geen tenantbrede instellingen."));
        }

        return new PlanTarget
        {
            TargetId = id,
            Kind = TargetKind.Place,
            TargetName = name,
            OperationType = "places.create",
            OperationLabel = label,
            Changes = changes,
            Operations = [new PlannedOperation { Command = PsCommand.Create("New-Place", parameters), Description = label }],
            Issues = issues,
            Notes = notes,
            Fingerprint = "new",
            Refingerprint = async c =>
            {
                var fresh = await places.GetHierarchyAsync(c);
                Cache.Places = new(fresh, Now);
                return fresh.Any(p => p.Type == intent.Type && string.Equals(p.ParentId, intent.ParentId, StringComparison.OrdinalIgnoreCase) && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)) ? "exists" : "new";
            },
            Verify = async c =>
            {
                var fresh = await places.GetHierarchyAsync(c);
                Cache.Places = new(fresh, Now);
                var created = fresh.FirstOrDefault(p => p.Type == intent.Type && string.Equals(p.ParentId, intent.ParentId, StringComparison.OrdinalIgnoreCase) && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));
                return created is null
                    ? new VerificationResult(VerificationOutcome.NotVerified, Text.L("The new place was not returned by Get-PlaceV3 yet.", "De nieuwe locatie wordt nog niet door Get-PlaceV3 geretourneerd."), [])
                    : new VerificationResult(VerificationOutcome.Verified, Text.L($"Created and read back (PlaceId {created.PlaceId}).", $"Aangemaakt en teruggelezen (PlaceId {created.PlaceId})."), [new FieldVerification("PlaceId", Text.L("Place id", "Locatie-id"), Text.L("(assigned by Places)", "(toegewezen door Places)"), created.PlaceId, true)]);
            },
            Invalidate = cache => cache.Places = null,
        };
    }

    internal async Task<PlanTarget> PlanUpdatePlaceAsync(UpdatePlaceIntent intent, CancellationToken ct)
    {
        var hierarchy = await inventory.GetPlacesByIdAsync(ct);
        if (!hierarchy.TryGetValue(intent.PlaceId, out var cachedPlace))
        {
            return BlockedTarget(intent.PlaceId, TargetKind.Place, intent.PlaceId, "places.update", Text.L("Update place", "Locatie bijwerken"), Text.L("The place was not found in Microsoft Places.", "De locatie is niet gevonden in Microsoft Places."));
        }
        if (cachedPlace.Type == PlaceType.Room && cachedPlace.EmailAddress is { } roomSmtp)
        {
            var room = await inventory.ResolveRoomAsync(roomSmtp, ct);
            return await PlanPlacesRoomAsync(room, intent.Fields, ct);
        }
        var label = cachedPlace.Type switch
        {
            PlaceType.Building => Text.L("Update building", "Gebouw bijwerken"),
            PlaceType.Floor => Text.L("Update floor", "Verdieping bijwerken"),
            _ => Text.L("Update section", "Sectie bijwerken"),
        };
        var sessionIssues = PlacesSessionIssues();
        if (sessionIssues.Count > 0) return BlockedTarget(intent.PlaceId, TargetKind.Place, cachedPlace.DisplayName, "places.update", label, sessionIssues[0].Message);

        var current = await places.GetPlaceAsync(intent.PlaceId, ct) ?? cachedPlace;
        var fields = PlaceFields.For(current.Type).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var issues = new List<ValidationIssue>();
        var parameters = new List<KeyValuePair<string, object?>> { new("Identity", current.PlaceId) };
        var changes = new List<PropertyChange>();
        var verifications = new List<(PlaceFieldInfo, object?)>();
        var revert = new Dictionary<string, JsonElement>();
        foreach (var (name, json) in intent.Fields)
        {
            if (!fields.TryGetValue(name, out var field))
            {
                issues.Add(new(name, Text.L($"'{name}' is not a documented Set-PlaceV3 parameter for a {current.Type.ToString().ToLowerInvariant()}.", $"'{name}' is geen gedocumenteerde parameter van Set-PlaceV3 voor een {PlaceFields.PlaceTypeName(current.Type)}."), IssueLevel.Error));
                continue;
            }
            object? desired;
            try { desired = PlaceFields.FromJson(field, json); }
            catch (FormatException ex) { issues.Add(new(name, ex.Message, IssueLevel.Error)); continue; }
            var before = field.Get(current);
            if (PlaceFields.AreEqual(before, desired)) continue;
            if (name == "DisplayName" && desired is null) issues.Add(new(name, Text.L("A name is required.", "Een naam is verplicht."), IssueLevel.Error));
            if (field.MaxLength is { } max && desired is string s && s.Length > max) issues.Add(new(name, Text.L($"{field.Label} can contain at most {max} characters.", $"{field.Label} mag maximaal {max} tekens bevatten."), IssueLevel.Error));
            if (name == "ParentId") issues.AddRange(ValidateParent(current.Type, desired as string, current.PlaceId, hierarchy));
            parameters.Add(new(field.Name, desired));
            verifications.Add((field, desired));
            revert[field.Name] = JsonSerializer.SerializeToElement(before);
            changes.Add(new(field.Name, field.Label, SourceSystem.MicrosoftPlaces,
                name == "ParentId" ? PlacePath(before as string, hierarchy) : PlaceFields.Format(before),
                name == "ParentId" ? PlacePath(desired as string, hierarchy) : PlaceFields.Format(desired),
                ChangeValues.ToJson(before), ChangeValues.ToJson(desired)));
        }
        var notes = new List<string>();
        if (current.Type == PlaceType.Building && changes.Any(c => c.Field is "Street" or "City" or "State" or "PostalCode" or "CountryOrRegion"))
        {
            notes.Add(Text.L("Rooms parented under this building inherit its address. Address values set on individual rooms are ignored by Places.", "Ruimtes onder dit gebouw nemen het adres ervan over. Adreswaarden op afzonderlijke ruimtes worden door Places genegeerd."));
        }

        return new PlanTarget
        {
            TargetId = current.PlaceId,
            Kind = TargetKind.Place,
            TargetName = current.DisplayName,
            OperationType = "places.update",
            OperationLabel = label,
            Changes = changes,
            Operations = changes.Count == 0 ? [] : [new PlannedOperation { Command = PsCommand.Create("Set-PlaceV3", parameters), Description = label }],
            Issues = issues,
            Notes = notes,
            Fingerprint = PlaceFingerprint(current),
            Refingerprint = async c => PlaceFingerprint(await places.GetPlaceAsync(current.PlaceId, c)),
            Verify = async c =>
            {
                var result = await VerifyPlaceAsync(current.PlaceId, verifications, c);
                // Buildings, floors and sections are documented to appear immediately, so a confirmed reread is fully verified.
                return result.Fields.Count > 0 && result.Fields.All(f => f.Matches)
                    ? result with { Outcome = VerificationOutcome.Verified, Message = Text.L($"All {result.Fields.Count} changed value(s) were read back from Places and match.", $"Alle {result.Fields.Count} gewijzigde waarde(n) zijn uit Places teruggelezen en komen overeen.") }
                    : result;
            },
            RevertIntent = changes.Count > 0 ? new UpdatePlaceIntent(current.PlaceId, revert) : null,
            Invalidate = cache => cache.Places = null,
        };
    }
}
