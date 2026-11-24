using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using Kind = MonsterElementalAilments.Kind;

/// <summary>
/// Brand skill system (Storm Brand + Indecision, Armageddon Brand + Volatility/Recall, Penance Brand +
/// Dissipation/Conduction, Wintertide Brand, Arcanist Brand, Brand Recall, Brandsurge).
///
/// Verified 3.29.3c client presentation (<c>Client/Game.exe</c> + <c>client.db</c>):
/// - Ground (detached) brand: <see cref="EffectObject"/> (<c>Metadata/Effects/PermanentServerEffect</c>) with the
///   family's <c>MiscAnimated</c> <c>sigil.ao</c>.
/// - Attached brand: native <c>0x1c1</c> <c>ActiveSigil</c> buff on the monster (<c>1218 conduit_sigil</c>,
///   <c>1230 cataclysm_sigil</c>, <c>1547 magma_sigil</c>, <c>1548 immolation_sigil</c>, <c>1549 trigger_sigil</c>).
///   The client's <c>ActiveSigil</c> ctor (<c>0x1415c6860</c>) and update (<c>0x1415c6d60</c>) read <c>buff+0x80</c>
///   (<c>list</c>) entries <c>[0]</c> (MiscAnimated row index: <c>4478..4482</c>) and <c>[1]</c> (animation mode
///   <c>0 = fadein</c>, <c>1 = chargeup_start</c>) unchecked, so every <c>ActiveSigil</c> <c>0x1c1</c> sends
///   <c>list: [attachedMiscAnimatedRow, 0]</c>.
/// - Penance energy: <c>1551 magma_pustule</c> (<c>PenanceBrandCharge</c>) displays stacks relative to the maximum in <c>list[0]</c>.
/// - Wintertide DoT: <c>1550 immolation_brand_burn</c> (primary) and <c>1552 immolation_brand_burn_secondary</c>
///   (Wintertide's End), neither reading <c>list</c>.
/// - HUD counters: <c>SigilDisplay</c> stats (<c>current_number_of_active_*_sigils</c>,
///   <c>current_number_of_inactive_*_sigils</c>, <c>current_total_number_of_sigils</c>) published via
///   <see cref="GameEvents.PlayerStatSources"/> and <c>0x1b3</c> <see cref="InstanceEntities.StatsUpdate"/>.
/// - Beams: <see cref="BeamEffectObject"/> (<c>Metadata/Effects/ServerBeamEffect</c>) using <c>MiscBeams</c> rows.
/// </summary>
public static partial class Game
{
    public enum BrandFamily { Conduit, Cataclysm, Magma, Immolation, Trigger }

    public sealed record BrandPresentation(
        BrandFamily Family,
        ushort AttachedBuffRow,
        uint AttachedMiscAnimatedRow,
        string GroundAo,
        ushort AttachBeamRow,
        string RecallCircleAo,
        string ExplosionAo,
        string ActiveCountStat,
        string InactiveCountStat);

    static readonly BrandPresentation[] BrandPresentations =
    [
        new(BrandFamily.Conduit, 1218, 4478,
            "Metadata/Effects/Spells/conduit_sigil/lightning/sigil.ao", 114,
            "Metadata/Effects/Spells/conduit_sigil/lightning/recall_circle.ao",
            "Metadata/Effects/Spells/conduit_sigil/explosion/lightning_explosion.ao",
            "current_number_of_active_conduit_sigils", "current_number_of_inactive_conduit_sigils"),
        new(BrandFamily.Cataclysm, 1230, 4479,
            "Metadata/Effects/Spells/conduit_sigil/fire/sigil.ao", 120,
            "Metadata/Effects/Spells/conduit_sigil/fire/recall_circle.ao",
            "Metadata/Effects/Spells/conduit_sigil/explosion/fire_explosion.ao",
            "current_number_of_active_cataclysm_sigils", "current_number_of_inactive_cataclysm_sigils"),
        new(BrandFamily.Magma, 1547, 4481,
            "Metadata/Effects/Spells/conduit_sigil/phys_lightning/sigil.ao", 273,
            "Metadata/Effects/Spells/conduit_sigil/phys_lightning/recall_circle.ao",
            "Metadata/Effects/Spells/conduit_sigil/explosion/lightning_explosion.ao",
            "current_number_of_active_magma_sigils", "current_number_of_inactive_magma_sigils"),
        new(BrandFamily.Immolation, 1548, 4482,
            "Metadata/Effects/Spells/conduit_sigil/cold/sigil.ao", 281,
            "Metadata/Effects/Spells/conduit_sigil/cold/recall_circle.ao",
            "Metadata/Effects/Spells/conduit_sigil/explosion/cold_explosion.ao",
            "current_number_of_active_immolation_sigils", "current_number_of_inactive_immolation_sigils"),
        new(BrandFamily.Trigger, 1549, 4480,
            "Metadata/Effects/Spells/conduit_sigil/trigger/sigil.ao", 271,
            "Metadata/Effects/Spells/conduit_sigil/trigger/recall_circle.ao",
            "Metadata/Effects/Spells/conduit_sigil/explosion/arcane_explosion.ao",
            "current_number_of_active_trigger_sigils", "current_number_of_inactive_trigger_sigils"),
    ];

    const ushort StormBrandDamageBeamRow = 115;       // MiscBeams ConduitSigilBeamEffect
    const ushort PenanceBrandChargeBeamRow = 274;     // MiscBeams PenanceBrandChargeBeam
    const ushort BrandsurgeCastBeamRow = 297;         // MiscBeams BrandDetonateCastBeam
    const ushort PenanceEnergyBuffRow = 1551;         // BuffDefinitions magma_pustule (PenanceBrandCharge)
    const ushort WintertidePrimaryBuffRow = 1550;     // BuffDefinitions immolation_brand_burn
    const ushort WintertideSecondaryBuffRow = 1552;   // BuffDefinitions immolation_brand_burn_secondary

    const string StormBrandImpactAo = "Metadata/Effects/Spells/conduit_sigil/lightning/impact.ao";
    const string ArmageddonMeteorAo = "Metadata/Effects/Spells/conduit_sigil/fire/meteor.ao";
    const string ArmageddonImpactAo = "Metadata/Effects/Spells/conduit_sigil/fire/impact.ao";
    const string PenanceExplosionSmallAo = "Metadata/Effects/Spells/conduit_sigil/phys_lightning/explosion_S.ao";
    const string PenanceExplosionBigAo = "Metadata/Effects/Spells/conduit_sigil/phys_lightning/explosion_B.ao";
    const string WintertideNovaAo = "Metadata/Effects/Spells/conduit_sigil/cold/nova.ao";

    /// <summary>Base brand attachment search radius in cells (PoE: 2.8m = 28 cells).</summary>
    public const double BrandBaseAttachmentRangeCells = 28;
    const int PenanceMaxEnergy = 20;
    const double ArmageddonMeteorDelaySeconds = 0.6;
    const double BrandMinActivationSeconds = 0.033;

    static readonly string[] WintertideDotSupportMore =
    [
        "support_area_concentrate_area_damage_+%_final",
        "support_controlled_destruction_spell_damage_+%_final",
        "support_gem_elemental_damage_+%_final",
        "support_rapid_decay_damage_over_time_+%_final",
        "support_efficacy_spell_damage_+%_final",
        "support_efficacy_damage_over_time_+%_final",
        "support_bonechill_cold_damage_+%_final",
        "support_hypothermia_cold_damage_over_time_+%_final",
        "cruelty_damage_over_time_+%_final",
    ];

    public sealed class ActiveBrand
    {
        public required long Id;
        public required ushort SkillId;
        public required int Effect;
        public required string EffectId;
        public required BrandFamily Family;
        public required int Level;
        public required int Quality;
        public required SupportedGem Gem;
        public required (int X, int Y) At;
        public required double CreatedAt;
        public required double AttachedDurationTotal;
        public required double AttachedRemaining;
        public required double DetachedDurationTotal;
        public required double DetachedRemaining;
        public required bool InfiniteAttachedDuration;
        public required bool CannotBeRecalled;
        public required bool RemovedAfterActivating;
        public required bool ReattachOnActivation;
        public required bool PenancePulses;
        public required bool PenanceSpreads;
        public required bool PenanceExplodesOnDetach;
        internal PlacedAreaHit? Placement;

        public uint GroundObjectId;
        public uint AttachedMonsterId;
        public SpawnedMonster? AttachedMonster;
        public double AttachedAt;
        public double DetachedAt;
        public int RecallSearchBonus;
        public double NextActivationAt;

        // Penance Brand energy stacks on this brand (Dissipation) or contributed to attached monster
        public int Energy;
        // Enemies energized by Penance Brand of Conduction from this brand
        public readonly HashSet<uint> EnergizedMonsters = [];

        // Wintertide Brand stage count (1..20)
        public int Stages;
        public double WintertidePrimaryDps;
        public int AttachedTakenPct;
        // Arcanist Brand linked spell rotation cursor
        public int TriggerRotation;
        // Brandsurge overcharge state
        public bool Overcharged;
        public double OverchargedAt;
        public ushort OverchargeSkillId;
        public SupportedGem? OverchargeGem;
        public int OverchargeFasterActivationPctPerSec;
        public int OverchargeFasterDurationPctPerSec;
        public double OverchargeDamageMore = 1;
        public double OverchargeAreaMore = 1;

        public bool Attached => AttachedMonsterId != 0 && AttachedMonster is not null;
    }

    sealed class PendingMeteor
    {
        public required uint VisualId;
        public required ushort SkillId;
        public required int Effect;
        public required string EffectId;
        public required SupportedGem Gem;
        public required (int X, int Y) At;
        public required uint BrandedTargetId;
        public required double ImpactAt;
        public required bool FromRecall;
        public required int Radius;
        public required double AreaMore;
        public PlacedAreaHit? Placement;
    }

    internal sealed class WintertideEndDot(SpawnedMonster monster, double now)
    {
        public readonly SpawnedMonster Monster = monster;
        readonly List<(double Start, double Until, double Dps)> effects = [];
        double last = now, carry;
        public ushort BuffInstance;
        public double ShownUntil;
        public double Until => effects.Count == 0 ? 0 : effects.Max(e => e.Until);

        public void Apply(double start, double until, double dps) => effects.Add((start, until, dps));

        public int Advance(double now, double factor)
        {
            double damage = carry;
            while (last < now && effects.Count > 0)
            {
                double next = now, strongest = 0;
                foreach (var effect in effects)
                {
                    if (effect.Start > last) next = Math.Min(next, effect.Start);
                    else if (effect.Until > last) strongest = Math.Max(strongest, effect.Dps);
                    if (effect.Until > last) next = Math.Min(next, effect.Until);
                }
                damage += (next - last) * strongest * factor;
                last = next;
            }
            effects.RemoveAll(e => e.Until <= now);
            last = now;
            int whole = (int)Math.Min(int.MaxValue, Math.Floor(damage));
            carry = damage - whole;
            return whole;
        }
    }

    sealed class MonsterBrandVisuals
    {
        public readonly Dictionary<BrandFamily, (ushort Buff, int Stacks, double Until)> Attached = [];
        public ushort PenanceBuff;
        public int PenanceStacks;
        public ushort WintertidePrimaryBuff;
        public double WintertideCarry;
        public double WintertideLastAt;
    }

    sealed class MonsterBrandMark
    {
        public int AttachedCount;
        public int TakenPct;
        public int PenanceEnergy;
    }

    sealed class BrandState
    {
        public readonly List<ActiveBrand> Brands = [];
        public readonly List<PendingMeteor> Meteors = [];
        public readonly List<(uint ObjectId, double RemoveAt)> TempObjects = [];
        public readonly Dictionary<uint, MonsterBrandVisuals> MonsterVisuals = [];
        public readonly Dictionary<uint, WintertideEndDot> WintertideEnds = [];
        public double LastTickAt = double.NaN;
        public double LastBrandCastAt = double.NegativeInfinity;
        public long NextBrandId = 1;
        public int Epoch;

        // Cached HUD counts (O(1) read in PlayerStatSources)
        public readonly int[] ActiveByFamily = new int[5];
        public readonly int[] InactiveByFamily = new int[5];
        public int TotalBrands;
        public readonly int[] PublishedActive = new int[5];
        public readonly int[] PublishedInactive = new int[5];
        public int PublishedTotal;
    }

