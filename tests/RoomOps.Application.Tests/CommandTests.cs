using RoomOps.Application.Commands;
using RoomOps.Application.Execution;
using RoomOps.Domain.Booking;
using RoomOps.Domain.Common;

namespace RoomOps.Application.Tests;

public class PsCommandTests
{
    [Fact]
    public void Commands_outside_the_catalog_cannot_be_created()
    {
        Assert.Throws<InvalidOperationException>(() => PsCommand.Create("Remove-Mailbox", ("Identity", "room@x.example")));
        Assert.Throws<InvalidOperationException>(() => PsCommand.Create("Invoke-Expression", ("Command", "whoami")));
    }

    [Fact]
    public void Parameters_outside_the_catalog_cannot_be_used()
    {
        Assert.Throws<InvalidOperationException>(() => PsCommand.Create("Set-Mailbox", ("Identity", "r@x.example"), ("ForwardingSmtpAddress", "attacker@x.example")));
        Assert.Throws<InvalidOperationException>(() => PsCommand.Create("Set-Mailbox", ("Identity", "r@x.example"), ("Identity", "other@x.example")));
    }

    [Fact]
    public void Undocumented_places_parameters_are_not_in_the_catalog()
    {
        // Present in the MicrosoftPlaces module but not documented on Microsoft Learn.
        foreach (var parameter in new[] { "SquareFootArea", "MaxOccupancy", "Features", "PhotoPath", "Kind", "Category" })
        {
            Assert.Throws<InvalidOperationException>(() => PsCommand.Create("Set-PlaceV3", ("Identity", "x"), (parameter, "v")));
        }
    }

    [Fact]
    public void Script_rendering_escapes_quotes_and_never_interpolates()
    {
        var command = PsCommand.Create("Set-CalendarProcessing", ("Identity", "o'brien@x.example"), ("AdditionalResponse", "It’s $(Remove-Item C:\\ -Recurse); 'done'"), ("AllowConflicts", false), ("BookingWindowInDays", 90));
        Assert.Equal("Set-CalendarProcessing -Identity 'o''brien@x.example' -AdditionalResponse 'It’’s $(Remove-Item C:\\ -Recurse); ''done''' -AllowConflicts $false -BookingWindowInDays 90", command.ToScript());
    }

    [Fact]
    public void Switches_arrays_and_nulls_render_as_PowerShell_literals()
    {
        var list = PsCommand.Create("New-DistributionGroup", ("Name", "Rooms"), ("RoomList", new PsSwitch(true)));
        Assert.Equal("New-DistributionGroup -Name 'Rooms' -RoomList", list.ToScript());
        var remove = PsCommand.Create("Remove-DistributionGroupMember", ("Identity", "l@x.example"), ("Member", "r@x.example"), ("Confirm", new PsSwitch(false)));
        Assert.EndsWith("-Confirm:$false", remove.ToScript(), StringComparison.Ordinal);
        var delegates = PsCommand.Create("Set-CalendarProcessing", ("Identity", "r@x.example"), ("ResourceDelegates", new[] { "a@x.example", "b@x.example" }), ("AdditionalResponse", null));
        Assert.Equal("Set-CalendarProcessing -Identity 'r@x.example' -ResourceDelegates 'a@x.example','b@x.example' -AdditionalResponse $null", delegates.ToScript());
        Assert.Equal("Set-CalendarProcessing -Identity 'r@x.example' -BookInPolicy @()", PsCommand.Create("Set-CalendarProcessing", ("Identity", "r@x.example"), ("BookInPolicy", Array.Empty<string>())).ToScript());
    }

    [Theory]
    [InlineData("POST", "https://graph.microsoft.com/v1.0/users/a@x.example?$select=id")]
    [InlineData("GET", "https://graph.microsoft.com/v1.0/users/a@x.example/memberOf")]
    [InlineData("GET", "https://graph.microsoft.com/beta/users/a@x.example?$select=id")]
    [InlineData("GET", "https://evil.example/v1.0/users/a@x.example?$select=id")]
    public void Graph_requests_are_limited_to_allowlisted_reads(string method, string uri) =>
        Assert.Throws<InvalidOperationException>(() => PsCommand.Create("Invoke-MgGraphRequest", ("Method", method), ("Uri", uri)));

