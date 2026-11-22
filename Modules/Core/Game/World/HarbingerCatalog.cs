using System.Collections.Concurrent;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Harbinger league data: the client's <c>Harbingers</c> table (normal and strong Harbinger varieties by level and
/// weight), the King Harbinger boss and its helper/obelisk varieties, wave summoning profiles from
/// <c>GrantedEffectStatSetsPerLevel</c>, variety skill kits and obelisk auras from <c>MonsterVarieties</c>/<c>Mods</c>,
/// and the 3.29 active currency shard drop table.
/// </summary>
public static class HarbingerCatalog
{
    public enum HarbingerKind { Normal, Strong, King }

    public enum SkillRole
    {
        Summon,
        Convocation,
        WarpFlame,
        MinionBuffWeak,
        MinionBuffStrong,
        FireMortar,
        BeamCannon,
        BallLightning,
        RighteousFire,
        SkyBeam,
        ChargedBlast,
        Upheaval,
        AllyDamageAura,
        AllyActionSpeedAura,
        PlayerCurseAura,
    }

    /// <summary>One <c>Harbingers</c> table row whose monster variety is supported by the server.</summary>
    public sealed record Entry(int Row, string Variety, int Weight, int MinLevel, int MaxLevel, bool Strong);

    /// <summary>Per-wave summoning cadence, counts, rarity limits, life-loss permyriad and <c>skill_repeat_count</c>
    /// (from <c>HarbingerSummonMonsters</c>, <c>HarbingerSummonMonsters2</c> and <c>HarbingerSummonMonstersAtlas</c>).</summary>
    public sealed record WaveProfile(double CooldownSeconds, int BaseCount, int ExtraCountChancePct, int Permyriad,
        int MaxMagic, int MaxRare, int MagicChancePct, int RareChancePct, int AliveCap, int RepeatCount = 0);

    /// <summary>One playable granted skill on a Harbinger, King helper or Empowered Obelisk.</summary>
    public sealed record ActorSkill(int EffectRow, string EffectId, ushort SkillId, SkillRole Role,
        double CastSeconds, double CooldownSeconds);

    /// <summary>One species-mod ally aura on an Empowered Obelisk (<c>HarbingerTotemScarab3</c>).</summary>
    public sealed record ObeliskAura(string ModId, ushort BuffRow, double Radius, IReadOnlyDictionary<string, int> Stats);

    /// <summary>Decoded skill kit and implicit auras for a managed Harbinger, King helper or Empowered Obelisk variety.</summary>
    public sealed record VarietyKit(string VarietyId, IReadOnlyList<ActorSkill> Skills, IReadOnlyList<ObeliskAura> ObeliskAuras);

    /// <summary>One currency shard entry in the Harbinger drop pool.</summary>
    public sealed record ShardEntry(string Id, double Weight, int MinStack, int MaxStack);

    // Disjoint skill-id base for Harbinger actors (player gems use 0x0102..0x0142, AtlasBosses use 0x4000..0x402c).
    public const ushort SkillIdBase = 0x4040;

    // GrantedEffects Ids for wave summoning across Normal, Strong and King tiers.
    public const string SummonNormalEffectId = "HarbingerSummonMonsters";
    public const string SummonStrongEffectId = "HarbingerSummonMonsters2";
    public const string SummonAtlasEffectId = "HarbingerSummonMonstersAtlas";

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

    // BuffDefinitions rows from client.db.
    public const ushort MinionBuffRow = 916;                 // harbinger_minion_new (blue Harbinger tint)
    public const ushort MinionEmpowerBuffRow = 930;          // harbinger_minion_buff
    public const ushort ColdBeaconBuffRow = 932;             // harbinger_cold_beacon
    public const ushort MortarBuffRow = 934;                 // harbinger_mortar_buff
    public const ushort DamageReductionAuraBuffRow = 137;    // monster_aura_damage_reduction
    public const ushort ActionSpeedAuraBuffRow = 921;        // harbinger_action_speed_aura
    public const ushort ActionSpeedAuraAtlasBuffRow = 2436;  // harbinger_action_speed_auraAtlasUber
    public const ushort HatredAuraBuffRow = 174;             // monster_aura_cold_damage
    public const ushort AngerAuraBuffRow = 175;              // monster_aura_fire_damage
    public const ushort WrathAuraBuffRow = 176;              // monster_aura_lightning_damage
    public const ushort TemporalChainsBuffRow = 69;          // curse_temporal_chains
    public const ushort EnfeebleBuffRow = 76;                // curse_enfeeble
    public const ushort AssassinsMarkBuffRow = 77;           // curse_assassins_mark

