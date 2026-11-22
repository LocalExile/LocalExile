using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>Imprisoned-monster essences, separate from the essences' equipment crafting recipes.</summary>
public static class MapEssenceCatalog
{
    public sealed record Essence(int Row, uint Hash, string Item, int Type, int Tier, bool Corrupted,
        IReadOnlyList<int> DropLevels, IReadOnlyList<int> MonsterMods, int DisplayMod);
    static readonly Lazy<IReadOnlyList<Essence>> Data = new(Load);
    public static IReadOnlyList<Essence> All => Data.Value;
    public static Essence? Find(int row) => All.FirstOrDefault(e => e.Row == row);

    // These rows require encounter behaviours beyond ordinary monster stats. Report the actual released modifiers,
    // including old/forced inputs, rather than assuming that a visible modifier has a server-side skill consumer.
    static readonly Lazy<IReadOnlyDictionary<int, string>> PendingBehaviours = new(() =>
    {
        var result = new Dictionary<int, string>();
        var mods = ClientTables.Table("Mods");
        foreach (int row in All.SelectMany(e => e.MonsterMods).Distinct())
        {
            if (Program.Items.Mods.Row(row) is not { } mod || mod.Id is Game.EnvyLifeDegenMod or Game.EssenceKaruiSummonMod or Game.EssenceInsectDeathMod
                || MonsterModCatalog.SupportOf(mod) == MonsterModCatalog.Support.Full) continue;
            string daemon = mods[row].TextOrNull("MonsterMetadata") ?? "";
            if (daemon.Length > 0 || mod.GrantedEffects > 0 || mod.MonsterOnDeath.Length > 0
                || mod.BuffTemplate is not null || mod.Stats.Any(s => s.Id == "essence_delirium_spawn_monster_on_death_variation"))
                result[row] = mod.Id;
        }
        return result;
    });

    public static IReadOnlyList<string> PendingEffects(IEnumerable<int> essences) => [.. essences.Select(Find)
        .OfType<Essence>().SelectMany(e => e.MonsterMods).Distinct()
        .Where(PendingBehaviours.Value.ContainsKey).Select(row => PendingBehaviours.Value[row])];

    /// <summary>The Remnant of Corruption essence (Essences' CurrencyCorruptMonolith row): tier 0, so the ordinary pool
    /// never rolls it, but the atlas Remnant-of-Corruption chance puts it on a monolith.</summary>
    public const string RemnantItem = "Metadata/Items/Currency/CurrencyCorruptMonolith";

    static IReadOnlyList<Essence> Load()
    {
        if (!ClientTables.Has("Essences") || !ClientTables.Has("EssenceType")) return [];
        var types = ClientTables.Table("EssenceType");
        var result = new List<Essence>();
        int index = 0;
        foreach (var row in ClientTables.Table("Essences"))
        {
            int key = row.Int("EssenceTypeKey"), tier = row.Int("Level"), at = index++;
            if (key < 0 || key >= types.Count || Program.Items.Row(row.Int("BaseItemTypesKey")) is not { Supported: true } item) continue;
            // A tier-0 row is an untiered special essence, not a bad row: the Remnant of Corruption (drop level 32, the
            // atlas chance adds it) and Desolation (CurrencyEssenceFaridun1, drop level 68, its own Faridun daemon) both
            // carry a drop level. Keep those; the ordinary pool still excludes every tier 0 (Pool), so they arrive only
            // through their own source or the dev spawn.
            if (tier <= 0 && row.Ints("DropLevel").Length == 0) continue;
            result.Add(new(at, row.UInt("HASH32"), item.Id, key, tier, types[key].Bool("IsCorruptedEssence"),
                row.Ints("DropLevel"), row.Ints("Monster_ModsKeys"), row.IntOrNull("Display_Monster_ModsKey") ?? -1));
        }
        return result;
    }

    public static IReadOnlyList<Essence> Pool(int level) => [.. All.Where(e => !e.Corrupted && e.Tier > 0
        && e.DropLevels.Any(l => l <= level))];

    public static Essence Higher(Essence essence) => All.FirstOrDefault(e => e.Type == essence.Type
        && e.Tier == essence.Tier + 1) ?? essence;

    static readonly Lazy<IReadOnlyDictionary<int, int>> Groups = new(() =>
        ClientTables.Table("EssenceType").ToDictionary(r => r.Index, r => r.Int("EssenceType")));

    /// <summary>The next colour group, keeping the tier or raising it to that group's minimum tier. The ladder is the tiered
    /// essences only: Desolation sits in the corrupted group at tier 0 (and the Remnant in group 0), and counting either
    /// would drop the group's minimum tier to 0 and leave the transform with no candidate.</summary>
    public static Essence Transform(Essence essence, Random random)
    {
        int next = Groups.Value.GetValueOrDefault(essence.Type) + 1;
        var group = All.Where(e => e.Tier > 0 && Groups.Value.GetValueOrDefault(e.Type) == next).ToArray();
        if (group.Length == 0) return essence;
        int tier = Math.Max(essence.Tier, group.Min(e => e.Tier));
        var choices = group.Where(e => e.Tier == tier).ToArray();
        return choices.Length == 0 ? essence : choices[random.Next(choices.Length)];
    }

