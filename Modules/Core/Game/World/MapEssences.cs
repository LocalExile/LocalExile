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
        // Each monolith as the area's clients were last shown it.
        public readonly Dictionary<uint, Monolith> Shown = [];
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

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player)
        .Where(c => !c.Closed && !c.LeavingArea && c.Instance == player.Instance).Distinct();
    static void Keep(Player player, Area area) => player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Monoliths);

    public static int Count(Player player)
    {
        if (MapRunOf(player) is not { } map || player.Instance.KeptAreaStats is not { } stats) return 0;
        // Loading the WorldAreas row costs ~3 ms on every new map instance; the row does not change.
        var area = AreaInfos.GetOrAdd((map.Area.Id, map.Area.Level), k => AreaInfo.Load(GamePack.Default.TryRead, k.Area, k.Level)) with
        { Stats = stats.ToDictionary(s => (int)s.Key, s => s.Value) };
        return ExtraTerrainFeatures.LeagueCount(area, new LevelRandom(player.Seed ^ 0x45535345, false), 6);
    }

    /// <summary>Replaces a planned natural rare before it exists; it earns no kill rewards until actually released and killed.</summary>
    public static async Task<bool> Imprison(Player player, AreaPopulation.Spawn spawn)
    {
        if (spawn.Rarity != MonsterRarity.Rare || MapModsOf(player) is not { } mods
            || mods.Get(Stat.MapRareMonstersTrappedInEssences) <= 0
            || Packs.For(player.Layout.Id)?.Scattered.Any(p => p.Pack.Id == spawn.Pack) != true
            || MapScarabPacks.Packs.Any(p => p.Id == spawn.Pack)
            || Monsters.Find(spawn.Variety.ToString()) is not { Supported: true } variety) return false;
        await Enter(player);
        var area = Areas.GetValue(player.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.Monoliths is not { } monoliths) return false;
            string source = $"{spawn.Pack}/{spawn.Variety}/{spawn.X}/{spawn.Y}";
            if (monoliths.Any(m => m.Source == source)) return true;
            var essences = MapEssenceCatalog.Roll(MapRunOf(player)!.Area.Level, mods,
                new Random(unchecked((int)player.Seed ^ spawn.X * 397 ^ spawn.Y)));
            if (essences.Count == 0) return false;
            bool Floor(int x, int y) => player.Layout.WalkableFor(x, y, 4);
            var blockers = player.MonsterPlacementBlockers(0);
            (int X, int Y)? at = CollisionEscape.Clear((spawn.X, spawn.Y), player.Layout.GridW, player.Layout.GridH, Floor, blockers, 4)
                ? (spawn.X, spawn.Y) : CollisionEscape.Find((spawn.X, spawn.Y), player.Layout.GridW, player.Layout.GridH, Floor, blockers, moverSize: 4);
            if (at is null)
            {
                Console.WriteLine($"[map-content] Calcification area={player.Layout.Id}: no clear monolith cell for natural rare {source}; retaining the rare");
                return false;
            }
            var monolith = new Monolith(player.Instance.World.NewMonsterId(), monoliths.Count, at.Value.X, at.Value.Y,
                variety.Id, essences) { Source = source };
            monoliths.Add(monolith);
            Keep(player, area);
            foreach (var member in Members(player)) await Present(member, monolith);
            return true;
        }
        finally { area.Gate.Release(); }
    }

    public static async Task Enter(Player player)
    {
        if (MapRunOf(player) is not { } map || MapModsOf(player) is not { } mods) return;
        var area = Areas.GetValue(player.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.Monoliths is null)
            {
                if (player.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    area.Monoliths = JsonSerializer.Deserialize<List<Monolith>>(saved) ?? [];
                else
                {
                    area.Monoliths = [];
                    int count = Count(player);
                    var pool = Packs.For(player.Layout.Id)?.Scattered.SelectMany(p => p.Pack.Entries)
                        .Where(e => Monsters.Find(e.Variety.ToString()) is { Supported: true }).ToArray() ?? [];
                    var random = new Random(unchecked((int)(player.Seed ^ 0x45535345)));
                    if (pool.Length > 0)
                        foreach (var (x, y) in MapStrongboxes.Positions(player.Layout, player.Seed ^ 0x45535345, count))
                        {
                            var essences = MapEssenceCatalog.Roll(map.Area.Level, mods, random);
                            if (essences.Count == 0) break;
                            int roll = random.Next(pool.Sum(e => e.Weight));
                            int variety = pool[^1].Variety;
                            foreach (var e in pool) if ((roll -= e.Weight) < 0) { variety = e.Variety; break; }
                            area.Monoliths.Add(new(player.Instance.World.NewMonsterId(), area.Monoliths.Count, x, y,
                                Monsters.Find(variety.ToString())!.Id, essences));
                        }
                    Keep(player, area);
                    if (count > 0) Console.WriteLine($"[map-content] essences area={player.Layout.Id}: placed {area.Monoliths.Count}/{count}");
                }
            }
            foreach (var monolith in area.Monoliths) await Present(player, monolith);
        }
        finally { area.Gate.Release(); }
    }

    static async Task Present(Player player, Monolith monolith, bool replace = false)
    {
        var shown = Presented.GetValue(player, _ => []);
        if (!replace && shown.Contains(monolith.Id)) return;
        if (Monsters.Find(monolith.Variety) is not { } variety) return;
        // The monolith is the area's: created once, and each stage once, for every client.
        var area = Areas.GetOrCreateValue(player.Instance);
        if (!area.Shown.TryGetValue(monolith.Id, out var was))
        {
            var obj = new MonolithObject(monolith.X, monolith.Y, variety.Hash16,
                [.. monolith.Essences.Select(MapEssenceCatalog.Find).OfType<MapEssenceCatalog.Essence>().Select(e => e.Hash)], monolith.Stage);
            await SendWorldCreate(player, monolith.Id,
                InstanceEntities.CreateObject(monolith.Id, InstanceEntities.ObjectTypeHash(Type), obj.Blob()),
                $"essence monolith 0x{monolith.Id:x} stage={monolith.Stage} essences={monolith.Essences.Count} at ({monolith.X}, {monolith.Y})");
            await SendWorldRecord(player, monolith.Id, InstanceProtocol.EnterWakeZone(monolith.Id), null);
            area.Shown[monolith.Id] = monolith;
        }
        else if (!ReferenceEquals(was, monolith))
        {
            await SendWorldRecord(player, monolith.Id, InstanceEntities.TransitionableUpdate(monolith.Id, 4, monolith.Stage), null);
            await SendWorldRecord(player, monolith.Id,
                InstanceEntities.StateMachineUpdate(monolith.Id, 6, MonolithObject.States(monolith.Stage)), null);
            area.Shown[monolith.Id] = monolith;
        }
        // It blocks each player's own walk until it is opened.
        if (monolith.Stage < 4) player.SetBlocker(monolith.Id, monolith.X, monolith.Y, 4);
        else player.RemoveBlocker(monolith.Id);
        shown.Add(monolith.Id);
    }

    public static async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!Areas.TryGetValue(player.Instance, out var area) || area.Monoliths?.FirstOrDefault(m => m.Id == use.TargetId) is not { } monolith) return false;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        if (monolith.Stage >= 4)
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
            return true;
        }
        // The native .ot overrides its interaction distance to twenty cells.
        if (!TryBeginObjectWalk(player, requestedAt, monolith.X, monolith.Y, 20, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, monolith.X, monolith.Y, use.SkillId, use.Sequence, use.Flags,
                    targetObjectId: monolith.Id), null, still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await area.Gate.WaitAsync();
        try
        {
            monolith = area.Monoliths!.First(m => m.Id == use.TargetId);
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || monolith.Stage >= 4
                || !PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), (monolith.X, monolith.Y), 20)) return true;
            if (monolith.Stage == 3)
            {
                if (Monsters.Find(monolith.Variety) is not { Supported: true } variety) return true;
                byte level = (byte)Math.Clamp(MapRunOf(player)!.Area.Level, 1, 100);
                var mods = MapEssenceCatalog.Mods(player, variety, level, monolith.Essences);
                uint monster = await SpawnMonster(player, variety, level, MonsterRarity.Rare,
                    monolith.X, monolith.Y, mods: mods, unrisen: false, alone: false,
                    owned: (id, _) =>
                    {
                        area.Monoliths[monolith.Index] = monolith with { Stage = 4, Monster = id };
                        Keep(player, area);
                    });
                if (monster == 0) return true;
                monolith = monolith with { Monster = monster };
            }
            monolith = monolith with { Stage = (byte)(monolith.Stage + 1) };
            area.Monoliths[monolith.Index] = monolith;
            Keep(player, area);
            foreach (var member in Members(player)) await Present(member, monolith, true);
        }
        finally
        {
            area.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    public static void Drops(DropRoll roll)
    {
        if (roll.Monster.ObjectId == 0 || !roll.Player.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) return;
        // Per kill: parse the snapshot only when Keep wrote a new one.
        var cache = Parsed.GetOrCreateValue(roll.Player.Instance);
        if (cache.Last is not { } last || last.Text != saved)
            cache.Last = last = new(saved, JsonSerializer.Deserialize<List<Monolith>>(saved) ?? []);
        var monolith = last.List.FirstOrDefault(m => m.Monster == roll.Monster.ObjectId && m.Stage == 4);
        if (monolith is null || !roll.Player.Instance.SetState($"Essence/rewards/{monolith.Index}")) return;
        foreach (int row in monolith.Essences)
            if (MapEssenceCatalog.Find(row) is { } essence) roll.Items.Add(essence.Item);
    }

    public static void Describe(Player player, JsonObject state)
    {
        if (Areas.TryGetValue(player.Instance, out var area) && area.Monoliths is not null)
            state["essences"] = JsonSerializer.SerializeToNode(area.Monoliths);
    }
    public static void Leave(Player player) => Presented.Remove(player);
}
