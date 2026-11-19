using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Harbinger league data: the client's <c>Harbingers</c> table (normal and strong Harbinger varieties by level and
/// weight), the King Harbinger boss and its helper/obelisk varieties, wave summoning profiles from
/// <c>GrantedEffectStatSetsPerLevel</c>, and the 3.29 active currency shard drop table.
/// </summary>
public static class HarbingerCatalog
{
    public enum HarbingerKind { Normal, Strong, King }

    /// <summary>One <c>Harbingers</c> table row whose monster variety is supported by the server.</summary>
    public sealed record Entry(int Row, string Variety, int Weight, int MinLevel, int MaxLevel, bool Strong);

    /// <summary>Per-wave summoning cadence, counts, rarity limits and life-loss permyriad per summoned minion killed
    /// (from <c>HarbingerSummonMonsters</c>, <c>HarbingerSummonMonsters2</c> and <c>HarbingerSummonMonstersAtlas</c>).</summary>
    public sealed record WaveProfile(double CooldownSeconds, int BaseCount, int ExtraCountChancePct, int Permyriad,
        int MaxMagic, int MaxRare, int MagicChancePct, int RareChancePct, int AliveCap);

    /// <summary>One currency shard entry in the Harbinger drop pool.</summary>
    public sealed record ShardEntry(string Id, double Weight, int MinStack, int MaxStack);

    // King Harbinger (First Wave / Harbinger Scarab of Regency): MonsterVarieties 5868 (<<HRB30>>, crown + cape visuals).
    public const string KingVariety = "Metadata/Monsters/Avatar/AvatarBossAtlas";

    // The King Harbinger's invulnerable helper avatars (SSMHarbingerGankSquad): MonsterVarieties 5870..5875.
    public static readonly string[] KingHelperVarieties =
    [
        "Metadata/Monsters/Avatar/AvatarAtlasMinion1",
        "Metadata/Monsters/Avatar/AvatarAtlasMinion2",
        "Metadata/Monsters/Avatar/AvatarAtlasMinion3",
        "Metadata/Monsters/Avatar/AvatarAtlasMinion4",
        "Metadata/Monsters/Avatar/AvatarAtlasMinion5",
        "Metadata/Monsters/Avatar/AvatarAtlasMinion6",
    ];

    // Empowered Obelisks (Harbinger Scarab of Obelisks, map_harbinger_scarab_empower): MonsterVarieties 5906..5908.
    public static readonly string[] EmpoweredObeliskVarieties =
    [
        "Metadata/Monsters/Totems/HarbingerTotemScarab2",
        "Metadata/Monsters/Totems/HarbingerTotemScarab3",
        "Metadata/Monsters/Totems/HarbingerTotemScarab4",
    ];

    // Fallback summoned minion varieties if a map area has no supported scattered pack entries.
    public static readonly string[] FallbackMinionVarieties =
    [
        "Metadata/Monsters/Cannibal/CannibalHarbingerCold",
        "Metadata/Monsters/Cannibal/CannibalHarbingerLightning",
        "Metadata/Monsters/ShieldCrabs/ShieldCrabHarbinger_",
        "Metadata/Monsters/SandSpitters/SandSpitterHarbinger",
        "Metadata/Monsters/CrabParasite/CrabParasiteSmallHarbinger",
        "Metadata/Monsters/HarbingerTiger/HarbingerTiger",
    ];

    // BuffDefinitions rows: 916 = harbinger_minion_new (blue Harbinger tint/spawn visual), 930 = harbinger_minion_buff.
    public const ushort MinionBuffRow = 916, MinionEmpowerBuffRow = 930;

    // MiscAnimated AO paths (rows 1594 and 1576): summon circle for wave spawns and burst for Harbinger defeat.
    public const string SummonCircleAo = "Metadata/Effects/Spells/monsters_effects/league_avatar/Summons/summon_circle.ao";
    public const string DefeatBurstAo = "Metadata/Effects/Spells/monsters_effects/league_avatar/Summons/avatarsummon.ao";

