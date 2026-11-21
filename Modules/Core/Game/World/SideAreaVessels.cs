using System.Globalization;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

public static class SideAreaVessels
{
    const string Type = "Metadata/Chests/SideArea/SideAreaChest";
    const string VesselKey = "vaal-side-vessels/id/";
    const string GuardCount = "vaal-side-vessels/guard-count";
    const string GuardKey = "vaal-side-vessels/guard/";
    const string Defeated = "vaal-side-vessels/defeated/";

    sealed record Vessel(uint Id, GlyphQuest.Placement At);
    sealed class Area(Vessel[] vessels)
    {
        public readonly Vessel[] Vessels = vessels;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }
    sealed record Chest(IReadOnlyList<string> Components, string Model, ItemMod[] Implicits);

    // Each of the 109 IsVaalArea rows in 3.29.3c names one boss variety.
    static readonly Lazy<Dictionary<string, int>> Bosses = new(() =>
        ClientTables.Table("WorldAreas").Where(r => r.Bool("IsVaalArea"))
            .ToDictionary(r => r.Text("Id")!, r => r.Ints("Bosses_MonsterVarietiesKeys").Single(), StringComparer.Ordinal));
    static readonly Lazy<Chest> Native = new(() =>
    {
        var chest = ClientTables.Table("Chests").Single(r => r.Text("Id") == Type);
        var mods = chest.Ints("ModsKeys").Select(row => Program.Items.Mods.Row(row)!.Roll(ModSection.Implicit, Random.Shared));
        return new(QuestObjectTypes.Find(Type)!.Components, chest.Texts("AOFiles").Single(), [.. mods]);
    });
    static readonly RuleTable<InstanceState, Area> Areas = new();
    static readonly RuleTable<ClientState, InstanceState> Entered = new();

    internal static AreaPopulation.Spawn[] PlanGuards(ClientState conn, IReadOnlyList<AreaPopulation.Spawn> spawns)
    {
        if (!Bosses.Value.TryGetValue(conn.Layout.Id, out int row)) return [];
        var guards = spawns.Where(s => s.Variety == row && s.Rarity == MonsterRarity.Unique).ToArray();
        conn.Instance.ContentState[GuardCount] = guards.Length.ToString(CultureInfo.InvariantCulture);
        return guards;
    }

    internal static void PlaceGuard(InstanceState instance, int slot, uint id) =>
        instance.ContentState[GuardKey + id] = slot.ToString(CultureInfo.InvariantCulture);

    // Each naturally planned boss, including a twin, owns a slot. Its lethal mutation marks
    // that slot before corpse consumption or a change of runner.
    internal static void Died(ClientState conn, SpawnedMonster monster)
    {
        if (monster.Rarity == MonsterRarity.Unique && monster.ObjectId != 0 && conn.Instance is { } instance
            && instance.ContentState.TryGetValue(GuardKey + monster.ObjectId, out string? slot))
            instance.SetState(Defeated + slot);
    }

    static bool AllGuardsDead(InstanceState instance)
    {
        if (!instance.ContentState.TryGetValue(GuardCount, out string? count)) return false;
        int guards = int.Parse(count, CultureInfo.InvariantCulture);
        return guards > 0 && Enumerable.Range(0, guards).All(i => instance.HasState(Defeated + i));
    }

    static Area In(ClientState conn) => Areas.GetValue(conn.Instance, instance =>
    {
        var placements = new QuestObjectTypes.AreaPlacements(conn.Layout.Id, conn.Seed, []).Of(Type);
        var vessels = new List<Vessel>(placements.Count);
        foreach (var at in placements)
        {
            string key = $"{VesselKey}{at.X}/{at.Y}";
            string id = instance.ContentState.GetOrAdd(key, _ => instance.World.TakeObjectId().ToString(CultureInfo.InvariantCulture));
            vessels.Add(new(uint.Parse(id, CultureInfo.InvariantCulture), at));
        }
        return new([.. vessels]);
    });

