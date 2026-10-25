namespace RoomOps.Domain.Common;

/// <summary>
/// The interface language of text the backend produces: policy explanations, findings, previews and messages. English is the
/// source language and every user-facing sentence is written as <c>L(english, dutch)</c>, so both stay side by side.
/// Log messages, command scripts and identifiers are not localized.
/// </summary>
public static class Text
{
    private static volatile bool _dutch;

    /// <summary>"en" or "nl"; any other value selects English. Set from the stored appearance setting.</summary>
    public static string Language
    {
        get => _dutch ? "nl" : "en";
        set => _dutch = string.Equals(value, "nl", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Dutch => _dutch;

    public static string L(string en, string nl) => _dutch ? nl : en;
}

/// <summary>A text in both languages for tables built once (field descriptors); it renders in the language current at use.</summary>
public readonly record struct Phrase(string En, string Nl)
{
    public override string ToString() => Text.L(En, Nl);

    public static implicit operator string(Phrase phrase) => phrase.ToString();
}