    // Projectiles.dat metadata paths and base speeds.
    public const string FireMortarProjectile = "Metadata/Projectiles/HarbingerFireMortar";
    public const string BallLightningProjectile = "Metadata/Projectiles/HarbingerBallLightning";
    public const int FireMortarSpeed = 750;
    public const int BallLightningSpeed = 300;

    // MiscAnimated AO paths verified in client.db.
    public const string SummonCircleAo = "Metadata/Effects/Spells/monsters_effects/league_avatar/Summons/summon_circle.ao";
    public const string DefeatBurstAo = "Metadata/Effects/Spells/monsters_effects/league_avatar/Summons/avatarsummon.ao";
    public const string WarpMarkerAo = "Metadata/Effects/Spells/monsters_effects/league_avatar/Summons/avatarteleport_marker.ao";
    public const string BeamMarkerAo = "Metadata/Effects/Spells/monsters_effects/harbinger_special_beam_cannon/marker.ao";
    public const string BeamCannonAo = "Metadata/Effects/Spells/monsters_effects/harbinger_special_beam_cannon/cold_beam.ao";
    public const string BeamNovaAo = "Metadata/Effects/Spells/monsters_effects/harbinger_special_beam_cannon/coldbeamnova.ao";
    public const string SkyBeamAo = "Metadata/Effects/Spells/monsters_effects/harbinger_solarbeam/solarbeam_beam.ao";
    public const string SkyBeamImpactAo = "Metadata/Effects/Spells/monsters_effects/harbinger_solarbeam/solarbeam_impact.ao";
    public const string FlameblastMarkerAo = "Metadata/Effects/Spells/monsters_effects/league_avatar/flameblast/flameblast_target.ao";
    public const string FlameblastExplodeAo = "Metadata/Effects/Spells/monsters_effects/league_avatar/flameblast/flameblast_explode_medium.ao";
    public const string UpheavalAo = "Metadata/Effects/Spells/monsters_effects/league_avatar/Upheaval/lightning_upheaval.ao";
    public const string RighteousFireAo = "Metadata/Effects/Spells/monsters_effects/league_avatar/Ri_fire_blue/blue_rifire.ao";
    public const string BurningGroundAo = "Metadata/Effects/Spells/ground_effects/fire/fire1.ao";

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
    static readonly ConcurrentDictionary<(HarbingerKind Kind, int Level), WaveProfile> WaveProfiles = new();
    static readonly ConcurrentDictionary<string, VarietyKit> VarietyKits = new(StringComparer.Ordinal);

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

    /// <summary>Wave summoning cadence, monster count, rarity limits, life-loss permyriad and <c>skill_repeat_count</c>
    /// read from <c>HarbingerSummonMonsters</c> / <c>2</c> / <c>Atlas</c> in <c>client.db</c>.</summary>
    public static WaveProfile Profile(HarbingerKind kind, int level) =>
        WaveProfiles.GetOrAdd((kind, Math.Clamp(level, 1, 100)), static key => LoadWaveProfile(key.Kind, key.Level));

