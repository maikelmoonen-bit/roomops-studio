using System.Text.Json.Nodes;
using RoomOps.Application.Commands;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Common;
using RoomOps.Domain.TeamsRooms;

namespace RoomOps.Application.Gateways;

/// <summary>Read-only Microsoft Graph access through Invoke-MgGraphRequest with allowlisted GET paths.</summary>
public sealed class GraphGateway(ISessionManager sessions)
{
    public bool IsAvailable => sessions.Current.IsConnected(ServiceKind.Graph);

    private Task<CommandResult> Get(string uri, CancellationToken ct) =>
        sessions.Runner(ServiceKind.Graph).RunAsync(PsCommand.Create("Invoke-MgGraphRequest", ("Method", "GET"), ("Uri", uri), ("OutputType", "Json")), ct);

    public async Task<DirectoryAccount?> GetAccountAsync(string userPrincipalName, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(userPrincipalName);
        JsonObject user;
        try
        {
            var result = await Get($"https://graph.microsoft.com/v1.0/users/{id}?$select=id,userPrincipalName,mail,accountEnabled,passwordPolicies,onPremisesSyncEnabled", ct);
            if (result.Objects.Count == 0) return null;
            user = result.Objects[0];
        }
        catch (CommandFailedException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            return null;
        }

        var licenses = await Get($"https://graph.microsoft.com/v1.0/users/{id}/licenseDetails", ct);
        var parts = licenses.Objects
            .SelectMany(o => o["value"] is JsonArray arr ? arr.OfType<JsonObject>() : [o])
            .Select(o => o.Str("skuPartNumber"))
            .Where(s => s is not null)
            .Cast<string>()
            .ToList();

        return new DirectoryAccount(
            user.StrOr("id", userPrincipalName),
            user.StrOr("userPrincipalName", userPrincipalName),
            user.Str("mail"),
            user.Bool("accountEnabled"),
            parts,
            user.Str("passwordPolicies"),
            user.Bool("onPremisesSyncEnabled"));
    }
}
