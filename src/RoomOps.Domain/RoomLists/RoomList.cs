using RoomOps.Domain.Common;

namespace RoomOps.Domain.RoomLists;

/// <summary>An Exchange room list: a distribution group created or converted with -RoomList. Exchange is authoritative.</summary>
public sealed record RoomList
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Alias { get; init; }
    public required string PrimarySmtpAddress { get; init; }
    public bool IsDirSynced { get; init; }
    public IReadOnlyList<RoomListMember> Members { get; init; } = [];
    public bool MembersLoaded { get; init; }
}

public sealed record RoomListMember(string Id, string DisplayName, string PrimarySmtpAddress, string RecipientTypeDetails)
{
    /// <summary>Microsoft documents room lists as "special distribution groups that contain only room mailboxes".</summary>
    public bool IsRoomMailbox => RecipientTypeDetails.Equals("RoomMailbox", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A distribution group that could be converted to a room list with Set-DistributionGroup -RoomList.</summary>
public sealed record ConvertibleGroup(string Id, string DisplayName, string PrimarySmtpAddress, string RecipientTypeDetails, int MemberCount);

public sealed record ConversionAssessment(
    ConvertibleGroup Group,
    bool CanConvert,
    IReadOnlyList<RoomListMember> Members,
    IReadOnlyList<RoomListMember> NonRoomMembers,
    IReadOnlyList<string> Reasons);

public static class RoomListRules
{
    /// <summary>
    /// Microsoft documents conversion for "regular distribution groups that contain room mailboxes". RoomOps requires every member
    /// to be a room mailbox and the group to be a plain (not mail-enabled security) distribution group before offering conversion.
    /// </summary>
    public static ConversionAssessment AssessConversion(ConvertibleGroup group, IReadOnlyList<RoomListMember> members)
    {
        var reasons = new List<string>();
        var nonRooms = members.Where(m => !m.IsRoomMailbox).ToList();
        if (!group.RecipientTypeDetails.Equals("MailUniversalDistributionGroup", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(Text.L($"The group type is {group.RecipientTypeDetails}. Only regular distribution groups are offered for conversion.", $"Het groepstype is {group.RecipientTypeDetails}. Alleen gewone distributiegroepen komen in aanmerking voor omzetting."));
        }
        if (members.Count == 0)
        {
            reasons.Add(Text.L("The group has no members. Create a new room list instead of converting an empty group.", "De groep heeft geen leden. Maak een nieuwe ruimtelijst in plaats van een lege groep om te zetten."));
        }
        if (nonRooms.Count > 0)
        {
            reasons.Add(Text.L($"{nonRooms.Count} member(s) are not room mailboxes. Room lists contain only room mailboxes; remove those members first.", $"{nonRooms.Count} lid/leden zijn geen ruimtepostvak. Ruimtelijsten bevatten alleen ruimtepostvakken; verwijder die leden eerst."));
        }
        return new(group, reasons.Count == 0, members, nonRooms, reasons);
    }
}
