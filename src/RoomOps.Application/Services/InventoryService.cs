using RoomOps.Application.Commands;
using RoomOps.Application.Gateways;
using RoomOps.Application.Sessions;
using RoomOps.Domain.Common;
using RoomOps.Domain.Places;
using RoomOps.Domain.RoomLists;
using RoomOps.Domain.Rooms;

namespace RoomOps.Application.Services;

public sealed record InventorySnapshot(
    IReadOnlyList<RoomMailbox> Rooms,
    IReadOnlyList<RoomList> RoomLists,
    IReadOnlyList<Place> Places,
    bool PlacesAvailable,
    string? PlacesError,
    DateTimeOffset LoadedAt);

/// <summary>
/// Lightweight inventory: room mailboxes, room lists with members and the Places hierarchy. Richer per-room data (calendar
/// processing, working hours) is loaded on demand or by an explicit scan, never preloaded for every room.
/// </summary>
public sealed class InventoryService(ISessionManager sessions, ExchangeGateway exchange, PlacesGateway places, TimeProvider time) : IDisposable
{
    public void Dispose() => _gate.Dispose();

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<InventorySnapshot> GetSnapshotAsync(bool refresh, CancellationToken ct)
    {
        var cache = sessions.Cache;
        if (refresh)
        {
            cache.InvalidateInventory();
        }
        await _gate.WaitAsync(ct);
        try
        {
            var rooms = cache.Rooms ??= new(await exchange.GetRoomMailboxesAsync(ct), time.GetUtcNow());
            var lists = cache.RoomLists ??= new(await exchange.GetRoomListsAsync(true, ct), time.GetUtcNow());
            string? placesError = null;
            if (places.IsAvailable && cache.Places is null)
            {
                try
                {
                    cache.Places = new(await places.GetHierarchyAsync(ct), time.GetUtcNow());
                }
                catch (RoomOpsException ex)
                {
                    placesError = ex.Message;
                }
            }
            var placeList = places.IsAvailable ? cache.Places?.Value ?? [] : [];
            return new InventorySnapshot(rooms.Value, lists.Value, placeList, places.IsAvailable && placesError is null, placesError, rooms.LoadedAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RoomMailbox>> GetMailboxesAsync(CancellationToken ct) => (await GetSnapshotAsync(false, ct)).Rooms;

    public async Task<IReadOnlyList<RoomList>> GetRoomListsAsync(bool refresh, CancellationToken ct)
    {
        if (refresh) sessions.Cache.RoomLists = null;
        return (await GetSnapshotAsync(false, ct)).RoomLists;
    }

    public async Task<IReadOnlyDictionary<string, Place>> GetPlacesByIdAsync(CancellationToken ct)
    {
        if (!places.IsAvailable) return new Dictionary<string, Place>();
        var snapshot = await GetSnapshotAsync(false, ct);
        return snapshot.Places.GroupBy(p => p.PlaceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<RoomMailbox> ResolveRoomAsync(string reference, CancellationToken ct) =>
        await TryResolveRoomAsync(reference, ct)
        ?? throw RoomOpsException.NotFound("room.notFound", Text.L($"No room mailbox matches '{reference}'.", $"Geen ruimtepostvak komt overeen met '{reference}'."));

    /// <summary>Finds a room by id, address, alias, or unique display name. Falls back to a direct mailbox lookup for addresses not yet in the cache.</summary>
    public async Task<RoomMailbox?> TryResolveRoomAsync(string reference, CancellationToken ct)
    {
        var value = reference.Trim();
        if (value.Length == 0) return null;
        var rooms = await GetMailboxesAsync(ct);
        var match = rooms.FirstOrDefault(r => r.Id.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? rooms.FirstOrDefault(r => r.PrimarySmtpAddress.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? rooms.FirstOrDefault(r => r.Alias.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        var byName = rooms.Where(r => r.DisplayName.Equals(value, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (value.Contains('@'))
        {
            var mailbox = await exchange.GetMailboxAsync(value, ct);
            return mailbox is { RecipientTypeDetails: "RoomMailbox" } ? mailbox : null;
        }
        return null;
    }

    public static Place? PlaceFor(RoomMailbox room, IReadOnlyList<Place> placeList) =>
        placeList.FirstOrDefault(p => p.Type == PlaceType.Room && string.Equals(p.EmailAddress, room.PrimarySmtpAddress, StringComparison.OrdinalIgnoreCase));

    public bool PlacesConnected => sessions.Current.IsConnected(ServiceKind.Places);
}