    public static Essence? Additional(int level, IReadOnlyList<int> current, MapMods mods, Random random)
    {
        var types = current.Select(Find).OfType<Essence>().Select(e => e.Type).ToHashSet();
        var pool = Pool(level).Where(e => !types.Contains(e.Type)).ToArray();
        if (pool.Length == 0) return null;
        var pick = pool[random.Next(pool.Length)];
        return mods.Get("map_essence_monsters_higher_tier") > 0
            || random.Next(100) < mods.Get("map_essences_are_1_tier_higher_chance_%") ? Higher(pick) : pick;
    }

    public static IReadOnlyList<int> Roll(int level, MapMods mods, Random random)
    {
        var pool = Pool(level);
        if (pool.Count == 0) return [];
        // As with ordinary pack density, the official server's base multiplicity/weights are unavailable.
        // Keep this replica distribution explicit rather than treating DropLevel entries as spawn weights.
        int count = random.Next(1, 4) + Math.Max(0, mods.Get("map_essence_monsters_have_additional_essences"));
        if (random.Next(100) < mods.Get("map_essence_monsters_have_an_additional_essence_%_chance")) count++;
        if (random.Next(100) < mods.Get("map_essence_monsters_chance_for_3_additional_essences_%")) count += 3;
        var chosen = new List<Essence>();
        for (int i = 0; i < count; i++)
        {
            var available = pool.Where(e => !chosen.Any(c => c.Type == e.Type)).ToArray();
            if (available.Length == 0) break;
            var pick = available[random.Next(available.Length)];
            if (mods.Get("map_essence_monsters_higher_tier") > 0
                || random.Next(100) < mods.Get("map_essences_are_1_tier_higher_chance_%")) pick = Higher(pick);
            chosen.Add(pick);
        }
        // Amplified Energies (atlas_essence_2_4, map_essence_X_essences_at_highest_tier): X of the essences sit at the
        // highest tier their type reaches in this area's pool.
        for (int i = 0; i < mods.Get("map_essence_X_essences_at_highest_tier") && i < chosen.Count; i++)
            chosen[i] = pool.Where(e => e.Type == chosen[i].Type).OrderByDescending(e => e.Tier).First();
        // Remnant of Corruption Essence Chance (atlas_essence_3_4/3_6, map_essence_monolith_contains_essence_of_corruption_%):
        // the monolith also holds the Remnant of Corruption essence. It is tier 0 (the ordinary pool never rolls it) and
        // carries the RemnantOfCorruption monster mods; its on-death fleshmonster summon is a separate mechanic.
        if (random.Next(100) < mods.Get("map_essence_monolith_contains_essence_of_corruption_%")
            && All.FirstOrDefault(e => e.Item == RemnantItem) is { } remnant)
            chosen.Add(remnant);
        return [.. chosen.Select(e => e.Row)];
    }

    public static MonsterModSet Mods(ClientState conn, MonsterCatalog.Variety variety, byte level, IReadOnlyList<int> essences)
    {
        var basis = Game.MonsterModsFor(conn, variety, level, MonsterRarity.Rare, null, false);
        var list = essences.Select(Find).OfType<Essence>().ToArray();
        var seen = new HashSet<int>();
        var extra = list.SelectMany(e => e.MonsterMods)
            .Select(Program.Items.Mods.Row).OfType<ModCatalog.Mod>()
            // Common damage/toughness and reward bonuses belong to every essence, even when their row is shared.
            // A repeated aura/daemon is still one behaviour, rather than another independently ticking copy.
            .Where(m => m.Id.StartsWith("EssenceDaemonModLifeAndDamage", StringComparison.Ordinal)
                || m.Id.StartsWith("EssenceDaemonModRarityAndQuantity", StringComparison.Ordinal) || seen.Add(m.Row))
            .Select(m => m.Roll(ModSection.Implicit, Random.Shared));
        // The essence-named display mod (Display_Monster_ModsKey) carries no stats but is what shows the essence on the
        // monster's hover panel; the stat mods above have empty names and would leave the essences invisible.
        var display = list.Select(e => e.DisplayMod).Where(r => r >= 0).Distinct()
            .Select(Program.Items.Mods.Row).OfType<ModCatalog.Mod>()
            .Select(m => m.Roll(ModSection.Explicit, Random.Shared));
        return Game.MonsterModRolls.Value.Create([.. basis.Mods, .. extra, .. display], basis.NameWords, MonsterRarity.Rare, level);
    }
}