    static WaveProfile LoadWaveProfile(HarbingerKind kind, int level)
    {
        string effectId = kind switch
        {
            HarbingerKind.King => SummonAtlasEffectId,
            HarbingerKind.Strong => SummonStrongEffectId,
            _ => SummonNormalEffectId,
        };
        var (magicChance, rareChance, aliveCap, fallbackCd) = kind switch
        {
            HarbingerKind.King => (20, 5, 22, 2.0),
            HarbingerKind.Strong => (20, 20, 12, 2.0),
            _ => (10, 10, 12, 3.0),
        };

        int row = Program.Skills.EffectRow(effectId);
        if (row >= 0)
        {
            int effLevel = Program.Skills.MaxLevelAt(row, level);
            var stats = Program.Skills.EffectStats(row, effLevel);
            int Get(string name)
            {
                if (Program.Stats.Key(name) is not uint key) return 0;
                int sum = 0;
                foreach (var (k, v) in stats) if (k == key) sum += v;
                return sum;
            }
            int baseCount = Get("number_of_monsters_to_summon");
            if (baseCount > 0)
            {
                int cdMs = Program.Skills.CooldownMs(row, effLevel);
                double cd = cdMs > 0 ? cdMs / 1000.0 : fallbackCd;
                return new(
                    CooldownSeconds: cd,
                    BaseCount: baseCount,
                    ExtraCountChancePct: Math.Max(0, Get("harbinger_minion_extra_monster_chance_%")),
                    Permyriad: Math.Max(1, Get("harbinger_minion_remove_parent_life_on_death_permyriad")),
                    MaxMagic: Math.Max(0, Get("harbinger_minion_max_magic_count")),
                    MaxRare: Math.Max(0, Get("harbinger_minion_max_rare_count")),
                    MagicChancePct: magicChance,
                    RareChancePct: rareChance,
                    AliveCap: aliveCap,
                    RepeatCount: Math.Max(0, Get("skill_repeat_count")));
            }
        }

        // Fallback if client tables are unavailable.
        return kind switch
        {
            HarbingerKind.King => new(2.0, 15, 75, 50, 15, 1, 20, 5, 22, 2),
            HarbingerKind.Strong => level <= 72
                ? new(2.0, 6, 25, 400, 4, 1, 20, 20, 12, 1)
                : level <= 84
                    ? new(2.0, 8, 61, 181, 6, 1, 20, 20, 12, 2)
                    : new(2.0, 8, 75, 100, 8, 1, 20, 20, 12, 2),
            _ => level <= 44
                ? new(3.0, 3, 0, 700, 1, 0, 10, 10, 12, 1)
                : level <= 72
                    ? new(3.0, 6, level >= 46 ? 25 : 0, 410, 2, level >= 46 ? 1 : 0, 10, 10, 12, 1)
                    : level <= 84
                        ? new(3.0, 8, 61, 198, 4, 1, 10, 10, 12, 2)
                        : new(3.0, 8, 75, 120, 4, 1, 10, 10, 12, 2),
        };
    }

    /// <summary>Decodes the real <c>GrantedEffectsKeys</c> skills and <c>ModsKeys</c> implicit auras of a Harbinger,
    /// King helper or Empowered Obelisk variety from <c>client.db</c>.</summary>
    public static VarietyKit KitFor(string varietyId) =>
        VarietyKits.GetOrAdd(varietyId, LoadVarietyKit);

    static VarietyKit LoadVarietyKit(string varietyId)
    {
        var variety = Game.Monsters.Find(varietyId);
        if (variety is null) return new(varietyId, [], []);

        var skills = new List<ActorSkill>();
        var seenEffects = new HashSet<int>();
        if (variety.GrantedEffects is { Count: > 0 } effects)
        {
            foreach (int effectRow in effects)
            {
                if (effectRow < 0 || !seenEffects.Add(effectRow)) continue;
                string? effectId = Program.Skills.EffectId(effectRow);
                if (effectId is null || ClassifyEffect(effectId) is not { } role) continue;
                double castSec = Program.Skills.CastTimeMs(effectRow) is int cm and > 0 ? cm / 1000.0 : 0.8;
                int cdMs = Program.Skills.CooldownMs(effectRow, 1);
                double cdSec = cdMs > 0 ? cdMs / 1000.0 : DefaultCooldown(role, effectId);
                ushort skillId = (ushort)(SkillIdBase + skills.Count);
                skills.Add(new(effectRow, effectId, skillId, role, Math.Max(0.25, castSec), cdSec));
            }
        }

        var auras = LoadObeliskAuras(variety);
        return new(varietyId, skills, auras);
    }

