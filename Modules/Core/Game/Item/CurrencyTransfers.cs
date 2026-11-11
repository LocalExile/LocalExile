using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static class CurrencyTransfers
{
    internal const uint CurrencyAffinity = 1u << 3;
    internal static bool IsCurrency(ItemCatalog catalog, Item item) =>
        catalog.ByHash(item.TypeHash) is { Quest: null, Class: "Currency" or "StackableCurrency" or "DelveSocketableCurrency" or "DelveStackableSocketableCurrency" };

    internal static int? BoundTab(AccountStash stash) => stash.Records(null)
        .Where(t => (t.Affinities & CurrencyAffinity) != 0 && t.Parent == 0xffff)
        .OrderBy(t => t.Position).ThenBy(t => t.Index).Select(t => (int?)t.Index).FirstOrDefault();

    internal sealed record Pickup(int Tab, AccountStash.Stacking Stacked);

    // Claim the shared ground id before saving. A full tab or a failed save restores the same object and ownership.
    internal static Pickup? PickUp(InstanceGround ground, uint id, WorldItemObject expected, object owner,
        AccountStash stash, ItemCatalog catalog, Func<Item, PlayerInventories.Shape?> shapeOf)
    {
        if (!IsCurrency(catalog, expected.Item)) return null;
        var origin = ground.Origin(id);
        if (!origin.Shared && !ReferenceEquals(origin.By, owner)) return null;
        if (ground.Take(id, expected) is null) return null;
        try
        {
            var picked = Saves.Atomically(() =>
            {
                if (BoundTab(stash) is not int tab) return null;
                var stacked = stash.AddStacked(tab, expected.Item, shapeOf, out _, requireAll: true);
                return stacked is null ? null : new Pickup(tab, stacked);
            });
            if (picked is null) ground.Restore(id, expected, origin.By, origin.Shared);
            return picked;
        }
        catch
        {
            ground.Restore(id, expected, origin.By, origin.Shared);
            throw;
        }
    }

}

static partial class Game
{
    internal static async Task PickUpPetCurrency(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var rules = CurrencyPickupPolicy.Current;
        if (rules.Radius == 0) return;
        if (!GameEvents.AllowsLoot(conn) || conn.Closed || conn.LeavingArea || !conn.PlayerSent || conn.PlayerLife == 0) return;
        if (rules.RequirePet && !WornCosmetics(conn).Any(w => w.Slot is 18 or 19 && MtxEffects.Of(w.MtxTypeHash)?.PetType is { Length: > 0 })) return;
        var stash = PersonalStashOf(conn);
        if (CurrencyTransfers.BoundTab(stash) is null) return;
        var catalog = ItemsFor(conn);
        var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        foreach (var (id, ground) in conn.Instance.Ground.Within(at.X, at.Y, rules.Radius, i => CurrencyTransfers.IsCurrency(catalog, i.Item)).Take(rules.MaximumItemsPerTick))
        {
            CurrencyTransfers.Pickup? picked;
            lock (conn.Monsters)
            {
                if (HardcoreProgressBlocked(conn) || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
                picked = CurrencyTransfers.PickUp(conn.Instance.Ground, id, ground, conn, stash, catalog, ShapeOf);
            }
            if (picked is null) continue;
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"pet picked up currency 0x{id:x} x{ground.Item.StackSize} into stash tab {picked.Tab}");
            if (TabsSentOf(conn, stash).Contains(picked.Tab))
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(stash.InventoryOf(picked.Tab), stash.NextRevision(picked.Tab),
                    picked.Stacked.Changed.Select(i => i.Id).ToArray(), [.. picked.Stacked.Changed, .. picked.Stacked.Added]), "pet currency stash update");
        }
    }
}
