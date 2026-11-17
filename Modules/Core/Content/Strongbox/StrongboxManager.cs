using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

namespace GameServer.Modules.Strongbox;

/// <summary>
/// The strongboxes of an area instance: planned once by its first connection (count, types, cells; kept in the
/// instance's ContentState, so a re-entry, a party member and a module reload see the same boxes in the same state),
/// created for each connection as it enters, opened by a walk-up click (C→S 0x24) or a warcry that opens chests.
/// An opened box is created again under a new id with its Chest opened and its StateMachine <c>opened</c> set (Chest has
/// no update reader, as for the quest chests), and the box's ambush and loot follow.
/// </summary>
public static class StrongboxManager
{
    const string PlanKey = "Strongbox/plan";

    /// <summary>A planned box: its Strongboxes row, cell, facing, current object id and whether it was opened.</summary>
    sealed record Box(int Row, int X, int Y, float Rotation, uint Id, bool Opened, byte Rarity = 0, RolledMod[]? Mods = null)
    {
        public IReadOnlyList<ItemMod> Explicits => [.. (Mods ?? []).Select(m => new ItemMod(ModSection.Explicit, m.Hash, m.Values))];
    }

    /// <summary>A rolled mod as the plan keeps it: its Mods.dat HASH16 and its values.</summary>
    sealed record RolledMod(ushort Hash, int[] Values);

    static RolledMod[] Kept(IEnumerable<ItemMod> mods) => [.. mods.Select(m => new RolledMod(m.Hash, [.. m.Values]))];

