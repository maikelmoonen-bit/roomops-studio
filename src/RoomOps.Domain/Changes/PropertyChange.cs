using System.Text.Json;
using RoomOps.Domain.Common;

namespace RoomOps.Domain.Changes;

/// <summary>One property that differs between the current and desired state of a target.</summary>
public sealed record PropertyChange(
    string Field,
    string Label,
    SourceSystem Source,
    string Before,
    string After,
    JsonElement? BeforeValue,
    JsonElement? AfterValue);

public enum VerificationOutcome
{
    /// <summary>Reread state matches the desired state.</summary>
    Verified,
    /// <summary>The service accepted the change but dependent experiences may update later (documented propagation delays).</summary>
    PendingPropagation,
    /// <summary>Reread state differs from the desired state.</summary>
    Mismatch,
    /// <summary>The state could not be reread (e.g. the object is still provisioning).</summary>
    NotVerified,
    NotApplicable,
}

public sealed record FieldVerification(string Field, string Label, string Expected, string Actual, bool Matches);

public sealed record VerificationResult(VerificationOutcome Outcome, string Message, IReadOnlyList<FieldVerification> Fields)
{
    public static VerificationResult NotApplicable(string message) => new(VerificationOutcome.NotApplicable, message, []);

    public static VerificationResult FromFields(IReadOnlyList<FieldVerification> fields, VerificationOutcome successOutcome = VerificationOutcome.Verified, string? successMessage = null)
    {
        var mismatches = fields.Where(f => !f.Matches).ToList();
        if (mismatches.Count == 0)
        {
            return new(successOutcome, successMessage ?? Text.L($"All {fields.Count} changed value(s) were read back and match.", $"Alle {fields.Count} gewijzigde waarde(n) zijn teruggelezen en komen overeen."), fields);
        }
        return new(VerificationOutcome.Mismatch, Text.L($"{mismatches.Count} value(s) differ after reread: {string.Join(", ", mismatches.Select(m => m.Label))}.", $"{mismatches.Count} waarde(n) wijken af na opnieuw lezen: {string.Join(", ", mismatches.Select(m => m.Label))}."), fields);
    }
}

public static class ChangeValues
{
    public static JsonElement? ToJson(object? value) => value is null ? null : JsonSerializer.SerializeToElement(value);
}