    static SkillRole? ClassifyEffect(string effectId) => effectId switch
    {
        SummonNormalEffectId or SummonStrongEffectId or SummonAtlasEffectId => SkillRole.Summon,
        "AvatarConvocation" => SkillRole.Convocation,
        "HarbingerWarpFlame" or "HarbingerWarpFlameTotem" => SkillRole.WarpFlame,
        "EmptyActionSpellHarbingerBuffWeak" => SkillRole.MinionBuffWeak,
        "EmptyActionSpellHarbingerBuffStrong" => SkillRole.MinionBuffStrong,
        "AvatarFireMortar" or "AvatarFireMortarAtlas" => SkillRole.FireMortar,
        "HarbingerSpecialBeamCannon" or "HarbingerSpecialBeamCannonAtlas" => SkillRole.BeamCannon,
        "HarbingerBallLightning" or "HarbingerBallLightningAtlas" or "HarbingerBallLightningTotem" => SkillRole.BallLightning,
        "RighteousFireSacrifice" => SkillRole.RighteousFire,
        "AttachedAvatarSkyBeam" => SkillRole.SkyBeam,
        "AttachedVaalChargedBlast2" => SkillRole.ChargedBlast,
        "HarbingerUpheaval" => SkillRole.Upheaval,
        "AvatarAnger" or "AvatarHatred" or "AvatarWrath" => SkillRole.AllyDamageAura,
        "HarbingerActionSpeedAuraAtlas" => SkillRole.AllyActionSpeedAura,
        "AvatarEnfeebleAura" or "AvatarTemporalChainsAura" or "AvatarAssassinsMarkAura" => SkillRole.PlayerCurseAura,
        _ => null,
    };

    static double DefaultCooldown(SkillRole role, string effectId) => role switch
    {
        SkillRole.Summon => effectId == SummonNormalEffectId ? 3.0 : 2.0,
        SkillRole.Convocation => 10.0,
        SkillRole.WarpFlame => effectId.EndsWith("Totem", StringComparison.Ordinal) ? 4.0 : 6.0,
        SkillRole.MinionBuffWeak or SkillRole.MinionBuffStrong => 5.0,
        SkillRole.FireMortar => effectId.EndsWith("Atlas", StringComparison.Ordinal) ? 1.0 : 2.0,
        SkillRole.BeamCannon => effectId.EndsWith("Atlas", StringComparison.Ordinal) ? 3.5 : 8.0,
        SkillRole.BallLightning => effectId.EndsWith("Totem", StringComparison.Ordinal) ? 4.0
            : effectId.EndsWith("Atlas", StringComparison.Ordinal) ? 3.0 : 5.0,
        SkillRole.RighteousFire => 10.0,
        SkillRole.SkyBeam => 5.5,
        SkillRole.ChargedBlast => 6.0,
        SkillRole.Upheaval => 5.0,
        SkillRole.AllyActionSpeedAura => 10.0,
        SkillRole.AllyDamageAura or SkillRole.PlayerCurseAura => 0.0,
        _ => 5.0,
    };

    static IReadOnlyList<ObeliskAura> LoadObeliskAuras(MonsterCatalog.Variety variety)
    {
        if (variety.SpeciesModsOrEmpty.Count == 0 || !Program.Items.Mods.Loaded || !ClientTables.Has("BuffTemplates"))
            return [];

        int auraAoePct = 0;
        var auraMods = new List<ModCatalog.Mod>();
        foreach (int modRow in variety.SpeciesModsOrEmpty)
        {
            if (Program.Items.Mods.Row(modRow) is not { } mod) continue;
            foreach (var stat in mod.Stats)
                if (stat.Id == "base_aura_area_of_effect_+%")
                    auraAoePct += stat.Min;
            if (mod.BuffTemplate is not null)
                auraMods.Add(mod);
        }
        if (auraMods.Count == 0) return [];

        double radiusScale = Math.Sqrt(Math.Max(0.1, 1.0 + auraAoePct / 100.0));
        var result = new List<ObeliskAura>(auraMods.Count);
        foreach (var mod in auraMods)
        {
            if (MonsterAuraTemplate.Read(mod) is not { } aura) continue;
            var statMap = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in mod.Stats)
                if (s.Min != 0)
                    statMap[s.Id] = statMap.GetValueOrDefault(s.Id) + s.Min;
            if (statMap.Count == 0) continue;
            result.Add(new(mod.Id, aura.Buff, aura.Radius * radiusScale, statMap));
        }
        return result;
    }

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
            // In tenths like the base stat: the Atlas mod's 38 is the strong Harbinger's own 38 (3.8 stacks).
            tenths += Math.Max(0, mods.Get("map_harbingers_drops_additional_currency_shards"));
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
