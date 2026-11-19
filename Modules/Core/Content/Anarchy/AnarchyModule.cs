using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Anarchy;

/// <summary>
/// Anarchy: wild Rogue Exiles. A new instance rolls CoreLeagues row 0 at its WorldAreaLeagueChances +0x8 chance (5% in
/// the campaign's and the maps' areas; none in The Twilight Strand and The Coast), and each exile it gets stands alone
/// away from the packs, a unique monster with its level's more life (RogueExileLifeScalingPerLevel). A map's stats (its
/// affixes, the Anarchy scarabs, the Atlas tree's Anarchy wheel) add exiles, pair them, gigantify them, make them
/// exceptional, guard the map boss with them and change what they drop. Agents/docs/game/league-encounters.md.
/// </summary>
public sealed class AnarchyModule : IModule
{
    /// <summary>The most exiles one area places, whatever its stats add up to.</summary>
    const int MostExiles = 40;

    /// <summary>An exile placed in this instance, for its drops.</summary>
    sealed record Exile(uint Id, string VarietyId, bool Exceptional, bool Special);

    sealed class Placed { public readonly List<Exile> Exiles = []; public object? Area; }
    static readonly ConditionalWeakTable<ClientState, Placed> PlacedExiles = new();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.AreaPopulated.Add(module.Manifest.Id, area => Place(module, area));
        GameEvents.Drops.Add(module.Manifest.Id, Drops);
    }

    static async Task Place(IModuleContext module, AreaPopulated area)
    {
        var conn = area.Conn;
        var layout = conn.Layout;
        if (layout.Walkable is not { } walkable) return;
        var stats = AreaLeagueStats(conn);
        int S(string name) => stats.GetValueOrDefault(name);
        var placed = PlacedExiles.GetValue(conn, _ => new());
        lock (placed) { placed.Exiles.Clear(); placed.Area = layout; }
        if (S("map_no_exiles") > 0) return;
        // The encounter's own draws, apart from the packs' (the same instance gets the same exiles).
        var rng = new Random(area.Seed ^ 0x416e6172);
        bool Chance(int percent) => percent > 0 && rng.Next(100) < percent;
        int count = S("map_disable_rogue_exiles_from_chance") > 0 ? 0
            : LeagueChances.Current.Count(layout.Id, LeagueChances.Anarchy, rng,
                increased: S("map_league_base_chance_+%_final"),
                added: S("map_league_content_additional_chance_%") + S("map_rogue_exile_chance_%"));
        // What the map adds: its affixes' and the Anarchy Scarab's exiles, Unexpected Stranger's one.
        count += S("map_spawn_extra_exiles") + S("map_hidden_spawn_extra_exiles") + S("map_leaguestone_additional_rogue_exiles");
        count = Math.Max(count, S("map_leaguestone_override_base_num_rogue_exiles"));
        // Additional Exile Spawn Chance: each 100% is one more, the rest a chance of one.
        int additional = S("map_additional_rogue_exile_chance_%");
        count += additional / 100 + (Chance(additional % 100) ? 1 : 0);
        if (Chance(S("map_spawn_exile_per_area_%"))) count++;
        // Ruckus: a chance of many at once.
        if (Chance(S("map_spawn_many_exiles_chance_%"))) count += S("map_spawn_many_exiles_num");
        if (Chance(S("map_spawn_20_additional_exiles_chance_%"))) count += 20;
        // Jumbled Abundance: each of its additions is a strongbox, an essence, a shrine or an exile alike.
        for (int i = S("map_contains_X_additional_strongboxes_essences_shrines_or_rogue_exiles"); i > 0; i--)
            if (rng.Next(4) == 0) count++;
        int exceptional = S("map_contains_x_additional_exceptional_rogue_exiles");
        // MapSupporterRogueGiants: "Area contains {0} Gigantic Rogue Exiles", each one more, gigantified.
        int giants = S("map_contains_X_rogue_giants");
        count = Math.Min(count + giants + exceptional, MostExiles);
        var taken = area.Taken.ToList();
        var transitions = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        List<string> used = [];
        for (int i = 0; i < count; i++)
        {
            if (AreaPopulation.EncounterSpot(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY), transitions, taken, rng)
                is not { } at) break;
            taken.Add(at);
            // The exceptional ones come last, so a capped count keeps the ones the scarab promised first; the gigantic
            // ones before them.
            bool isExceptional = i >= count - exceptional;
            bool isGiant = !isExceptional && i >= count - exceptional - giants;
            bool paired = S("map_rogue_exiles_are_doubled") > 0 || Chance(S("map_rogue_exiles_are_doubled_chance_%"));
            for (int n = 0; n < (paired ? 2 : 1); n++)
            {
                var spot = n == 0 ? at : (at.X + 5, at.Y);
                if (await SpawnExile(module, conn, area.Level, spot, stats, rng, used, isExceptional && n == 0, placed, "wild",
                    giant: isGiant && n == 0) is null) return;
            }
        }
        await GuardBosses(module, conn, area.Level, stats, rng, used, placed);
    }

    /// <summary>Royal Guard and the two-exile map boss: exiles next to each map boss.</summary>
    static async Task GuardBosses(IModuleContext module, ClientState conn, byte level, Dictionary<string, int> stats, Random rng,
        List<string> used, Placed placed)
    {
        int guards = stats.GetValueOrDefault("map_boss_has_rogue_exile_bodyguards_chance_%");
        int two = stats.GetValueOrDefault("map_boss_has_two_rogues_exiles_chance_%");
        if (guards <= 0 && two <= 0) return;
        List<(int X, int Y)> bosses = [];
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
            foreach (var m in conn.Monsters.Values)
                if (m.Hostile && !m.Dead && m.Rarity == MonsterRarity.Unique && m.Variety is { BossHealthBar: true }
                    && !RogueExiles.Current.IsExile(m.Variety.Id))
                    bosses.Add(m.Walk.Position(now));
        foreach (var boss in bosses)
        {
            bool guarded = rng.Next(100) < guards, accompanied = rng.Next(100) < two;
            if (!guarded && !accompanied) continue;
            foreach (var offset in new[] { (-6, 3), (6, 3) })
                if (await SpawnExile(module, conn, level, (boss.X + offset.Item1, boss.Y + offset.Item2), stats, rng, used, false, placed,
                    guarded ? "map boss bodyguard" : "with the map boss", bodyguard: guarded) is null) return;
        }
    }

    /// <summary>One exile: which (Faridun's, a gigantified one), its stats from the map, and its spawn. Null when none can
    /// spawn here (the caller places no more).</summary>
    static async Task<uint?> SpawnExile(IModuleContext module, ClientState conn, byte level, (int X, int Y) at,
        Dictionary<string, int> stats, Random rng, List<string> used, bool exceptional, Placed placed, string kind, bool bodyguard = false,
        bool giant = false)
    {
        int S(string name) => stats.GetValueOrDefault(name);
        bool anyLevel = S("map_override_rogue_exile_min_level") > 0;
        var exile = rng.Next(100) < S("map_rogue_exile_%_chance_to_be_faridun") ? RogueExiles.Current.PickFaridun(rng) : null;
        // A gigantic one is of the exiles that have a gigantified variety (RogueExileGiants).
        exile ??= giant ? RogueExiles.Current.PickWithGiant(level, rng, anyLevel, used) : RogueExiles.Current.Pick(level, rng, anyLevel, used);
        if (exile is null)
        {
            module.Log($"{conn.Layout.Id}: no Rogue Exile can spawn at level {level}");
            return null;
        }
        used.Add(exile.Id);
        giant = (giant || rng.Next(100) < S("map_rogue_exile_gigantify_chance_%")) && RogueExiles.Current.Giant(exile) is not null;
        if (giant) exile = RogueExiles.Current.Giant(exile)!;
        bool special = rng.Next(100) < S("map_wild_rogue_exiles_have_special_reward_chance_%");
        // Their more life: the level's (RogueExileLifeScalingPerLevel), times Rogue Trader's on the special ones.
        double more = (1 + RogueExiles.Current.LifeMore(level) / 100.0)
            * (special ? 1 + S("rogue_trader_map_rogue_exile_maximum_life_+%_final") / 100.0 : 1);
        Dictionary<string, int> effects = [];
        void Add(string name, int value) { if (value != 0) effects[name] = effects.GetValueOrDefault(name) + value; }
        Add(MonsterModCatalog.EncounterLifeStat, (int)Math.Round((more - 1) * 100));
        Add("maximum_life_+%", S("map_rogue_exiles_maximum_life_+%"));
        Add("damage_+%", S("map_rogue_exiles_damage_+%"));
        int speed = S("map_rogue_exile_attack_cast_and_movement_speed_+%");
        Add("attack_speed_+%", speed);
        Add("base_cast_speed_+%", speed);
        Add("base_movement_velocity_+%", speed);
        List<(uint Key, int Value)> extraStats = [];
        void Stat(string name, int value) { if (value != 0 && Stats.Key(name) is uint key) extraStats.Add((key, value)); }
        if (giant)
            foreach (var (name, value) in ModStats(RogueExiles.GiantMod))
            {
                if (name == "base_actor_scale_+%") Stat(name, value);
                else Add(name, value);
            }
        Stat("is_rogue_exile", 1);
        if (exceptional) Stat("rogue_exile_is_exceptional", 1);
        var mods = new MonsterModSet([], [], new MonsterModEffects(effects));
        uint id = await SpawnMonster(conn, exile, level, MonsterRarity.Unique, at.X, at.Y,
            log: true, alone: true, extraMods: mods, extraStats: extraStats);
        if (id != 0) lock (placed) placed.Exiles.Add(new(id, exile.Id, exceptional, special));
        module.Log($"{conn.Layout.Id}: Rogue Exile {exile.Name} ({exile.Id}, {kind}{(giant ? ", gigantified" : "")}" +
            $"{(exceptional ? ", exceptional" : "")}{(special ? ", special reward" : "")}{(bodyguard ? ", bodyguard" : "")}) at ({at.X}, {at.Y}), " +
            $"level {level}, {(more - 1) * 100:0}% more life" + (id == 0 ? ": not created" : $", id {id}"));
        return id;
    }

    /// <summary>A Mods row's stats at their lowest values, by stat name.</summary>
    static IEnumerable<(string Name, int Value)> ModStats(string modId)
    {
        if (ClientTables.TableOrNull("Mods")?.FirstOrDefault(r => r.Text("Id") == modId) is not { } mod) yield break;
        for (int i = 1; i <= 6; i++)
            if (mod.IntOrNull($"StatsKey{i}") is int row && row >= 0)
                yield return (Stats.Name((uint)row + 1), mod.IntOrNull($"Stat{i}Min") ?? 0);
    }

    // ---- Drops -----------------------------------------------------------------------------------------------------------

    static void Drops(DropRoll roll)
    {
        var conn = roll.Conn;
        var stats = AreaLeagueStats(conn);
        int S(string name) => stats.GetValueOrDefault(name);
        var rng = new Random(HashCode.Combine(roll.Monster.X, roll.Monster.Y, roll.Monster.VarietyId, conn.Seed));
        // Any monster: the altar's and the map's chance of an additional Anarchy scarab; a map boss its scarabs.
        if (rng.Next(100) < S("chance_%_to_drop_additional_scarab_anarchy")) roll.Items.Add(AnarchyScarab(rng));
        if (roll.Monster.Rarity == MonsterRarity.Unique && Monsters.Find(roll.Monster.VarietyId) is { BossHealthBar: true } boss
            && !RogueExiles.Current.IsExile(boss.Id))
            for (int i = S("map_boss_drops_X_additional_scarabs_anarchy"); i > 0; i--) roll.Items.Add(AnarchyScarab(rng));
        if (!PlacedExiles.TryGetValue(conn, out var placed)) return;
        Exile? exile;
        lock (placed)
        {
            if (!ReferenceEquals(placed.Area, conn.Layout)) return;
            exile = placed.Exiles.FirstOrDefault(e => e.VarietyId == roll.Monster.VarietyId);
            if (exile is not null) placed.Exiles.Remove(exile);
        }
        if (exile is null) return;
        // An exile drops as a rare monster does, more richly: the wild exiles' increased rarity and the map's.
        roll.Rarity *= Math.Max(0, 1 + (S("map_wild_rogue_exile_dropped_item_rarity_+%") + S("map_leaguestone_rogue_exiles_dropped_item_rarity_+%_final")) / 100.0);
        // Exile Currency Chance: each 100% one currency item, the rest a chance of one; the leaguestone's several.
        int currency = S("map_rogue_exiles_chance_to_drop_additional_currency_%");
        int currencies = currency / 100 + (rng.Next(100) < currency % 100 ? 1 : 0) + S("map_rogue_exiles_drop_additional_currency_items_with_quality");
        for (int i = 0; i < currencies; i++) roll.Items.Add(Currency(rng));
        if (S("map_rogue_exile_drop_skill_gem_with_quality") > 0) roll.Items.Add("Metadata/Items/Currency/CurrencyGemQuality");
        // Rogue Trader's special reward and the Exceptional exile's (what they are is not in the tables): richer drops.
        if (exile.Special) roll.Rarity *= 2;
        if (exile.Exceptional) { roll.Quantity *= 2; roll.Rarity *= 2; }
        // The Anarchy scarabs' and leaguestones' rewards: additional jewels, rolled as the exile's own drops are.
        var jewels = JewelBases(conn);
        for (int i = S("map_rogue_exiles_drop_x_additional_jewels"); i > 0 && jewels.Count > 0; i--)
            roll.RolledItems.Add(jewels[rng.Next(jewels.Count)]);
        // What the exile's items drop as: fully linked, fractured, corrupted, then duplicated (a mirrored copy as well).
        if (S("map_rogue_exiles_dropped_items_are_fully_linked") > 0) roll.Changes.Add((d, _) => [FullyLinked(d)]);
        int fractured = S("map_rogue_exiles_dropped_items_are_fractured_chance_%");
        if (fractured > 0) roll.Changes.Add((d, r) => [r.Next(100) < fractured ? Fractured(d) : d]);
        if (S("map_rogue_exiles_dropped_items_are_corrupted") > 0) roll.Changes.Add((d, _) => [Modifiable(d) ? d with { Item = d.Item with { Corrupted = true } } : d]);
        if (S("map_rogue_exiles_dropped_items_are_duplicated") > 0)
            roll.Changes.Add((d, _) => Modifiable(d) ? [d, d with { Item = d.Item with { Mirrored = true } }] : [d]);
    }

    // An item a currency could change: not a stack (currency, fragments, gold), not already mirrored.
    static bool Modifiable(ItemDrops.Drop d) => !d.Type.Stackable && d.Type.Class != ItemDrops.GoldClass && !d.Item.Mirrored;

    static ItemDrops.Drop FullyLinked(ItemDrops.Drop d) =>
        Modifiable(d) && d.Item.Sockets.Count > 1 ? d with { Item = d.Item with { Links = [(byte)d.Item.Sockets.Count] } } : d;

    // A Fracturing Orb's outcome: the first explicit mod fractured, on an item with more than one (a unique's are fixed).
    static ItemDrops.Drop Fractured(ItemDrops.Drop d) =>
        Modifiable(d) && d.Item.Rarity is ItemRarity.Magic or ItemRarity.Rare && d.Item.FracturedCount == 0
            && d.Item.Mods.Count(m => m.Section == ModSection.Explicit) > 1
            ? d with { Item = d.Item with { FracturedCount = 1 } } : d;

    // The jewel bases a kill can drop: the general drop table's rule (ItemDrops.Droppable) for the Jewel and Abyss Jewel classes.
    static readonly ConditionalWeakTable<ItemCatalog, List<string>> Jewels = [];
    static List<string> JewelBases(ClientState conn) => Jewels.GetValue(global::Game.ItemsFor(conn), catalog =>
        [.. catalog.All.Where(t => t.Class is "Jewel" or "AbyssJewel" && ItemDrops.Droppable(t)).Select(t => t.Id)]);

    static string AnarchyScarab(Random rng) => $"Metadata/Items/Scarabs/ScarabAnarchy{1 + rng.Next(4)}";

    static string Currency(Random rng)
    {
        double total = ItemDrops.Currency.Sum(c => c.Weight), pick = rng.NextDouble() * total;
        foreach (var (id, weight) in ItemDrops.Currency)
            if ((pick -= weight) < 0) return id;
        return ItemDrops.Currency[0].Id;
    }
}
