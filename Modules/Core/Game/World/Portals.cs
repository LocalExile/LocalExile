using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // Lists the portals already standing again, so a portal cosmetic takes effect without reopening the map device: 0x008f
    // carries the variation of every entry and the client re-applies the visual. False when nothing stands.
    internal static async Task<bool> ResendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        uint deviceId;
        lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
        lock (conn.MapPortals)
            if (conn.MapPortals.Count == 0) return false;
        var open = MapDevice.OpenOf(CharacterName(conn));
        await SendMapPortals(remote, stream, ciphers, conn, deviceId, open?.Area.Hash16 ?? conn.Layout.Hash, areaId: open?.Area.Id);
        return true;
    }

    // S→C 0x8f: the device's portals and where they lead (21-hideouts.md §5). Without it the client never draws them; a
    // portal listed with area 0 (one just taken) closes. <paramref name="areaId"/> is the map area's WorldAreas Id, for the
    // completed mark below; without it the portals are listed as before.
    internal static async Task SendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint deviceId, ushort area,
        uint closed = 0, string? areaId = null)
    {
        byte style = conn.MapPortalStyle;
        List<uint> ids;
        lock (conn.MapPortals) ids = [.. conn.MapPortals.Keys.Order()];
        string owner = CharacterName(conn);
        var list = (closed == 0 ? [] : new List<InstanceMapDevice.Portal> { new(closed, 0) })
            .Concat(ids.Select(id => new InstanceMapDevice.Portal(id, area, owner, style, conn.CosmeticPortalVariation))).ToList();
        byte[] bytes = InstanceMapDevice.Portals(deviceId, list);
        // The floating label over a portal is the Portal component's own nametag (0x14161c0e0 -> 0x14161ca80): the area's
        // name, and with bit 1 of 0x008f's first byte set, " (Completed)" through ClientStrings 0x1fe5. The builder is the
        // Gateway's and cannot be rebuilt while it runs, so the bit is set on the bytes here. Bit 0 stays clear: it is the
        // "just opened" render path.
        bool completed = areaId is not null && RunBossKilled(conn, areaId);
        if (completed) bytes[2] |= 0x02;
        await Send(remote, stream, ciphers, bytes,
            $"0x008f map device 0x{deviceId:x} portals: {ids.Count} open to 0x{area:x4}{(completed ? " (completed)" : "")}{(closed == 0 ? "" : $", 0x{closed:x} closed")}");
    }

    /// <summary>The key the Atlas module marks a run's boss kill under (it decides whether a monster is the area's map boss).</summary>
    internal const string MapRunBossKey = "LocalExile.Portals.MapRunBoss";

    // The map device's run whose boss has died, by character: an area change is a new connection of the same character,
    // so the mark cannot live on the ClientState — the hideout's connection would not have it. Activating a map clears it
    // (PortalsFeature.ActivateMapDevice), so a fresh map starts unmarked.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> CompletedRuns = new(StringComparer.Ordinal);

    /// <summary>This run's boss died: from now on the area's portals read " (Completed)".</summary>
    internal static void MarkRunBossKilled(ClientState conn, string areaId)
    {
        string character = CharacterName(conn);
        if (character.Length == 0) return;
        if (!CompletedRuns.TryGetValue(character, out var done) || done != areaId)
            Console.WriteLine($"[gateway] {character} map run completed: {areaId} (its portals read completed)");
        CompletedRuns[character] = areaId;
    }

    /// <summary>A new map was put in the device: its run has no completed mark yet.</summary>
    internal static void ResetRunCompleted(ClientState conn) => CompletedRuns.TryRemove(CharacterName(conn), out _);

    static bool RunBossKilled(ClientState conn, string areaId) =>
        CompletedRuns.TryGetValue(CharacterName(conn), out var done) && done == areaId;

    /// <summary>A boss by the test <c>/kill boss</c> uses: unique, the big boss health bar, or a boss stage.</summary>
    internal static bool MapBoss(SpawnedMonster m) => m.Rarity == MonsterRarity.Unique || m.Variety?.BossHealthBar == true || m.Stage is not null;

    /// <summary>Whether the map's boss is already dead in this run: a boss stands in the area's roster and none of them is
    /// alive (a corpse stays in the roster, so this survives the moment of the kill).</summary>
    internal static bool MapBossDead(ClientState conn)
    {
        lock (conn.Monsters)
        {
            bool any = false;
            foreach (var m in conn.Monsters.Values)
            {
                if (!MapBoss(m)) continue;
                any = true;
                if (!m.Dead && m.Life > 0) return false;
            }
            return any;
        }
    }
}
