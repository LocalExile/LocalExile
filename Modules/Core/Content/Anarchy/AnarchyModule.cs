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
    static readonly ConditionalWeakTable<Player, Placed> PlacedExiles = new();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.AreaPopulated.Add(module.Manifest.Id, area => Place(module, area));
        GameEvents.Drops.Add(module.Manifest.Id, Drops);
    }

    static async Task Place(IModuleContext module, AreaPopulated area)
    {
        var player = area.Player;
        var layout = player.Layout;
        if (layout.Walkable is not { } walkable) return;
        var stats = AreaLeagueStats(player);
        int S(uint name) => stats.GetValueOrDefault(name);
        var placed = PlacedExiles.GetValue(player, _ => new());
        lock (placed) { placed.Exiles.Clear(); placed.Area = layout; }
        if (S(Stat.MapNoExiles) > 0) return;
        // The encounter's own draws, apart from the packs' (the same instance gets the same exiles).
        var rng = new Random(area.Seed ^ 0x416e6172);
        bool Chance(int percent) => percent > 0 && rng.Next(100) < percent;
        int count = S(Stat.MapDisableRogueExilesFromChance) > 0 ? 0
            : LeagueChances.Current.Count(layout.Id, LeagueChances.Anarchy, rng,
                increased: S(Stat.MapLeagueBaseChancePlusPctFinal),
                added: S(Stat.MapLeagueContentAdditionalChancePct) + S(Stat.MapRogueExileChancePct));
        // What the map adds: its affixes' and the Anarchy Scarab's exiles, Unexpected Stranger's one.
        count += S(Stat.MapSpawnExtraExiles) + S(Stat.MapHiddenSpawnExtraExiles) + S(Stat.MapLeaguestoneAdditionalRogueExiles);
        count = Math.Max(count, S(Stat.MapLeaguestoneOverrideBaseNumRogueExiles));
        // Additional Exile Spawn Chance: each 100% is one more, the rest a chance of one.
        int additional = S(Stat.MapAdditionalRogueExileChancePct);
        count += additional / 100 + (Chance(additional % 100) ? 1 : 0);
        if (Chance(S(Stat.MapSpawnExilePerAreaPct))) count++;
        // Ruckus: a chance of many at once.
        if (Chance(S(Stat.MapSpawnManyExilesChancePct))) count += S(Stat.MapSpawnManyExilesNum);
        if (Chance(S(Stat.MapSpawn20AdditionalExilesChancePct))) count += 20;
        // Jumbled Abundance: each of its additions is a strongbox, an essence, a shrine or an exile alike.
        for (int i = S(Stat.MapContainsXAdditionalStrongboxesEssencesShrinesOrRogueExiles); i > 0; i--)
            if (rng.Next(4) == 0) count++;
        int exceptional = S(Stat.MapContainsXAdditionalExceptionalRogueExiles);
        // MapSupporterRogueGiants: "Area contains {0} Gigantic Rogue Exiles", each one more, gigantified.
        int giants = S(Stat.MapContainsXRogueGiants);
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
            bool paired = S(Stat.MapRogueExilesAreDoubled) > 0 || Chance(S(Stat.MapRogueExilesAreDoubledChancePct));
            for (int n = 0; n < (paired ? 2 : 1); n++)
            {
                var spot = n == 0 ? at : (at.X + 5, at.Y);
                if (await SpawnExile(module, player, area.Level, spot, stats, rng, used, isExceptional && n == 0, placed, "wild",
                    giant: isGiant && n == 0) is null) return;
            }
        }
        await GuardBosses(module, player, area.Level, stats, rng, used, placed);
    }

    /// <summary>Royal Guard and the two-exile map boss: exiles next to each map boss.</summary>
    static async Task GuardBosses(IModuleContext module, Player player, byte level, Dictionary<uint, int> stats, Random rng,
        List<string> used, Placed placed)
    {
        int guards = stats.GetValueOrDefault(Stat.MapBossHasRogueExileBodyguardsChancePct);
        int two = stats.GetValueOrDefault(Stat.MapBossHasTwoRoguesExilesChancePct);
        if (guards <= 0 && two <= 0) return;
        List<(int X, int Y)> bosses = [];
        double now = AreaTime(player);
        foreach (var m in player.Area.Monsters.Values)
            if (m.Hostile && !m.Dead && m.Rarity == MonsterRarity.Unique && m.Variety is { BossHealthBar: true }
                && !RogueExiles.Current.IsExile(m.Variety.Id))
                bosses.Add(m.Walk.Position(now));
        foreach (var boss in bosses)
        {
            bool guarded = rng.Next(100) < guards, accompanied = rng.Next(100) < two;
            if (!guarded && !accompanied) continue;
            foreach (var offset in new[] { (-6, 3), (6, 3) })
                if (await SpawnExile(module, player, level, (boss.X + offset.Item1, boss.Y + offset.Item2), stats, rng, used, false, placed,
                    guarded ? "map boss bodyguard" : "with the map boss", bodyguard: guarded) is null) return;
        }
    }

    /// <summary>One exile: which (Faridun's, a gigantified one), its stats from the map, and its spawn. Null when none can
    /// spawn here (the caller places no more).</summary>
    static async Task<uint?> SpawnExile(IModuleContext module, Player player, byte level, (int X, int Y) at,
        Dictionary<uint, int> stats, Random rng, List<string> used, bool exceptional, Placed placed, string kind, bool bodyguard = false,
        bool giant = false)
    {
        int S(uint name) => stats.GetValueOrDefault(name);
        bool anyLevel = S(Stat.MapOverrideRogueExileMinLevel) > 0;
        var exile = rng.Next(100) < S(Stat.MapRogueExilePctChanceToBeFaridun) ? RogueExiles.Current.PickFaridun(rng) : null;
        // A gigantic one is of the exiles that have a gigantified variety (RogueExileGiants).
        exile ??= giant ? RogueExiles.Current.PickWithGiant(level, rng, anyLevel, used) : RogueExiles.Current.Pick(level, rng, anyLevel, used);
        if (exile is null)
        {
            module.Log($"{player.Layout.Id}: no Rogue Exile can spawn at level {level}");
            return null;
        }
        used.Add(exile.Id);
        giant = (giant || rng.Next(100) < S(Stat.MapRogueExileGigantifyChancePct)) && RogueExiles.Current.Giant(exile) is not null;
        if (giant) exile = RogueExiles.Current.Giant(exile)!;
        bool special = rng.Next(100) < S(Stat.MapWildRogueExilesHaveSpecialRewardChancePct);
        // Their more life: the level's (RogueExileLifeScalingPerLevel), times Rogue Trader's on the special ones.
        double more = (1 + RogueExiles.Current.LifeMore(level) / 100.0)
            * (special ? 1 + S(Stat.RogueTraderMapRogueExileMaximumLifePlusPctFinal) / 100.0 : 1);
        Dictionary<uint, int> effects = [];
        void Add(uint name, int value) { if (value != 0) effects[name] = effects.GetValueOrDefault(name) + value; }
        Add(MonsterModCatalog.EncounterLifeStat, (int)Math.Round((more - 1) * 100));
        Add(Stat.MaximumLifePlusPct, S(Stat.MapRogueExilesMaximumLifePlusPct));
        Add(Stat.DamagePlusPct, S(Stat.MapRogueExilesDamagePlusPct));
        int speed = S(Stat.MapRogueExileAttackCastAndMovementSpeedPlusPct);
        Add(Stat.AttackSpeedPlusPct, speed);
        Add(Stat.BaseCastSpeedPlusPct, speed);
        Add(Stat.BaseMovementVelocityPlusPct, speed);
        List<(uint Key, int Value)> extraStats = [];
        void Extra(uint key, int value) { if (value != 0) extraStats.Add((key, value)); }
        if (giant)
            foreach (var (name, value) in ModStats(RogueExiles.GiantMod))
            {
                if (name == Stat.BaseActorScalePlusPct) Extra(name, value);
                else Add(name, value);
            }
        Extra(Stat.IsRogueExile, 1);
        if (exceptional) Extra(Stat.RogueExileIsExceptional, 1);
        var mods = new MonsterModSet([], [], new MonsterModEffects(effects));
        uint id = await SpawnMonster(player, exile, level, MonsterRarity.Unique, at.X, at.Y,
            log: true, alone: true, extraMods: mods, extraStats: extraStats);
        if (id != 0) lock (placed) placed.Exiles.Add(new(id, exile.Id, exceptional, special));
        module.Log($"{player.Layout.Id}: Rogue Exile {exile.Name} ({exile.Id}, {kind}{(giant ? ", gigantified" : "")}" +
            $"{(exceptional ? ", exceptional" : "")}{(special ? ", special reward" : "")}{(bodyguard ? ", bodyguard" : "")}) at ({at.X}, {at.Y}), " +
            $"level {level}, {(more - 1) * 100:0}% more life" + (id == 0 ? ": not created" : $", id {id}"));
        return id;
    }

    /// <summary>A Mods row's stats at their lowest values, by stat name.</summary>
    static IEnumerable<(uint Id, int Value)> ModStats(string modId)
    {
        if (ClientTables.TableOrNull("Mods")?.FirstOrDefault(r => r.Text("Id") == modId) is not { } mod) yield break;
        for (int i = 1; i <= 6; i++)
            if (mod.IntOrNull($"StatsKey{i}") is int row && row >= 0)
                yield return ((uint)row + 1, mod.IntOrNull($"Stat{i}Min") ?? 0);
    }

    // ---- Drops -----------------------------------------------------------------------------------------------------------

    static void Drops(DropRoll roll)
    {
        var player = roll.Player;
        var stats = AreaLeagueStats(player);
        int S(uint name) => stats.GetValueOrDefault(name);
        var rng = new Random(HashCode.Combine(roll.Monster.X, roll.Monster.Y, roll.Monster.VarietyId, player.Seed));
        // Any monster: the altar's and the map's chance of an additional Anarchy scarab; a map boss its scarabs.
        if (S(Stat.MapDisableScarabDrops) <= 0)
        {
            if (rng.Next(100) < S(Stat.ChancePctToDropAdditionalScarabAnarchy)) roll.Items.Add(AnarchyScarab(rng));
            if (roll.Monster.Rarity == MonsterRarity.Unique && Monsters.Find(roll.Monster.VarietyId) is { BossHealthBar: true } boss
                && !RogueExiles.Current.IsExile(boss.Id))
                for (int i = S(Stat.MapBossDropsXAdditionalScarabsAnarchy); i > 0; i--) roll.Items.Add(AnarchyScarab(rng));
        }
        if (!PlacedExiles.TryGetValue(player, out var placed)) return;
        Exile? exile;
        lock (placed)
        {
            if (!ReferenceEquals(placed.Area, player.Layout)) return;
            exile = placed.Exiles.FirstOrDefault(e => e.VarietyId == roll.Monster.VarietyId);
            if (exile is not null) placed.Exiles.Remove(exile);
        }
        if (exile is null) return;
        // An exile drops as a rare monster does, more richly: the wild exiles' increased rarity and the map's.
        roll.Rarity *= Math.Max(0, 1 + (S(Stat.MapWildRogueExileDroppedItemRarityPlusPct) + S(Stat.MapLeaguestoneRogueExilesDroppedItemRarityPlusPctFinal)) / 100.0);
        // Exile Currency Chance: each 100% one currency item, the rest a chance of one; the leaguestone's several.
        int currency = S(Stat.MapRogueExilesChanceToDropAdditionalCurrencyPct);
        int currencies = currency / 100 + (rng.Next(100) < currency % 100 ? 1 : 0) + S(Stat.MapRogueExilesDropAdditionalCurrencyItemsWithQuality);
        for (int i = 0; i < currencies; i++) roll.Items.Add(Currency(rng));
        if (S(Stat.MapRogueExileDropSkillGemWithQuality) > 0) roll.Items.Add("Metadata/Items/Currency/CurrencyGemQuality");
        // Rogue Trader's special reward and the Exceptional exile's (what they are is not in the tables): richer drops.
        if (exile.Special) roll.Rarity *= 2;
        if (exile.Exceptional) { roll.Quantity *= 2; roll.Rarity *= 2; }
        // The Anarchy scarabs' and leaguestones' rewards: additional jewels, rolled as the exile's own drops are.
        var jewels = JewelBases(player);
        for (int i = S(Stat.MapRogueExilesDropXAdditionalJewels); i > 0 && jewels.Count > 0; i--)
            roll.RolledItems.Add(jewels[rng.Next(jewels.Count)]);
        // What the exile's items drop as: fully linked, fractured, corrupted, then duplicated (a mirrored copy as well).
        if (S(Stat.MapRogueExilesDroppedItemsAreFullyLinked) > 0) roll.Changes.Add((d, _) => [FullyLinked(d)]);
        int fractured = S(Stat.MapRogueExilesDroppedItemsAreFracturedChancePct);
        if (fractured > 0) roll.Changes.Add((d, r) => [r.Next(100) < fractured ? Fractured(d) : d]);
        if (S(Stat.MapRogueExilesDroppedItemsAreCorrupted) > 0) roll.Changes.Add((d, _) => [Modifiable(d) ? d with { Item = d.Item with { Corrupted = true } } : d]);
        if (S(Stat.MapRogueExilesDroppedItemsAreDuplicated) > 0)
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
    static List<string> JewelBases(Player player) => Jewels.GetValue(global::Game.ItemsFor(player), catalog =>
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