    static IEnumerable<ClientState> Members(ClientState conn) => conn.Instance.PlayersExcept(conn.PeerId)
        .Select(p => p.Owner).OfType<ClientState>().Prepend(conn).Distinct()
        .Where(c => c.Instance == conn.Instance && !c.Closed && !c.LeavingArea);

    public static async Task Enter(ClientState conn)
    {
        if (!Bosses.Value.ContainsKey(conn.Layout.Id)) return;
        Entered.AddOrUpdate(conn, conn.Instance);
        // A kept area retains the contents it was populated with. A guest can finish entering a
        // fresh instance while its runner is still planning the packs; the runner presents to both.
        if (!conn.Instance.ContentState.ContainsKey(GuardCount)) return;
        var area = In(conn);
        await area.Gate.WaitAsync();
        try
        {
            foreach (var member in Members(conn))
                if (Entered.TryGetValue(member, out var entered) && entered == conn.Instance)
                    foreach (var vessel in area.Vessels)
                        if (member.StationaryInteractionSize(vessel.Id) == 0)
                            await Present(member, vessel, replace: false);
        }
        finally { area.Gate.Release(); }
    }

    static async Task Present(ClientState conn, Vessel vessel, bool replace)
    {
        var at = vessel.At;
        var chest = Native.Value;
        if (replace)
            await SendWorldRemoval(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, vessel.Id, null);
        // The .ot has one Transitionable state. Chest carries the opened bit.
        var body = new QuestObject(at.X, at.Y, chest.Components, at.Rotation, at.Z, chest.Model,
            Opened: conn.Instance.ChestOpened(at.X, at.Y)) { Identified = false, Mods = chest.Implicits };
        if (await SendWorldCreate(conn, vessel.Id, () => InstanceEntities.CreateObject(vessel.Id,
            InstanceEntities.ObjectTypeHash(Type), body.Blob()), "Vaal side-area vessel", wake: true))
            conn.SetBlocker(vessel.Id, at.X, at.Y, QuestChestSize);
    }

    public static async Task<bool> Use(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (conn.StationaryInteractionSize(use.TargetId) == 0 || !Bosses.Value.ContainsKey(conn.Layout.Id)) return false;
        var area = In(conn);
        if (area.Vessels.FirstOrDefault(v => v.Id == use.TargetId) is not { } vessel) return false;
        var at = vessel.At;
        var wire = conn.Wire;
        int action = conn.ActionCount;
        if (!AllGuardsDead(conn.Instance) || conn.Instance.ChestOpened(at.X, at.Y))
        {
            await End();
            return true;
        }
        if (!TryBeginObjectWalk(conn, requestedAt, at.X, at.Y, QuestChestSize, out var walk)) return true;
        action = walk.Action;
        if (!use.Started)
            await SendWorldRecord(wire.Remote, wire.Stream, wire.Ciphers, conn, Program.PlayerId,
                InstanceProtocol.ActionStarted(Program.PlayerId, walk.Here.X, walk.Here.Y, at.X, at.Y,
                    use.SkillId, use.Sequence, use.Flags, targetObjectId: vessel.Id), null,
                still: () => PlayerActionCurrent(conn, action));
        if (walk.Arrive > walk.Now) await Task.Delay(TimeSpan.FromSeconds(walk.Arrive - walk.Now));
        await area.Gate.WaitAsync();
        try
        {
            if (PlayerActionCurrent(conn, action) && conn.PlayerLife > 0 &&
                PlayerWalk.InWalkUpRange(conn.Walk.Position(AreaTime(conn)), (at.X, at.Y), QuestChestSize))
                await Open(conn, vessel);
        }
        finally { area.Gate.Release(); }
        await End();
        return true;

        Task End() => SendWorldRecord(wire.Remote, wire.Stream, wire.Ciphers, conn, Program.PlayerId,
            InstanceProtocol.ActionEnded(Program.PlayerId), null, still: () => PlayerActionCurrent(conn, action));
    }

