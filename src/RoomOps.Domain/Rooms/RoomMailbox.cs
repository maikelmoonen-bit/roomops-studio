using System.Text.RegularExpressions;
using RoomOps.Domain.Common;

namespace RoomOps.Domain.Rooms;

/// <summary>Exchange Online room mailbox as returned by Get-EXOMailbox. Exchange is authoritative for every property here.</summary>
public sealed record RoomMailbox
{
    /// <summary>Stable identifier: the Entra object id (ExternalDirectoryObjectId), falling back to the Exchange GUID.</summary>
    public required string Id { get; init; }
    public string? ExchangeGuid { get; init; }
    public required string DisplayName { get; init; }
    public required string Alias { get; init; }
    public required string PrimarySmtpAddress { get; init; }
    public string? UserPrincipalName { get; init; }
    public string? Name { get; init; }
    public int? ResourceCapacity { get; init; }
    public string? Office { get; init; }
    public IReadOnlyList<string> ResourceCustom { get; init; } = [];
    public bool IsDirSynced { get; init; }
    public bool? AccountDisabled { get; init; }
    public bool HiddenFromAddressLists { get; init; }
    public string RecipientTypeDetails { get; init; } = "RoomMailbox";
    public DateTimeOffset? WhenCreated { get; init; }
}

/// <summary>Resolution of an identity stored in a recipient-valued Exchange property.</summary>
public enum RecipientResolution
{
    Resolved,
    Unresolved,
    Ambiguous,
    NotChecked,
}

/// <summary>
/// A recipient referenced by a calendar-processing property (ResourceDelegates, BookInPolicy, …).
/// Exchange returns these as identity strings; RoomOps resolves them to SMTP addresses for display and comparison.
/// </summary>
public sealed record RecipientRef
{
    public required string Identity { get; init; }
    public string? DisplayName { get; init; }
    public string? PrimarySmtpAddress { get; init; }
    public string? RecipientTypeDetails { get; init; }
    public RecipientResolution Resolution { get; init; } = RecipientResolution.NotChecked;

    /// <summary>Comparison key. SMTP addresses are unique; raw identities are a fallback for unresolved entries.</summary>
    public string Key => (PrimarySmtpAddress ?? Identity).Trim().ToLowerInvariant();

    public bool IsGroup => RecipientTypeDetails is not null &&
        (RecipientTypeDetails.Contains("Group", StringComparison.OrdinalIgnoreCase) ||
         RecipientTypeDetails.Equals("RoomList", StringComparison.OrdinalIgnoreCase));

    public bool IsDynamicGroup => RecipientTypeDetails?.Equals("DynamicDistributionGroup", StringComparison.OrdinalIgnoreCase) == true;

    public static RecipientRef FromSmtp(string smtp, string? displayName = null, string? type = null) => new()
    {
        Identity = smtp,
        PrimarySmtpAddress = smtp,
        DisplayName = displayName,
        RecipientTypeDetails = type,
        Resolution = RecipientResolution.Resolved,
    };
}

public sealed record Recipient(
    string Id,
    string DisplayName,
    string PrimarySmtpAddress,
    string RecipientTypeDetails,
    string? Alias);

public sealed record AcceptedDomain(string DomainName, bool IsDefault, string DomainType);

/// <summary>Identity rules for new Exchange recipients. Exchange performs final validation; these checks catch errors before preview.</summary>
public static partial class RecipientNaming
{
    public const int MaxNameLength = 64;
    public const int MaxAliasLength = 64;
    public const int MaxDisplayNameLength = 256;

    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+\-/=?^_`{|}~]+(\.[A-Za-z0-9!#$%&'*+\-/=?^_`{|}~]+)*$")]
    private static partial Regex AliasPattern();

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$")]
    private static partial Regex DomainPattern();

    public static IReadOnlyList<ValidationIssue> ValidateName(string? name)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new("name", Text.L("A name is required.", "Een naam is verplicht."), IssueLevel.Error));
        }
        else if (name.Length > MaxNameLength)
        {
            issues.Add(new("name", Text.L($"The name can contain at most {MaxNameLength} characters.", $"De naam mag maximaal {MaxNameLength} tekens bevatten."), IssueLevel.Error));
        }
        return issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateDisplayName(string? displayName)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            issues.Add(new("displayName", Text.L("A display name is required.", "Een weergavenaam is verplicht."), IssueLevel.Error));
        }
        else if (displayName.Length > MaxDisplayNameLength)
        {
            issues.Add(new("displayName", Text.L($"The display name can contain at most {MaxDisplayNameLength} characters.", $"De weergavenaam mag maximaal {MaxDisplayNameLength} tekens bevatten."), IssueLevel.Error));
        }
        return issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateAlias(string? alias)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(alias))
        {
            issues.Add(new("alias", Text.L("A mail alias is required.", "Een e-mailalias is verplicht."), IssueLevel.Error));
            return issues;
        }
        if (alias.Length > MaxAliasLength)
        {
            issues.Add(new("alias", Text.L($"The alias can contain at most {MaxAliasLength} characters.", $"De alias mag maximaal {MaxAliasLength} tekens bevatten."), IssueLevel.Error));
        }
        if (!AliasPattern().IsMatch(alias))
        {
            issues.Add(new("alias", Text.L("The alias contains characters Exchange does not accept, or starts, ends or repeats a period.", "De alias bevat tekens die Exchange niet accepteert, of begint of eindigt met een punt of bevat twee punten achter elkaar."), IssueLevel.Error));
        }
        return issues;
    }

    public static bool IsValidDomain(string? domain) => !string.IsNullOrWhiteSpace(domain) && DomainPattern().IsMatch(domain);

    public static IReadOnlyList<ValidationIssue> ValidateSmtpAddress(string? address, string field = "primarySmtpAddress")
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(address))
        {
            issues.Add(new(field, Text.L("An email address is required.", "Een e-mailadres is verplicht."), IssueLevel.Error));
            return issues;
        }
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            issues.Add(new(field, Text.L("Enter a complete email address (alias@domain).", "Voer een volledig e-mailadres in (alias@domein)."), IssueLevel.Error));
            return issues;
        }
        var local = address[..at];
        var domain = address[(at + 1)..];
        if (local.Length > MaxAliasLength || !AliasPattern().IsMatch(local))
        {
            issues.Add(new(field, Text.L("The local part of the address is not valid.", "Het deel vóór de @ is niet geldig."), IssueLevel.Error));
        }
        if (!DomainPattern().IsMatch(domain))
        {
            issues.Add(new(field, Text.L("The domain part of the address is not valid.", "Het domeindeel van het adres is niet geldig."), IssueLevel.Error));
        }
        return issues;
    }

    /// <summary>Suggests an alias from a display name: lower-case, accents removed, separators collapsed to periods.</summary>
    public static string SuggestAlias(string displayName)
    {
        var normalized = displayName.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        var lastWasDot = true;
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasDot = false;
            }
            else if (!lastWasDot)
            {
                sb.Append('.');
                lastWasDot = true;
            }
        }
        var alias = sb.ToString().Trim('.');
        return alias.Length > MaxAliasLength ? alias[..MaxAliasLength].Trim('.') : alias;
    }
}
