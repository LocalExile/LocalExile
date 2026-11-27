using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Delirium;

/// <summary>The five Delirium scarabs use the instance's composed map stats, including Atlas and Idol sources.</summary>
public static class DeliriumScarabs
{
    public const string Content = "DeliriumMirror", Delusions = "Delirium.Delusions";
    static ModCatalog.Mod? stacks;
    static ModCatalog.Mod[] rewards = [];

    public static void Load()
    {
        stacks = Program.Items.Mods.All.FirstOrDefault(m => m.Id == "AfflictionMapDeliriumStacks");
        rewards = [.. ClientTables.TableOrEmpty("AfflictionRewardMapMods")
            .Select(r => Program.Items.Mods.Row(r.Int("ModsKey"))).OfType<ModCatalog.Mod>()];
    }

    public static bool Eligible(ushort area, IReadOnlyDictionary<uint, int> stats)
    {
        int S(uint stat) => stats.GetValueOrDefault(stat);
        return MapDevices.IsMapArea(area) && S(Stat.MapNukeEverything) == 0 && S(Stat.IsBlightedMap) == 0
            && S(Stat.GoAwayOtherLeagues) == 0 && S(Stat.AtlasKeystoneNoExtraContent) == 0
            && (S(Stat.MapDisableLeagues) & (1 << LeagueChances.Delirium)) == 0;
    }

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening || !MapDevices.IsMapArea(opening.Area.Hash16)) return;
        if (AfflictionRuleset.EncounterRow is not null && AfflictionRuleset.TerrainPlugin is not null
            && opening.Mods.Get(Stat.MapAreaUnderlevelled) == 0 && Eligible(opening.Area.Hash16, opening.Mods.Stats))
            request.HandledContent.Add(Content);
        if (stacks is not null && rewards.Length >= 5) request.HandledContent.Add(Delusions);
    }

    public static int MonsterDepth(int depth, int increase) =>
        (int)Math.Clamp(Math.Max(0L, depth) * Math.Max(0L, 100L + increase) / 100, 0, 100);

    public static Item Prepare(Player player, Item item)
    {
        if (!MapDevices.IsMapArea(player.Layout.Hash) || MapModsOf(player) is not { } mods) return item;
        return Enchant(item, ItemsFor(player), mods.Get(Stat.MapMapsDropWithAfflictionEnchantmentsChancePct), AreaRng(player));
    }

    public static Item Enchant(Item item, ItemCatalog catalog, int chance, Random rng)
    {
        var layer = stacks;
        if (chance <= 0 || layer is null || rewards.Length < 5 || item.Rarity == ItemRarity.Unique
            || catalog.ByHash(item.TypeHash) is not { Class: "Map" or "MapKey" } type
            || !CurrencyEffects.IsMap(item, type)
            || item.Mods.Any(m => catalog.Mods.ByHash(m.Hash)?.Id == layer.Id)
            || !RandomRolls.Chance(chance, 100, rng)) return item;
        // The client gives the 20% layer and nine reward slots, but not the distribution of found layers/types.
        // Uniform 1..5 layers and reward types are estimates until an official drop capture supplies the weights.
        int count = rng.Next(1, 6);
        var mods = item.Mods.ToList();
        mods.Add(new(ModSection.Enchant, layer.Hash, [20 * count]));
        var pool = DeliriumMirrorEncounter.MirrorRewardPool;
        for (int i = 0; i < count; i++) mods.Add(new(ModSection.Enchant, rewards[i].Hash, [pool[rng.Next(pool.Length)] + 1]));
        return item with { Mods = mods };
    }
}
