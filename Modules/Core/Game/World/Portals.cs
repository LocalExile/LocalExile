using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The map device's portal labels (21-hideouts.md Â§5): the client's own nametag over each portal says
    // "<area> (Completed)" once that run's map boss is dead, and nothing before. Kept by character: an area change is a new
    // connection of the same character, and the list is re-sent from the new one (the hideout), so a mark kept on the
    // connection would be lost exactly when the label needs it. Putting a map in the device clears it.
    static readonly ConcurrentDictionary<string, string> CompletedRuns = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, DateTime> RunChecks = new(StringComparer.Ordinal);

    /// <summary>Records that the area's map boss died: its portals read " (Completed)" from now on.</summary>
    public static void MarkRunBossKilled(ClientState conn, string areaId)
    {
        string character = CharacterName(conn);
        if (character.Length == 0) return;
        if (!CompletedRuns.TryGetValue(character, out var done) || !string.Equals(done, areaId, StringComparison.Ordinal))
            Console.WriteLine($"[gateway] {character} map run completed: {areaId} (its portals read completed)");
        CompletedRuns[character] = areaId;
    }

    /// <summary>A map went into the device: its run has not had its boss killed yet.</summary>
    public static void ResetRunCompleted(ClientState conn) => CompletedRuns.TryRemove(CharacterName(conn), out _);

    /// <summary>Whether this character's run of the area is done: its boss died, which its portals' labels show.</summary>
    public static bool RunBossKilled(ClientState conn, string areaId) =>
        CompletedRuns.TryGetValue(CharacterName(conn), out var done) && string.Equals(done, areaId, StringComparison.Ordinal);

    /// <summary>A boss by the test <c>/kill boss</c> uses: unique, the big boss health bar, or a boss stage.</summary>
    public static bool MapBoss(SpawnedMonster m) =>
        m.Rarity == MonsterRarity.Unique || m.Variety?.BossHealthBar == true || m.Stage is not null;

    /// <summary>The tick: in the map the device opened, once every boss of it is dead the run is marked. Reading the roster
    /// rather than the kill covers the kills no hook reports (a minion's, a projectile's) and a boss that died before this
    /// was loaded. Four checks a second at most, and none outside the device's own area.</summary>
    public static void TickMapRunBoss(ClientState conn)
    {
        if (!conn.PlayerSent || conn.Closed || conn.LeavingArea || conn.Instance is null) return;
        string character = CharacterName(conn);
        if (character.Length == 0) return;
        var now = DateTime.UtcNow;
        if (RunChecks.TryGetValue(character, out var checkedAt) && now - checkedAt < TimeSpan.FromMilliseconds(250)) return;
        RunChecks[character] = now;
        if (MapDevice.OpenOf(character) is not { } open || !string.Equals(open.Area.Id, conn.Layout.Id, StringComparison.Ordinal)) return;
        lock (conn.Monsters)
        {
            bool any = false;
            foreach (var m in conn.Monsters.Values)
            {
                if (!MapBoss(m)) continue;
                any = true;
                if (!m.Dead && m.Life > 0) return;
            }
            if (!any) return;
        }
        MarkRunBossKilled(conn, open.Area.Id);
    }


    public static (uint Id, int X, int Y, bool Campaign)? MapDeviceHere(ClientState conn)
    {
        if (conn.Layout.Id == MapDevice.CampaignDeviceArea)
            lock (conn.QuestObjects)
                foreach (var (id, obj) in conn.QuestObjects)
                    if (obj.Rule.ObjectType == MapDevice.CampaignDeviceType)
                        return (id, obj.At.X, obj.At.Y, true);
        lock (conn.HideoutDoodads)
            foreach (var (id, device) in conn.HideoutDoodads)
                return (id, device.X, device.Y, false);
        return null;
    }
    // Lists the portals already standing again, so a portal cosmetic takes effect without reopening the map device or
    // using another Portal Scroll: 0x008f carries the variation of every entry and the client re-applies the visual.
    // False when nothing stands.
    public static async Task<bool> ResendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        bool single = await SendSinglePortals(remote, stream, ciphers, conn, "listed again");
        uint deviceId = MapDeviceHere(conn)?.Id ?? 0;
        lock (conn.MapPortals)
            if (conn.MapPortals.Count == 0) return single;
        await SendMapPortals(remote, stream, ciphers, conn, deviceId,
            conn.MapPortalRun?.Area.Hash16 ?? conn.Layout.Hash);
        return true;
    }

    // The portals that stand without a map device (a Portal Scroll's, a map's own way back, the portal back to an open
    // town portal): object id â†’ the area it leads to, for the connection that made them.
    static readonly RuleTable<ClientState, System.Collections.Concurrent.ConcurrentDictionary<uint, ushort>> SinglePortals = new();

    /// <summary>
    /// Lists a portal that stands without a map device, <paramref name="afterMs"/> after its create and once more
    /// MapPortalRelistMs later. 0x8f only reaches a portal object the client has built. The client reads its socket once
    /// per update and handles 0x8f at once, while the create waits for its world frame, so a list that arrives in the
    /// same read as the create is lost and the portal is never drawn (seen live 2026-11-12, a background window in the
    /// lockstep mode: 0x8f handled 3 ms before the object was built, sent 100 ms after it). An entry listed again
    /// unchanged leaves the portal as it is.
    /// </summary>
    public static void ListPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, ushort area,
        string what, int afterMs)
    {
        SinglePortals.GetOrCreateValue(conn)[id] = area;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(afterMs);
                if (!await SendSinglePortals(remote, stream, ciphers, conn, what, id)) return;
                await Task.Delay(MapPortalRelistMs);
                await SendSinglePortals(remote, stream, ciphers, conn, what + ", listed again", id);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection closed while sending.
            }
        });
    }

    /// <summary>
    /// The effect one portal is drawn with. The Portal slot has two choices that are no cosmetic
    /// (MicrotransactionSlotAdditionalDefaultOptions; Câ†’S 0x1f9 with record 0 and the option): 1 "Use one random
    /// variation for every portal" and 2 "Use random variations for all portals". The pick is kept, per area visit for
    /// 1 and per portal for 2, so a portal listed again keeps its effect. Otherwise the worn effect.
    /// </summary>
    public static ushort PortalVariation(ClientState conn, uint portal)
    {
        byte mode = CharacterSaves.Cosmetics(CharacterName(conn)).GetValueOrDefault(InstanceMicrotransactions.PortalSlot) is
            { Record: 0, Option: 1 or 2 } choice ? choice.Option : (byte)0;
        if (mode == 0 || Cosmetics.Portals.Count == 0) return conn.CosmeticPortalVariation;
        int seed = mode == 1 ? HashCode.Combine(conn.Seed, conn.Ticket) : HashCode.Combine(conn.Seed, conn.Ticket, portal);
        return (ushort)Cosmetics.Portals[(int)((uint)seed % (uint)Cosmetics.Portals.Count)].Index;
    }

    // One 0x8f without a device for every portal of SinglePortals that still stands, each with the worn portal effect.
    // With an id, only while that portal stands. False when nothing was sent.
    static async Task<bool> SendSinglePortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why, uint only = 0)
    {
        if (conn.Closed || !SinglePortals.TryGetValue(conn, out var portals)) return false;
        bool Stands(uint id) => conn.ReturnPortal?.Id == id || conn.BackPortal?.Id == id;
        foreach (uint gone in portals.Keys.Where(id => !Stands(id)).ToList()) portals.TryRemove(gone, out _);
        if (portals.IsEmpty || (only != 0 && !portals.ContainsKey(only))) return false;
        string owner = CharacterName(conn);
        var list = portals.OrderBy(p => p.Key)
            .Select(p => new InstanceMapDevice.Portal(p.Key, p.Value, owner, Variation: PortalVariation(conn, p.Key))).ToList();
        await Send(remote, stream, ciphers, InstanceMapDevice.Portals(0, list),
            $"0x008f {string.Join(", ", list.Select(p => $"portal 0x{p.ObjectId:x} leads to 0x{p.Area:x4}"))} (variation {string.Join(", ", list.Select(p => p.Variation))}; {why})");
        return true;
    }

    // Sâ†’C 0x8f: the device's portals and where they lead (21-hideouts.md Â§5). Without it the client never draws them; a
    // portal listed with area 0 (one just taken) closes.
    public static async Task SendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint deviceId, ushort area, uint closed = 0)
    {
        byte style = conn.MapPortalStyle;
        List<(uint Id, int Index)> ids;
        lock (conn.MapPortals) ids = [.. conn.MapPortals.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Index))];
        var run = conn.MapPortalRun;
        string owner = run?.Owner ?? CharacterName(conn);
        var list = (closed == 0 ? [] : new List<InstanceMapDevice.Portal> { new(closed, 0) })
            .Concat(ids.Select(p => new InstanceMapDevice.Portal(p.Id, area, owner, style,
                run is null ? PortalVariation(conn, p.Id) : MapPortalVariation(run, p.Index)))).ToList();
        await Send(remote, stream, ciphers, InstanceMapDevice.Portals(deviceId, list, run is not null && RunBossKilled(conn, run.Area.Id)),
            $"0x008f map device 0x{deviceId:x} portals: {ids.Count} open to 0x{area:x4}{(closed == 0 ? "" : $", 0x{closed:x} closed")}");
    }

    static ushort MapPortalVariation(MapDevice.OpenMap run, int index)
    {
        byte mode = CharacterSaves.Cosmetics(run.Owner).GetValueOrDefault(InstanceMicrotransactions.PortalSlot) is
            { Record: 0, Option: 1 or 2 } choice ? choice.Option : (byte)0;
        if (mode == 0 || Cosmetics.Portals.Count == 0) return CharacterSaves.PortalCosmetic(run.Owner);
        int seed = HashCode.Combine(run.Run, mode == 1 ? 0 : index);
        return (ushort)Cosmetics.Portals[(int)((uint)seed % (uint)Cosmetics.Portals.Count)].Index;
    }
}