    static readonly RuleTable<Player, BrandState> BrandStates = new();
    static readonly RuleTable<SpawnedMonster, MonsterBrandMark> BrandMonsterMarks = new();
    static int _globalActiveBrandCount;

    static int BrandStat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    public static BrandFamily? BrandFamilyOf(string? effectId) => effectId switch
    {
        "StormBrand" or "StormBrandAltX" or "StormBrandAltY" => BrandFamily.Conduit,
        "ArmageddonBrand" or "ArmageddonBrandAltX" or "ArmageddonBrandAltY" => BrandFamily.Cataclysm,
        "PenanceBrand" or "PenanceBrandAltX" or "PenanceBrandAltY" => BrandFamily.Magma,
        "WintertideBrand" or "WintertideBrandAltX" or "WintertideBrandAltY" => BrandFamily.Immolation,
        "BrandSupport" => BrandFamily.Trigger,
        _ => null,
    };

    public static BrandPresentation PresentationOf(BrandFamily family) => BrandPresentations[(int)family];

    public static bool IsBrandSkill(Player player, int effect) =>
        SkillsFor(player).IsBrand(effect) && BrandFamilyOf(SkillsFor(player).EffectId(effect)) is not null;

    public static bool IsBrandRecall(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) == "BrandRecall";

    public static bool IsBrandsurge(Player player, int effect) =>
        IsBrandsurgeEffect(SkillsFor(player).EffectId(effect));

    public static bool IsBrandsurgeEffect(string? effectId) => effectId == "Brandsurge";

    public static int BrandCountFor(Player player) =>
        BrandStates.TryGetValue(player, out var state) ? state.TotalBrands : 0;

    public static int AttachedBrandCountFor(Player player)
    {
        if (!BrandStates.TryGetValue(player, out var state)) return 0;
        int sum = 0;
        for (int i = 0; i < state.ActiveByFamily.Length; i++) sum += state.ActiveByFamily[i];
        return sum;
    }

    public static IReadOnlyList<ActiveBrand> ActiveBrandsFor(Player player) =>
        BrandStates.TryGetValue(player, out var state) ? [.. state.Brands] : [];

    public static int BrandMaxTotal(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        int total = player.GetValueOrDefault(Stat.BaseTotalNumberOfSigilsAllowed)
            + own.GetValueOrDefault(Stat.BaseTotalNumberOfSigilsAllowed);
        if (total <= 0) total = PanelStatDefaults.Default(Stat.BaseTotalNumberOfSigilsAllowed);
        return Math.Clamp(total, 1, 20);
    }

    public static int BrandMaxPerTarget(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        // Note: BrandPlayerStats already adds +1 to base_number_of_sigils_allowed_per_target when keystone_runebinder
        // is present in player stats, so check raw sources or avoid double-counting.
        int perTarget = BrandStat(own, "base_number_of_sigils_allowed_per_target");
        if (perTarget <= 0) perTarget = 1;
        int playerBase = BrandStat(player, "base_number_of_sigils_allowed_per_target");
        if (playerBase > 0) perTarget += playerBase;
        else if (BrandStat(player, "keystone_runebinder") != 0) perTarget += 1;
        perTarget += BrandStat(own, "active_skill_brands_allowed_on_enemy_+")
            + BrandStat(player, "active_skill_brands_allowed_on_enemy_+");
        return Math.Clamp(perTarget, 1, 10);
    }

    public static double BrandAttachmentRange(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own,
        double detachedSeconds = 0, int recallBonusPct = 0)
    {
        long pct = (long)BrandStat(player, "sigil_target_search_range_+%")
            + BrandStat(own, "sigil_target_search_range_+%")
            + recallBonusPct;
        int perSec = BrandStat(player, "unattached_sigil_attachment_range_+%_per_second")
            + BrandStat(own, "unattached_sigil_attachment_range_+%_per_second");
        if (perSec > 0 && detachedSeconds > 0)
            pct += (long)Math.Floor(detachedSeconds) * perSec;
        return Math.Max(5, BrandBaseAttachmentRangeCells * Math.Max(0.1, 1 + pct / 100.0));
    }

    public static double BrandAttachedDurationSeconds(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        if (BrandStat(own, "brand_atttached_duration_is_infinite") != 0) return double.PositiveInfinity;
        int baseMs = GolemStat(own, Stat.BaseSkillEffectDuration);
        if (baseMs <= 0) return BrandStat(own, "brand_removed_after_activating") != 0 ? 10.0 : 5.0;
        return Math.Max(0.1, SkillDurationModifiers.Seconds(baseMs,
            k => player.GetValueOrDefault(k), k => own.GetValueOrDefault(k), SkillDurationModifiers.Kinds.Brand));
    }

    public static double BrandDetachedDurationSeconds(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        int baseMs = GolemStat(own, Stat.BaseSecondarySkillEffectDuration);
        if (baseMs <= 0) baseMs = 6000;
        return Math.Max(0.1, SkillDurationModifiers.Seconds(baseMs,
            k => player.GetValueOrDefault(k), k => own.GetValueOrDefault(k),
            SkillDurationModifiers.Kinds.Brand | SkillDurationModifiers.Kinds.Secondary));
    }

    public static int BrandCastSpeedBonusPct(Player player, int effect, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int> own) =>
        BrandCastSpeedBonusPct(player, effect, k => playerStats.GetValueOrDefault(k), k => own.GetValueOrDefault(k));

    public static int BrandCastSpeedBonusPct(Player player, int effect, Func<uint, int> playerStat, Func<uint, int> own)
    {
        if (!SkillsFor(player).IsBrand(effect)) return 0;
        int bonus = playerStat(Stat.CastSpeedForBrandSkillsPlusPct) + own(Stat.CastSpeedForBrandSkillsPlusPct);
        string? id = SkillsFor(player).EffectId(effect);
        if (id is "PenanceBrand" or "PenanceBrandAltX" or "PenanceBrandAltY")
            bonus += playerStat(Stat.PenanceBrandCastSpeedPlusPct) + own(Stat.PenanceBrandCastSpeedPlusPct);
        else if (id == "BrandSupport")
            bonus += playerStat(Stat.ArcanistBrandCastSpeedPlusPct) + own(Stat.ArcanistBrandCastSpeedPlusPct);
        return bonus;
    }

    public static double BrandActivationSeconds(Player player, SupportedGem gem, IReadOnlyDictionary<uint, int> playerStats,
        double attachedFraction = 0, double overchargeElapsed = 0, int overchargeFasterPctPerSec = 0)
    {
        var own = gem.Stats;
        int baseMs = BrandStat(own, "base_sigil_repeat_frequency_ms");
        if (baseMs <= 0) return 0.5;
        long increased = (long)BrandStat(playerStats, "sigil_repeat_frequency_+%") + BrandStat(own, "sigil_repeat_frequency_+%");
        if (BrandStat(own, "additive_cast_speed_modifiers_apply_to_sigil_repeat_frequency") != 0)
            increased += CastSpeedPct(playerStats) + BrandStat(own, "base_cast_speed_+%")
                + BrandCastSpeedBonusPct(player, gem.Effect, playerStats, own);
        string? effectId = SkillsFor(player).EffectId(gem.Effect);
        if (effectId is "ArmageddonBrand" or "ArmageddonBrandAltX" or "ArmageddonBrandAltY")
            increased += BrandStat(playerStats, "armageddon_brand_repeat_frequency_+%")
                + BrandStat(own, "armageddon_brand_repeat_frequency_+%");
        int notRecent = BrandStat(playerStats, "sigil_repeat_frequency_+%_if_havent_used_a_brand_skill_recently");
        if (notRecent != 0 && (!BrandStates.TryGetValue(player, out var st)
            || AreaTime(player) - st.LastBrandCastAt > 4.0))
            increased += notRecent;

        double more = Math.Max(0.05, 1 + BrandStat(own, "support_rapid_activation_brand_activation_rate_+%_final") / 100.0);
        if (attachedFraction <= 0.2)
            more *= Math.Max(0.05, 1 + BrandStat(playerStats, "brand_activation_rate_+%_final_during_first_20%_of_active_duration") / 100.0);
        if (attachedFraction >= 0.8)
            more *= Math.Max(0.05, 1 + BrandStat(playerStats, "brand_activation_rate_+%_final_during_last_20%_of_active_duration") / 100.0);
        if (attachedFraction >= 0.75)
            more *= Math.Max(0.05, 1 + (BrandStat(playerStats, "combined_brand_activation_rate_+%_final_if_75%_attached_duration_expired")
                + BrandStat(own, "from_quality_brand_activation_rate_+%_final_if_75%_attached_duration_expired")) / 100.0);
        if (overchargeElapsed > 0 && overchargeFasterPctPerSec > 0)
            more *= 1 + overchargeElapsed * overchargeFasterPctPerSec / 100.0;

        double rate = Math.Max(0.05, (1 + increased / 100.0) * more);
        return Math.Max(BrandMinActivationSeconds, baseMs / 1000.0 / rate);
    }

    public static int BrandRecallCooldownBonusPct(Player player, IReadOnlyDictionary<uint, int> playerStats)
    {
        int bonus = BrandStat(playerStats, "sigil_recall_cooldown_speed_+%");
        int perBrand = BrandStat(playerStats, "sigil_recall_cooldown_speed_+%_per_brand_up_to_40%");
        if (perBrand > 0) bonus += Math.Min(40, perBrand * BrandCountFor(player));
        return bonus;
    }

    /// <summary>Hot-path-cheap player stat hook (<see cref="GameEvents.PlayerStatSources"/>): Runebinder +1 brand per
    /// target, SigilDisplay HUD counters, per-brand crit/cooldown bonuses, and recent brand flag.</summary>
    public static Dictionary<uint, int> BrandPlayerStats(Player player, Dictionary<uint, int> stats)
    {
        if (Stats.Key("keystone_runebinder") is uint rbKey && stats.GetValueOrDefault(rbKey) != 0
            && Stats.Key("base_number_of_sigils_allowed_per_target") is uint perKey)
            stats[perKey] = stats.GetValueOrDefault(perKey) + 1;

        if (!BrandStates.TryGetValue(player, out var state)) return stats;
        int total = state.TotalBrands;
        if (total > 0)
        {
            for (int i = 0; i < BrandPresentations.Length; i++)
            {
                var p = BrandPresentations[i];
                if (state.ActiveByFamily[i] > 0 && Stats.Key(p.ActiveCountStat) is uint ak)
                    stats[ak] = state.ActiveByFamily[i];
                if (state.InactiveByFamily[i] > 0 && Stats.Key(p.InactiveCountStat) is uint ik)
                    stats[ik] = state.InactiveByFamily[i];
            }
            if (Stats.Key("current_total_number_of_sigils") is uint tk) stats[tk] = total;
            if (Stats.Key("critical_strike_chance_+%_per_brand") is uint critPerKey
                && stats.GetValueOrDefault(critPerKey) is int critPer and not 0
                && Stats.Key("critical_strike_chance_+%") is uint critKey)
                stats[critKey] = stats.GetValueOrDefault(critKey) + critPer * total;
            if (Stats.Key("cooldown_speed_+%_per_brand_up_to_40%") is uint cdPerKey
                && stats.GetValueOrDefault(cdPerKey) is int cdPer and not 0
                && Stats.Key("base_cooldown_speed_+%") is uint cdKey)
                stats[cdKey] = stats.GetValueOrDefault(cdKey) + Math.Min(40, cdPer * total);
        }
        if (Stats.Key("have_used_a_brand_skill_recently") is uint recentKey
            && AreaTime(player) - state.LastBrandCastAt <= 4.0)
            stats[recentKey] = 1;
        return stats;
    }

    /// <summary>O(1) fast path for <see cref="MonsterDamageTaken"/>: zero overhead when no brands exist.</summary>
    public static int BrandMonsterTakenPctLocked(SpawnedMonster m) =>
        Volatile.Read(ref _globalActiveBrandCount) == 0 ? 0
        : BrandMonsterMarks.TryGetValue(m, out var mark) ? mark.TakenPct : 0;

    public static void RecordBrandSkillUsedLocked(Player player, double at)
    {
        BrandStates.GetOrCreateValue(player).LastBrandCastAt = at;
    }

    public static IDisposable CleanupBrandsOnUnload() => new BrandUnloadHandle();