    static async Task Open(ClientState conn, Vessel vessel)
    {
        var at = vessel.At;
        if (conn.Instance.ChestOpened(at.X, at.Y)) return;
        var rewards = Contents(conn);
        if (!conn.Instance.OpenChest(at.X, at.Y)) return;
        // Commit every item to shared ground before sending to the opener or its peers.
        var ground = new List<(uint Id, WorldItemObject Item, string Name)>();
        foreach (var drop in rewards)
        {
            var cell = conn.Layout.Near((at.X, at.Y + 3));
            var item = new WorldItemObject(cell.X, cell.Y, drop.Item);
            uint id = conn.Instance.Ground.Add(item, conn);
            ground.Add((id, item, drop.Type.Name));
        }
        var (remote, stream, ciphers) = conn.Wire;
        var sends = ground.Select(drop => SendGroundItem(remote, stream, ciphers, conn, drop.Id, drop.Item,
            $"Vaal side-area vessel drops {drop.Name}", justDropped: true)).ToList();
        foreach (var member in Members(conn))
            if (member.StationaryInteractionSize(vessel.Id) > 0)
                sends.Add(Present(member, vessel, replace: true));
        await Task.WhenAll(sends);
    }

    static IReadOnlyList<ItemDrops.Drop> Contents(ClientState conn)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(conn)) return [];
        var run = MapRunOf(conn);
        int level = run is { Area.Level: > 0 } ? run.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id);
        var catalog = ItemsFor(conn);
        var stats = conn.Instance.KeptAreaStats;
        int Count(string id) => stats is null ? run?.Mods.Get(id) ?? 0 : stats.GetValueOrDefault(Program.Stats.Key(id)!.Value);

        var rewards = new List<ItemDrops.Drop> { Reward(catalog, level, Random.Shared) };
        int gems = Count("map_vaal_vessel_drop_X_vaal_gems");
        for (int n = 0; n < gems; n++) rewards.Add(VaalGem(catalog, level, Random.Shared));

        int levelledVaalGems = Count("map_vaal_vessel_drop_X_levelled_vaal_gems");
        for (int n = 0; n < levelledVaalGems; n++) rewards.Add(VaalGem(catalog, level, Random.Shared, gemLevel: 20));

        int qualityGems = Count("map_vaal_vessel_drop_X_quality_23_gems");
        for (int n = 0; n < qualityGems; n++)
            if (DropsFor(conn).GemBase(Random.Shared, level) is { } gem)
                rewards.Add(new(ItemCatalog.Create(gem) with { ItemLevel = 1, Quality = 23, Corrupted = true }, gem));

        int level21Gems = Count("map_vaal_vessel_drop_X_level_21_gems");
        for (int n = 0; n < level21Gems; n++)
            if (DropsFor(conn).GemBase(Random.Shared, level, t => catalog.Gem(t)!.MaxLevel >= 20) is { } type)
            {
                var gem = catalog.Gem(type)!;
                int experience = gem.MaxLevel == 20 ? -1 : gem.ExperienceFor(21);
                rewards.Add(new(ItemCatalog.Create(type) with { ItemLevel = 1, Corrupted = true, GemExperience = experience }, type));
            }

        int incursionRares = Count("map_vaal_vessel_drop_X_rare_temple_items");
        if (incursionRares > 0)
            rewards.AddRange(VaalVesselIncursionItems.Roll(catalog, RollsFor(conn), level, incursionRares, Random.Shared));

        int cards = Count("map_vaal_vessel_drop_X_divination_cards");
        if (cards > 0 && !LeagueFor(conn).HardMode)
            rewards.AddRange(DivinationCards(catalog, level, cards, Random.Shared));

        int uniques = Count("map_vaal_vessel_drop_x_single_implicit_corrupted_uniques");
        if (uniques > 0)
            rewards.AddRange(VaalVesselUniques.Roll(catalog, RollsFor(conn), UniquesFor(conn), DropsFor(conn), level, uniques, Random.Shared));

        int doubleUniques = Count("map_vaal_vessel_drop_x_double_implicit_corrupted_uniques");
        if (doubleUniques > 0)
            rewards.AddRange(VaalVesselUniques.Roll(catalog, RollsFor(conn), UniquesFor(conn), DropsFor(conn), level,
                doubleUniques, Random.Shared, doubleImplicit: true));

        int templeMaps = Count("map_vaal_vessel_drop_X_vaal_temple_maps");
        if (templeMaps > 0)
        {
            var temple = catalog.Find("Metadata/Items/Maps/MapKeyVaalTemple")!;
            var rolls = RollsFor(conn);
            for (int n = 0; n < templeMaps; n++)
            {
                var map = ItemCatalog.Create(temple, random: Random.Shared) with { ItemLevel = (byte)level };
                var rare = rolls.Roll(map, ItemRarity.Rare, Random.Shared);
                rewards.Add(new(rare with { Corrupted = true, Identified = true }, temple));
            }
        }

        int orbs = Count("map_vaal_vessel_drop_X_vaal_orbs");
        if (orbs <= 0) return rewards;
        var orb = catalog.Find(CurrencyEffects.VaalOrb)!;
        var drop = new ItemDrops.Drop(ItemCatalog.Create(orb), orb);
        return ItemDrops.Stacked(rewards.Concat(Enumerable.Repeat(drop, orbs)));
    }

    // The guaranteed reward uses Chris Wilson's published 2014 category and fragment ratios.
    // The 3.29 client records the Apex recipe and eligible gem variants, but has no current server weights.
    static readonly (int Number, int Weight)[] Fragments = [(1, 1), (2, 3), (3, 2), (4, 4)];
    static readonly RuleTable<ItemCatalog, ItemCatalog.BaseType[]> VaalGems = new();
    static readonly RuleTable<GameData.Snapshot, StrongboxLoot.CardReward[]> VesselCards = new();

    internal static IEnumerable<ItemDrops.Drop> DivinationCards(ItemCatalog catalog, int level, int count, Random random)
    {
        var rows = VesselCards.GetValue(GameData.Current, _ =>
            [.. StrongboxLoot.CardRewards().Where(r => r.Corrupted && r.VaalVesselWeight > 0)]);
        var pool = rows.Select(r => (Type: catalog.Find(r.Card)!, Weight: (double)r.VaalVesselWeight))
            .Where(p => p.Type.Supported && p.Type.DropLevel <= level).ToList();
        for (int i = 0; i < count; i++)
            yield return ItemDrops.WeightedCard(pool, random)
                ?? throw new InvalidOperationException($"No Vaal vessel divination cards at area level {level}");
    }

    internal static ItemDrops.Drop Reward(ItemCatalog items, int level, Random random)
    {
        var fragments = Fragments.SelectMany(f => Enumerable.Repeat(
            items.Find($"Metadata/Items/MapFragments/CurrencyVaalFragment1_{f.Number}")!, f.Weight))
            .Where(t => t.DropLevel <= level).ToArray();
        if (fragments.Length > 0 && random.Next(2) == 0)
        {
            var fragment = fragments[random.Next(fragments.Length)];
            return new(ItemCatalog.Create(fragment), fragment);
        }
        return VaalGem(items, level, random);
    }

    static ItemDrops.Drop VaalGem(ItemCatalog items, int level, Random random, int gemLevel = 1)
    {
        var variants = VaalGems.GetValue(items, catalog => [.. catalog.All.Select(t => catalog.Gem(t)?.VaalBaseRow)
            .OfType<int>().Distinct().Select(row => catalog.Row(row)!).Where(t => t.Supported)]);
        var gems = variants.Where(t => t.DropLevel <= level).ToArray();
        var gem = gems[random.Next(gems.Length)];
        int experience = items.Gem(gem)!.Thresholds[gemLevel - 1];
        return new(ItemCatalog.Create(gem) with { ItemLevel = 1, Corrupted = true, GemExperience = experience }, gem);
    }
}
