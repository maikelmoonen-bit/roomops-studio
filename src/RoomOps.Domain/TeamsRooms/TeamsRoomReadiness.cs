namespace RoomOps.Domain.TeamsRooms;

public enum SignalStatus
{
    Ready,
    Attention,
    NotApplicable,
    Unverifiable,
}

public sealed record ReadinessSignal(string Key, string Label, SignalStatus Status, string Value, string Explanation, string Source);

public sealed record TeamsRoomReadiness(
    string RoomId,
    string PrimarySmtpAddress,
    bool GraphAvailable,
    IReadOnlyList<ReadinessSignal> Signals,
    IReadOnlyList<string> DetectedTeamsRoomsLicenses,
    string Summary);

/// <summary>Graph user data relevant to a Teams Rooms resource account. Licenses come from /users/{id}/licenseDetails.</summary>
public sealed record DirectoryAccount(string Id, string UserPrincipalName, string? Mail, bool? AccountEnabled, IReadOnlyList<string> LicenseSkuPartNumbers, string? PasswordPolicies, bool? OnPremisesSyncEnabled);

public static class TeamsRoomsLicensing
{
    /// <summary>
    /// SKU part numbers associated with Microsoft Teams Rooms licenses. Matching is by part number pattern so that regional or
    /// bundle variants are detected; the UI shows the raw part numbers so administrators can confirm.
    /// </summary>
    public static bool IsTeamsRoomsSku(string skuPartNumber) =>
        skuPartNumber.Contains("Teams_Rooms", StringComparison.OrdinalIgnoreCase) ||
        skuPartNumber.Equals("MEETING_ROOM", StringComparison.OrdinalIgnoreCase) ||
        skuPartNumber.Equals("MTR_PREM", StringComparison.OrdinalIgnoreCase);
}