    static Func<string, int> StatOf(IReadOnlyDictionary<uint, int> stats) =>
        name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    sealed class SharedArea
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<Box>? Boxes;
    }

    static readonly ConditionalWeakTable<InstanceState, SharedArea> Areas = new();
    // The box ids each connection was sent, by the box's index in the plan: who gets an opened box's replacement.
    static readonly ConditionalWeakTable<ClientState, Dictionary<int, uint>> Presented = new();

    public static IDisposable Lifetime() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose() { Areas.Clear(); Presented.Clear(); }
    }

    static int AreaLevel(ClientState conn) =>
        Math.Clamp(MapRunOf(conn) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);

    static IReadOnlyDictionary<uint, int> AreaStats(ClientState conn) => conn.Instance?.KeptAreaStats ?? new Dictionary<uint, int>();

    /// <summary>GameEvents.AreaGenerated: the instance's boxes (planned by its first connection), created for this one.</summary>
    public static async Task SpawnArea(ClientState conn)
    {
        if (conn.Instance is not { } instance || InTown(conn) || conn.Layout.Walkable is null) return;
        var area = Areas.GetOrCreateValue(instance);
        await area.Gate.WaitAsync();
        try
        {
            var boxes = area.Boxes ??= Load(instance) ?? Plan(conn, instance);
            for (int i = 0; i < boxes.Count; i++) await Present(conn, i, boxes[i]);
        }
        finally { area.Gate.Release(); }
    }

    static List<Box>? Load(InstanceState instance) =>
        instance.ContentState.TryGetValue(PlanKey, out string? json) ? JsonSerializer.Deserialize<List<Box>>(json) : null;

    static void Save(InstanceState instance, List<Box> boxes) => instance.ContentState[PlanKey] = JsonSerializer.Serialize(boxes);

    static List<Box> Plan(ClientState conn, InstanceState instance)
    {
        var boxes = new List<Box>();
        int level = AreaLevel(conn);
        var stats = AreaStats(conn);
        var (count, roll) = StrongboxRules.Count(conn.Layout.Id, level, conn.Seed, stats);
        var rng = new Random(unchecked((int)StrongboxRules.Seed(conn.Seed, "boxes")));
        var spots = StrongboxRules.Spots(conn.Layout, count, rng);
        bool hardMode = LeagueFor(conn).HardMode;
        foreach (var (x, y) in spots)
        {
            if (StrongboxRules.Pick(StrongboxCatalog.All, hardMode, level, stats, rng) is not { } type) break;
            byte rarity = StrongboxMods.RollRarity(StatOf(stats), rng);
            var mods = StrongboxMods.Roll(ItemsFor(conn).Mods, type, rarity, level, StatOf(stats), rng);
            boxes.Add(new Box(type.Row, x, y, (float)(rng.NextDouble() * Math.Tau), conn.TakeObjectId(), false, rarity, Kept(mods)));
        }
        Console.WriteLine($"[gateway] {conn.Wire.Remote} strongboxes: {conn.Layout.Id} seed {conn.Seed} level {level}" +
            $"{(hardMode ? " (HardMode weights)" : "")}: Ambush roll {roll} → {count}; {spots.Count} spot(s), {boxes.Count} box(es)" +
            string.Concat(boxes.Select(b => $"; {Describe(conn, b)} 0x{b.Id:x} at ({b.X}, {b.Y})")));
        Save(instance, boxes);
        return boxes;
    }

    /// <summary>Creates the box for this connection unless it has it already, and blocks its cell on the server's walk
    /// estimate like the client's (Chest.ot <c>blocking</c>).</summary>
    static async Task Present(ClientState conn, int index, Box box)
    {
        if (StrongboxCatalog.ByRow(box.Row) is not { } type) return;
        var shown = Presented.GetOrCreateValue(conn);
        lock (shown)
        {
            if (shown.TryGetValue(index, out uint id) && id == box.Id) return;
            shown[index] = box.Id;
        }
        var (remote, stream, ciphers) = conn.Wire;
        conn.SetBlocker(box.Id, box.X, box.Y, type.Size);
        await SendWorldCreate(remote, stream, ciphers, conn, box.Id,
            InstanceEntities.CreateObject(box.Id, InstanceEntities.ObjectTypeHash(type.Type), type.Blob(box.X, box.Y, box.Rotation, box.Opened, box.Rarity, box.Explicits)),
            $"0x02bb strongbox 0x{box.Id:x} {Describe(conn, box)} at ({box.X}, {box.Y}), opened={box.Opened}");
        await SendWorldRecord(remote, stream, ciphers, conn, box.Id, InstanceProtocol.EnterWakeZone(box.Id), null);
    }

    static readonly string[] RarityNames = ["Normal", "Magic", "Rare", "Unique"];

    /// <summary>The box for the log: its rarity, name, Strongboxes row and mods.</summary>
    static string Describe(ClientState conn, Box box)
    {
        var catalog = ItemsFor(conn).Mods;
        string mods = string.Join(", ", (box.Mods ?? []).Select(m => catalog.ByHash(m.Hash) is { } mod
            ? $"{mod.Id}[{string.Join("/", m.Values)}]" : $"0x{m.Hash:x4}"));
        return $"{RarityNames[Math.Min(box.Rarity, (byte)3)]} {StrongboxCatalog.ByRow(box.Row)?.Name} (row {box.Row})" + (mods.Length > 0 ? $" {{{mods}}}" : "");
    }

    /// <summary>The box this object id is in the instance's plan, with its index.</summary>
    static async Task<(int Index, Box Box)?> Find(ClientState conn, uint id)
    {
        if (conn.Instance is not { } instance) return null;
        var area = Areas.GetOrCreateValue(instance);
        await area.Gate.WaitAsync();
        try
        {
            var boxes = area.Boxes ??= Load(instance);
            if (boxes is null) return null;
            int index = boxes.FindIndex(b => b.Id == id);
            return index < 0 ? null : (index, boxes[index]);
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>GameEvents.ObjectUse: a click on a box. The player walks up (the quest chests' walk-up, 0x01ab started and
    /// ended), and on arrival the box opens, once per instance.</summary>
    public static async Task<bool> Use(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (await Find(conn, use.TargetId) is not var (index, box) || StrongboxCatalog.ByRow(box.Row) is not { } type) return false;
        if (box.Opened || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return true;
        if (!TryBeginObjectWalk(conn, requestedAt, box.X, box.Y, type.Size, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        var (remote, stream, ciphers) = conn.Wire;
        if (!use.Started)   // a lockstep client got its start from the frame pass
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, box.X, box.Y,
                    use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player opens strongbox 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
                still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return true;
        if (PlayerWalk.InReach(stop, (box.X, box.Y), type.Size, conn.Layout.Walkable)) await Open(conn, index, "walked up");
        else Console.WriteLine($"[gateway] {remote} strongbox 0x{box.Id:x}: out of reach from ({stop.X}, {stop.Y})");
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
        return true;
    }

    /// <summary>GameEvents.ChestsOpened: a warcry that opens nearby chests opens the boxes in its radius.</summary>
    public static async Task OpenNear(ClientState conn, (int X, int Y) at, int radius)
    {
        if (conn.Instance is not { } instance) return;
        var area = Areas.GetOrCreateValue(instance);
        List<int> near;
        await area.Gate.WaitAsync();
        try
        {
            if ((area.Boxes ??= Load(instance)) is not { } boxes) return;
            near =[.. Enumerable.Range(0, boxes.Count).Where(i => !boxes[i].Opened
                && (long)(boxes[i].X - at.X) * (boxes[i].X - at.X) + (long)(boxes[i].Y - at.Y) * (boxes[i].Y - at.Y) <= (long)radius * radius)];
        }
        finally { area.Gate.Release(); }
        foreach (int index in near) await Open(conn, index, "warcry");
    }

    /// <summary>Opens the box (the first opener only): its replacement for every connection that has it, then its ambush
    /// and its loot, rolled and dropped for the opener.</summary>
    static async Task Open(ClientState conn, int index, string how)
    {
        if (conn.Instance is not { } instance) return;
        var area = Areas.GetOrCreateValue(instance);
        Box opened;
        StrongboxCatalog.Entry type;
        bool first;
        await area.Gate.WaitAsync();
        try
        {
            if (area.Boxes is not { } boxes || index >= boxes.Count || boxes[index].Opened
                || StrongboxCatalog.ByRow(boxes[index].Row) is not { } found) return;
            type = found;
            var closed = boxes[index];
            first = !boxes.Any(b => b.Opened);
            opened = closed with { Opened = true, Id = conn.TakeObjectId() };
            boxes[index] = opened;
            Save(instance, boxes);
            Console.WriteLine($"[gateway] {conn.Wire.Remote} strongbox 0x{closed.Id:x} {type.Name} at ({closed.X}, {closed.Y}) opened ({how}) by " +
                $"{CharacterName(conn)}: now 0x{opened.Id:x}");
            foreach (var member in Program.InGame.Keys.Where(c => ReferenceEquals(c.Instance, instance) && !c.Closed && !c.LeavingArea))
            {
                if (!Presented.TryGetValue(member, out var shown)) continue;
                uint old;
                lock (shown) if (!shown.TryGetValue(index, out old) || old != closed.Id) continue;
                member.RemoveBlocker(old);
                var (remote, stream, ciphers) = member.Wire;
                await SendWorldRemoval(remote, stream, ciphers, member, old, null);
                await Present(member, index, opened);
            }
        }
        finally { area.Gate.Release(); }
        var stats = AreaStats(conn);
        int level = AreaLevel(conn);
        var rng = new Random();
        await Ambush(conn, type, opened, level, stats, first, rng);
        await DropLoot(conn, type, opened, level, stats, rng);
    }

    /// <summary>The box's guards: what its mods summon (each mod with <c>summoned_monster_rarity</c>: its
    /// <c>number_of_packs_to_summon</c> packs and <c>number_of_monsters_to_summon</c> single monsters at that rarity; the
    /// implicit ChestSummonNormals is 3 normal packs), one more normal pack with <c>map_strongboxes_additional_pack_chance_%</c>,
    /// and on the instance's first box <c>map_first_strongbox_contains_x_additional_rare_monsters</c> rares. A pack is one of
    /// the area's own MonsterPacks by weight at its count; a single monster a variety of one of them. They stand around the
    /// box and come for the player, with the area's <c>map_strongbox_monsters_*</c> life, damage, speeds, size and item
    /// quantity.</summary>
    static async Task<List<uint>> Ambush(ClientState conn, StrongboxCatalog.Entry box, Box state, int level,
        IReadOnlyDictionary<uint, int> stats, bool first, Random rng)
    {
        var (remote, stream, ciphers) = conn.Wire;
        var guards = new List<uint>();
        var packs = (Packs.For(conn.Layout.Id)?.Scattered ?? [])
            .Where(p => p.Pack.Entries.Any(e => Spawnable(e.Variety) is not null)).ToList();
        if (packs.Count == 0)
        {
            Console.WriteLine($"[gateway] {remote} strongbox {box.Name}: no monster packs for {conn.Layout.Id}; no ambush");
            return guards;
        }
        var stat = StatOf(stats);
        var groups = new List<(int Packs, int Monsters, MonsterRarity Rarity, string Why)>();
        foreach (var line in StrongboxMods.Lines(ItemsFor(conn).Mods, box, state.Explicits))
            if (line.Mod.Stats.Any(s => s.Id == "summoned_monster_rarity"))
                groups.Add((StrongboxMods.Value(line, "number_of_packs_to_summon"), StrongboxMods.Value(line, "number_of_monsters_to_summon"),
                    (MonsterRarity)Math.Clamp(StrongboxMods.Value(line, "summoned_monster_rarity"), 0, 2), line.Mod.Id));
        if (groups.Count == 0) groups.Add((1, 0, MonsterRarity.Normal, "no summoning mod"));
        int extra = stat("map_strongboxes_additional_pack_chance_%");
        if (extra > 0 && rng.Next(100) < extra) groups.Add((1, 0, MonsterRarity.Normal, "additional pack chance"));
        if (first && stat("map_first_strongbox_contains_x_additional_rare_monsters") is int firstRares and > 0)
            groups.Add((0, firstRares, MonsterRarity.Rare, "first strongbox"));
        int Both(string name) => stat("map_" + name) + stat(name);
        var effects = new Dictionary<string, int>
        {
            ["maximum_life_+%"] = Both("strongbox_monsters_life_+%"), ["damage_+%"] = Both("strongbox_monsters_damage_+%"),
            ["attack_speed_+%"] = Both("strongbox_monsters_attack_speed_+%"),
            ["base_movement_velocity_+%"] = Both("strongbox_monsters_movement_velocity_+%"),
            ["base_actor_scale_+%"] = Both("strongbox_monsters_actor_scale_+%"),
        }.Where(e => e.Value != 0).ToDictionary(e => e.Key, e => e.Value);
        var extraMods = effects.Count == 0 ? null : new MonsterModSet([], [], new MonsterModEffects(effects));
        int quantity = Both("strongbox_monsters_item_quantity_+%");
        var made = new List<string>();
        int spot = 0;
        foreach (var (packCount, monsters, rarity, why) in groups)
        {
            for (int p = 0; p < packCount; p++)
            {
                var pack = PickPack();
                int count = rng.Next(Math.Max(1, pack.MinCount), Math.Max(pack.MinCount, pack.MaxCount) + 1);
                int spawned = 0;
                for (int i = 0; i < count; i++) if (await Spawn(pack, rarity)) spawned++;
                made.Add($"{pack.Id} × {spawned} {rarity} ({why})");
            }
            int singles = 0;
            for (int i = 0; i < monsters; i++) if (await Spawn(PickPack(), rarity)) singles++;
            if (monsters > 0) made.Add($"{singles} {rarity} monster(s) ({why})");
        }
        Console.WriteLine($"[gateway] {remote} strongbox {box.Name} ambush, level {level}: {string.Join(", ", made)}" +
            (effects.Count > 0 || quantity != 0 ? $"; monsters {string.Join(", ", effects.Select(e => $"{e.Key} {e.Value}"))}" +
                (quantity != 0 ? $" item quantity {quantity}%" : "") : ""));
        return guards;

        PackCatalog.Pack PickPack()
        {
            int roll = rng.Next(Math.Max(1, packs.Sum(x => x.Weight)));
            return packs.FirstOrDefault(x => (roll -= x.Weight) < 0).Pack ?? packs[^1].Pack;
        }

        async Task<bool> Spawn(PackCatalog.Pack pack, MonsterRarity rarity)
        {
            if (Spawnable(pack.PickVariety(rng)) is not { } variety) return false;
            // A ring around the box, each guard a little further round it.
            double angle = spot++ * 2.399963229728653, radius = 5 + rng.NextDouble() * 5;
            int x = state.X + (int)Math.Round(Math.Cos(angle) * radius), y = state.Y + (int)Math.Round(Math.Sin(angle) * radius);
            uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, (byte)level, rarity, x, y,
                alone: false, log: false, extraMods: extraMods, owned: (_, monster) =>
                {
                    monster.Aggro = true;
                    // Item quantity of the guards' own drops, as the Sulphite enrage's (UtilityFlasks.ItemQuantityFactor).
                    if (quantity != 0) monster.EncounterStats = new Dictionary<string, int> { [MapSulphite.EnragedQuantity] = quantity * 100 };
                });
            if (id == 0) return false;
            guards.Add(id);
            return true;
        }

        static MonsterCatalog.Variety? Spawnable(int row) =>
            Monsters.Find(row.ToString()) is { Supported: true } v && Emerges.Kind(v) == EmergeKind.None ? v : null;
    }

    /// <summary>The box's items (<see cref="StrongboxLoot"/>) on the ground around it, as a monster's drops lie.</summary>
    static async Task DropLoot(ClientState conn, StrongboxCatalog.Entry box, Box state, int level,
        IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(conn)) return;
        var (remote, stream, ciphers) = conn.Wire;
        var catalog = ItemsFor(conn).Mods;
        var mods = state.Explicits;
        var items = ItemDrops.Stacked(StrongboxLoot.Roll(conn, box, id => StrongboxMods.Stat(catalog, box, mods, id), level, stats, rng));
        foreach (var drop in items)
        {
            double angle = rng.NextDouble() * Math.Tau, r = 2 + rng.NextDouble() * 3;
            int x = state.X + (int)Math.Round(Math.Cos(angle) * r), y = state.Y + (int)Math.Round(Math.Sin(angle) * r);
            if (conn.Layout.Walkable is { } walk && !walk(x, y)) (x, y) = conn.Layout.Near((x, y));
            var ground = new WorldItemObject(x, y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground, conn);
            await SendGroundItem(remote, stream, ciphers, conn, id, ground,
                $"0x02bb create world item 0x{id:x} {drop.Type.Name} (item level {drop.Item.ItemLevel}) from strongbox {box.Name} at ({x}, {y})",
                justDropped: true);
        }
        Console.WriteLine($"[gateway] {remote} strongbox {box.Name} ({box.Kind}, {box.Items} base items) dropped {items.Count} item(s): " +
            string.Join(", ", items.Select(d => d.Item.StackSize > 1 ? $"{d.Type.Name} ×{d.Item.StackSize}" : d.Type.Name)));
    }
}
