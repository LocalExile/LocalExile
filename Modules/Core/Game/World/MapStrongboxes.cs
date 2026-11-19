using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;
using static Game;
using static Program;

/// <summary>Instance-owned strongboxes: interaction starts their guards; rewards follow the last guard's death.</summary>
public static class MapStrongboxes
{
    public const string Content = "Ambush";
    public const uint CountStat = Stat.MapNumExtraStrongboxes;
    public const string Reopen = "Ambush.HiddenCompartments", Discernment = "Ambush.Discernment", Containment = "Ambush.Containment";
    const string SaveKey = "Ambush/boxes";

    public sealed record Box(uint Id, int Index, int X, int Y, string Type, int Phase = 0, int Cycle = 0)
    {
        public List<uint> Guards { get; init; } = [];
        public List<AreaPopulation.Spawn> Pending { get; init; } = [];
    }
    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Box>? Boxes;
    }
    static readonly Store<InstanceState, Area> Areas = new();
    static readonly Store<Player, HashSet<uint>> Presented = new();

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening
            || !StrongboxCatalog.All.Any(t => StrongboxCatalog.Weight(t, opening.Area.Level, LeagueFor(request.Conn).HardMode, opening.Mods) > 0)) return;
        request.HandledContent.UnionWith([Content, Reopen, Discernment, Containment]);
        MapScarabPacks.RollFragmentCount(request, CountStat, Random.Shared);
        if (opening.Mods.Get(CountStat) > 0)
            request.ContentWarnings.Add(("Ambush.Affixes",
                $"area={opening.Area.Id} content=Ambush: guard/reward lifecycle enabled; box rarity/affix rolling, affix actions and currency targeting remain unsupported; ordinary container reward budget is replica balance"));
    }

    public static IReadOnlyList<(int X, int Y)> Positions(AreaLayout layout, uint seed, int count)
    {
        if (count <= 0 || layout.Walkable is not { } walk) return [];
        var reach = AreaPopulation.Reachable(layout.GridW, layout.GridH, walk, (layout.SpawnX, layout.SpawnY));
        if (reach is null) return [];
        var random = new Random(unchecked((int)(seed ^ 0x414d4255)));
        var candidates = new List<(int X, int Y)>();
        for (int y = 3; y < layout.GridH - 3; y += 7)
            for (int x = 3; x < layout.GridW - 3; x += 7)
                if (reach[y * layout.GridW + x] && layout.WalkableFor(x, y, 3)
                    && MapSulphite.Distance((x, y), (layout.SpawnX, layout.SpawnY)) >= 60
                    && !layout.AreaTransitions.Any(t => MapSulphite.Distance((x, y), (t.Transition.GridX, t.Transition.GridY)) < 40))
                    candidates.Add((x, y));
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        var result = new List<(int X, int Y)>();
        foreach (var p in candidates)
        {
            if (result.Count >= count) break;
            if (result.All(other => MapSulphite.Distance(p, other) >= 30)) result.Add(p);
        }
        return result;
    }

    static void Keep(Player conn, Area area) => conn.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.Boxes);

    static IEnumerable<Player> Members(Player conn) => conn.Instance.PlayersExcept(conn.PeerId)
        .Select(p => p.Owner).OfType<Player>().Prepend(conn)
        .Where(p => !p.Closed && !p.LeavingArea && p.Instance == conn.Instance).Distinct();

    public static async Task Enter(Player conn)
    {
        if (MapModsOf(conn) is not { } mods || mods.Get(CountStat) <= 0) return;
        var area = Areas.GetOrCreateValue(conn.Instance);
        await area.Gate.WaitAsync();
        try
        {
            if (area.Boxes is null)
            {
                if (conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved))
                    area.Boxes = JsonSerializer.Deserialize<List<Box>>(saved) ?? [];
                else
                {
                    area.Boxes = [];
                    var random = new Random(unchecked((int)(conn.Seed ^ 0x53544258)));
                    int level = MapRunOf(conn)!.Area.Level;
                    foreach (var (x, y) in Positions(conn.Layout, conn.Seed, mods.Get(CountStat)))
                        if (StrongboxCatalog.Roll(level, LeagueFor(conn).HardMode, mods, random) is { } type)
                            area.Boxes.Add(new(conn.Instance.World.NewMonsterId(), area.Boxes.Count, x, y, type.Id));
                    Keep(conn, area);
                    Console.WriteLine($"[map-content] strongboxes area={conn.Layout.Id}: placed {area.Boxes.Count}/{mods.Get(CountStat)}");
                }
            }
            foreach (var box in area.Boxes) await Present(conn, box);
        }
        finally { area.Gate.Release(); }
    }

    static async Task Present(Player conn, Box box, bool replace = false)
    {
        var shown = Presented.GetOrCreateValue(conn);
        if (!replace && shown.Contains(box.Id)) return;
        if (StrongboxCatalog.Find(box.Type) is not { } type) return;
        if (shown.Contains(box.Id)) await SendWorldRemoval(conn, box.Id, null);
        await SendWorldCreate(conn, box.Id,
            InstanceEntities.CreateObject(box.Id, InstanceEntities.ObjectTypeHash(type.Id),
                StrongboxCatalog.Blob(type, box.X, box.Y, box.Phase > 0, box.Phase == 2)),
            $"strongbox 0x{box.Id:x} {type.Id} phase={box.Phase} cycle={box.Cycle} at ({box.X}, {box.Y})");
        await SendWorldRecord(conn, box.Id, InstanceProtocol.EnterWakeZone(box.Id), null);
        conn.SetBlocker(box.Id, box.X, box.Y, 1);
        shown.Add(box.Id);
    }

    public static async Task<bool> Use(Player conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area) || area.Boxes?.FirstOrDefault(b => b.Id == use.TargetId) is not { } box) return false;
        if (conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return true;
        if (box.Phase != 0)
        {
            int current = conn.ActionCount;
            await SendWorldRecord(conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
                still: () => PlayerActionCurrent(conn, current));
            return true;
        }
        if (!TryBeginObjectWalk(conn, requestedAt, box.X, box.Y, 1, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(conn, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, box.X, box.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: box.Id), null,
                still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        await area.Gate.WaitAsync();
        try
        {
            box = area.Boxes!.First(b => b.Id == use.TargetId);
            if (box.Phase != 0 || !PlayerActionCurrent(conn, action) || conn.PlayerLife == 0
                || !PlayerWalk.InWalkUpRange(conn.Walk.Position(AreaTime(conn)), (box.X, box.Y), 1)) return true;
            var guards = Guards(conn, box);
            if (guards.Count == 0)
            {
                Console.WriteLine($"[map-content] strongbox {box.Type}: no supported guards or free cells; remains closed");
                return true;
            }
            box = box with { Phase = 1, Pending = guards };
            area.Boxes[box.Index] = box;
            Keep(conn, area);
            foreach (var member in Members(conn)) await Present(member, box, true);
            await SpawnPending(conn, area, box);
        }
        finally
        {
            area.Gate.Release();
            await SendWorldRecord(conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
                still: () => PlayerActionCurrent(conn, action));
        }
        return true;
    }

    public static List<AreaPopulation.Spawn> Guards(Player conn, Box box)
    {
        if (Packs.For(conn.Layout.Id) is not { } packs || StrongboxCatalog.Find(box.Type) is not { } type) return [];
        var pool = packs.Scattered.Where(p => p.Pack.Entries.Count > 0
            && p.Pack.Entries.All(e => Monsters.Find(e.Variety.ToString()) is { Supported: true })).ToList();
        if (pool.Count == 0) return [];
        var values = ItemModStats.Sum([new Item(0, [], 0) { Mods = type.Mods }], Items.Mods);
        int count = values.GetValueOrDefault(Stat.NumberOfPacksToSummon, 3);
        var random = new Random(unchecked((int)conn.Seed ^ box.Index * 397 ^ box.Cycle * 7919));
        var plans = new List<(PackCatalog.Pack, int, int, MonsterRarity)>();
        for (int i = 0; i < count; i++)
        {
            int roll = random.Next(pool.Sum(p => p.Weight));
            var picked = pool[^1].Pack;
            foreach (var p in pool) if ((roll -= p.Weight) < 0) { picked = p.Pack; break; }
            double angle = i * Math.Tau / Math.Max(1, count);
            plans.Add((picked, box.X + (int)Math.Round(Math.Cos(angle) * 10), box.Y + (int)Math.Round(Math.Sin(angle) * 10), MonsterRarity.Normal));
        }
        var occupied = conn.Monsters.Values.Where(m => !m.Dead).Select(m => m.Walk.Position(AreaTime(conn))).Append((box.X, box.Y));
        return AreaPopulation.AtPacks(plans, (x, y) => conn.Layout.WalkableFor(x, y, 1),
            row => Monsters.Find(row.ToString()) is { Supported: true }, random.Next(), MapModsOf(conn)?.PackSizeFactor ?? 1, occupied);
    }

    public static bool ClosesAgain(int chance, Random random) => random.Next(100) < chance;

    static async Task SpawnPending(Player conn, Area area, Box box)
    {
        byte level = (byte)Math.Clamp(MapRunOf(conn)!.Area.Level, 1, 100);
        while (box.Pending.Count > 0 && !conn.Closed && !conn.LeavingArea)
        {
            var spawn = box.Pending[0];
            if (Monsters.Find(spawn.Variety.ToString()) is not { } variety) return;
            uint id = await SpawnMonster(conn, variety, level, spawn.Rarity, spawn.X, spawn.Y,
                unrisen: false, alone: false);
            if (id == 0) return;
            box.Guards.Add(id);
            box.Pending.RemoveAt(0);
            Keep(conn, area);
        }
    }

    public static async Task Tick(Player conn)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area) || area.Boxes is null || conn.Closed || conn.LeavingArea) return;
        await area.Gate.WaitAsync();
        try
        {
            foreach (var prior in area.Boxes.Where(b => b.Phase == 1).ToArray())
            {
                if (prior.Pending.Count > 0) await SpawnPending(conn, area, prior);
                if (prior.Pending.Count > 0 || prior.Guards.Count == 0) continue;
                lock (conn.Monsters)
                    if (prior.Guards.Any(id => conn.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0)) continue;
                if (!conn.Instance.SetState($"Ambush/rewards/{prior.Index}/{prior.Cycle}")) continue;
                var opened = prior with { Phase = 2 };
                area.Boxes[prior.Index] = opened;
                Keep(conn, area);
                foreach (var member in Members(conn)) await Present(member, opened, true);
                await Rewards(conn, opened);
                if (ClosesAgain(MapModsOf(conn)?.Get(Stat.MapStrongboxCloseAfterOpeningChancePct) ?? 0, AreaRng(conn)))
                {
                    var closed = opened with { Phase = 0, Cycle = opened.Cycle + 1, Guards = [] };
                    area.Boxes[prior.Index] = closed;
                    Keep(conn, area);
                    foreach (var member in Members(conn)) await Present(member, closed, true);
                }
            }
        }
        finally { area.Gate.Release(); }
    }

    static readonly HashSet<string> Armour = ["Body Armour", "Helmet", "Gloves", "Boots", "Shield"];
    public static bool Accepts(string type, ItemCatalog.BaseType item) => type.Split('/')[^1] switch
    {
        "Arcanist" => item.Class == "StackableCurrency",
        "Artisan" => item.Id is "Metadata/Items/Currency/CurrencyArmourQuality" or "Metadata/Items/Currency/CurrencyWeaponQuality"
            or "Metadata/Items/Currency/CurrencyFlaskQuality" or "Metadata/Items/Currency/CurrencyMapQuality" or "Metadata/Items/Currency/CurrencyGemQuality",
        "Armory" => Armour.Contains(item.Class),
        "Arsenal" => ItemDrops.EquipmentClasses.Contains(item.Class) && !Armour.Contains(item.Class) && item.Class is not ("Ring" or "Amulet" or "Belt"),
        "Gemcutter" => item.Class.Contains("Gem", StringComparison.Ordinal),
        "Jeweller" => item.Class is "Ring" or "Amulet" or "Belt",
        _ => true,
    };

    static async Task Rewards(Player conn, Box box)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(conn)) return;
        int level = Math.Clamp(MapRunOf(conn)!.Area.Level, 1, 100);
        var drops = DropsFor(conn);
        // The replica's ordinary container budget; the official server's quantity distribution is not in client tables.
        int count = box.Type.EndsWith("Large", StringComparison.Ordinal) || box.Type.EndsWith("Ornate", StringComparison.Ordinal) ? 10 : 5;
        var result = new List<ItemDrops.Drop>();
        for (int i = 0; i < count; i++)
        {
            ItemDrops.Drop? item = box.Type.Contains("Divination", StringComparison.Ordinal) ? drops.Card(AreaRng(conn), level)
                : box.Type.Contains("Scarab", StringComparison.Ordinal) ? drops.Scarab(AreaRng(conn), level) : null;
            if (item is not null) result.Add(item);
        }
        if (box.Type.Contains("Cartographer", StringComparison.Ordinal))
            for (int i = 0; result.Count < count && i < count * 64; i++)
                if (drops.Map(level, MonsterRarity.Unique, MapRunOf(conn)!.Area.Tier, AreaRng(conn)) is { } map) result.Add(map);
        if (result.Count == 0 && !box.Type.Contains("Cartographer", StringComparison.Ordinal)
            && !box.Type.Contains("Divination", StringComparison.Ordinal) && !box.Type.Contains("Scarab", StringComparison.Ordinal))
            result.AddRange(drops.Container(level, count, 0, AreaRng(conn), item => Accepts(box.Type, item)));
        foreach (var drop in ItemDrops.Stacked(result))
        {
            var at = conn.Layout.Near((box.X + AreaRng(conn).Next(-5, 6), box.Y + AreaRng(conn).Next(-5, 6)));
            var ground = new WorldItemObject(at.X, at.Y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground, conn);
            await SendGroundItem(conn, id, ground, $"strongbox 0x{box.Id:x} drops {drop.Type.Name}", justDropped: true);
        }
    }

    public static void Describe(Player conn, JsonObject state)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area) || area.Boxes is null) return;
        state["strongboxes"] = JsonSerializer.SerializeToNode(area.Boxes);
    }

    public static void Leave(Player conn) => Presented.Remove(conn);
}
