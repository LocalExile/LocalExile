using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

partial class Program
{
    internal static async Task RefreshBenchBalance(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool clear = false)
    {
        clear |= !LeagueFor(conn).Rules.CraftingBench;
        if (clear && !conn.BenchBalanceSent) return;
        IReadOnlyList<InventoryItem> snapshot = clear ? [] : Saves.Atomically(() => BenchBalance.Snapshot(PersonalStashOf(conn), Items, Bench));
        byte[] packet;
        if (!conn.BenchBalanceSent)
        {
            packet = InstanceInventories.AddInventory(InstanceBench.BalanceInventoryId, InstanceBench.BalanceSlot, 0, snapshot);
            conn.BenchBalanceSent = true;
        }
        else
            packet = InstanceItems.InventoryChange(InstanceBench.BalanceInventoryId, ++conn.BenchBalanceRevision,
                [.. conn.BenchBalanceItems.Select(i => i.Id)], snapshot);
        conn.BenchBalanceItems = snapshot;
        await Send(remote, stream, ciphers, packet, $"bench stash balance: {snapshot.Count} currency stack(s), {snapshot.Sum(i => (long)i.Item.StackSize)} units");
    }

    // A click on the guild stash (C→S 0x24): walk there, then its tab list (S→C 0x136 kind 1) and its panel (0x7b kind 2).
    // While it is open the stash messages (0xc7, 0x28, 0x2a, Ctrl + click …) act on it (StashOf); the personal stash's open
    // switches back.
    internal static async Task OpenGuildStash(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (conn.Ssf || conn.GuildStashObject == 0) return;
        if (!TryBeginObjectWalk(conn, requestedAt, conn.GuildStashAt.X, conn.GuildStashAt.Y, ClientState.StashBlockerSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {remote} <= 0x0024 guild stash ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        conn.GuildStashOpen = true;
        var guild = GuildStashOf(conn);
        var tabs = guild.Records(null, conn.GuildStashTabsSent);
        await Send(remote, stream, ciphers, InstanceStash.TabList(tabs, InstanceStash.Guild), $"0x0136 guild stash tab list: {tabs.Count} tab(s)");
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceStash.OpenGuildStashUi, conn.GuildStashObject, InstanceStash.NoAffinity),
            "0x007b open the guild stash", still: () => PlayerActionCurrent(conn, action));
    }

    // A hideout's placed doodads (HideoutCatalog.IsPlaced: the map device) from its decoration, with their HideoutDoodad data.
    internal static async Task SpawnHideoutDoodads(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (HideoutFurniture(conn) is not { } scene) return;
        foreach (var d in new[] { scene.Device })
        {
            if (HideoutCatalog.TypeOf(d.Type!) is not var (components, states) || !HideoutObject.CanSerialize(components))
            {
                Console.WriteLine($"[gateway] {remote} hideout doodad {d.Name} ({d.Type}): unknown type or unsupported components, not created");
                continue;
            }
            uint id = conn.NextObjectId++;
            // "active" 1: the map device stands open and idle (its .ot: per-player quest flag MapDeviceActive).
            var obj = new HideoutObject(d.X, d.Y, d.Rotation, components, new HideoutDoodad(d.Hash, (byte)d.Variation),
                [.. states.Select(s => s == "active" ? 1 : 0)]);
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(d.Type!), obj.Blob()),
                $"0x02b9 create hideout doodad 0x{id:x} {d.Name} ({d.Type}) at ({d.X}, {d.Y}), components {string.Join(", ", components)}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 hideout doodad 0x{id:x} enters wake zone");
            lock (conn.HideoutDoodads) conn.HideoutDoodads[id] = d;
            await RegisterStationaryCollision(remote, stream, ciphers, conn, id, d.Type!, d.X, d.Y, d.Rotation, "map-device", d.Model);
        }
    }
}
