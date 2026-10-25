using RoomOps.Domain.Common;
using System.Text.Json;

namespace RoomOps.Domain.Booking;

/// <summary>
/// A booking access model: a named, explicit combination of Set-CalendarProcessing parameters. Presets never rely on a single
/// checkbox: every parameter that participates in the scenario is set explicitly, and recipient lists the model doesn't use are
/// cleared. Recipient lists the model needs (<see cref="RequiredInputs"/>) are kept, so the administrator only adds the people.
/// Booking limits, privacy and every other unrelated setting are left as they are.
/// </summary>
public sealed record BookingPreset(
    string Id,
    BookingMode Mode,
    string Name,
    string Description,
    string Source,
    IReadOnlyDictionary<string, JsonElement> Fields,
    IReadOnlyList<string> RequiredInputs);

public static class BookingPresets
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);

    private static readonly string[] None = [];

    /// <summary>Set by every model: requests are processed automatically, and only in-policy requests are eligible.</summary>
    private static Dictionary<string, JsonElement> Base() => new()
    {
        ["AutomateProcessing"] = J("AutoAccept"),
        ["BookingType"] = J("Standard"),
        ["AllRequestOutOfPolicy"] = J(false),
        ["RequestOutOfPolicy"] = J(None),
        ["RequestInPolicy"] = J(None),
    };

    private static Dictionary<string, JsonElement> With(params (string Name, object Value)[] fields)
    {
        var result = Base();
        foreach (var (name, value) in fields) result[name] = J(value);
        return result;
    }

    /// <summary>
    /// The four access models, from "Manage resource mailboxes in Exchange Online" (Microsoft Learn), appendix "Booking scenarios".
    /// Models without an approval step leave ForwardRequestsToDelegates and TentativePendingApproval alone: with no delegates
    /// and no request policy they have no effect. Built on access so names and descriptions follow the interface language.
    /// </summary>
    public static IReadOnlyList<BookingPreset> All =>
    [
        new(
            "automatic",
            BookingMode.Automatic,
            Text.L("Everyone – Automatic booking", "Iedereen – automatisch boeken"),
            Text.L("Everyone in the organization can book the room automatically, within the booking limits.", "Iedereen in de organisatie kan de ruimte automatisch boeken, binnen de boekingslimieten."),
            "Microsoft Learn — Default booking: AutomateProcessing AutoAccept, AllBookInPolicy True; no request policies and no delegates.",
            With(("AllBookInPolicy", true), ("BookInPolicy", None), ("AllRequestInPolicy", false), ("ResourceDelegates", None)),
            []),
        new(
            "restricted",
            BookingMode.Restricted,
            Text.L("Selected users only – Automatic booking", "Alleen geselecteerde gebruikers – automatisch boeken"),
            Text.L("Only the people or groups you select can book automatically. Requests from everyone else are declined.", "Alleen de personen of groepen die je selecteert, kunnen automatisch boeken. Aanvragen van alle anderen worden geweigerd."),
            "Microsoft Learn — Scoped auto-approval: AutoAccept, AllBookInPolicy False, BookInPolicy set, AllRequestInPolicy False; no delegates.",
            With(("AllBookInPolicy", false), ("AllRequestInPolicy", false), ("ResourceDelegates", None)),
            ["BookInPolicy"]),
        new(
            "delegate-approval",
            BookingMode.DelegateApproval,
            Text.L("Delegate approval required", "Goedkeuring door gemachtigde vereist"),
            Text.L("Everyone can request the room. A booking becomes final only after a delegate approves it.", "Iedereen kan de ruimte aanvragen. Een boeking is pas definitief nadat een gemachtigde die heeft goedgekeurd."),
            "Microsoft Learn — Delegate approval: AutoAccept, AllBookInPolicy False, AllRequestInPolicy True, ResourceDelegates set, ForwardRequestsToDelegates True.",
            With(("AllBookInPolicy", false), ("BookInPolicy", None), ("AllRequestInPolicy", true), ("ForwardRequestsToDelegates", true), ("TentativePendingApproval", true)),
            ["ResourceDelegates"]),
        new(
            "hybrid",
            BookingMode.Hybrid,
            Text.L("Hybrid booking", "Hybride boeken"),
            Text.L("Selected people or groups book automatically. Everyone else can request the room, and a delegate approves those requests.", "Geselecteerde personen of groepen boeken automatisch. Alle anderen kunnen de ruimte aanvragen, en een gemachtigde keurt die aanvragen goed."),
            "Microsoft Learn — Scoped auto-approval combined with delegate approval: AllBookInPolicy False, BookInPolicy set, AllRequestInPolicy True, ResourceDelegates set, ForwardRequestsToDelegates True.",
            With(("AllBookInPolicy", false), ("AllRequestInPolicy", true), ("ForwardRequestsToDelegates", true), ("TentativePendingApproval", true)),
            ["BookInPolicy", "ResourceDelegates"]),
    ];

    /// <summary>Not an access model: keeps every Exchange Online default. Used when a room is created without choosing a model.</summary>
    public static BookingPreset Custom => new(
        "custom",
        BookingMode.Custom,
        Text.L("Custom configuration", "Aangepaste configuratie"),
        Text.L("Every parameter keeps its current value.", "Elke parameter behoudt de huidige waarde."),
        "All Set-CalendarProcessing parameters documented for Exchange Online.",
        new Dictionary<string, JsonElement>(),
        []);

    public static BookingPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id) ?? (id == Custom.Id ? Custom : null);

    /// <summary>
    /// Recognizes the access model a policy implements. Returns null (a custom configuration) when any participating parameter
    /// deviates from the model, so a configuration is never presented as a model it doesn't exactly match.
    /// </summary>
    public static BookingPreset? Detect(BookingPolicy p)
    {
        if (p.AutomateProcessing != AutomateProcessing.AutoAccept || p.BookingType != ResourceBookingType.Standard) return null;
        if (p.AllRequestOutOfPolicy || p.RequestOutOfPolicy.Count > 0 || p.RequestInPolicy.Count > 0) return null;

        var selected = p.BookInPolicy.Count > 0;
        var delegates = p.ResourceDelegates.Count > 0;
        var approvalRouted = delegates && p.ForwardRequestsToDelegates && p.TentativePendingApproval;
        var id = (p.AllBookInPolicy, selected, p.AllRequestInPolicy) switch
        {
            (true, false, false) when !delegates => "automatic",
            (false, true, false) when !delegates => "restricted",
            (false, false, true) when approvalRouted => "delegate-approval",
            (false, true, true) when approvalRouted => "hybrid",
            _ => null,
        };
        return id is null ? null : Find(id);
    }
}
