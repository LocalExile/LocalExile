using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using static Game;
using static Program;

/// <summary>Retained Essence monoliths, released through the ordinary monster and loot paths.</summary>
public static class MapEssences
{
    public const string Type = "Metadata/MiscellaneousObjects/Monolith", Content = "Essence", Ascent = "Essence.Ascent",
        Calcification = "Essence.Calcification";
    const string SaveKey = "Essence/monoliths";
    public sealed record Monolith(uint Id, int Index, int X, int Y, string Variety, IReadOnlyList<int> Essences,
        byte Stage = 1, uint Monster = 0)
    {
        public string? Source { get; init; }
    }
    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Monolith>? Monoliths;
    }
    static readonly Store<InstanceState, Area> Areas = new();
    static readonly Store<Player, HashSet<uint>> Presented = new();
    sealed record Snapshot(string Text, List<Monolith> List);
    sealed class Parse { public volatile Snapshot? Last; }
    static readonly Store<InstanceState, Parse> Parsed = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Area, int Level), AreaInfo> AreaInfos = new();

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening || MapEssenceCatalog.Pool(opening.Area.Level).Count == 0) return;
        if (Packs.For(opening.Area.Id)?.Scattered.Any(p => p.Pack.Entries.Any(e =>
            Monsters.Find(e.Variety.ToString()) is { Supported: true })) != true) return;
        request.HandledContent.UnionWith([Content, Ascent, Calcification]);
        if (opening.Mods.Get(Stat.MapHasMonoliths) > 0 || opening.Mods.Get(Stat.MapRareMonstersTrappedInEssences) > 0
            || opening.Mods.Get(Stat.MapEssencesAre1TierHigherChancePct) > 0)
            request.ContentWarnings.Add(("Essence.Parity",
                $"area={opening.Area.Id} content=Essence: release/reward lifecycle enabled; base essence multiplicity/weights use replica balance, corruption and specialised essence skills remain unsupported"));
    }

    static IEnumerable<Player> Members(Player conn) => conn.Instance.PlayersExcept(conn.PeerId)
        .Select(p => p.Owner).OfType<Player>().Prepend(conn)
        .Where(c => !c.Closed && !c.LeavingArea && c.Instance == conn.Instance).Distinct();
    static void Keep(Player conn, Area area) => conn.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Monoliths);

    public static int Count(Player conn)
    {
        if (MapRunOf(conn) is not { } map || conn.Instance.KeptAreaStats is not { } stats) return 0;
        // Loading the WorldAreas row costs ~3 ms on every new map instance; the row does not change.
        var area = AreaInfos.GetOrAdd((map.Area.Id, map.Area.Level), k => AreaInfo.Load(GamePack.Default.TryRead, k.Area, k.Level)) with
        { Stats = stats.ToDictionary(s => (int)s.Key, s => s.Value) };
        return ExtraTerrainFeatures.LeagueCount(area, new LevelRandom(conn.Seed ^ 0x45535345, false), 6);
    }

    /// <summary>Replaces a planned natural rare before it exists; it earns no kill rewards until actually released and killed.</summary>
    public static async Task<bool> Imprison(Player conn, AreaPopulation.Spawn spawn)
    {
        if (spawn.Rarity != MonsterRarity.Rare || MapModsOf(conn) is not { } mods
            || mods.Get(Stat.MapRareMonstersTrappedInEssences) <= 0
            || Packs.For(conn.Layout.Id)?.Scattered.Any(p => p.Pack.Id == spawn.Pack) != true
            || MapScarabPacks.Packs.Any(p => p.Id == spawn.Pack)
            || Monsters.Find(spawn.Variety.ToString()) is not { Supported: true } variety) return false;
        await Enter(conn);
        var area = Areas.GetValue(conn.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.Monoliths is not { } monoliths) return false;
            string source = $"{spawn.Pack}/{spawn.Variety}/{spawn.X}/{spawn.Y}";
            if (monoliths.Any(m => m.Source == source)) return true;
            var essences = MapEssenceCatalog.Roll(MapRunOf(conn)!.Area.Level, mods,
                new Random(unchecked((int)conn.Seed ^ spawn.X * 397 ^ spawn.Y)));
            if (essences.Count == 0) return false;
            bool Floor(int x, int y) => conn.Layout.WalkableFor(x, y, 4);
            var blockers = conn.MonsterPlacementBlockers(0);
            (int X, int Y)? at = CollisionEscape.Clear((spawn.X, spawn.Y), conn.Layout.GridW, conn.Layout.GridH, Floor, blockers, 4)
                ? (spawn.X, spawn.Y) : CollisionEscape.Find((spawn.X, spawn.Y), conn.Layout.GridW, conn.Layout.GridH, Floor, blockers, moverSize: 4);
            if (at is null)
            {
                Console.WriteLine($"[map-content] Calcification area={conn.Layout.Id}: no clear monolith cell for natural rare {source}; retaining the rare");
                return false;
            }
            var monolith = new Monolith(conn.Instance.World.NewMonsterId(), monoliths.Count, at.Value.X, at.Value.Y,
                variety.Id, essences) { Source = source };
            monoliths.Add(monolith);
            Keep(conn, area);
            foreach (var member in Members(conn)) await Present(member, monolith);
            return true;
        }
        finally { area.Gate.Release(); }
    }

    public static async Task Enter(Player conn)
    {
        if (MapRunOf(conn) is not { } map || MapModsOf(conn) is not { } mods) return;
        var area = Areas.GetValue(conn.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.Monoliths is null)
            {
                if (conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    area.Monoliths = JsonSerializer.Deserialize<List<Monolith>>(saved) ?? [];
                else
                {
                    area.Monoliths = [];
                    int count = Count(conn);
                    var pool = Packs.For(conn.Layout.Id)?.Scattered.SelectMany(p => p.Pack.Entries)
                        .Where(e => Monsters.Find(e.Variety.ToString()) is { Supported: true }).ToArray() ?? [];
                    var random = new Random(unchecked((int)(conn.Seed ^ 0x45535345)));
                    if (pool.Length > 0)
                        foreach (var (x, y) in MapStrongboxes.Positions(conn.Layout, conn.Seed ^ 0x45535345, count))
                        {
                            var essences = MapEssenceCatalog.Roll(map.Area.Level, mods, random);
                            if (essences.Count == 0) break;
                            int roll = random.Next(pool.Sum(e => e.Weight));
                            int variety = pool[^1].Variety;
                            foreach (var e in pool) if ((roll -= e.Weight) < 0) { variety = e.Variety; break; }
                            area.Monoliths.Add(new(conn.Instance.World.NewMonsterId(), area.Monoliths.Count, x, y,
                                Monsters.Find(variety.ToString())!.Id, essences));
                        }
                    Keep(conn, area);
                    if (count > 0) Console.WriteLine($"[map-content] essences area={conn.Layout.Id}: placed {area.Monoliths.Count}/{count}");
                }
            }
            foreach (var monolith in area.Monoliths) await Present(conn, monolith);
        }
        finally { area.Gate.Release(); }
    }

    static async Task Present(Player conn, Monolith monolith, bool replace = false)
    {
        var shown = Presented.GetValue(conn, _ => []);
        if (!replace && shown.Contains(monolith.Id)) return;
        if (Monsters.Find(monolith.Variety) is not { } variety) return;
        if (replace && shown.Contains(monolith.Id))
        {
            await SendWorldRecord(conn, monolith.Id,
                InstanceEntities.TransitionableUpdate(monolith.Id, 4, monolith.Stage), null);
            await SendWorldRecord(conn, monolith.Id,
                InstanceEntities.StateMachineUpdate(monolith.Id, 6, MonolithObject.States(monolith.Stage)), null);
            if (monolith.Stage >= 4) conn.RemoveBlocker(monolith.Id);
            return;
        }
        var obj = new MonolithObject(monolith.X, monolith.Y, variety.Hash16,
            [.. monolith.Essences.Select(MapEssenceCatalog.Find).OfType<MapEssenceCatalog.Essence>().Select(e => e.Hash)], monolith.Stage);
        await SendWorldCreate(conn, monolith.Id,
            InstanceEntities.CreateObject(monolith.Id, InstanceEntities.ObjectTypeHash(Type), obj.Blob()),
            $"essence monolith 0x{monolith.Id:x} stage={monolith.Stage} essences={monolith.Essences.Count} at ({monolith.X}, {monolith.Y})");
        await SendWorldRecord(conn, monolith.Id, InstanceProtocol.EnterWakeZone(monolith.Id), null);
        if (monolith.Stage < 4) conn.SetBlocker(monolith.Id, monolith.X, monolith.Y, 4);
        else conn.RemoveBlocker(monolith.Id);
        shown.Add(monolith.Id);
    }

    public static async Task<bool> Use(Player conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area) || area.Monoliths?.FirstOrDefault(m => m.Id == use.TargetId) is not { } monolith) return false;
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return true;
        if (monolith.Stage >= 4)
        {
            await SendWorldRecord(conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
            return true;
        }
        // The native .ot overrides its interaction distance to twenty cells.
        if (!TryBeginObjectWalk(conn, requestedAt, monolith.X, monolith.Y, 20, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(conn, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, monolith.X, monolith.Y, use.SkillId, use.Sequence, use.Flags,
                    targetObjectId: monolith.Id), null, still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        await area.Gate.WaitAsync();
        try
        {
            monolith = area.Monoliths!.First(m => m.Id == use.TargetId);
            if (!PlayerActionCurrent(conn, action) || conn.PlayerLife == 0 || monolith.Stage >= 4
                || !PlayerWalk.InWalkUpRange(conn.Walk.Position(AreaTime(conn)), (monolith.X, monolith.Y), 20)) return true;
            if (monolith.Stage == 3)
            {
                if (Monsters.Find(monolith.Variety) is not { Supported: true } variety) return true;
                byte level = (byte)Math.Clamp(MapRunOf(conn)!.Area.Level, 1, 100);
                var mods = MapEssenceCatalog.Mods(conn, variety, level, monolith.Essences);
                uint monster = await SpawnMonster(conn, variety, level, MonsterRarity.Rare,
                    monolith.X, monolith.Y, mods: mods, unrisen: false, alone: false,
                    owned: (id, _) =>
                    {
                        area.Monoliths[monolith.Index] = monolith with { Stage = 4, Monster = id };
                        Keep(conn, area);
                    });
                if (monster == 0) return true;
                monolith = monolith with { Monster = monster };
            }
            monolith = monolith with { Stage = (byte)(monolith.Stage + 1) };
            area.Monoliths[monolith.Index] = monolith;
            Keep(conn, area);
            foreach (var member in Members(conn)) await Present(member, monolith, true);
        }
        finally
        {
            area.Gate.Release();
            await SendWorldRecord(conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
                still: () => PlayerActionCurrent(conn, action));
        }
        return true;
    }

    public static void Drops(DropRoll roll)
    {
        if (roll.Monster.ObjectId == 0 || !roll.Conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) return;
        // Per kill: parse the snapshot only when Keep wrote a new one.
        var cache = Parsed.GetOrCreateValue(roll.Conn.Instance);
        if (cache.Last is not { } last || last.Text != saved)
            cache.Last = last = new(saved, JsonSerializer.Deserialize<List<Monolith>>(saved) ?? []);
        var monolith = last.List.FirstOrDefault(m => m.Monster == roll.Monster.ObjectId && m.Stage == 4);
        if (monolith is null || !roll.Conn.Instance.SetState($"Essence/rewards/{monolith.Index}")) return;
        foreach (int row in monolith.Essences)
            if (MapEssenceCatalog.Find(row) is { } essence) roll.Items.Add(essence.Item);
    }

    public static void Describe(Player conn, JsonObject state)
    {
        if (Areas.TryGetValue(conn.Instance, out var area) && area.Monoliths is not null)
            state["essences"] = JsonSerializer.SerializeToNode(area.Monoliths);
    }
    public static void Leave(Player conn) => Presented.Remove(conn);
}
