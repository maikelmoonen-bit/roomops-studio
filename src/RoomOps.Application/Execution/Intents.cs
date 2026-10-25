using System.Text.Json;
using System.Text.Json.Serialization;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Places;
using RoomOps.Domain.WorkingHours;

namespace RoomOps.Application.Execution;

/// <summary>
/// What the administrator wants to achieve. Intents carry desired state only; planners load the current state, compute the diff
/// and produce the exact commands. The frontend can never submit commands, only intents.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UpdateBookingPolicyIntent), "updateBookingPolicy")]
[JsonDerivedType(typeof(PatchBookingPolicyIntent), "patchBookingPolicy")]
[JsonDerivedType(typeof(UpdateWorkingHoursIntent), "updateWorkingHours")]
[JsonDerivedType(typeof(UpdateMailboxIntent), "updateMailbox")]
[JsonDerivedType(typeof(UpdatePlacesRoomIntent), "updatePlacesRoom")]
[JsonDerivedType(typeof(SetFolderPermissionIntent), "setFolderPermission")]
[JsonDerivedType(typeof(CreateRoomListIntent), "createRoomList")]
[JsonDerivedType(typeof(UpdateRoomListIntent), "updateRoomList")]
[JsonDerivedType(typeof(ConvertToRoomListIntent), "convertToRoomList")]
[JsonDerivedType(typeof(CreatePlaceIntent), "createPlace")]
[JsonDerivedType(typeof(UpdatePlaceIntent), "updatePlace")]
[JsonDerivedType(typeof(CreateRoomIntent), "createRoom")]
[JsonDerivedType(typeof(ApplyTemplateIntent), "applyTemplate")]
[JsonDerivedType(typeof(BulkImportIntent), "bulkImport")]
[JsonDerivedType(typeof(RemediateIntent), "remediate")]
[JsonDerivedType(typeof(RevertIntent), "revert")]
[JsonDerivedType(typeof(ResumeIntent), "resume")]
[JsonDerivedType(typeof(CompositeIntent), "composite")]
public abstract record ChangeIntent;

/// <summary>Full desired booking policy for one room (policy designer).</summary>
public sealed record UpdateBookingPolicyIntent(string RoomId, BookingPolicy Policy) : ChangeIntent;

/// <summary>Sparse booking settings applied to one or many rooms (bulk, templates). Recipient lists are not allowed in patches.</summary>
public sealed record PatchBookingPolicyIntent(IReadOnlyList<string> RoomIds, IReadOnlyDictionary<string, JsonElement> Fields) : ChangeIntent;

public sealed record UpdateWorkingHoursIntent(IReadOnlyList<string> RoomIds, WorkingHoursConfig WorkingHours) : ChangeIntent;

public sealed record UpdateMailboxIntent(string RoomId, string? DisplayName, int? ResourceCapacity, bool ClearCapacity = false) : ChangeIntent;

/// <summary>Places metadata for room(s). A key with a JSON null clears the value (documented "$null indicates no device").</summary>
public sealed record UpdatePlacesRoomIntent(IReadOnlyList<string> RoomIds, IReadOnlyDictionary<string, JsonElement> Fields) : ChangeIntent;

public enum FolderPermissionAction
{
    Grant,
    Remove,
}

public sealed record SetFolderPermissionIntent(string RoomId, string User, FolderPermissionAction Action, string AccessRights, bool DelegateFlag) : ChangeIntent;

public sealed record CreateRoomListIntent(string Name, string DisplayName, string Alias, string PrimarySmtpAddress, IReadOnlyList<string> MemberRoomIds) : ChangeIntent;

public sealed record UpdateRoomListIntent(string RoomListId, string? DisplayName, IReadOnlyList<string> AddRoomIds, IReadOnlyList<string> RemoveMemberAddresses) : ChangeIntent;

public sealed record ConvertToRoomListIntent(string GroupIdentity) : ChangeIntent;

public sealed record CreatePlaceIntent(
    PlaceType Type,
    string DisplayName,
    string? ParentId,
    string? Label,
    int? SortOrder,
    string? Street,
    string? City,
    string? State,
    string? PostalCode,
    string? CountryOrRegion) : ChangeIntent;

/// <summary>Updates a building, floor or section, including reparenting. Keys follow Set-PlaceV3 parameter names.</summary>
public sealed record UpdatePlaceIntent(string PlaceId, IReadOnlyDictionary<string, JsonElement> Fields) : ChangeIntent;

public enum ProvisioningStrategy
{
    /// <summary>New-Mailbox -Room, then Exchange settings, then Set-PlaceV3 once Places has synchronized the mailbox.</summary>
    ExchangeFirst,
    /// <summary>New-Place -Type Room -Mailbox, which provisions the mailbox; Exchange settings follow once the mailbox exists.</summary>
    PlacesFirst,
}

public sealed record NewRoomListSpec(string Name, string DisplayName, string Alias, string PrimarySmtpAddress);

public sealed record CreateRoomIntent : ChangeIntent
{
    public required ProvisioningStrategy Strategy { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Alias { get; init; }
    public required string PrimarySmtpAddress { get; init; }
    public int? Capacity { get; init; }
    public string? PlacesParentId { get; init; }
    public IReadOnlyDictionary<string, JsonElement> PlacesFields { get; init; } = new Dictionary<string, JsonElement>();
    public string BookingPresetId { get; init; } = "automatic";
    public BookingPolicy? Booking { get; init; }
    public WorkingHoursConfig? WorkingHours { get; init; }
    public string? RoomListId { get; init; }
    public NewRoomListSpec? NewRoomList { get; init; }
}

public sealed record ApplyTemplateIntent(string TemplateId, IReadOnlyList<string> RoomIds, IReadOnlyList<string> ExcludedFields, bool AssignTemplate) : ChangeIntent;

public sealed record BulkRow
{
    public required int RowNumber { get; init; }
    public required string RoomIdentity { get; init; }
    public IReadOnlyDictionary<string, JsonElement> BookingFields { get; init; } = new Dictionary<string, JsonElement>();
    public WorkingHoursConfig? WorkingHours { get; init; }
    public int? Capacity { get; init; }
    public IReadOnlyDictionary<string, JsonElement> PlacesFields { get; init; } = new Dictionary<string, JsonElement>();
    public string? AddToRoomList { get; init; }
}

public sealed record BulkImportIntent(IReadOnlyList<BulkRow> Rows) : ChangeIntent;

public sealed record RemediateIntent(IReadOnlyList<string> FindingIds) : ChangeIntent;

public sealed record RevertIntent(string AuditId) : ChangeIntent;

public sealed record ResumeIntent(string AuditId) : ChangeIntent;

public sealed record CompositeIntent(string Title, IReadOnlyList<ChangeIntent> Intents) : ChangeIntent;