    // Base shard stack counts in tenths from Mods.dat: HarbingerCurrencyShards (22), HarbingerCurrencyShardsStrong (38),
    // HarbingerCurrencyShardsStrongAtlas (104), HarbingerCurrencyShardsObelisk (5..20).
    public const int NormalBaseTenths = 22, StrongBaseTenths = 38, KingBaseTenths = 104;
    public const int ObeliskMinTenths = 5, ObeliskMaxTenths = 20;
    // LeagueAvatarStrongAtlas grants harbinger_currency_shard_stack_size_+% = 100 (double stack size per drop).
    public const int KingStackSizePct = 100;

    // Active 3.29 currency shards (all 11 whose full-stack orb is usable in 3.29, CurrencyUseType = 2).
    // Excluded in 3.29: Horizon, Harbinger's and Engineer's shards (full orbs disabled in client) and Scroll Fragment.
    // Weights are estimated against world.db drop_currency_weights / community rarity tiers (see currency-drops.md).
    public static readonly IReadOnlyList<ShardEntry> Shards =
    [
        new("Metadata/Items/Currency/CurrencyUpgradeToMagicShard", 260, 2, 6),          // Transmutation Shard (DropLvl 1)
        new("Metadata/Items/Currency/CurrencyRerollMagicShard", 260, 2, 6),             // Alteration Shard (DropLvl 1)
        new("Metadata/Items/Currency/CurrencyUpgradeToRareShard", 140, 2, 5),           // Alchemy Shard (DropLvl 1)
        new("Metadata/Items/Currency/CurrencyRerollRareShard", 120, 1, 4),              // Chaos Shard (DropLvl 8)
        new("Metadata/Items/Currency/CurrencyUpgradeToRareAndSetSocketsShard", 110, 1, 4), // Binding Shard (DropLvl 25)
        new("Metadata/Items/Currency/CurrencyUpgradeMagicToRareShard", 75, 1, 3),       // Regal Shard (DropLvl 12)
        new("Metadata/Items/Currency/CurrencyRemoveModShard", 32, 1, 3),                // Annulment Shard (DropLvl 35)
        new("Metadata/Items/Currency/CurrencyRerollUniqueShard", 28, 1, 2),             // Ancient Shard (DropLvl 35)
        new("Metadata/Items/Currency/CurrencyAddModToRareShard", 18, 1, 2),             // Exalted Shard (DropLvl 35)
        new("Metadata/Items/Currency/CurrencyFractureRareShard", 3.5, 1, 2),            // Fracturing Shard (DropLvl 35)
        new("Metadata/Items/Currency/CurrencyDuplicateShard", 0.15, 1, 1),              // Mirror Shard (DropLvl 35)
    ];