    sealed class BrandUnloadHandle : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref _globalActiveBrandCount, 0);
    }

    public static void ClearBrands(Player player)
    {
        if (!BrandStates.TryGetValue(player, out var state)) return;
        foreach (var brand in state.Brands)
            ClearBrandMonsterMarkLocked(brand);
        int removed = state.Brands.Count;
        if (removed > 0) Interlocked.Add(ref _globalActiveBrandCount, -removed);
        state.Brands.Clear();
        state.Meteors.Clear();
        state.TempObjects.Clear();
        state.MonsterVisuals.Clear();
        state.WintertideEnds.Clear();
        Array.Clear(state.ActiveByFamily);
        Array.Clear(state.InactiveByFamily);
        state.TotalBrands = 0;
        BrandStates.Remove(player);
    }

    // ---- Casting: Brand, Brand Recall, Brandsurge -----------------------------------------------------------------------

    public static async Task<bool> CastBrand(Player player, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, Func<bool> current,
        SkillCriticalRoll? criticalRoll)
    {
        int? castMs = CastMs(player, gem.Effect, skillId);
        if (castMs is null) return false;
        double delay = castMs.Value / 1000.0 * Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.03, delay)));
        await WhilePaused(player);
        if (player.Closed || player.LeavingArea || !current() || player.ActionInterruptionGeneration != recoveryGeneration)
            return false;

        return await ResolveCastBrand(player, skillId, gem, to, recoveryGeneration, current);
    }

    internal static async Task<bool> ResolveCastBrand(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) to,
        int? recoveryGeneration, Func<bool> current)
    {
        bool spawned = false;
        await SendWorldBatch(player, [], admission =>
        {
            var bytes = new WorldRecordBuilder(player, admission);
            spawned = ResolveCastBrandLocked(player, skillId, gem.Effect, to, recoveryGeneration, current, bytes);
            return bytes.Count > 0 ? bytes.Build() : null;
        });

        if (spawned) await PublishMinionOutcomes(player);
        return spawned;
    }

    static bool ResolveCastBrandLocked(Player player, ushort skillId, int effect, (int X, int Y) to,
        int? recoveryGeneration, Func<bool> current, WorldRecordBuilder bytes, PlacedAreaHit? placement = null)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0
            || !current() || recoveryGeneration is int generation && player.ActionInterruptionGeneration != generation) return false;
        var supported = SupportedSkill(player, skillId);
        if (supported.Effect != effect || !supported.RequirementsMet) return false;
        string? effectId = SkillsFor(player).EffectId(effect);
        if (BrandFamilyOf(effectId) is not { } family) return false;

        double now = AreaTime(player);
        var state = BrandStates.GetOrCreateValue(player);
        state.Epoch = player.ProjectileEpoch;
        if (recoveryGeneration is not null) state.LastBrandCastAt = now;

        var playerStats = CharacterPlayerStats(player);
        var own = supported.Stats;
        int maxTotal = BrandMaxTotal(playerStats, own);
        int count = Math.Clamp(1 + BrandStat(playerStats, "additional_number_of_brands_to_create")
            + BrandStat(own, "additional_number_of_brands_to_create"), 1, maxTotal);
        int extraChance = BrandStat(playerStats, "create_additional_brand_%_chance")
            + BrandStat(own, "create_additional_brand_%_chance");
        if (extraChance > 0 && AreaRng(player).Next(100) < extraChance) count++;

        var place = player.Layout.WalkableFor(to.X, to.Y, 0) ? to : player.Walk.Position(now);
        var session = ProjectilesFor(player);

        for (int i = 0; i < count; i++)
        {
            while (state.Brands.Count >= maxTotal && state.Brands.Count > 0)
                ExpireBrandLocked(player, session, state, state.Brands[0], now, bytes, replaced: true);

            var brand = CreateBrandLocked(state, skillId, effect, effectId!, family, supported, playerStats, place, now);
            brand.Placement = placement;
            state.Brands.Add(brand);
            Interlocked.Increment(ref _globalActiveBrandCount);

            if (!TryAttachBrandLocked(player, session, state, brand, playerStats, now, bytes, excludeMonster: 0))
                SpawnDetachedVisualLocked(player, brand, bytes);
        }

        ReconcileBrandPresentationLocked(player, state, now, bytes);
        return true;
    }

    public static async Task<bool> CastBrandRecall(Player player, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        Func<bool> current)
    {
        bool recalled = false;
        bool paid = false;
        uint extraLifeSpent = 0;
        await SendWorldBatch(player, [], admission =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0 || !current())
                return null;
            if (!BrandStates.TryGetValue(player, out var state) || state.Brands.Count == 0) return null;

            double now = AreaTime(player);
            var recallGem = SupportedSkill(player, skillId);
            var playerStats = CharacterPlayerStats(player);
            var here = player.Walk.Position(now);
            int searchBonus = BrandStat(recallGem.Stats, "recall_sigil_target_search_range_+%")
                + BrandStat(playerStats, "recall_sigil_target_search_range_+%");
            int spendPct = BrandStat(recallGem.Stats, "brand_recall_spend_%_of_recalled_brands_cost");
            double extendAttached = BrandStat(recallGem.Stats, "sigil_recall_extend_base_skill_effect_duration") / 1000.0
                + BrandStat(playerStats, "sigil_recall_extend_base_skill_effect_duration") / 1000.0;
            double extendDetached = BrandStat(recallGem.Stats, "sigil_recall_extend_base_secondary_skill_effect_duration") / 1000.0
                + BrandStat(playerStats, "sigil_recall_extend_base_secondary_skill_effect_duration") / 1000.0;

            var bytes = new WorldRecordBuilder(player, admission);
            var session = ProjectilesFor(player);
            var recalledFamilies = new HashSet<BrandFamily>();

            foreach (var brand in state.Brands.ToArray())
            {
                if (!state.Brands.Contains(brand) || brand.CannotBeRecalled) continue;
                // Detach from old target (triggers Penance / Wintertide detach effects at old position)
                if (brand.Attached)
                    DetachBrandLocked(player, session, state, brand, now, bytes, spawnGround: false);
                else
                    RemoveDetachedVisualLocked(brand, bytes);

                // Move to player position and refresh/extend remaining duration
                brand.At = here;
                brand.RecallSearchBonus = searchBonus;
                brand.DetachedAt = now;
                if (!brand.InfiniteAttachedDuration && extendAttached > 0)
                    brand.AttachedRemaining = Math.Min(brand.AttachedDurationTotal, brand.AttachedRemaining + extendAttached);
                if (extendDetached > 0)
                    brand.DetachedRemaining = Math.Min(brand.DetachedDurationTotal, brand.DetachedRemaining + extendDetached);

                recalledFamilies.Add(brand.Family);
                recalled = true;

                // Reattach near player with Brand Recall search range bonus
                if (!TryAttachBrandLocked(player, session, state, brand, playerStats, now, bytes,
                    excludeMonster: 0, triggerOnAttach: false))
                    SpawnDetachedVisualLocked(player, brand, bytes);

                // Immediate activation on every recalled brand (bypassing Armageddon Brand of Recall's -80% penalty)
                if (state.Brands.Contains(brand))
                {
                    if (spendPct > 0)
                    {
                        if (SupportedSkill(player, brand.SkillId).Effect != brand.Effect ||
                            !PayMana(player, brand.SkillId, out var mana, out var life, spendPct, useSpell: false)) continue;
                        paid |= mana > 0 || life > 0;
                        extraLifeSpent += life;
                    }
                    ActivateBrandLocked(player, session, state, brand, playerStats, now, bytes, fromRecall: true);
                    double interval = BrandActivationSeconds(player, brand.Gem, playerStats, AttachedFraction(brand),
                        brand.Overcharged ? now - brand.OverchargedAt : 0, brand.OverchargeFasterActivationPctPerSec);
                    brand.NextActivationAt = now + interval;
                }
            }

            // Spawn recall circle art at player
            foreach (var fam in recalledFamilies)
                SpawnTempEffectLocked(player, state, here, PresentationOf(fam).RecallCircleAo, now + 0.6, bytes);

            ReconcileBrandPresentationLocked(player, state, now, bytes);
            return bytes.Count > 0 ? bytes.Build() : null;
        });

        if (paid) await SendSkillCost(player, extraLifeSpent, "Brand Recall activation");
        if (recalled) await PublishMinionOutcomes(player);
        return true;
    }

    public static async Task<bool> CastBrandsurge(Player player, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, Func<bool> current,
        SkillCriticalRoll? criticalRoll)
    {
        int? castMs = CastMs(player, gem.Effect, skillId);
        if (castMs is null) return false;
        double delay = castMs.Value / 1000.0 * Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.03, delay)));
        await WhilePaused(player);
        if (player.Closed || player.LeavingArea || !current() || player.ActionInterruptionGeneration != recoveryGeneration)
            return false;

        await SendWorldBatch(player, [], admission =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0
                || !current() || player.ActionInterruptionGeneration != recoveryGeneration) return null;
            if (!BrandStates.TryGetValue(player, out var state) || state.Brands.Count == 0) return null;

            double now = AreaTime(player);
            var surgeGem = SupportedSkill(player, skillId);
            var here = player.Walk.Position(now);
            int fasterAct = BrandStat(surgeGem.Stats, "brand_detonate_faster_activation_%_per_second");
            if (fasterAct <= 0) fasterAct = 35;
            int fasterDur = BrandStat(surgeGem.Stats, "brand_detonate_faster_duration_%_per_second");
            if (fasterDur <= 0) fasterDur = 150;
            double dmgMore = Math.Max(0.1, 1 + BrandStat(surgeGem.Stats, "brand_detonate_detonating_brand_all_damage_+%_final") / 100.0);
            double aoeMore = SkillArea.RadiusMultiplier(BrandStat(surgeGem.Stats, "brand_detonate_detonating_brand_area_of_effect_+%_final"));

            var bytes = new WorldRecordBuilder(player, admission);
            foreach (var brand in state.Brands)
            {
                if (Dist(brand.At, to) > 100 && Dist(brand.At, here) > 100) continue;
                brand.Overcharged = true;
                brand.OverchargedAt = now;
                brand.OverchargeSkillId = skillId;
                brand.OverchargeGem = surgeGem;
                brand.OverchargeFasterActivationPctPerSec = fasterAct;
                brand.OverchargeFasterDurationPctPerSec = fasterDur;
                brand.OverchargeDamageMore = dmgMore;
                brand.OverchargeAreaMore = aoeMore;
                // For infinite-duration base Penance Brand, give it a finite countdown so Brandsurge can detonate it
                if (brand.InfiniteAttachedDuration)
                {
                    brand.InfiniteAttachedDuration = false;
                    brand.AttachedDurationTotal = 2.0;
                    brand.AttachedRemaining = 2.0;
                }
                SpawnBeamLocked(player, state, here, player.Id, brand.At, brand.Attached ? brand.AttachedMonsterId : 0,
                    BrandsurgeCastBeamRow, now + 0.35, bytes);
            }
            return bytes.Count > 0 ? bytes.Build() : null;
        });

        return true;
    }

    // ---- Main 100ms Brand Tick (GameEvents.CompanionTick) ---------------------------------------------------------------

    public static async Task TickBrands(Player player, double now)
    {
        if (!BrandStates.TryGetValue(player, out var state)) return;
        await SendWorldBatch(player, [], admission =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return null;
            if (!BrandStates.TryGetValue(player, out var live) || !ReferenceEquals(live, state)) return null;
            if (state.Brands.Count == 0 && state.Meteors.Count == 0 && state.TempObjects.Count == 0
                && state.MonsterVisuals.Count == 0 && state.WintertideEnds.Count == 0)
                return null;

            double dt = double.IsNaN(state.LastTickAt) ? 0.1 : Math.Clamp(now - state.LastTickAt, 0, 0.5);
            state.LastTickAt = now;
            var bytes = new WorldRecordBuilder(player, admission);
            var session = ProjectilesFor(player);

            // On player death or area epoch change, tear down all active brands cleanly
            if (player.PlayerLife == 0 || state.Epoch != player.ProjectileEpoch)
            {
                foreach (var b in state.Brands.ToArray())
                    RemoveBrandCleanlyLocked(state, b, bytes);
                foreach (var m in state.Meteors)
                    if (m.VisualId != 0) bytes.Remove(m.VisualId);
                state.Meteors.Clear();
                foreach (var t in state.TempObjects) bytes.Remove(t.ObjectId);
                state.TempObjects.Clear();
                ClearAllBrandVisualsLocked(state, bytes);
                ReconcileBrandPresentationLocked(player, state, now, bytes);
                return bytes.Count > 0 ? bytes.Build() : null;
            }

            var playerStats = CharacterPlayerStats(player);

            // 1. Step all active brands
            foreach (var brand in state.Brands.ToArray())
            {
                if (!state.Brands.Contains(brand)) continue;
                double durationScale = brand.Overcharged
                    ? 1.0 + Math.Max(0, now - brand.OverchargedAt) * brand.OverchargeFasterDurationPctPerSec / 100.0
                    : 1.0;

                if (brand.Attached)
                {
                    bool targetAlive = player.Area.Monsters.TryGetValue(brand.AttachedMonsterId, out var target)
                        && ReferenceEquals(target, brand.AttachedMonster)
                        && target is { Hostile: true, Dead: false, Life: > 0 };
                    if (!targetAlive)
                    {
                        DetachBrandLocked(player, session, state, brand, now, bytes, spawnGround: true);
                        continue;
                    }

                    brand.At = target!.Walk.Position(now);
                    if (!brand.InfiniteAttachedDuration)
                    {
                        brand.AttachedRemaining -= dt * durationScale;
                        if (brand.AttachedRemaining <= 0)
                        {
                            ExpireBrandLocked(player, session, state, brand, now, bytes, replaced: false);
                            continue;
                        }
                    }

                    // Execute all due activations (handles sub-100ms cadence like high-cast-speed Penance Brand cleanly)
                    int guard = 0;
                    while (state.Brands.Contains(brand) && brand.Attached && now >= brand.NextActivationAt && guard++ < 5)
                    {
                        double interval = BrandActivationSeconds(player, brand.Gem, playerStats, AttachedFraction(brand),
                            brand.Overcharged ? now - brand.OverchargedAt : 0, brand.OverchargeFasterActivationPctPerSec);
                        brand.NextActivationAt += interval;
                        if (brand.NextActivationAt < now - interval) brand.NextActivationAt = now + interval;

                        ActivateBrandLocked(player, session, state, brand, playerStats, now, bytes, fromRecall: false);
                        if (!state.Brands.Contains(brand)) break;

                        if (brand.RemovedAfterActivating)
                        {
                            ExpireBrandLocked(player, session, state, brand, now, bytes, replaced: false);
                            break;
                        }

                        // Reattach on activation (Storm Brand of Indecision, Brand Mastery Brand3, Armageddon Brand enchant)
                        bool shouldReattach = brand.ReattachOnActivation
                            || BrandStat(playerStats, "brands_reattach_on_activation") != 0
                            || (brand.Family == BrandFamily.Cataclysm
                                && BrandStat(playerStats, "armageddon_brand_brands_attach_to_new_enemy_each_activation_%_chance") is int ch and > 0
                                && AreaRng(player).Next(100) < ch);
                        if (shouldReattach && brand.Attached)
                        {
                            uint prevTarget = brand.AttachedMonsterId;
                            var candidate = FindAttachTargetLocked(player, state, brand, playerStats, now, excludeMonster: prevTarget);
                            if (candidate is not null)
                            {
                                DetachBrandLocked(player, session, state, brand, now, bytes, spawnGround: false);
                                AttachToMonsterLocked(player, state, brand, candidate.Value.Id, candidate.Value.Monster, playerStats, now, bytes, triggerImmediately: false, session);
                            }
                        }
                    }
                }
                else
                {
                    brand.DetachedRemaining -= dt * durationScale;
                    if (brand.DetachedRemaining <= 0)
                    {
                        ExpireBrandLocked(player, session, state, brand, now, bytes, replaced: false);
                        continue;
                    }
                    TryAttachBrandLocked(player, session, state, brand, playerStats, now, bytes,
                        excludeMonster: 0);
                }
            }

            // 2. Step falling Armageddon Brand meteors
            for (int i = state.Meteors.Count - 1; i >= 0; i--)
            {
                var meteor = state.Meteors[i];
                if (now < meteor.ImpactAt) continue;
                state.Meteors.RemoveAt(i);
                if (meteor.VisualId != 0) bytes.Remove(meteor.VisualId);
                ResolveArmageddonMeteorImpactLocked(player, state, meteor, playerStats, now, bytes);
            }

            // 3. Step Wintertide Brand Cold DoTs (primary attached + secondary Wintertide's End)
            TickWintertideDotsLocked(player, state, now, dt, bytes);

            // 4. Clean up expired temporary effects/beams
            for (int i = state.TempObjects.Count - 1; i >= 0; i--)
            {
                if (now < state.TempObjects[i].RemoveAt) continue;
                bytes.Remove(state.TempObjects[i].ObjectId);
                state.TempObjects.RemoveAt(i);
            }

            // 5. Reconcile monster buffs and HUD brand counters
            ReconcileBrandPresentationLocked(player, state, now, bytes);
            return bytes.Count > 0 ? bytes.Build() : null;
        });
    }

    public static void BrandOnMonsterKill(KillContext kill)
    {
        var player = kill.Player;
        if (Volatile.Read(ref _globalActiveBrandCount) == 0) return;
        if (!BrandStates.TryGetValue(player, out var state)) return;
        // Branded enemy chaos explosion on death (branded_enemy_explode_for_25%_life_as_chaos_on_death_chance_%)
        if (BrandMonsterMarks.TryGetValue(kill.Monster, out var mark) && mark.AttachedCount > 0)
        {
            var playerStats = CharacterPlayerStats(player);
            int chance = BrandStat(playerStats, "branded_enemy_explode_for_25%_life_as_chaos_on_death_chance_%");
            if (state.Brands.Any(b => ReferenceEquals(b.AttachedMonster, kill.Monster) && b.Family is BrandFamily.Immolation or BrandFamily.Trigger))
                chance += BrandStat(playerStats, "wintertide_and_arcanist_brand_branded_enemy_explode_for_25%_life_as_chaos_on_death_chance_%");
            if (chance > 0 && AreaRng(player).Next(100) < chance && kill.Monster.MaxLife > 0)
            {
                double chaosDmg = kill.Monster.MaxLife * 0.25;
                double now = AreaTime(player);
                foreach (var (id, m) in player.Area.Monsters.ToArray())
                {
                    if (ReferenceEquals(m, kill.Monster) || m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false })
                        continue;
                    if (Dist(m.Walk.Position(now), kill.At) > 22) continue;
                    var packet = DamagePacket.Of(DamageType.Chaos, chaosDmg, chaosDmg);
                    var mitigated = DamageModel.Mitigate(packet, type => MonsterHitResistance(m, type, default, now));
                    int dmg = (int)Math.Floor(mitigated.Components.Sum(c => c.Range.Min) * MonsterDamageTaken(m, false, now, false, true, true));
                    if (dmg <= 0 || ApplyTypedMonsterHitLocked(player, m, dmg, dmg) is not { } hit) continue;
                    CommitAcceptedHitLocked(player, id, m, hit, dmg, now, "branded enemy explosion", playerKill: true);
                }
            }
        }
    }

    // ---- Core Lifecycle Helpers -----------------------------------------------------------------------------------------

    static ActiveBrand CreateBrandLocked(BrandState state, ushort skillId, int effect, string effectId,
        BrandFamily family, SupportedGem gem, IReadOnlyDictionary<uint, int> player, (int X, int Y) at, double now)
    {
        var own = gem.Stats;
        double attachedDur = BrandAttachedDurationSeconds(player, own);
        double detachedDur = BrandDetachedDurationSeconds(player, own);
        return new ActiveBrand
        {
            Id = state.NextBrandId++,
            SkillId = skillId,
            Effect = effect,
            EffectId = effectId,
            Family = family,
            Level = gem.Level,
            Quality = gem.Quality,
            Gem = gem,
            At = at,
            CreatedAt = now,
            DetachedAt = now,
            AttachedDurationTotal = attachedDur,
            AttachedRemaining = attachedDur,
            DetachedDurationTotal = detachedDur,
            DetachedRemaining = detachedDur,
            InfiniteAttachedDuration = BrandStat(own, "brand_atttached_duration_is_infinite") != 0,
            CannotBeRecalled = BrandStat(own, "brand_cannot_be_recalled") != 0,
            RemovedAfterActivating = BrandStat(own, "brand_removed_after_activating") != 0,
            ReattachOnActivation = BrandStat(own, "brands_reattach_on_activation") != 0,
            PenancePulses = BrandStat(own, "penance_brand_pulses_instead_of_explode") != 0,
            PenanceSpreads = BrandStat(own, "penance_brand_spreads_energy") != 0,
            PenanceExplodesOnDetach = BrandStat(own, "penance_brand_explodes_on_death_and_detach") != 0,
        };
    }

    static double AttachedFraction(ActiveBrand brand) =>
        brand.InfiniteAttachedDuration || brand.AttachedDurationTotal <= 0
            ? 0 : Math.Clamp(1.0 - brand.AttachedRemaining / brand.AttachedDurationTotal, 0, 1);

    static (uint Id, SpawnedMonster Monster)? FindAttachTargetLocked(Player player, BrandState state,
        ActiveBrand brand, IReadOnlyDictionary<uint, int> playerStats, double now, uint excludeMonster)
    {
        double detachedSecs = brand.Attached ? 0 : Math.Max(0, now - brand.DetachedAt);
        double range = BrandAttachmentRange(playerStats, brand.Gem.Stats, detachedSecs, brand.RecallSearchBonus);
        int perTargetLimit = BrandMaxPerTarget(playerStats, brand.Gem.Stats);

        uint bestId = 0;
        SpawnedMonster? bestMonster = null;
        double bestDist = double.MaxValue;
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (id == excludeMonster || m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || m.OwnerId is not null || MonsterAsleep(player, id)) continue;
            double d = Dist(m.Walk.Position(now), brand.At);
            if (d > range || d >= bestDist) continue;
            int attachedCount = 0;
            for (int i = 0; i < state.Brands.Count; i++)
                if (state.Brands[i].AttachedMonsterId == id) attachedCount++;
            if (attachedCount >= perTargetLimit) continue;
            bestDist = d;
            bestId = id;
            bestMonster = m;
        }
        return bestMonster is not null ? (bestId, bestMonster) : null;
    }

    static bool TryAttachBrandLocked(Player player,
        ProjectileSession session, BrandState state, ActiveBrand brand, IReadOnlyDictionary<uint, int> playerStats,
        double now, WorldRecordBuilder bytes, uint excludeMonster, bool triggerOnAttach = true)
    {
        var target = FindAttachTargetLocked(player, state, brand, playerStats, now, excludeMonster);
        if (target is null) return false;
        AttachToMonsterLocked(player, state, brand, target.Value.Id, target.Value.Monster, playerStats, now, bytes,
            triggerOnAttach, session);
        return true;
    }

    static void AttachToMonsterLocked(Player player, BrandState state, ActiveBrand brand, uint monsterId,
        SpawnedMonster monster, IReadOnlyDictionary<uint, int> playerStats, double now, WorldRecordBuilder bytes,
        bool triggerImmediately, ProjectileSession session)
    {
        var fromAt = brand.At;
        brand.RecallSearchBonus = 0;
        RemoveDetachedVisualLocked(brand, bytes);

        brand.AttachedMonsterId = monsterId;
        brand.AttachedMonster = monster;
        brand.AttachedAt = now;
        brand.At = monster.Walk.Position(now);

        var mark = BrandMonsterMarks.GetOrCreateValue(monster);
        mark.AttachedCount++;
        brand.AttachedTakenPct = BrandStat(playerStats, "sigil_attached_target_damage_taken_+%");
        mark.TakenPct += brand.AttachedTakenPct;

        // Visual attach beam from old location to target
        if (Dist(fromAt, brand.At) >= 2)
            SpawnBeamLocked(player, state, fromAt, 0, brand.At, monsterId, PresentationOf(brand.Family).AttachBeamRow, now + 0.25, bytes);

        double interval = BrandActivationSeconds(player, brand.Gem, playerStats, AttachedFraction(brand),
            brand.Overcharged ? now - brand.OverchargedAt : 0, brand.OverchargeFasterActivationPctPerSec);
        if (triggerImmediately)
        {
            brand.NextActivationAt = now + interval;
            ActivateBrandLocked(player, session, state, brand, playerStats, now, bytes, fromRecall: false);
            if (state.Brands.Contains(brand) && brand.RemovedAfterActivating)
                ExpireBrandLocked(player, session, state, brand, now, bytes, replaced: false);
        }
        else
        {
            brand.NextActivationAt = now + interval;
        }
    }

    static void DetachBrandLocked(Player player,
        ProjectileSession session, BrandState state, ActiveBrand brand, double now, WorldRecordBuilder bytes, bool spawnGround)
    {
        if (!brand.Attached) return;
        var oldMonster = brand.AttachedMonster!;
        uint oldId = brand.AttachedMonsterId;
        var at = oldMonster.Walk.Position(now);
        brand.At = at;

        var playerStats = CharacterPlayerStats(player);
        // Trigger end-of-attachment effects before detaching
        if (brand.PenanceExplodesOnDetach)
            TriggerPenanceExplosionLocked(player, state, brand, oldId, oldMonster, at, playerStats, now, bytes);
        else if (brand.Family == BrandFamily.Immolation)
            TriggerWintertideEndLocked(player, state, brand, at, playerStats, now, bytes);

        ClearBrandMonsterMarkLocked(brand);
        brand.AttachedMonsterId = 0;
        brand.AttachedMonster = null;
        brand.DetachedAt = now;
        brand.Energy = 0;

        if (spawnGround && state.Brands.Contains(brand))
            SpawnDetachedVisualLocked(player, brand, bytes);
    }

    static void ExpireBrandLocked(Player player,
        ProjectileSession session, BrandState state, ActiveBrand brand, double now, WorldRecordBuilder bytes, bool replaced)
    {
        if (!state.Brands.Contains(brand)) return;
        var playerStats = CharacterPlayerStats(player);
        bool wasAttached = brand.Attached;
        var oldMonster = brand.AttachedMonster;
        uint oldId = brand.AttachedMonsterId;
        var at = wasAttached && oldMonster is not null ? oldMonster.Walk.Position(now) : brand.At;

        // End-of-attachment effects (Penance Brand explosion, Wintertide's End)
        if (wasAttached && oldMonster is not null)
        {
            if (brand.PenanceExplodesOnDetach)
                TriggerPenanceExplosionLocked(player, state, brand, oldId, oldMonster, at, playerStats, now, bytes);
            else if (brand.Family == BrandFamily.Immolation)
                TriggerWintertideEndLocked(player, state, brand, at, playerStats, now, bytes);

            // Brand Mastery Brand1: "Recover 10% of Mana when a Brand expires while Attached"
            if (!replaced && BrandStat(playerStats, "recover_%_mana_when_attached_brand_expires") is int manaPct and > 0)
            {
                uint gain = (uint)Math.Max(1, player.Pools.Mana * (long)manaPct / 100);
                uint cap = (uint)Math.Max(0, UnreservedMana(player));
                if (player.PlayerMana < cap)
                {
                    player.PlayerMana = Math.Min(cap, player.PlayerMana + gain);
                    bytes.World(player.Id, InstanceProtocol.LifeUpdate(player.Id, player.PlayerMana, (uint)ReservedMana(player), pool: ManaPool));
                }
            }
        }

        // Brandsurge overcharged expiry explosion!
        if (brand.Overcharged && brand.OverchargeGem is not null)
            TriggerBrandsurgeExplosionLocked(player, state, brand, at, playerStats, now, bytes);

        RemoveBrandCleanlyLocked(state, brand, bytes);
    }

    static void RemoveBrandCleanlyLocked(BrandState state, ActiveBrand brand, WorldRecordBuilder bytes)
    {
        RemoveDetachedVisualLocked(brand, bytes);
        ClearBrandMonsterMarkLocked(brand);
        brand.AttachedMonsterId = 0;
        brand.AttachedMonster = null;
        if (state.Brands.Remove(brand))
            Interlocked.Decrement(ref _globalActiveBrandCount);
    }

    static void ClearBrandMonsterMarkLocked(ActiveBrand brand)
    {
        if (brand.AttachedMonster is not { } m) return;
        if (BrandMonsterMarks.TryGetValue(m, out var mark))
        {
            if (brand.Family == BrandFamily.Magma) mark.PenanceEnergy = 0;
            mark.TakenPct -= brand.AttachedTakenPct;
            brand.AttachedTakenPct = 0;
            brand.WintertidePrimaryDps = 0;
            if (mark.AttachedCount > 0) mark.AttachedCount--;
            if (mark.AttachedCount == 0 && mark.PenanceEnergy == 0)
                BrandMonsterMarks.Remove(m);
        }
    }

    // ---- Per-Brand Activation Dispatcher --------------------------------------------------------------------------------

    static void ActivateBrandLocked(Player player,
        ProjectileSession session, BrandState state, ActiveBrand brand, IReadOnlyDictionary<uint, int> playerStats,
        double now, WorldRecordBuilder bytes, bool fromRecall)
    {
        switch (brand.Family)
        {
            case BrandFamily.Conduit:
                ActivateStormBrandLocked(player, state, brand, playerStats, now, bytes);
                break;
            case BrandFamily.Cataclysm:
                ActivateArmageddonBrandLocked(player, state, brand, playerStats, now, bytes, fromRecall);
                break;
            case BrandFamily.Magma:
                ActivatePenanceBrandLocked(player, state, brand, playerStats, now, bytes);
                break;
            case BrandFamily.Immolation:
                ActivateWintertideBrandLocked(player, state, brand, playerStats, now);
                break;
            case BrandFamily.Trigger:
                ActivateArcanistBrandLocked(player, session, brand, playerStats, now);
                break;
        }
    }

    // ---- 1. Storm Brand (+ Indecision) ----------------------------------------------------------------------------------

    static void ActivateStormBrandLocked(
        Player player, BrandState state, ActiveBrand brand, IReadOnlyDictionary<uint, int> playerStats,
        double now, WorldRecordBuilder bytes)
    {
        var origin = brand.At;
        int radius = BrandEffectiveRadius(player, brand, playerStats);
        int beamTargets = Math.Max(1, BrandStat(brand.Gem.Stats, "conduit_sigil_number_of_beam_targets"));
        int extraChainChance = BrandStat(playerStats, "storm_brand_additional_chain_chance_%")
            + BrandStat(brand.Gem.Stats, "storm_brand_additional_chain_chance_%");
        if (extraChainChance > 0 && AreaRng(player).Next(100) < extraChainChance) beamTargets++;

        var targets = new List<(uint Id, SpawnedMonster Monster)>(beamTargets);
        if (brand.Attached && brand.AttachedMonster is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false })
            targets.Add((brand.AttachedMonsterId, brand.AttachedMonster));

        if (targets.Count < beamTargets)
        {
            var extras = player.Area.Monsters
                .Where(kv => kv.Key != brand.AttachedMonsterId
                    && kv.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    && kv.Value.OwnerId is null && !MonsterAsleep(player, kv.Key)
                    && Dist(kv.Value.Walk.Position(now), origin) <= radius)
                .OrderBy(kv => Dist(kv.Value.Walk.Position(now), origin))
                .ThenBy(kv => kv.Key)
                .Take(beamTargets - targets.Count);
            foreach (var kv in extras) targets.Add((kv.Key, kv.Value));
        }

        if (targets.Count == 0) return;
        int attachedMorePct = BrandStat(brand.Gem.Stats, "sigil_attached_target_hit_damage_+%_final");
        int extraPen = BrandStat(playerStats, "storm_brand_attached_target_lightning_penetration_%")
            + BrandStat(playerStats, "sigil_attached_target_lightning_penetration_%");

        foreach (var (tid, monster) in targets)
        {
            bool isBranded = tid == brand.AttachedMonsterId;
            double hitMore = isBranded ? Math.Max(0, 1 + attachedMorePct / 100.0) : 1.0;
            var tAt = monster.Walk.Position(now);
            SpawnBeamLocked(player, state, origin, brand.Attached ? brand.AttachedMonsterId : 0, tAt, tid,
                StormBrandDamageBeamRow, now + 0.25, bytes);
            SpawnTempEffectLocked(player, state, tAt, StormBrandImpactAo, now + 0.25, bytes);
            DealBrandSpellHitLocked(player, brand.SkillId, brand.Gem, playerStats, tid, monster,
                now, hitMore, ailmentMore: 1.0, extraPenetrationType: isBranded && extraPen > 0 ? (DamageType.Lightning, extraPen) : null,
                label: brand.EffectId, placement: brand.Placement);
        }
    }

    // ---- 2. Armageddon Brand (+ Volatility, Recall) ---------------------------------------------------------------------

    static void ActivateArmageddonBrandLocked(Player player, BrandState state, ActiveBrand brand,
        IReadOnlyDictionary<uint, int> playerStats, double now, WorldRecordBuilder bytes, bool fromRecall)
    {
        var at = brand.At;
        int radius = BrandEffectiveRadius(player, brand, playerStats);
        uint visualId = player.TakeObjectId();
        bytes.Create(visualId, InstanceEntities.CreateObject(visualId, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(at.X, at.Y, ArmageddonMeteorAo) { Flags0 = EffectObject.FixedRotation }.Blob()));
        bytes.Presentation(visualId, InstanceProtocol.EnterWakeZone(visualId));

        state.Meteors.Add(new PendingMeteor
        {
            VisualId = visualId,
            SkillId = brand.SkillId,
            Effect = brand.Effect,
            EffectId = brand.EffectId,
            Gem = brand.Gem,
            At = at,
            BrandedTargetId = brand.AttachedMonsterId,
            ImpactAt = now + ArmageddonMeteorDelaySeconds,
            FromRecall = fromRecall,
            Radius = radius,
            AreaMore = 1.0,
            Placement = brand.Placement,
        });
    }

    static void ResolveArmageddonMeteorImpactLocked(
        Player player, BrandState state, PendingMeteor meteor, IReadOnlyDictionary<uint, int> playerStats,
        double now, WorldRecordBuilder bytes)
    {
        float scale = (float)Math.Clamp(meteor.Radius / 22.0, 0.5, 3.0);
        SpawnTempEffectLocked(player, state, meteor.At, ArmageddonImpactAo, now + 0.5, bytes, scale);

        int attachedMorePct = BrandStat(meteor.Gem.Stats, "sigil_attached_target_hit_damage_+%_final");
        // Armageddon Brand of Recall (ArmageddonBrandAltY) has -80% hit damage on normal activations,
        // bypassed when triggered by Brand Recall.
        bool bypassPenalty = meteor.FromRecall && meteor.EffectId == "ArmageddonBrandAltY";
        int extraFirePen = BrandStat(playerStats, "armageddon_brand_attached_target_fire_penetration_%")
            + BrandStat(playerStats, "sigil_attached_target_fire_penetration_%");

        foreach (var (id, m) in player.Area.Monsters.ToArray())
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || m.OwnerId is not null || MonsterAsleep(player, id)
                || Dist(m.Walk.Position(now), meteor.At) > meteor.Radius) continue;
            bool isBranded = id == meteor.BrandedTargetId;
            double hitMore = bypassPenalty ? 1.0
                : isBranded ? Math.Max(0, 1 + attachedMorePct / 100.0)
                : meteor.EffectId == "ArmageddonBrandAltY" ? Math.Max(0, 1 + attachedMorePct / 100.0) : 1.0;
            DealBrandSpellHitLocked(player, meteor.SkillId, meteor.Gem, playerStats, id, m,
                now, hitMore, ailmentMore: 1.0,
                extraPenetrationType: isBranded && extraFirePen > 0 ? (DamageType.Fire, extraFirePen) : null,
                label: meteor.EffectId, placement: meteor.Placement);
        }
    }

    // ---- 3. Penance Brand (+ Dissipation, Conduction) -------------------------------------------------------------------

    static void ActivatePenanceBrandLocked(
        Player player, BrandState state, ActiveBrand brand, IReadOnlyDictionary<uint, int> playerStats,
        double now, WorldRecordBuilder bytes)
    {
        if (!brand.Attached || brand.AttachedMonster is not { } target) return;
        uint targetId = brand.AttachedMonsterId;
        var at = target.Walk.Position(now);

        // Dissipation (PenanceBrandAltX): builds energy on the brand/enemy and pulses AoE damage EVERY activation!
        if (brand.PenancePulses)
        {
            var mark = BrandMonsterMarks.GetOrCreateValue(target);
            brand.Energy = mark.PenanceEnergy = Math.Min(PenanceMaxEnergy, mark.PenanceEnergy + 1);

            int perEnergyHit = BrandStat(brand.Gem.Stats, "magma_brand_hit_damage_+%_final_per_additional_pustule");
            int perEnergyAilment = BrandStat(brand.Gem.Stats, "magma_brand_ailment_damage_+%_final_per_additional_pustule");
            int attachedMorePct = BrandStat(brand.Gem.Stats, "sigil_attached_target_hit_damage_+%_final");
            int extraEnergy = Math.Max(0, brand.Energy - 1);
            double energyHitMore = Math.Max(0, 1 + extraEnergy * perEnergyHit / 100.0);
            double energyAilmentMore = Math.Max(0, 1 + extraEnergy * perEnergyAilment / 100.0);

            int radius = BrandEffectiveRadius(player, brand, playerStats, extraBaseRadius: brand.Energy / 2);
            bool atMax = brand.Energy >= PenanceMaxEnergy;
            SpawnTempEffectLocked(player, state, at, atMax ? PenanceExplosionBigAo : PenanceExplosionSmallAo,
                now + 0.45, bytes, (float)Math.Clamp(radius / 22.0, 0.5, 3.0));

            foreach (var (id, m) in player.Area.Monsters.ToArray())
            {
                if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    || m.OwnerId is not null || MonsterAsleep(player, id)
                    || Dist(m.Walk.Position(now), at) > radius) continue;
                double brandedMore = id == targetId ? Math.Max(0, 1 + attachedMorePct / 100.0) : 1.0;
                DealBrandSpellHitLocked(player, brand.SkillId, brand.Gem, playerStats, id, m,
                    now, energyHitMore * brandedMore, energyAilmentMore, label: "PenanceBrandAltX", placement: brand.Placement);
            }
            return;
        }

        // Base Penance Brand & Penance Brand of Conduction: build energy stacks on the attached enemy
        var targetMark = BrandMonsterMarks.GetOrCreateValue(target);
        if (targetMark.PenanceEnergy < PenanceMaxEnergy) targetMark.PenanceEnergy++;
        brand.Energy = targetMark.PenanceEnergy;
        if (brand.PenanceSpreads) brand.EnergizedMonsters.Add(targetId);

        // Conduction (PenanceBrandAltY) spreads energy to nearby enemies on activation!
        if (brand.PenanceSpreads)
        {
            int spreadRadius = 18 + BrandStat(brand.Gem.Stats, "penance_brand_base_spread_radius_+");
            var spreadTargets = player.Area.Monsters
                .Where(kv => kv.Key != targetId
                    && kv.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    && (!BrandMonsterMarks.TryGetValue(kv.Value, out var existing) || existing.PenanceEnergy == 0)
                    && kv.Value.OwnerId is null && !MonsterAsleep(player, kv.Key)
                    && Dist(kv.Value.Walk.Position(now), at) <= spreadRadius)
                .OrderBy(kv => Dist(kv.Value.Walk.Position(now), at))
                .Take(2);
            foreach (var (otherId, other) in spreadTargets)
            {
                var otherMark = BrandMonsterMarks.GetOrCreateValue(other);
                if (otherMark.PenanceEnergy < PenanceMaxEnergy) otherMark.PenanceEnergy++;
                brand.EnergizedMonsters.Add(otherId);
                SpawnBeamLocked(player, state, at, targetId, other.Walk.Position(now), otherId,
                    PenanceBrandChargeBeamRow, now + 0.25, bytes);
            }
        }

        // Base Penance Brand explodes & removes itself when reaching max energy (20)
        if (!brand.PenanceExplodesOnDetach && targetMark.PenanceEnergy >= PenanceMaxEnergy)
        {
            TriggerPenanceExplosionLocked(player, state, brand, targetId, target, at, playerStats, now, bytes);
            RemoveBrandCleanlyLocked(state, brand, bytes);
        }
    }

    static void TriggerPenanceExplosionLocked(
        Player player, BrandState state, ActiveBrand brand, uint primaryId, SpawnedMonster primary,
        (int X, int Y) primaryAt, IReadOnlyDictionary<uint, int> playerStats, double now, WorldRecordBuilder bytes)
    {
        int attachedMorePct = BrandStat(brand.Gem.Stats, "sigil_attached_target_hit_damage_+%_final");

        void ExplodeAt(uint centreId, SpawnedMonster? centreMonster, (int X, int Y) centreAt, int energy)
        {
            if (energy <= 0) return;
            bool full = energy >= PenanceMaxEnergy;
            int radius = BrandEffectiveRadius(player, brand, playerStats, extraBaseRadius: energy / 2);
            SpawnTempEffectLocked(player, state, centreAt, full ? PenanceExplosionBigAo : PenanceExplosionSmallAo,
                now + 0.55, bytes, (float)Math.Clamp(radius / 22.0, 0.6, 3.0));

            foreach (var (id, m) in player.Area.Monsters.ToArray())
            {
                if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    || m.OwnerId is not null || MonsterAsleep(player, id)
                    || (brand.PenanceSpreads ? id != centreId : Dist(m.Walk.Position(now), centreAt) > radius)) continue;
                double brandedMore = id == primaryId ? Math.Max(0, 1 + attachedMorePct / 100.0) : 1.0;
                DealBrandSpellHitLocked(player, brand.SkillId, brand.Gem, playerStats, id, m,
                    now, brandedMore, 1.0, label: brand.EffectId, placement: brand.Placement);
            }
            if (centreMonster is not null && BrandMonsterMarks.TryGetValue(centreMonster, out var cm))
                cm.PenanceEnergy = 0;
        }

        // Explode attached target
        int primaryEnergy = BrandMonsterMarks.TryGetValue(primary, out var pm) ? pm.PenanceEnergy : brand.Energy;
        ExplodeAt(primaryId, primary, primaryAt, primaryEnergy);

        // Conduction: explode any nearby enemies that received spread energy from this brand
        if (brand.PenanceSpreads && brand.EnergizedMonsters.Count > 0)
        {
            foreach (uint otherId in brand.EnergizedMonsters.ToArray())
            {
                if (otherId == primaryId) continue;
                if (!player.Area.Monsters.TryGetValue(otherId, out var other)) continue;
                if (!BrandMonsterMarks.TryGetValue(other, out var om) || om.PenanceEnergy <= 0) continue;
                ExplodeAt(otherId, other, other.Walk.Position(now), om.PenanceEnergy);
            }
        }
        brand.EnergizedMonsters.Clear();
        brand.Energy = 0;
    }

    // ---- 4. Wintertide Brand (Stacking Cold DoT + Wintertide's End) -----------------------------------------------------

    static double WintertideDpsFor(SupportedGem gem, IReadOnlyDictionary<uint, int> player, int stages, PlacedAreaHit? placement = null)
    {
        var merged = MergeSkillStats(player, gem.Stats);
        int Get(uint key) => merged.GetValueOrDefault(key);
        int Own(string name) => BrandStat(gem.Stats, name);
        double baseDps = Get(Stat.BaseColdDamageToDealPerMinute) / 60.0;
        if (!(baseDps > 0) || DamageStats.Prohibited(DamageType.Cold, Get)) return 0;

        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.ColdDamagePlusPct) + Get(Stat.ElementalDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.ColdDamageOverTimePlusPct) + Get(Stat.AreaDamagePlusPct)
            + Get(Stat.SigilDamagePlusPct) + Get(Stat.WinterBrandDamagePlusPct)
            + (Get(Stat.SpellDamageModifiersApplyToSkillDot) != 0 ? Get(Stat.SpellDamagePlusPct) : 0);
        double more = WintertideDotSupportMore.Aggregate(
            DamageStats.ElementalMore(Get(Stat.CombinedElementalDamagePlusPctFinal)),
            (m, name) => m * Math.Max(0, 1 + Own(name) / 100.0));
        if (placement is not null)
        {
            increased += (double)Get(placement.Device.Mine ? Stat.MineDamagePlusPct : Stat.TrapDamagePlusPct)
                + Get(Stat.TrapOrMineDamagePlusPct);
            more *= placement.DamageMore * Math.Max(0, 1 + Get(Stat.SupportTrapDamagePlusPctFinal) / 100d)
                * Math.Max(0, 1 + Get(Stat.SupportRemoteMine2DamagePlusPctFinal) / 100d)
                * Math.Max(0, 1 + Get(Stat.SupportTrapAndMineDamagePlusPctFinal) / 100d);
        }
        if (Get(Stat.KeystoneElementalOverloadDamagePlusPctFinal) is int eo and not 0)
            more *= Math.Max(0, 1 + eo / 100.0);
        int perStage = Math.Max(0, GolemStat(gem.Stats, Stat.ImmolationBrandBurnDamagePlusPctFinalPerStage));
        more *= 1.0 + Math.Max(0, stages) * perStage / 100.0;
        double dotMult = Math.Max(0, 1.0 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.ColdDotMultiplierPlus)) / 100.0);
        return baseDps * Math.Max(0, 1 + increased / 100.0) * more * dotMult;
    }

    static void ActivateWintertideBrandLocked(Player player, BrandState state, ActiveBrand brand,
        IReadOnlyDictionary<uint, int> playerStats, double now)
    {
        if (!brand.Attached || brand.AttachedMonster is not { } target) return;
        int maxStages = Math.Max(1, BrandStat(brand.Gem.Stats, "winter_brand_max_number_of_stages")
            + BrandStat(playerStats, "winter_brand_max_number_of_stages_+"));
        if (brand.Stages < maxStages) brand.Stages++;

        brand.WintertidePrimaryDps = WintertideDpsFor(brand.Gem, playerStats, brand.Stages, brand.Placement);

        // Inflict Chill on the attached enemy
        int chillEffect = BrandStat(playerStats, "winter_brand_chill_effect_+%") + BrandStat(brand.Gem.Stats, "winter_brand_chill_effect_+%");
        var merged = MergeSkillStats(playerStats, brand.Gem.Stats);
        int Get(uint k) => merged.GetValueOrDefault(k) + (k == Stat.ChillEffectPlusPct ? chillEffect : 0);
        InflictedLocked(player, target, target.Ailments.WithoutDamage(Kind.Chill, 2.0, now, Get, k => target.Effects[k], 1.0));
    }

    static void TriggerWintertideEndLocked(Player player, BrandState state, ActiveBrand brand,
        (int X, int Y) at, IReadOnlyDictionary<uint, int> playerStats, double now, WorldRecordBuilder bytes)
    {
        int radius = BrandEffectiveRadius(player, brand, playerStats);
        SpawnTempEffectLocked(player, state, at, WintertideNovaAo, now + 0.5, bytes, (float)Math.Clamp(radius / 22.0, 0.6, 3.0));

        double dps = WintertideDpsFor(brand.Gem, playerStats, Math.Max(1, brand.Stages), brand.Placement);
        if (!(dps > 0)) return;
        int baseEndMs = BrandStat(brand.Gem.Stats, "base_tertiary_skill_effect_duration");
        if (baseEndMs <= 0) baseEndMs = 2000;
        double duration = Math.Max(0.2, SkillDurationModifiers.Seconds(baseEndMs,
            k => playerStats.GetValueOrDefault(k), k => brand.Gem.Stats.GetValueOrDefault(k), SkillDurationModifiers.Kinds.Brand));
        double until = now + duration;

        var merged = MergeSkillStats(playerStats, brand.Gem.Stats);
        int chillEffect = BrandStat(merged, "winter_brand_chill_effect_+%");
        int Get(uint k) => merged.GetValueOrDefault(k) + (k == Stat.ChillEffectPlusPct ? chillEffect : 0);

        foreach (var (id, m) in player.Area.Monsters)
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || m.OwnerId is not null || Dist(m.Walk.Position(now), at) > radius) continue;
            if (!state.WintertideEnds.TryGetValue(id, out var end) || !ReferenceEquals(end.Monster, m))
                state.WintertideEnds[id] = end = new(m, now);
            end.Apply(now, until, dps);
            InflictedLocked(player, m, m.Ailments.WithoutDamage(Kind.Chill, duration, now, Get, k => m.Effects[k], 1.0));
        }
    }

    static void TickWintertideDotsLocked(
        Player player, BrandState state, double now, double dt, WorldRecordBuilder bytes)
    {
        // Primary attached Wintertide Brand DoTs (multiple attached brands stack on the same enemy!)
        var targets = state.Brands.Where(b => b.Attached && b.WintertidePrimaryDps > 0).GroupBy(b => b.AttachedMonsterId);
        foreach (var group in targets)
        {
            uint id = group.Key;
            if (!player.Area.Monsters.TryGetValue(id, out var m) || m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }) continue;
            double totalDps = group.Where(b => ReferenceEquals(b.AttachedMonster, m)).Sum(b => b.WintertidePrimaryDps);
            if (!(totalDps > 0)) continue;

            var vis = state.MonsterVisuals.TryGetValue(id, out var existing) ? existing : state.MonsterVisuals[id] = new();
            double step = vis.WintertideLastAt > 0 ? Math.Clamp(now - vis.WintertideLastAt, 0, 0.5) : dt;
            vis.WintertideLastAt = now;
            double total = vis.WintertideCarry + totalDps * step * MonsterDotTaken(m, DamageType.Cold, now);
            int damage = (int)Math.Min(int.MaxValue, Math.Floor(total));
            vis.WintertideCarry = total - damage;
            if (damage <= 0 || ApplyMonsterHitLocked(player, m, damage, allyAt: m.Walk.Position(now)) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(player, id, m, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(player, id, m, hit, damage, now, "Wintertide Brand", playerKill: true);
        }

        // Secondary Wintertide's End DoTs (after detach/expiry)
        foreach (var (id, end) in state.WintertideEnds.ToArray())
        {
            var m = end.Monster;
            bool alive = player.Area.Monsters.TryGetValue(id, out var live) && ReferenceEquals(live, m)
                && m is { Hostile: true, Dead: false, Life: > 0 };
            int damage = alive ? end.Advance(now, m.Invulnerable ? 0 : MonsterDotTaken(m, DamageType.Cold, now)) : 0;
            if (!alive || end.Until <= now)
            {
                if (end.BuffInstance != 0)
                {
                    if (player.Area.Monsters.ContainsKey(id)) bytes.World(id, InstanceEntities.RemoveBuff(id, end.BuffInstance));
                    end.BuffInstance = 0;
                }
                state.WintertideEnds.Remove(id);
            }
            else if (end.BuffInstance == 0 || end.ShownUntil != end.Until)
            {
                if (end.BuffInstance == 0) end.BuffInstance = player.NextBuff++;
                end.ShownUntil = end.Until;
                bytes.World(id, InstanceEntities.AddBuff(id, end.BuffInstance, WintertideSecondaryBuffRow, player.Id,
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0.1, end.Until - now))));
            }
            if (damage <= 0 || ApplyMonsterHitLocked(player, m, damage, allyAt: m.Walk.Position(now)) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(player, id, m, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(player, id, m, hit, damage, now, "Wintertide's End", playerKill: true);
        }
    }

    // ---- 5. Arcanist Brand (Linked Spell Triggering) --------------------------------------------------------------------

    static List<(ushort Id, SupportedGem Gem)> ArcanistBrandLinkedSpellsLocked(Player player, SupportedGem brand)
    {
        var result = new List<(ushort, SupportedGem)>();
        foreach (var source in PlayerSkillSourcesLocked(player).Where(s => s.Active && s.SourceGem is not null
            && s.Identity.Host == brand.Host && s.Effect != brand.Effect).OrderBy(s => s.Identity.Socket))
        {
            var linked = SupportedSkill(player, source.Id);
            if (linked.Group == brand.Group && linked.RequirementsMet && linked.Supports.Any(s => s.Effect.Id == "SupportBrandSupport")
                && TriggeredSpellExecutable(player, source.Id, linked))
                result.Add((source.Id, linked));
        }
        return result;
    }

    static void ActivateArcanistBrandLocked(Player player, ProjectileSession session,
        ActiveBrand brand, IReadOnlyDictionary<uint, int> playerStats, double now)
    {
        var liveBrand = SupportedSkill(player, brand.SkillId);
        if (liveBrand.Effect != brand.Effect || !liveBrand.RequirementsMet) return;
        var linked = ArcanistBrandLinkedSpellsLocked(player, liveBrand);
        if (linked.Count == 0) return;

        var catalog = SkillsFor(player);
        var aim = brand.At;
        var origin = brand.At;
        if (!brand.Attached)
        {
            var near = player.Area.Monsters.Values
                .Where(m => m is { Hostile: true, Dead: false, Life: > 0 } && Dist(m.Walk.Position(now), origin) <= 60)
                .OrderBy(m => Dist(m.Walk.Position(now), origin)).FirstOrDefault();
            aim = near?.Walk.Position(now) ?? (origin.X + 20, origin.Y);
        }

        var triggerState = LinkedTriggersOf(player);
        var rules = CombatLifetime;
        for (int i = 0; i < linked.Count; i++)
        {
            int idx = (brand.TriggerRotation + i) % linked.Count;
            var (spellSkillId, spellGem) = linked[idx];
            if (now < triggerState.ReadyAt.GetValueOrDefault(spellGem.Effect)) continue;
            if (GemSkillInstanceDisabled(player, spellGem.Effect, spellSkillId, triggered: true) is not null) continue;
            bool Current() => session.Active && CurrentGameRules && ReferenceEquals(CombatLifetime, rules)
                && !player.Closed && !player.LeavingArea && !player.Paused && player.PlayerLife > 0;

            var release = PrepareTriggeredPayload(player, session, spellSkillId, aim, Current, origin, brand.AttachedMonster);
            if (release is null) continue;
            if (!PayMana(player, spellSkillId, out uint mana, out uint life))
            {
                brand.TriggerRotation = (idx + 1) % linked.Count;
                return;
            }
            int cdMs = catalog.CooldownMs(spellGem.Effect, spellGem.Level);
            if (spellGem.Stats.GetValueOrDefault(Stat.SpellCastTimeAddedToCooldownIfTriggered) != 0)
                cdMs += catalog.CastTimeMs(spellGem.Effect);
            if (cdMs > 0)
            {
                double rate = Math.Max(0.05, 1 + (BrandStat(playerStats, "virtual_cooldown_speed_+%")
                    + BrandStat(playerStats, "base_spell_cooldown_speed_+%") + BrandStat(spellGem.Stats, "base_cooldown_speed_+%")) / 100.0);
                triggerState.ReadyAt[spellGem.Effect] = now + cdMs / 1000.0 / rate;
            }
            brand.TriggerRotation = (idx + 1) % linked.Count;

            if (mana > 0 || life > 0)
                session.Enqueue(() => SendSkillCost(player, life, "Arcanist Brand trigger cost"));

            release();
            return;
        }
    }

    // ---- 6. Brandsurge Explosion ----------------------------------------------------------------------------------------

    static void TriggerBrandsurgeExplosionLocked(
        Player player, BrandState state, ActiveBrand brand, (int X, int Y) at,
        IReadOnlyDictionary<uint, int> playerStats, double now, WorldRecordBuilder bytes)
    {
        if (brand.OverchargeGem is not { } surgeGem) return;
        int radius = (int)Math.Round(SkillRadius(player, surgeGem.Stats, spell: true, surgeGem.Effect) * brand.OverchargeAreaMore);
        SpawnTempEffectLocked(player, state, at, PresentationOf(brand.Family).ExplosionAo, now + 0.55, bytes,
            (float)Math.Clamp(radius / 24.0, 0.7, 3.0));

        foreach (var (id, m) in player.Area.Monsters.ToArray())
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || m.OwnerId is not null || MonsterAsleep(player, id)
                || Dist(m.Walk.Position(now), at) > radius) continue;
            DealBrandSpellHitLocked(player, brand.OverchargeSkillId, surgeGem, playerStats, id, m,
                now, brand.OverchargeDamageMore, brand.OverchargeDamageMore, label: "Brandsurge", placement: brand.Placement);
        }
    }

    // ---- Shared Brand Spell Hit Executor --------------------------------------------------------------------------------

    static int BrandEffectiveRadius(Player player, ActiveBrand brand, IReadOnlyDictionary<uint, int> playerStats, int extraBaseRadius = 0)
    {
        var own = brand.Gem.Stats;
        int extraAreaPct = AttachedFraction(brand) >= 0.5
            ? BrandStat(playerStats, "brand_area_of_effect_+%_if_50%_attached_duration_expired") : 0;
        if (brand.Family == BrandFamily.Magma)
            extraAreaPct += BrandStat(playerStats, "penance_brand_area_of_effect_+%");
        if (extraAreaPct != 0)
            own = new Dictionary<uint, int>(own) { [SkillArea.BaseIncreased] = own.GetValueOrDefault(SkillArea.BaseIncreased) + extraAreaPct };
        int radius = SkillRadius(player, own, spell: true, brand.Effect);
        return Math.Max(4, radius + extraBaseRadius);
    }

    static void DealBrandSpellHitLocked(
        Player player, ushort skillId, SupportedGem gem, IReadOnlyDictionary<uint, int> playerStats,
        uint monsterId, SpawnedMonster m, double now, double hitMore, double ailmentMore,
        (DamageType Type, int Pct)? extraPenetrationType = null, string label = "Brand", PlacedAreaHit? placement = null)
    {
        if (m.Invulnerable || MonsterBlocks(m, AreaRng(player), spell: true, now, gem.Stats)) return;
        if (SupportedHitInputsFor(player, skillId, capturedGem: gem) is not { } inputs) return;
        if (placement is not null)
        {
            inputs = inputs with { Caster = TotemCasterStats(inputs.Caster) };
            hitMore *= placement.DamageMore;
            ailmentMore *= placement.DamageMore;
        }

        long vsBlinded = Blinded(player, monsterId) ? inputs.Get(PlayerHitDamage.VsBlindedDamageStat) : 0;
        long brandSpecific = BrandFamilyOf(SkillsFor(player).EffectId(gem.Effect)) switch
        {
            BrandFamily.Conduit => BrandStat(playerStats, "storm_brand_damage_+%"),
            BrandFamily.Cataclysm => BrandStat(playerStats, "armageddon_brand_damage_+%"),
            BrandFamily.Magma => BrandStat(playerStats, "penance_brand_damage_+%"),
            _ => 0,
        };
        if (BrandMonsterMarks.TryGetValue(m, out var bm) && bm.AttachedCount > 0)
            brandSpecific += BrandStat(playerStats, "sigil_attached_target_damage_+%");
        var hit = SupportedHitFrom(inputs, suppress: true, additionalIncreased: vsBlinded + brandSpecific);
        if (hitMore != 1.0) hit = hit.Scale(hitMore);

        var gambler = GamblerFor(playerStats);
        var rolled = WithConditionalSupportDamageLocked(player, gem.Stats, attack: false, m,
            GameEvents.WithOutgoingDamage(player, playerStats, m, hit.Roll(AreaRng(player))), now);

        int critChance = CriticalChanceOn(player, monsterId, SupportedCriticalChance(player, skillId),
            SupportedCriticalChanceVsBlinded(player, skillId, release: false),
            SupportedCriticalChanceVsGround(player, skillId, release: false));
        bool critical = RollCriticalStrike(player, critChance, gem.Stats, attack: false, playerStats: playerStats);
        int critMult = CriticalMultiplierOn(m, SupportedCriticalMultiplier(player, skillId), now, playerStats, gem.Stats);

        var pen = Penetration.Of(playerStats, Stats, attack: false, gem.Stats);
        if (extraPenetrationType is { } ep)
            pen = ep.Type switch
            {
                DamageType.Fire => pen with { Fire = pen.Fire + ep.Pct },
                DamageType.Cold => pen with { Cold = pen.Cold + ep.Pct },
                DamageType.Lightning => pen with { Lightning = pen.Lightning + ep.Pct },
                _ => pen,
            };
        var resistMods = HitResistMods(playerStats, gem.Stats);

        var (damage, _, mitigated) = MonsterHitDamage(m, rolled, critical, attack: false, now, resistMods, pen,
            gambler.InvertsResistance(AreaRng(player)), AreaRng(player), trapOrMine: placement is not null);
        if (critical) damage = DamageModel.CriticalDamage(damage, critMult);
        if (damage <= 0) return;

        if (placement is null)
        {
            var taken = MonsterTakenComponents(m, mitigated, spell: true, now);
            var leech = HitLeechSources(playerStats, attack: false, gem.Stats, null, melee: false);
            LeechOnHitLocked(player, m, taken, damage, leech);
        }
        else if (placement.Device.Mine)
            LeechPlacedMineHitLocked(player, m, mitigated, spell: true, damage, now, playerStats, monsterId);

        var stun = OwnerStun(player, gem.Effect, m, damage, mitigated, melee: false, spell: true, critical, playerStats, gem.Stats);
        int chaosDamage = MonsterHitChaosDamage(m, mitigated, spell: true, now, damage,
            placement is null ? 0 : BearTrapDamageTaken(m, now));
        if (ApplyMonsterHitLocked(player, m, damage, null, null, allyAt: placement?.Device.At,
                cull: placement is null ? Cull.Of(playerStats, gem.Stats, critical, spell: true) : default, cullPlayer: playerStats,
                chaosDamage: chaosDamage)
            is not { } applied) return;
        if (applied.StageStarts) { CommitBossStage(player, monsterId, m, now); applied = applied with { StageStarts = false }; }

        if (placement is null)
        {
            CrueltyOnHitLocked(player, skillId, damage, m.Life + (uint)damage, m.MaxLife, gem.Stats, now);
            ArcaneSurgeOnSpellHitLocked(player, playerStats, m);
            if (ElementalOverloadCritLocked(player, skillId, SkillsFor(player).EffectId(gem.Effect), playerStats, critical) is { } eoPacket)
                player.Minions.OutputQueue.Add(new(eoPacket, "Elemental Overload from brand crit", WorldId: player.Id));
        }
        else OwnedKillLocked(m, applied.Life);

        if (!m.Dead)
        {
            var poison = SupportedPoison(player, skillId, spell: true, playerStats, additionalIncreased: vsBlinded, capturedInputs: inputs);
            if (ailmentMore != 1.0)
                poison = poison with { Min = poison.Min * ailmentMore, Max = poison.Max * ailmentMore,
                    CriticalMin = poison.CriticalMin * ailmentMore, CriticalMax = poison.CriticalMax * ailmentMore };
            AilmentsOnHitLocked(player, monsterId, m, mitigated, spell: true, critical, critMult, playerStats, gem.Stats, now,
                AreaRng(player), poison: poison, sourceSkill: skillId, blind: BlindHitInput.Capture(playerStats, gem.Stats), playerHit: placement is null);
            var ignite = SupportedIgnite(player, skillId, spell: true, playerStats, additionalIncreased: vsBlinded, capturedInputs: inputs);
            if (ignite.Max > 0)
            {
                if (ailmentMore != 1.0)
                    ignite = ignite with { Min = ignite.Min * ailmentMore, Max = ignite.Max * ailmentMore,
                        CriticalMin = ignite.CriticalMin * ailmentMore, CriticalMax = ignite.CriticalMax * ailmentMore };
                foreach (var packet in IgniteOnHitLocked(player, monsterId, m, ignite, critical, now, playerIgnite: placement is null) ?? [])
                    player.Minions.OutputQueue.Add(new(packet, $"{label} ignite", monsterId, m));
            }
        }
        CommitAcceptedHitLocked(player, monsterId, m, applied, damage, now, label, playerKill: placement is null, stun: stun);
    }

    // ---- Client Presentation: Effects, Beams, Monster Buffs, HUD Counters -----------------------------------------------

    static void SpawnDetachedVisualLocked(Player player, ActiveBrand brand, WorldRecordBuilder bytes)
    {
        if (brand.GroundObjectId != 0) return;
        uint id = player.TakeObjectId();
        brand.GroundObjectId = id;
        bytes.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(brand.At.X, brand.At.Y, PresentationOf(brand.Family).GroundAo) { Flags0 = EffectObject.FixedRotation }.Blob()));
        bytes.Presentation(id, InstanceProtocol.EnterWakeZone(id));
    }

    static void RemoveDetachedVisualLocked(ActiveBrand brand, WorldRecordBuilder bytes)
    {
        if (brand.GroundObjectId == 0) return;
        bytes.Remove(brand.GroundObjectId);
        brand.GroundObjectId = 0;
    }

    static void SpawnTempEffectLocked(Player player, BrandState state, (int X, int Y) at, string ao,
        double removeAt, WorldRecordBuilder bytes, float scale = 1f)
    {
        uint id = player.TakeObjectId();
        bytes.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(at.X, at.Y, ao) { Flags0 = EffectObject.FixedRotation, Scale = scale }.Blob()));
        bytes.Presentation(id, InstanceProtocol.EnterWakeZone(id));
        state.TempObjects.Add((id, removeAt));
    }

    static void SpawnBeamLocked(Player player, BrandState state, (int X, int Y) start, uint startObj,
        (int X, int Y) end, uint endObj, ushort beamRow, double removeAt, WorldRecordBuilder bytes)
    {
        uint id = player.TakeObjectId();
        bytes.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath),
            new BeamEffectObject(start.X, start.Y, startObj, end.X * BeamEffectObject.WorldPerCell,
                end.Y * BeamEffectObject.WorldPerCell, endObj, beamRow).Blob()));
        bytes.Presentation(id, InstanceProtocol.EnterWakeZone(id));
        state.TempObjects.Add((id, removeAt));
    }

    static void ClearAllBrandVisualsLocked(BrandState state, WorldRecordBuilder bytes)
    {
        foreach (var (monsterId, vis) in state.MonsterVisuals)
        {
            foreach (var (_, entry) in vis.Attached)
                if (entry.Buff != 0) bytes.World(monsterId, InstanceEntities.RemoveBuff(monsterId, entry.Buff));
            if (vis.PenanceBuff != 0) bytes.World(monsterId, InstanceEntities.RemoveBuff(monsterId, vis.PenanceBuff));
            if (vis.WintertidePrimaryBuff != 0) bytes.World(monsterId, InstanceEntities.RemoveBuff(monsterId, vis.WintertidePrimaryBuff));
        }
        state.MonsterVisuals.Clear();
        foreach (var (monsterId, end) in state.WintertideEnds)
            if (end.BuffInstance != 0) bytes.World(monsterId, InstanceEntities.RemoveBuff(monsterId, end.BuffInstance));
        state.WintertideEnds.Clear();
    }

    internal static byte[] PenanceEnergyBuff(uint monsterId, ushort instance, ushort energy, uint sourceId = 0) =>
        InstanceEntities.AddBuff(monsterId, instance, PenanceEnergyBuffRow, sourceId,
            stacks: energy, list: [PenanceMaxEnergy]);

    static void ReconcileBrandPresentationLocked(Player player, BrandState state, double now, WorldRecordBuilder bytes)
    {
        // 1. Recompute HUD counters
        Array.Clear(state.ActiveByFamily);
        Array.Clear(state.InactiveByFamily);
        state.TotalBrands = state.Brands.Count;

        // Per-monster desired attached brand buffs by family
        var desiredAttached = new Dictionary<uint, Dictionary<BrandFamily, (int Count, double MaxRemaining, ushort SkillId)>>();
        foreach (var b in state.Brands)
        {
            int fi = (int)b.Family;
            if (b.Attached && player.Area.Monsters.ContainsKey(b.AttachedMonsterId))
            {
                state.ActiveByFamily[fi]++;
                if (!desiredAttached.TryGetValue(b.AttachedMonsterId, out var map))
                    desiredAttached[b.AttachedMonsterId] = map = [];
                double rem = b.InfiniteAttachedDuration ? 0 : Math.Max(0.1, b.AttachedRemaining);
                if (map.TryGetValue(b.Family, out var prev))
                    map[b.Family] = (prev.Count + 1, Math.Max(prev.MaxRemaining, rem), b.SkillId);
                else
                    map[b.Family] = (1, rem, b.SkillId);
            }
            else
            {
                state.InactiveByFamily[fi]++;
            }
        }

        // 2. Reconcile monster buffs (ActiveSigil, magma_pustule, immolation_brand_burn)
        var allMonsters = new HashSet<uint>(state.MonsterVisuals.Keys);
        foreach (uint mid in desiredAttached.Keys) allMonsters.Add(mid);
        foreach (var brand in state.Brands)
            if (brand.PenanceSpreads) allMonsters.UnionWith(brand.EnergizedMonsters);

        foreach (uint mid in allMonsters)
        {
            bool exists = player.Area.Monsters.TryGetValue(mid, out var monster) && monster is { Dead: false, Life: > 0 };
            if (!state.MonsterVisuals.TryGetValue(mid, out var vis))
            {
                if (!exists) continue;
                vis = state.MonsterVisuals[mid] = new();
            }

            desiredAttached.TryGetValue(mid, out var wantedFamilies);
            // Remove ended attached family buffs
            foreach (var fam in vis.Attached.Keys.ToArray())
            {
                if (!exists || wantedFamilies is null || !wantedFamilies.ContainsKey(fam))
                {
                    if (player.Area.Monsters.ContainsKey(mid))
                        bytes.World(mid, InstanceEntities.RemoveBuff(mid, vis.Attached[fam].Buff));
                    vis.Attached.Remove(fam);
                }
            }
            // Add or update active attached family buffs (with verified 2-entry list [MiscAnimatedRow, 0]!)
            if (exists && wantedFamilies is not null)
            {
                foreach (var (fam, info) in wantedFamilies)
                {
                    var pres = PresentationOf(fam);
                    if (!vis.Attached.TryGetValue(fam, out var cur) || cur.Stacks != info.Count)
                    {
                        ushort buffId = cur.Buff != 0 ? cur.Buff : player.NextBuff++;
                        uint? dur = info.MaxRemaining > 0 ? BitConverter.SingleToUInt32Bits((float)info.MaxRemaining) : null;
                        bytes.World(mid, InstanceEntities.AddBuff(mid, buffId, pres.AttachedBuffRow, player.Id, info.SkillId,
                            u68: dur, stacks: (ushort)info.Count, list: [pres.AttachedMiscAnimatedRow, 0]));
                        vis.Attached[fam] = (buffId, info.Count, now + info.MaxRemaining);
                    }
                }
            }

            // Penance Brand energy stacks (BuffDefinitions 1551 magma_pustule)
            int energy = exists && BrandMonsterMarks.TryGetValue(monster!, out var mark) ? mark.PenanceEnergy : 0;
            if (energy <= 0 && vis.PenanceBuff != 0)
            {
                if (player.Area.Monsters.ContainsKey(mid)) bytes.World(mid, InstanceEntities.RemoveBuff(mid, vis.PenanceBuff));
                vis.PenanceBuff = 0;
                vis.PenanceStacks = 0;
            }
            else if (energy > 0 && energy != vis.PenanceStacks)
            {
                if (vis.PenanceBuff == 0) vis.PenanceBuff = player.NextBuff++;
                bytes.World(mid, PenanceEnergyBuff(mid, vis.PenanceBuff, (ushort)energy, player.Id));
                vis.PenanceStacks = energy;
            }

            // Wintertide Brand primary DoT visual (BuffDefinitions 1550 immolation_brand_burn)
            bool hasWinterPrimary = exists && desiredAttached.TryGetValue(mid, out var winterFamilies) && winterFamilies.ContainsKey(BrandFamily.Immolation);
            if (!hasWinterPrimary && vis.WintertidePrimaryBuff != 0)
            {
                if (player.Area.Monsters.ContainsKey(mid)) bytes.World(mid, InstanceEntities.RemoveBuff(mid, vis.WintertidePrimaryBuff));
                vis.WintertidePrimaryBuff = 0;
                vis.WintertideLastAt = 0;
            }
            else if (hasWinterPrimary && vis.WintertidePrimaryBuff == 0)
            {
                vis.WintertidePrimaryBuff = player.NextBuff++;
                bytes.World(mid, InstanceEntities.AddBuff(mid, vis.WintertidePrimaryBuff, WintertidePrimaryBuffRow, player.Id));
            }

            if (vis.Attached.Count == 0 && vis.PenanceBuff == 0 && vis.WintertidePrimaryBuff == 0)
                state.MonsterVisuals.Remove(mid);
        }

        // 3. Publish changed SigilDisplay HUD counter stats on player.Id via 0x1b3
        List<(uint, int)>? statDelta = null;
        for (int i = 0; i < BrandPresentations.Length; i++)
        {
            var p = BrandPresentations[i];
            if (state.ActiveByFamily[i] != state.PublishedActive[i])
            {
                state.PublishedActive[i] = state.ActiveByFamily[i];
                if (Stats.Key(p.ActiveCountStat) is uint k) (statDelta ??= []).Add((k, state.ActiveByFamily[i]));
            }
            if (state.InactiveByFamily[i] != state.PublishedInactive[i])
            {
                state.PublishedInactive[i] = state.InactiveByFamily[i];
                if (Stats.Key(p.InactiveCountStat) is uint k) (statDelta ??= []).Add((k, state.InactiveByFamily[i]));
            }
        }
        if (state.TotalBrands != state.PublishedTotal)
        {
            state.PublishedTotal = state.TotalBrands;
            if (Stats.Key("current_total_number_of_sigils") is uint tk) (statDelta ??= []).Add((tk, state.TotalBrands));
        }
        if (statDelta is { Count: > 0 })
            bytes.World(player.Id, InstanceEntities.StatsUpdate(player.Id, statDelta));
    }
}