    [Fact]
    public void Allowlisted_graph_read_is_accepted()
    {
        var command = PsCommand.Create("Invoke-MgGraphRequest", ("Method", "GET"), ("Uri", "https://graph.microsoft.com/v1.0/users/room%40x.example/licenseDetails"), ("OutputType", "Json"));
        Assert.Equal(ServiceKind.Graph, command.Service);
        Assert.Equal(CommandEffect.Read, command.Effect);
    }

    [Fact]
    public void Catalog_includes_every_calendar_processing_parameter()
    {
        var spec = CommandCatalog.Get("Set-CalendarProcessing");
        Assert.All(BookingPolicyFields.All, f => Assert.Contains(f.Name, spec.Parameters));
    }

    [Fact]
    public void Removals_are_classified_for_extra_confirmation()
    {
        Assert.Equal(CommandEffect.Removal, CommandCatalog.Get("Remove-DistributionGroupMember").Effect);
        Assert.Equal(CommandEffect.Removal, CommandCatalog.Get("Remove-MailboxFolderPermission").Effect);
        Assert.Equal(CommandEffect.Write, CommandCatalog.Get("Set-CalendarProcessing").Effect);
    }

    [Fact]
    public void No_catalog_command_deletes_objects()
    {
        Assert.DoesNotContain(CommandCatalog.All, c => c.Name.StartsWith("Remove-Mailbox", StringComparison.Ordinal) && c.Name.Length == "Remove-Mailbox".Length);
        Assert.DoesNotContain(CommandCatalog.All, c => c.Name is "Remove-Place" or "Remove-DistributionGroup" or "Set-PlacesSettings");
    }
}

public class ErrorClassifierTests
{
    [Theory]
    [InlineData("Server busy: request was throttled (micro delay applied).", ErrorKind.Throttled)]
    [InlineData("The operation couldn't be performed because object 'x' couldn't be found on 'contoso'.", ErrorKind.NotFound)]
    [InlineData("The specified mailbox Identity:\"x\" doesn't exist.", ErrorKind.NotFound)]
    [InlineData("The operation on mailbox \"x\" failed because it's out of the current user's write scope (access denied).", ErrorKind.PermissionDenied)]
    [InlineData("The proxy address \"SMTP:x\" is already being used by the proxy addresses or LegacyExchangeDN.", ErrorKind.Conflict)]
    [InlineData("The recipient \"x\" is already a member of the group \"y\".", ErrorKind.Conflict)]
    [InlineData("AADSTS50076: Due to a configuration change made by your administrator, you must use multi-factor authentication.", ErrorKind.Authentication)]
    [InlineData("The operation has timed out.", ErrorKind.Transient)]
    [InlineData("Something unexpected.", ErrorKind.Internal)]
    public void Classifies_service_errors(string message, ErrorKind expected) => Assert.Equal(expected, ErrorClassifier.Classify(message));
}

public class CommandStorageTests
{
    [Fact]
    public void Stored_commands_round_trip_through_the_allowlist()
    {
        var original = new PlannedOperation
        {
            Command = PsCommand.Create("Add-DistributionGroupMember", ("Identity", "l@x.example"), ("Member", "r@x.example"), ("BypassSecurityGroupManagerCheck", new PsSwitch(true))),
            Description = "Add room",
        };
        var restored = CommandStorage.FromStored(CommandStorage.ToStored(original));
        Assert.Equal(original.Command.ToScript(), restored.ToScript());
    }

    [Fact]
    public void Tampered_stored_commands_are_rejected()
    {
        var stored = new Abstractions.StoredCommand("Remove-Mailbox", new Dictionary<string, System.Text.Json.JsonElement>(), "tampered");
        Assert.Throws<InvalidOperationException>(() => CommandStorage.FromStored(stored));
    }
}