    static readonly Lazy<IReadOnlyList<Entry>> Data = new(Load);
    static readonly Lazy<HashSet<string>> ManagedVarieties = new(() =>
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { KingVariety };
        foreach (var e in Data.Value) set.Add(e.Variety);
        foreach (string v in KingHelperVarieties) set.Add(v);
        foreach (string v in EmpoweredObeliskVarieties) set.Add(v);
        return set;
    });

    public static IReadOnlyList<Entry> All => Data.Value;

    public static bool IsManagedVariety(string? varietyId) => varietyId is not null && ManagedVarieties.Value.Contains(varietyId);

    static IReadOnlyList<Entry> Load()
    {
        if (!ClientTables.Has("Harbingers")) return [];
        var result = new List<Entry>();
        int index = 0;
        foreach (var row in ClientTables.Table("Harbingers"))
        {
            int at = index++;
            if (Game.Monsters.Find(row.Int("MonsterVarietiesKey").ToString()) is not { Supported: true } variety) continue;
            int weight = row.Int("__col1");
            int minLevel = row.Int("MinLevel");
            int maxLevel = row.Int("MaxLevel");
            // Rows 0..18 have weight 100 (LeagueAvatarNormal); rows 19..27 have weight 20, MinLevel 60 (LeagueAvatarStrong).
            bool strong = weight <= 20 && minLevel >= 60;
            result.Add(new(at, variety.Id, weight, minLevel, maxLevel, strong));
        }
        return result;
    }

    public static IReadOnlyList<Entry> Pool(int level) =>
        [.. All.Where(e => e.MinLevel <= level && level <= e.MaxLevel)];

    public static Entry? Pick(IReadOnlyList<Entry> pool, Random random)
    {
        if (pool.Count == 0) return null;
        int total = 0;
        foreach (var e in pool) total += Math.Max(1, e.Weight);
        int roll = random.Next(total);
        foreach (var e in pool)
            if ((roll -= Math.Max(1, e.Weight)) < 0) return e;
        return pool[^1];
    }

    /// <summary>Wave summoning cadence, monster count, rarity limits and life-loss permyriad for a Harbinger tier and area level.</summary>
    public static WaveProfile Profile(HarbingerKind kind, int level) => kind switch
    {
        HarbingerKind.King => new(CooldownSeconds: 2.0, BaseCount: 15, ExtraCountChancePct: 75, Permyriad: 50,
            MaxMagic: 8, MaxRare: 2, MagicChancePct: 20, RareChancePct: 5, AliveCap: 22),
        HarbingerKind.Strong => level >= 85
            ? new(CooldownSeconds: 2.0, BaseCount: 8, ExtraCountChancePct: 75, Permyriad: 100,
                MaxMagic: 8, MaxRare: 1, MagicChancePct: 20, RareChancePct: 20, AliveCap: 12)
            : new(CooldownSeconds: 2.0, BaseCount: 8, ExtraCountChancePct: 61, Permyriad: 181,
                MaxMagic: 6, MaxRare: 1, MagicChancePct: 20, RareChancePct: 20, AliveCap: 12),
        _ => level switch
        {
            <= 44 => new(CooldownSeconds: 3.0, BaseCount: 3, ExtraCountChancePct: 0, Permyriad: 700,
                MaxMagic: 1, MaxRare: 0, MagicChancePct: 10, RareChancePct: 10, AliveCap: 12),
            <= 72 => new(CooldownSeconds: 3.0, BaseCount: 6, ExtraCountChancePct: 0, Permyriad: 410,
                MaxMagic: 2, MaxRare: level >= 46 ? 1 : 0, MagicChancePct: 10, RareChancePct: 10, AliveCap: 12),
            <= 84 => new(CooldownSeconds: 3.0, BaseCount: 8, ExtraCountChancePct: 61, Permyriad: 198,
                MaxMagic: 4, MaxRare: 1, MagicChancePct: 10, RareChancePct: 10, AliveCap: 12),
            _ => new(CooldownSeconds: 3.0, BaseCount: 8, ExtraCountChancePct: 75, Permyriad: 120,
                MaxMagic: 4, MaxRare: 1, MagicChancePct: 10, RareChancePct: 10, AliveCap: 12),
        },
    };

    /// <summary>
    /// Rolls Harbinger or Empowered Obelisk currency shard drops (unaffected by IIQ/IIR).
    /// Returns individual stacks (not coalesced) so Warhoards duplication visibly emits duplicate stacks.
    /// </summary>
    public static List<ItemDrops.Drop> RollShards(ItemCatalog catalog, int areaLevel, int baseTenths, int stackSizePct,
        MapMods? mods, Random random)
    {
        var eligible = new List<(ShardEntry Entry, ItemCatalog.BaseType Type)>(Shards.Count);
        foreach (var entry in Shards)
            if (catalog.Find(entry.Id) is { Supported: true } type && type.DropLevel <= areaLevel)
                eligible.Add((entry, type));
        if (eligible.Count == 0) return [];

        int tenths = baseTenths;
        int extraStackChance = 0;
        int fullCurrencyStat = 0;
        int dupChance = 0;
        int luckyDraws = 1;
        bool singleType = false;

        if (mods is not null)
        {
            tenths += Math.Max(0, mods.Get("map_harbinger_additional_currency_shard_stack_tenths_to_drop"));
            tenths += Math.Max(0, mods.Get("map_harbingers_drops_additional_currency_shards")) * 10;
            extraStackChance = Math.Max(0, mods.Get("map_harbinger_additional_currency_shard_stack_chance_%"));
            fullCurrencyStat = Math.Max(0, mods.Get("map_harbingers_have_chance_to_drop_full_currency"));
            dupChance = Math.Max(0, mods.Get("map_harbinger_currency_shards_duplicate_chance"));
            if (mods.Get("map_harbinger_base_currency_shard_stack_tenths_to_drop_best_of_3_outcomes") > 0) luckyDraws = 3;
            else if (mods.Get("map_harbinger_lucky_shard_drops") > 0) luckyDraws = 2;
            singleType = mods.Get("map_harbinger_single_currency_type") > 0;
        }

        int stacks = Math.Max(0, tenths) / 10 + (random.Next(10) < Math.Max(0, tenths) % 10 ? 1 : 0);
        if (extraStackChance > 0)
            stacks += extraStackChance / 100 + (random.Next(100) < extraStackChance % 100 ? 1 : 0);
        if (stacks <= 0) return [];

        double totalWeight = 0;
        foreach (var (entry, _) in eligible) totalWeight += entry.Weight;

        (ShardEntry Entry, ItemCatalog.BaseType Type) PickShard()
        {
            var best = eligible[0];
            for (int d = 0; d < luckyDraws; d++)
            {
                double roll = random.NextDouble() * totalWeight;
                var candidate = eligible[^1];
                foreach (var item in eligible)
                    if ((roll -= item.Entry.Weight) < 0) { candidate = item; break; }
                // Lucky / best-of-3 picks the rarer shard (lower drop weight).
                if (d == 0 || candidate.Entry.Weight < best.Entry.Weight) best = candidate;
            }
            return best;
        }

        (ShardEntry Entry, ItemCatalog.BaseType Type)? fixedShard = singleType ? PickShard() : null;
        int fullOrbChance = fullCurrencyStat > 0 ? Math.Min(75, 25 * fullCurrencyStat) : 0;
        double sizeScale = Math.Max(0.1, (100 + stackSizePct) / 100.0);

        var drops = new List<ItemDrops.Drop>(stacks * (dupChance > 0 ? 2 : 1));
        for (int i = 0; i < stacks; i++)
        {
            var (entry, shardType) = fixedShard ?? PickShard();
            int rawSize = random.Next(entry.MinStack, entry.MaxStack + 1);
            int size = Math.Max(1, (int)Math.Round(rawSize * sizeScale));
            bool upgradeToOrb = fullOrbChance > 0 && random.Next(100) < fullOrbChance
                || shardType.BecomesWhenFull && size >= shardType.MaxStack;

            ItemDrops.Drop drop;
            if (upgradeToOrb && shardType.BecomesWhenFull && catalog.Row(shardType.FullStackRow) is { Supported: true } orbType)
            {
                drop = new(ItemCatalog.Create(orbType), orbType);
            }
            else
            {
                ushort clamped = (ushort)Math.Clamp(size, 1, Math.Max(1, shardType.MaxStack - 1));
                drop = new(ItemCatalog.Create(shardType) with { StackSize = clamped }, shardType);
            }

            drops.Add(drop);
            if (dupChance > 0 && random.Next(100) < dupChance)
                drops.Add(new(drop.Item, drop.Type));
        }

        return drops;
    }
}
