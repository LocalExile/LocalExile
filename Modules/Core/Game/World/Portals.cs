using System.Net.Sockets;
using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    sealed class MapRunProgress(Guid run, int[] bosses)
    {
        public readonly Guid Run = run;
        public readonly HashSet<int> Bosses = [.. bosses];
        public readonly HashSet<int> Killed = [];
        public double NextCheck;
        public volatile bool Completed;
    }
    static readonly Store<InstanceState, MapRunProgress> MapRuns = new();

    // A run survives its owner's area change and belongs to guests too. The instance keeps its exact run even when
    // the owner opens another copy of the same area; neither character names nor area ids identify a run.
    public static bool RunBossKilled(MapDevice.OpenMap? run) => run is not null
        && MapRuns.Any(p => p.Value.Run == run.Run && p.Value.Completed);

    static int[] CompletionBosses(string area)
    {
        var bosses = (Packs.For(area)?.Bosses ?? []).Select(row => Monsters.Find(row.ToString()))
            .OfType<MonsterCatalog.Variety>().ToArray();
        // LaterForms places a second form only when its first flees or falls. Only the terminal form has to die;
        // multiple distinct bosses (Strand, for example) must all die. Other unique monsters are irrelevant.
        return [.. bosses.Where(first => !bosses.Any(last => last.Id == first.Id + "2" && last.Name == first.Name))
            .Select(b => b.Row)];
    }

    public static async Task TickMapRunBoss(Player player, bool force = false)
    {
        if (!player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Instance is not { } instance
            || MapDevice.ForInstance(player) is not { } run) return;
        var progress = MapRuns.GetValue(instance, _ => new(run.Run, CompletionBosses(run.Area.Id)));
        double now = AreaTime(player);
        if (progress.Completed || progress.Bosses.Count == 0 || !force && now < progress.NextCheck) return;
        progress.NextCheck = now + 0.25;
        bool alive = false;
        foreach (var monster in player.Area.Monsters.Values.Concat(player.Area.DormantMonsters.Values))
        {
            if (!monster.Hostile || monster.Variety is not { } variety || !progress.Bosses.Contains(variety.Row)) continue;
            if (monster.Dead || monster.Life <= 0) progress.Killed.Add(variety.Row);
            else alive = true;
        }
        // Remember kills before a corpse is consumed, but never complete while a twin or a later boss is alive
        // or a required boss has not spawned yet.
        if (alive || !progress.Bosses.IsSubsetOf(progress.Killed)) return;
        progress.Completed = true;
        Console.WriteLine($"[gateway] {run.Owner} map run {run.Run} completed: {run.Area.Id}");
        foreach (var visitor in Program.InGame.Keys.Where(c => c.MapPortalRun?.Run == run.Run
            && c.Connection.PlayerSent && !c.Closed && !c.LeavingArea))
        {
            try
            {
                await SendMapPortals(visitor,
                    MapDeviceHere(visitor)?.Id ?? 0, run.Area.Hash16);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A visitor disconnected; the next listing still reads this run's completed state.
            }
        }
    }

    public static (uint Id, int X, int Y, bool Campaign)? MapDeviceHere(Player player)
    {
        if (player.Layout.Id == MapDevice.CampaignDeviceArea)
            lock (player.QuestObjects)
                foreach (var (id, obj) in player.QuestObjects)
                    if (obj.Rule.ObjectType == MapDevice.CampaignDeviceType)
                        return (id, obj.At.X, obj.At.Y, true);
        lock (player.HideoutDoodads)
            foreach (var (id, device) in player.HideoutDoodads)
                return (id, device.X, device.Y, false);
        return null;
    }
    // Lists the portals already standing again, so a portal cosmetic takes effect without reopening the map device or
    // using another Portal Scroll: 0x008f carries the variation of every entry and the client re-applies the visual.
    // False when nothing stands.
    public static async Task<bool> ResendMapPortals(Player player)
    {
        bool single = await SendSinglePortals(player, "listed again");
        uint deviceId = MapDeviceHere(player)?.Id ?? 0;
        lock (player.MapPortals)
            if (player.MapPortals.Count == 0) return single;
        await SendMapPortals(player, deviceId,
            player.MapPortalRun?.Area.Hash16 ?? player.Layout.Hash);
        return true;
    }

    // The portals that stand without a map device (a Portal Scroll's, a map's own way back, the portal back to an open
    // town portal): object id Ã¢â€ ’ the area it leads to, for the connection that made them.
    static readonly Store<Player, System.Collections.Concurrent.ConcurrentDictionary<uint, ushort>> SinglePortals = new();

    /// <summary>
    /// Lists a portal that stands without a map device, <paramref name="afterMs"/> after its create and once more
    /// MapPortalRelistMs later. 0x8f only reaches a portal object the client has built. The client reads its socket once
    /// per update and handles 0x8f at once, while the create waits for its world frame, so a list that arrives in the
    /// same read as the create is lost and the portal is never drawn (seen live 2026-11-12, a background window in the
    /// lockstep mode: 0x8f handled 3 ms before the object was built, sent 100 ms after it). An entry listed again
    /// unchanged leaves the portal as it is.
    /// </summary>
    public static void ListPortal(Player player, uint id, ushort area,
        string what, int afterMs)
    {
        SinglePortals.GetOrCreateValue(player)[id] = area;
        _ = AreaFrames.Start(async () =>
        {
            try
            {
                await Task.Delay(afterMs);
                if (!await SendSinglePortals(player, what, id)) return;
                await Task.Delay(MapPortalRelistMs);
                await SendSinglePortals(player, what + ", listed again", id);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection closed while sending.
            }
        });
    }

    /// <summary>
    /// The effect one portal is drawn with. The Portal slot has two choices that are no cosmetic
    /// (MicrotransactionSlotAdditionalDefaultOptions; CÃ¢â€ ’S 0x1f9 with record 0 and the option): 1 "Use one random
    /// variation for every portal" and 2 "Use random variations for all portals". The pick is kept, per area visit for
    /// 1 and per portal for 2, so a portal listed again keeps its effect. Otherwise the worn effect.
    /// </summary>
    public static ushort PortalVariation(Player player, uint portal)
    {
        byte mode = CharacterSaves.Cosmetics(CharacterName(player)).GetValueOrDefault(InstanceMicrotransactions.PortalSlot) is
            { Record: 0, Option: 1 or 2 } choice ? choice.Option : (byte)0;
        if (mode == 0 || Cosmetics.Portals.Count == 0) return player.Character.CosmeticPortalVariation;
        int seed = mode == 1 ? HashCode.Combine(player.Seed, player.Ticket) : HashCode.Combine(player.Seed, player.Ticket, portal);
        return (ushort)Cosmetics.Portals[(int)((uint)seed % (uint)Cosmetics.Portals.Count)].Index;
    }

    // One 0x8f without a device for every portal of SinglePortals that still stands, each with the worn portal effect.
    // With an id, only while that portal stands. False when nothing was sent.
    static async Task<bool> SendSinglePortals(Player player, string why, uint only = 0)
    {
        if (player.Closed || !SinglePortals.TryGetValue(player, out var portals)) return false;
        bool Stands(uint id) => player.ReturnPortal?.Id == id || player.BackPortal?.Id == id;
        foreach (uint gone in portals.Keys.Where(id => !Stands(id)).ToList()) portals.TryRemove(gone, out _);
        if (portals.IsEmpty || (only != 0 && !portals.ContainsKey(only))) return false;
        string owner = CharacterName(player);
        var list = portals.OrderBy(p => p.Key)
            .Select(p => new InstanceMapDevice.Portal(p.Key, p.Value, owner, Variation: PortalVariation(player, p.Key))).ToList();
        await Send(player, InstanceMapDevice.Portals(0, list),
            $"0x008f {string.Join(", ", list.Select(p => $"portal 0x{p.ObjectId:x} leads to 0x{p.Area:x4}"))} (variation {string.Join(", ", list.Select(p => p.Variation))}; {why})");
        return true;
    }

    // SÃ¢â€ ’C 0x8f: the device's portals and where they lead (21-hideouts.md Ã‚§5). Without it the client never draws them; a
    // portal listed with area 0 (one just taken) closes.
    public static async Task SendMapPortals(Player player, uint deviceId, ushort area, uint closed = 0)
    {
        byte style = player.MapPortalStyle;
        List<(uint Id, int Index)> ids;
        MapDevice.OpenMap? run;
        lock (player.MapPortals)
        {
            ids = [.. player.MapPortals.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Index))];
            run = player.MapPortalRun;
            area = run?.Area.Hash16 ?? area;
        }
        string owner = run?.Owner ?? CharacterName(player);
        var list = (closed == 0 ? [] : new List<InstanceMapDevice.Portal> { new(closed, 0) })
            .Concat(ids.Select(p => new InstanceMapDevice.Portal(p.Id, area, owner, style,
                run is null ? PortalVariation(player, p.Id) : MapPortalVariation(run, p.Index)))).ToList();
        await Send(player, InstanceMapDevice.Portals(deviceId, list, RunBossKilled(run)),
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
