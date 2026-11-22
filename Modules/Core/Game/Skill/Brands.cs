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

    static readonly RuleTable<ClientState, BrandState> BrandStates = new();
    static readonly RuleTable<SpawnedMonster, MonsterBrandMark> BrandMonsterMarks = new();
    static int _globalActiveBrandCount;

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

    public static bool IsBrandSkill(ClientState conn, int effect) =>
        SkillsFor(conn).IsBrand(effect) && BrandFamilyOf(SkillsFor(conn).EffectId(effect)) is not null;

    public static bool IsBrandRecall(ClientState conn, int effect) =>
        SkillsFor(conn).EffectId(effect) == "BrandRecall";

    public static bool IsBrandsurge(ClientState conn, int effect) =>
        IsBrandsurgeEffect(SkillsFor(conn).EffectId(effect));

    public static bool IsBrandsurgeEffect(string? effectId) => effectId == "Brandsurge";

    public static int BrandCountFor(ClientState conn) =>
        BrandStates.TryGetValue(conn, out var state) ? state.TotalBrands : 0;

    public static int AttachedBrandCountFor(ClientState conn)
    {
        if (!BrandStates.TryGetValue(conn, out var state)) return 0;
        int sum = 0;
        for (int i = 0; i < state.ActiveByFamily.Length; i++) sum += state.ActiveByFamily[i];
        return sum;
    }

    public static IReadOnlyList<ActiveBrand> ActiveBrandsFor(ClientState conn) =>
        BrandStates.TryGetValue(conn, out var state) ? [.. state.Brands] : [];

    public static int BrandMaxTotal(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        int total = GolemStat(player, "base_total_number_of_sigils_allowed")
            + GolemStat(own, "base_total_number_of_sigils_allowed");
        if (total <= 0) total = PanelStatDefaults.Default("base_total_number_of_sigils_allowed");
        return Math.Clamp(total, 1, 20);
    }

    public static int BrandMaxPerTarget(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        // Note: BrandPlayerStats already adds +1 to base_number_of_sigils_allowed_per_target when keystone_runebinder
        // is present in player stats, so check raw sources or avoid double-counting.
        int perTarget = GolemStat(own, "base_number_of_sigils_allowed_per_target");
        if (perTarget <= 0) perTarget = 1;
        int playerBase = GolemStat(player, "base_number_of_sigils_allowed_per_target");
        if (playerBase > 0) perTarget += playerBase;
        else if (GolemStat(player, "keystone_runebinder") != 0) perTarget += 1;
        perTarget += GolemStat(own, "active_skill_brands_allowed_on_enemy_+")
            + GolemStat(player, "active_skill_brands_allowed_on_enemy_+");
        return Math.Clamp(perTarget, 1, 10);
    }

    public static double BrandAttachmentRange(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own,
        double detachedSeconds = 0, int recallBonusPct = 0)
    {
        long pct = (long)GolemStat(player, "sigil_target_search_range_+%")
            + GolemStat(own, "sigil_target_search_range_+%")
            + recallBonusPct;
        int perSec = GolemStat(player, "unattached_sigil_attachment_range_+%_per_second")
            + GolemStat(own, "unattached_sigil_attachment_range_+%_per_second");
        if (perSec > 0 && detachedSeconds > 0)
            pct += (long)Math.Floor(detachedSeconds) * perSec;
        return Math.Max(5, BrandBaseAttachmentRangeCells * Math.Max(0.1, 1 + pct / 100.0));
    }

    public static double BrandAttachedDurationSeconds(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        if (GolemStat(own, "brand_atttached_duration_is_infinite") != 0) return double.PositiveInfinity;
        int baseMs = GolemStat(own, "base_skill_effect_duration");
        if (baseMs <= 0) return GolemStat(own, "brand_removed_after_activating") != 0 ? 10.0 : 5.0;
        return Math.Max(0.1, SkillDurationModifiers.Seconds(baseMs,
            n => GolemStat(player, n), n => GolemStat(own, n), SkillDurationModifiers.Kinds.Brand));
    }

    public static double BrandDetachedDurationSeconds(IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        int baseMs = GolemStat(own, "base_secondary_skill_effect_duration");
        if (baseMs <= 0) baseMs = 6000;
        return Math.Max(0.1, SkillDurationModifiers.Seconds(baseMs,
            n => GolemStat(player, n), n => GolemStat(own, n),
            SkillDurationModifiers.Kinds.Brand | SkillDurationModifiers.Kinds.Secondary));
    }

    public static int BrandCastSpeedBonusPct(ClientState conn, int effect, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int> own)
    {
        if (!SkillsFor(conn).IsBrand(effect)) return 0;
        int bonus = GolemStat(player, "cast_speed_for_brand_skills_+%") + GolemStat(own, "cast_speed_for_brand_skills_+%");
        string? id = SkillsFor(conn).EffectId(effect);
        if (id is "PenanceBrand" or "PenanceBrandAltX" or "PenanceBrandAltY")
            bonus += GolemStat(player, "penance_brand_cast_speed_+%") + GolemStat(own, "penance_brand_cast_speed_+%");
        else if (id == "BrandSupport")
            bonus += GolemStat(player, "arcanist_brand_cast_speed_+%") + GolemStat(own, "arcanist_brand_cast_speed_+%");
        return bonus;
    }

    public static double BrandActivationSeconds(ClientState conn, SupportedGem gem, IReadOnlyDictionary<uint, int> player,
        double attachedFraction = 0, double overchargeElapsed = 0, int overchargeFasterPctPerSec = 0)
    {
        var own = gem.Stats;
        int baseMs = GolemStat(own, "base_sigil_repeat_frequency_ms");
        if (baseMs <= 0) return 0.5;
        long increased = (long)GolemStat(player, "sigil_repeat_frequency_+%") + GolemStat(own, "sigil_repeat_frequency_+%");
        if (GolemStat(own, "additive_cast_speed_modifiers_apply_to_sigil_repeat_frequency") != 0)
            increased += CastSpeedPct(player) + GolemStat(own, "base_cast_speed_+%")
                + BrandCastSpeedBonusPct(conn, gem.Effect, player, own);
        string? effectId = SkillsFor(conn).EffectId(gem.Effect);
        if (effectId is "ArmageddonBrand" or "ArmageddonBrandAltX" or "ArmageddonBrandAltY")
            increased += GolemStat(player, "armageddon_brand_repeat_frequency_+%")
                + GolemStat(own, "armageddon_brand_repeat_frequency_+%");
        int notRecent = GolemStat(player, "sigil_repeat_frequency_+%_if_havent_used_a_brand_skill_recently");
        if (notRecent != 0 && (!BrandStates.TryGetValue(conn, out var st)
            || AreaTime(conn) - st.LastBrandCastAt > 4.0))
            increased += notRecent;

        double more = Math.Max(0.05, 1 + GolemStat(own, "support_rapid_activation_brand_activation_rate_+%_final") / 100.0);
        if (attachedFraction <= 0.2)
            more *= Math.Max(0.05, 1 + GolemStat(player, "brand_activation_rate_+%_final_during_first_20%_of_active_duration") / 100.0);
        if (attachedFraction >= 0.8)
            more *= Math.Max(0.05, 1 + GolemStat(player, "brand_activation_rate_+%_final_during_last_20%_of_active_duration") / 100.0);
        if (attachedFraction >= 0.75)
            more *= Math.Max(0.05, 1 + (GolemStat(player, "combined_brand_activation_rate_+%_final_if_75%_attached_duration_expired")
                + GolemStat(own, "from_quality_brand_activation_rate_+%_final_if_75%_attached_duration_expired")) / 100.0);
        if (overchargeElapsed > 0 && overchargeFasterPctPerSec > 0)
            more *= 1 + overchargeElapsed * overchargeFasterPctPerSec / 100.0;

        double rate = Math.Max(0.05, (1 + increased / 100.0) * more);
        return Math.Max(BrandMinActivationSeconds, baseMs / 1000.0 / rate);
    }

    public static int BrandRecallCooldownBonusPct(ClientState conn, IReadOnlyDictionary<uint, int> player)
    {
        int bonus = GolemStat(player, "sigil_recall_cooldown_speed_+%");
        int perBrand = GolemStat(player, "sigil_recall_cooldown_speed_+%_per_brand_up_to_40%");
        if (perBrand > 0) bonus += Math.Min(40, perBrand * BrandCountFor(conn));
        return bonus;
    }

    /// <summary>Hot-path-cheap player stat hook (<see cref="GameEvents.PlayerStatSources"/>): Runebinder +1 brand per
    /// target, SigilDisplay HUD counters, per-brand crit/cooldown bonuses, and recent brand flag.</summary>
    public static Dictionary<uint, int> BrandPlayerStats(ClientState conn, Dictionary<uint, int> stats)
    {
        if (Stats.Key("keystone_runebinder") is uint rbKey && stats.GetValueOrDefault(rbKey) != 0
            && Stats.Key("base_number_of_sigils_allowed_per_target") is uint perKey)
            stats[perKey] = stats.GetValueOrDefault(perKey) + 1;

        if (!BrandStates.TryGetValue(conn, out var state)) return stats;
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
            && AreaTime(conn) - state.LastBrandCastAt <= 4.0)
            stats[recentKey] = 1;
        return stats;
    }

    /// <summary>O(1) fast path for <see cref="MonsterDamageTaken"/>: zero overhead when no brands exist.</summary>
    public static int BrandMonsterTakenPctLocked(SpawnedMonster m) =>
        Volatile.Read(ref _globalActiveBrandCount) == 0 ? 0
        : BrandMonsterMarks.TryGetValue(m, out var mark) ? mark.TakenPct : 0;

    public static void RecordBrandSkillUsedLocked(ClientState conn, double at)
    {
        BrandStates.GetOrCreateValue(conn).LastBrandCastAt = at;
    }

    public static IDisposable CleanupBrandsOnUnload() => new BrandUnloadHandle();

    sealed class BrandUnloadHandle : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref _globalActiveBrandCount, 0);
    }

    public static void ClearBrands(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!BrandStates.TryGetValue(conn, out var state)) return;
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
            BrandStates.Remove(conn);
        }
    }

    // ---- Casting: Brand, Brand Recall, Brandsurge -----------------------------------------------------------------------

    public static async Task<bool> CastBrand(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, Func<bool> current,
        SkillCriticalRoll? criticalRoll)
    {
        int? castMs = CastMs(conn, gem.Effect, skillId);
        if (castMs is null) return false;
        double delay;
        lock (conn.Monsters)
            delay = castMs.Value / 1000.0 * Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.03, delay)));
        await WhilePaused(conn);
        if (conn.Closed || conn.LeavingArea || !current() || conn.ActionInterruptionGeneration != recoveryGeneration)
            return false;

        return await ResolveCastBrand(remote, stream, ciphers, conn, skillId, gem, to, recoveryGeneration, current);
    }

    internal static async Task<bool> ResolveCastBrand(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) to, int recoveryGeneration, Func<bool> current)
    {
        bool spawned = false;
        await SendWorldBatch(conn, [], admission =>
        {
            using (ProjectileMutation(conn))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0
                    || !current() || conn.ActionInterruptionGeneration != recoveryGeneration) return null;
                var supported = SupportedSkill(conn, skillId);
                if (supported.Effect != gem.Effect || !supported.RequirementsMet) return null;
                if (BrandFamilyOf(SkillsFor(conn).EffectId(gem.Effect)) is not { } family) return null;

                double now = AreaTime(conn);
                var state = BrandStates.GetOrCreateValue(conn);
                state.Epoch = conn.ProjectileEpoch;
                state.LastBrandCastAt = now;

                var player = CharacterPlayerStats(conn);
                var own = supported.Stats;
                int maxTotal = BrandMaxTotal(player, own);
                int count = Math.Clamp(1 + GolemStat(player, "additional_number_of_brands_to_create")
                    + GolemStat(own, "additional_number_of_brands_to_create"), 1, maxTotal);
                int extraChance = GolemStat(player, "create_additional_brand_%_chance")
                    + GolemStat(own, "create_additional_brand_%_chance");
                if (extraChance > 0 && Random.Shared.Next(100) < extraChance) count++;

                var place = conn.Layout.WalkableFor(to.X, to.Y, 0) ? to : conn.Walk.Position(now);
                var bytes = new WorldRecordBuilder(conn, admission);
                var session = ProjectilesFor(remote, stream, ciphers, conn);
                string effectId = SkillsFor(conn).EffectId(gem.Effect)!;

                for (int i = 0; i < count; i++)
                {
                    while (state.Brands.Count >= maxTotal && state.Brands.Count > 0)
                        ExpireBrandLocked(remote, stream, ciphers, conn, session, state, state.Brands[0], now, bytes, replaced: true);

                    var brand = CreateBrandLocked(state, skillId, gem.Effect, effectId, family, supported, player, place, now);
                    state.Brands.Add(brand);
                    Interlocked.Increment(ref _globalActiveBrandCount);

                    if (!TryAttachBrandLocked(remote, stream, ciphers, conn, session, state, brand, player, now, bytes, excludeMonster: 0))
                        SpawnDetachedVisualLocked(conn, brand, bytes);
                    spawned = true;
                }

                ReconcileBrandPresentationLocked(conn, state, now, bytes);
                return bytes.Count > 0 ? bytes.Build() : null;
            }
        }, transport: (remote, stream, ciphers));

        if (spawned) await PublishMinionOutcomes(remote, stream, ciphers, conn);
        return spawned;
    }

    public static async Task<bool> CastBrandRecall(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        Func<bool> current)
    {
        bool recalled = false;
        bool paid = false;
        uint extraLifeSpent = 0;
        await SendWorldBatch(conn, [], admission =>
        {
            using (ProjectileMutation(conn))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0 || !current())
                    return null;
                if (!BrandStates.TryGetValue(conn, out var state) || state.Brands.Count == 0) return null;

                double now = AreaTime(conn);
                var recallGem = SupportedSkill(conn, skillId);
                var player = CharacterPlayerStats(conn);
                var here = conn.Walk.Position(now);
                int searchBonus = GolemStat(recallGem.Stats, "recall_sigil_target_search_range_+%")
                    + GolemStat(player, "recall_sigil_target_search_range_+%");
                int spendPct = GolemStat(recallGem.Stats, "brand_recall_spend_%_of_recalled_brands_cost");
                double extendAttached = GolemStat(recallGem.Stats, "sigil_recall_extend_base_skill_effect_duration") / 1000.0
                    + GolemStat(player, "sigil_recall_extend_base_skill_effect_duration") / 1000.0;
                double extendDetached = GolemStat(recallGem.Stats, "sigil_recall_extend_base_secondary_skill_effect_duration") / 1000.0
                    + GolemStat(player, "sigil_recall_extend_base_secondary_skill_effect_duration") / 1000.0;

                var bytes = new WorldRecordBuilder(conn, admission);
                var session = ProjectilesFor(remote, stream, ciphers, conn);
                var recalledFamilies = new HashSet<BrandFamily>();

                foreach (var brand in state.Brands.ToArray())
                {
                    if (!state.Brands.Contains(brand) || brand.CannotBeRecalled) continue;
                    // Detach from old target (triggers Penance / Wintertide detach effects at old position)
                    if (brand.Attached)
                        DetachBrandLocked(remote, stream, ciphers, conn, session, state, brand, now, bytes, spawnGround: false);
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
                    if (!TryAttachBrandLocked(remote, stream, ciphers, conn, session, state, brand, player, now, bytes,
                        excludeMonster: 0, triggerOnAttach: false))
                        SpawnDetachedVisualLocked(conn, brand, bytes);

                    // Immediate activation on every recalled brand (bypassing Armageddon Brand of Recall's -80% penalty)
                    if (state.Brands.Contains(brand))
                    {
                        if (spendPct > 0)
                        {
                            if (SupportedSkill(conn, brand.SkillId).Effect != brand.Effect ||
                                !PayMana(conn, brand.SkillId, out var mana, out var life, spendPct)) continue;
                            paid |= mana > 0 || life > 0;
                            extraLifeSpent += life;
                        }
                        ActivateBrandLocked(remote, stream, ciphers, conn, session, state, brand, player, now, bytes, fromRecall: true);
                        double interval = BrandActivationSeconds(conn, brand.Gem, player, AttachedFraction(brand),
                            brand.Overcharged ? now - brand.OverchargedAt : 0, brand.OverchargeFasterActivationPctPerSec);
                        brand.NextActivationAt = now + interval;
                    }
                }

                // Spawn recall circle art at player
                foreach (var fam in recalledFamilies)
                    SpawnTempEffectLocked(conn, state, here, PresentationOf(fam).RecallCircleAo, now + 0.6, bytes);

                ReconcileBrandPresentationLocked(conn, state, now, bytes);
                return bytes.Count > 0 ? bytes.Build() : null;
            }
        }, transport: (remote, stream, ciphers));

        if (paid) await SendSkillCost(remote, stream, ciphers, conn, extraLifeSpent, "Brand Recall activation");
        if (recalled) await PublishMinionOutcomes(remote, stream, ciphers, conn);
        return true;
    }

    public static async Task<bool> CastBrandsurge(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, Func<bool> current,
        SkillCriticalRoll? criticalRoll)
    {
        int? castMs = CastMs(conn, gem.Effect, skillId);
        if (castMs is null) return false;
        double delay;
        lock (conn.Monsters)
            delay = castMs.Value / 1000.0 * Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.03, delay)));
        await WhilePaused(conn);
        if (conn.Closed || conn.LeavingArea || !current() || conn.ActionInterruptionGeneration != recoveryGeneration)
            return false;

        await SendWorldBatch(conn, [], admission =>
        {
            using (ProjectileMutation(conn))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0
                    || !current() || conn.ActionInterruptionGeneration != recoveryGeneration) return null;
                if (!BrandStates.TryGetValue(conn, out var state) || state.Brands.Count == 0) return null;

                double now = AreaTime(conn);
                var surgeGem = SupportedSkill(conn, skillId);
                var player = CharacterPlayerStats(conn);
                var here = conn.Walk.Position(now);
                int fasterAct = GolemStat(surgeGem.Stats, "brand_detonate_faster_activation_%_per_second");
                if (fasterAct <= 0) fasterAct = 35;
                int fasterDur = GolemStat(surgeGem.Stats, "brand_detonate_faster_duration_%_per_second");
                if (fasterDur <= 0) fasterDur = 150;
                double dmgMore = Math.Max(0.1, 1 + GolemStat(surgeGem.Stats, "brand_detonate_detonating_brand_all_damage_+%_final") / 100.0);
                double aoeMore = SkillArea.RadiusMultiplier(GolemStat(surgeGem.Stats, "brand_detonate_detonating_brand_area_of_effect_+%_final"));

                var bytes = new WorldRecordBuilder(conn, admission);
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
                    SpawnBeamLocked(conn, state, here, PlayerId, brand.At, brand.Attached ? brand.AttachedMonsterId : 0,
                        BrandsurgeCastBeamRow, now + 0.35, bytes);
                }
                return bytes.Count > 0 ? bytes.Build() : null;
            }
        }, transport: (remote, stream, ciphers));

        return true;
    }

    // ---- Main 100ms Brand Tick (GameEvents.CompanionTick) ---------------------------------------------------------------

    public static async Task TickBrands(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, double now)
    {
        if (!BrandStates.TryGetValue(conn, out var state)) return;
        await SendWorldBatch(conn, [], admission =>
        {
            using (ProjectileMutation(conn))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return null;
                if (!BrandStates.TryGetValue(conn, out var live) || !ReferenceEquals(live, state)) return null;
                if (state.Brands.Count == 0 && state.Meteors.Count == 0 && state.TempObjects.Count == 0
                    && state.MonsterVisuals.Count == 0 && state.WintertideEnds.Count == 0)
                    return null;

                double dt = double.IsNaN(state.LastTickAt) ? 0.1 : Math.Clamp(now - state.LastTickAt, 0, 0.5);
                state.LastTickAt = now;
                var bytes = new WorldRecordBuilder(conn, admission);
                var session = ProjectilesFor(remote, stream, ciphers, conn);

                // On player death or area epoch change, tear down all active brands cleanly
                if (conn.PlayerLife == 0 || state.Epoch != conn.ProjectileEpoch)
                {
                    foreach (var b in state.Brands.ToArray())
                        RemoveBrandCleanlyLocked(state, b, bytes);
                    foreach (var m in state.Meteors)
                        if (m.VisualId != 0) bytes.Remove(m.VisualId);
                    state.Meteors.Clear();
                    foreach (var t in state.TempObjects) bytes.Remove(t.ObjectId);
                    state.TempObjects.Clear();
                    ClearAllBrandVisualsLocked(state, bytes);
                    ReconcileBrandPresentationLocked(conn, state, now, bytes);
                    return bytes.Count > 0 ? bytes.Build() : null;
                }

                var player = CharacterPlayerStats(conn);

                // 1. Step all active brands
                foreach (var brand in state.Brands.ToArray())
                {
                    if (!state.Brands.Contains(brand)) continue;
                    double durationScale = brand.Overcharged
                        ? 1.0 + Math.Max(0, now - brand.OverchargedAt) * brand.OverchargeFasterDurationPctPerSec / 100.0
                        : 1.0;

                    if (brand.Attached)
                    {
                        bool targetAlive = conn.Monsters.TryGetValue(brand.AttachedMonsterId, out var target)
                            && ReferenceEquals(target, brand.AttachedMonster)
                            && target is { Hostile: true, Dead: false, Life: > 0 };
                        if (!targetAlive)
                        {
                            DetachBrandLocked(remote, stream, ciphers, conn, session, state, brand, now, bytes, spawnGround: true);
                            continue;
                        }

                        brand.At = target!.Walk.Position(now);
                        if (!brand.InfiniteAttachedDuration)
                        {
                            brand.AttachedRemaining -= dt * durationScale;
                            if (brand.AttachedRemaining <= 0)
                            {
                                ExpireBrandLocked(remote, stream, ciphers, conn, session, state, brand, now, bytes, replaced: false);
                                continue;
                            }
                        }

                        // Execute all due activations (handles sub-100ms cadence like high-cast-speed Penance Brand cleanly)
                        int guard = 0;
                        while (state.Brands.Contains(brand) && brand.Attached && now >= brand.NextActivationAt && guard++ < 5)
                        {
                            double interval = BrandActivationSeconds(conn, brand.Gem, player, AttachedFraction(brand),
                                brand.Overcharged ? now - brand.OverchargedAt : 0, brand.OverchargeFasterActivationPctPerSec);
                            brand.NextActivationAt += interval;
                            if (brand.NextActivationAt < now - interval) brand.NextActivationAt = now + interval;

                            ActivateBrandLocked(remote, stream, ciphers, conn, session, state, brand, player, now, bytes, fromRecall: false);
                            if (!state.Brands.Contains(brand)) break;

                            if (brand.RemovedAfterActivating)
                            {
                                ExpireBrandLocked(remote, stream, ciphers, conn, session, state, brand, now, bytes, replaced: false);
                                break;
                            }

                            // Reattach on activation (Storm Brand of Indecision, Brand Mastery Brand3, Armageddon Brand enchant)
                            bool shouldReattach = brand.ReattachOnActivation
                                || GolemStat(player, "brands_reattach_on_activation") != 0
                                || (brand.Family == BrandFamily.Cataclysm
                                    && GolemStat(player, "armageddon_brand_brands_attach_to_new_enemy_each_activation_%_chance") is int ch and > 0
                                    && Random.Shared.Next(100) < ch);
                            if (shouldReattach && brand.Attached)
                            {
                                uint prevTarget = brand.AttachedMonsterId;
                                var candidate = FindAttachTargetLocked(conn, state, brand, player, now, excludeMonster: prevTarget);
                                if (candidate is not null)
                                {
                                    DetachBrandLocked(remote, stream, ciphers, conn, session, state, brand, now, bytes, spawnGround: false);
                                    AttachToMonsterLocked(conn, state, brand, candidate.Value.Id, candidate.Value.Monster, player, now, bytes, triggerImmediately: false, remote, stream, ciphers, session);
                                }
                            }
                        }
                    }
                    else
                    {
                        brand.DetachedRemaining -= dt * durationScale;
                        if (brand.DetachedRemaining <= 0)
                        {
                            ExpireBrandLocked(remote, stream, ciphers, conn, session, state, brand, now, bytes, replaced: false);
                            continue;
                        }
                        TryAttachBrandLocked(remote, stream, ciphers, conn, session, state, brand, player, now, bytes,
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
                    ResolveArmageddonMeteorImpactLocked(remote, stream, ciphers, conn, state, meteor, player, now, bytes);
                }

                // 3. Step Wintertide Brand Cold DoTs (primary attached + secondary Wintertide's End)
                TickWintertideDotsLocked(remote, stream, ciphers, conn, state, now, dt, bytes);

                // 4. Clean up expired temporary effects/beams
                for (int i = state.TempObjects.Count - 1; i >= 0; i--)
                {
                    if (now < state.TempObjects[i].RemoveAt) continue;
                    bytes.Remove(state.TempObjects[i].ObjectId);
                    state.TempObjects.RemoveAt(i);
                }

                // 5. Reconcile monster buffs and HUD brand counters
                ReconcileBrandPresentationLocked(conn, state, now, bytes);
                return bytes.Count > 0 ? bytes.Build() : null;
            }
        }, transport: (remote, stream, ciphers));
    }

    public static void BrandOnMonsterKill(KillContext kill)
    {
        var conn = kill.Conn;
        if (Volatile.Read(ref _globalActiveBrandCount) == 0) return;
        if (!BrandStates.TryGetValue(conn, out var state)) return;
        // Branded enemy chaos explosion on death (branded_enemy_explode_for_25%_life_as_chaos_on_death_chance_%)
        if (BrandMonsterMarks.TryGetValue(kill.Monster, out var mark) && mark.AttachedCount > 0)
        {
            var player = CharacterPlayerStats(conn);
            int chance = GolemStat(player, "branded_enemy_explode_for_25%_life_as_chaos_on_death_chance_%");
            if (state.Brands.Any(b => ReferenceEquals(b.AttachedMonster, kill.Monster) && b.Family is BrandFamily.Immolation or BrandFamily.Trigger))
                chance += GolemStat(player, "wintertide_and_arcanist_brand_branded_enemy_explode_for_25%_life_as_chaos_on_death_chance_%");
            if (chance > 0 && Random.Shared.Next(100) < chance && kill.Monster.MaxLife > 0)
            {
                double chaosDmg = kill.Monster.MaxLife * 0.25;
                double now = AreaTime(conn);
                foreach (var (id, m) in conn.Monsters.ToArray())
                {
                    if (ReferenceEquals(m, kill.Monster) || m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false })
                        continue;
                    if (Dist(m.Walk.Position(now), kill.At) > 22) continue;
                    var packet = DamagePacket.Of(DamageType.Chaos, chaosDmg, chaosDmg);
                    var mitigated = DamageModel.Mitigate(packet, type => MonsterHitResistance(m, type, default, now));
                    int dmg = (int)Math.Floor(mitigated.Components.Sum(c => c.Range.Min) * MonsterDamageTaken(m, false, now, false, true, true));
                    if (dmg <= 0 || ApplyMonsterHitLocked(conn, m, dmg) is not { } hit) continue;
                    CommitAcceptedHitLocked(conn, id, m, hit, dmg, now, "branded enemy explosion", playerKill: true);
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
            InfiniteAttachedDuration = GolemStat(own, "brand_atttached_duration_is_infinite") != 0,
            CannotBeRecalled = GolemStat(own, "brand_cannot_be_recalled") != 0,
            RemovedAfterActivating = GolemStat(own, "brand_removed_after_activating") != 0,
            ReattachOnActivation = GolemStat(own, "brands_reattach_on_activation") != 0,
            PenancePulses = GolemStat(own, "penance_brand_pulses_instead_of_explode") != 0,
            PenanceSpreads = GolemStat(own, "penance_brand_spreads_energy") != 0,
            PenanceExplodesOnDetach = GolemStat(own, "penance_brand_explodes_on_death_and_detach") != 0,
        };
    }

    static double AttachedFraction(ActiveBrand brand) =>
        brand.InfiniteAttachedDuration || brand.AttachedDurationTotal <= 0
            ? 0 : Math.Clamp(1.0 - brand.AttachedRemaining / brand.AttachedDurationTotal, 0, 1);

    static (uint Id, SpawnedMonster Monster)? FindAttachTargetLocked(ClientState conn, BrandState state,
        ActiveBrand brand, IReadOnlyDictionary<uint, int> player, double now, uint excludeMonster)
    {
        double detachedSecs = brand.Attached ? 0 : Math.Max(0, now - brand.DetachedAt);
        double range = BrandAttachmentRange(player, brand.Gem.Stats, detachedSecs, brand.RecallSearchBonus);
        int perTargetLimit = BrandMaxPerTarget(player, brand.Gem.Stats);

        uint bestId = 0;
        SpawnedMonster? bestMonster = null;
        double bestDist = double.MaxValue;
        foreach (var (id, m) in conn.Monsters)
        {
            if (id == excludeMonster || m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || m.OwnerId is not null || conn.AsleepMonsters.Contains(id)) continue;
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

    static bool TryAttachBrandLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ProjectileSession session, BrandState state, ActiveBrand brand, IReadOnlyDictionary<uint, int> player,
        double now, WorldRecordBuilder bytes, uint excludeMonster, bool triggerOnAttach = true)
    {
        var target = FindAttachTargetLocked(conn, state, brand, player, now, excludeMonster);
        if (target is null) return false;
        AttachToMonsterLocked(conn, state, brand, target.Value.Id, target.Value.Monster, player, now, bytes,
            triggerOnAttach, remote, stream, ciphers, session);
        return true;
    }

    static void AttachToMonsterLocked(ClientState conn, BrandState state, ActiveBrand brand, uint monsterId,
        SpawnedMonster monster, IReadOnlyDictionary<uint, int> player, double now, WorldRecordBuilder bytes,
        bool triggerImmediately, string remote, NetworkStream stream, ConnectionCiphers ciphers, ProjectileSession session)
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
        brand.AttachedTakenPct = GolemStat(player, "sigil_attached_target_damage_taken_+%");
        mark.TakenPct += brand.AttachedTakenPct;

        // Visual attach beam from old location to target
        if (Dist(fromAt, brand.At) >= 2)
            SpawnBeamLocked(conn, state, fromAt, 0, brand.At, monsterId, PresentationOf(brand.Family).AttachBeamRow, now + 0.25, bytes);

        double interval = BrandActivationSeconds(conn, brand.Gem, player, AttachedFraction(brand),
            brand.Overcharged ? now - brand.OverchargedAt : 0, brand.OverchargeFasterActivationPctPerSec);
        if (triggerImmediately)
        {
            brand.NextActivationAt = now + interval;
            ActivateBrandLocked(remote, stream, ciphers, conn, session, state, brand, player, now, bytes, fromRecall: false);
            if (state.Brands.Contains(brand) && brand.RemovedAfterActivating)
                ExpireBrandLocked(remote, stream, ciphers, conn, session, state, brand, now, bytes, replaced: false);
        }
        else
        {
            brand.NextActivationAt = now + interval;
        }
    }

    static void DetachBrandLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ProjectileSession session, BrandState state, ActiveBrand brand, double now, WorldRecordBuilder bytes, bool spawnGround)
    {
        if (!brand.Attached) return;
        var oldMonster = brand.AttachedMonster!;
        uint oldId = brand.AttachedMonsterId;
        var at = oldMonster.Walk.Position(now);
        brand.At = at;

        var player = CharacterPlayerStats(conn);
        // Trigger end-of-attachment effects before detaching
        if (brand.PenanceExplodesOnDetach)
            TriggerPenanceExplosionLocked(remote, stream, ciphers, conn, state, brand, oldId, oldMonster, at, player, now, bytes);
        else if (brand.Family == BrandFamily.Immolation)
            TriggerWintertideEndLocked(conn, state, brand, at, player, now, bytes);

        ClearBrandMonsterMarkLocked(brand);
        brand.AttachedMonsterId = 0;
        brand.AttachedMonster = null;
        brand.DetachedAt = now;
        brand.Energy = 0;

        if (spawnGround && state.Brands.Contains(brand))
            SpawnDetachedVisualLocked(conn, brand, bytes);
    }

    static void ExpireBrandLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ProjectileSession session, BrandState state, ActiveBrand brand, double now, WorldRecordBuilder bytes, bool replaced)
    {
        if (!state.Brands.Contains(brand)) return;
        var player = CharacterPlayerStats(conn);
        bool wasAttached = brand.Attached;
        var oldMonster = brand.AttachedMonster;
        uint oldId = brand.AttachedMonsterId;
        var at = wasAttached && oldMonster is not null ? oldMonster.Walk.Position(now) : brand.At;

        // End-of-attachment effects (Penance Brand explosion, Wintertide's End)
        if (wasAttached && oldMonster is not null)
        {
            if (brand.PenanceExplodesOnDetach)
                TriggerPenanceExplosionLocked(remote, stream, ciphers, conn, state, brand, oldId, oldMonster, at, player, now, bytes);
            else if (brand.Family == BrandFamily.Immolation)
                TriggerWintertideEndLocked(conn, state, brand, at, player, now, bytes);

            // Brand Mastery Brand1: "Recover 10% of Mana when a Brand expires while Attached"
            if (!replaced && GolemStat(player, "recover_%_mana_when_attached_brand_expires") is int manaPct and > 0)
            {
                uint gain = (uint)Math.Max(1, conn.Pools.Mana * (long)manaPct / 100);
                uint cap = (uint)Math.Max(0, UnreservedMana(conn));
                if (conn.PlayerMana < cap)
                {
                    conn.PlayerMana = Math.Min(cap, conn.PlayerMana + gain);
                    bytes.World(PlayerId, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMana, (uint)ReservedMana(conn), pool: ManaPool));
                }
            }
        }

        // Brandsurge overcharged expiry explosion!
        if (brand.Overcharged && brand.OverchargeGem is not null)
            TriggerBrandsurgeExplosionLocked(remote, stream, ciphers, conn, state, brand, at, player, now, bytes);

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

    static void ActivateBrandLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ProjectileSession session, BrandState state, ActiveBrand brand, IReadOnlyDictionary<uint, int> player,
        double now, WorldRecordBuilder bytes, bool fromRecall)
    {
        switch (brand.Family)
        {
            case BrandFamily.Conduit:
                ActivateStormBrandLocked(remote, stream, ciphers, conn, state, brand, player, now, bytes);
                break;
            case BrandFamily.Cataclysm:
                ActivateArmageddonBrandLocked(conn, state, brand, player, now, bytes, fromRecall);
                break;
            case BrandFamily.Magma:
                ActivatePenanceBrandLocked(remote, stream, ciphers, conn, state, brand, player, now, bytes);
                break;
            case BrandFamily.Immolation:
                ActivateWintertideBrandLocked(conn, state, brand, player, now);
                break;
            case BrandFamily.Trigger:
                ActivateArcanistBrandLocked(conn, session, brand, player, now);
                break;
        }
    }

    // ---- 1. Storm Brand (+ Indecision) ----------------------------------------------------------------------------------

    static void ActivateStormBrandLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, BrandState state, ActiveBrand brand, IReadOnlyDictionary<uint, int> player,
        double now, WorldRecordBuilder bytes)
    {
        var origin = brand.At;
        int radius = BrandEffectiveRadius(conn, brand, player);
        int beamTargets = Math.Max(1, GolemStat(brand.Gem.Stats, "conduit_sigil_number_of_beam_targets"));
        int extraChainChance = GolemStat(player, "storm_brand_additional_chain_chance_%")
            + GolemStat(brand.Gem.Stats, "storm_brand_additional_chain_chance_%");
        if (extraChainChance > 0 && Random.Shared.Next(100) < extraChainChance) beamTargets++;

        var targets = new List<(uint Id, SpawnedMonster Monster)>(beamTargets);
        if (brand.Attached && brand.AttachedMonster is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false })
            targets.Add((brand.AttachedMonsterId, brand.AttachedMonster));

        if (targets.Count < beamTargets)
        {
            var extras = conn.Monsters
                .Where(kv => kv.Key != brand.AttachedMonsterId
                    && kv.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    && kv.Value.OwnerId is null && !conn.AsleepMonsters.Contains(kv.Key)
                    && Dist(kv.Value.Walk.Position(now), origin) <= radius)
                .OrderBy(kv => Dist(kv.Value.Walk.Position(now), origin))
                .ThenBy(kv => kv.Key)
                .Take(beamTargets - targets.Count);
            foreach (var kv in extras) targets.Add((kv.Key, kv.Value));
        }

        if (targets.Count == 0) return;
        int attachedMorePct = GolemStat(brand.Gem.Stats, "sigil_attached_target_hit_damage_+%_final");
        int extraPen = GolemStat(player, "storm_brand_attached_target_lightning_penetration_%")
            + GolemStat(player, "sigil_attached_target_lightning_penetration_%");

        foreach (var (tid, monster) in targets)
        {
            bool isBranded = tid == brand.AttachedMonsterId;
            double hitMore = isBranded ? Math.Max(0, 1 + attachedMorePct / 100.0) : 1.0;
            var tAt = monster.Walk.Position(now);
            SpawnBeamLocked(conn, state, origin, brand.Attached ? brand.AttachedMonsterId : 0, tAt, tid,
                StormBrandDamageBeamRow, now + 0.25, bytes);
            SpawnTempEffectLocked(conn, state, tAt, StormBrandImpactAo, now + 0.25, bytes);
            DealBrandSpellHitLocked(remote, stream, ciphers, conn, brand.SkillId, brand.Gem, player, tid, monster,
                now, hitMore, ailmentMore: 1.0, extraPenetrationType: isBranded && extraPen > 0 ? (DamageType.Lightning, extraPen) : null,
                label: brand.EffectId);
        }
    }

    // ---- 2. Armageddon Brand (+ Volatility, Recall) ---------------------------------------------------------------------

    static void ActivateArmageddonBrandLocked(ClientState conn, BrandState state, ActiveBrand brand,
        IReadOnlyDictionary<uint, int> player, double now, WorldRecordBuilder bytes, bool fromRecall)
    {
        var at = brand.At;
        int radius = BrandEffectiveRadius(conn, brand, player);
        uint visualId = conn.TakeObjectId();
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
        });
    }

    static void ResolveArmageddonMeteorImpactLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, BrandState state, PendingMeteor meteor, IReadOnlyDictionary<uint, int> player,
        double now, WorldRecordBuilder bytes)
    {
        float scale = (float)Math.Clamp(meteor.Radius / 22.0, 0.5, 3.0);
        SpawnTempEffectLocked(conn, state, meteor.At, ArmageddonImpactAo, now + 0.5, bytes, scale);

        int attachedMorePct = GolemStat(meteor.Gem.Stats, "sigil_attached_target_hit_damage_+%_final");
        // Armageddon Brand of Recall (ArmageddonBrandAltY) has -80% hit damage on normal activations,
        // bypassed when triggered by Brand Recall.
        bool bypassPenalty = meteor.FromRecall && meteor.EffectId == "ArmageddonBrandAltY";
        int extraFirePen = GolemStat(player, "armageddon_brand_attached_target_fire_penetration_%")
            + GolemStat(player, "sigil_attached_target_fire_penetration_%");

        foreach (var (id, m) in conn.Monsters.ToArray())
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || m.OwnerId is not null || conn.AsleepMonsters.Contains(id)
                || Dist(m.Walk.Position(now), meteor.At) > meteor.Radius) continue;
            bool isBranded = id == meteor.BrandedTargetId;
            double hitMore = bypassPenalty ? 1.0
                : isBranded ? Math.Max(0, 1 + attachedMorePct / 100.0)
                : meteor.EffectId == "ArmageddonBrandAltY" ? Math.Max(0, 1 + attachedMorePct / 100.0) : 1.0;
            DealBrandSpellHitLocked(remote, stream, ciphers, conn, meteor.SkillId, meteor.Gem, player, id, m,
                now, hitMore, ailmentMore: 1.0,
                extraPenetrationType: isBranded && extraFirePen > 0 ? (DamageType.Fire, extraFirePen) : null,
                label: meteor.EffectId);
        }
    }

    // ---- 3. Penance Brand (+ Dissipation, Conduction) -------------------------------------------------------------------

    static void ActivatePenanceBrandLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, BrandState state, ActiveBrand brand, IReadOnlyDictionary<uint, int> player,
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

            int perEnergyHit = GolemStat(brand.Gem.Stats, "magma_brand_hit_damage_+%_final_per_additional_pustule");
            int perEnergyAilment = GolemStat(brand.Gem.Stats, "magma_brand_ailment_damage_+%_final_per_additional_pustule");
            int attachedMorePct = GolemStat(brand.Gem.Stats, "sigil_attached_target_hit_damage_+%_final");
            int extraEnergy = Math.Max(0, brand.Energy - 1);
            double energyHitMore = Math.Max(0, 1 + extraEnergy * perEnergyHit / 100.0);
            double energyAilmentMore = Math.Max(0, 1 + extraEnergy * perEnergyAilment / 100.0);

            int radius = BrandEffectiveRadius(conn, brand, player, extraBaseRadius: brand.Energy / 2);
            bool atMax = brand.Energy >= PenanceMaxEnergy;
            SpawnTempEffectLocked(conn, state, at, atMax ? PenanceExplosionBigAo : PenanceExplosionSmallAo,
                now + 0.45, bytes, (float)Math.Clamp(radius / 22.0, 0.5, 3.0));

            foreach (var (id, m) in conn.Monsters.ToArray())
            {
                if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    || m.OwnerId is not null || conn.AsleepMonsters.Contains(id)
                    || Dist(m.Walk.Position(now), at) > radius) continue;
                double brandedMore = id == targetId ? Math.Max(0, 1 + attachedMorePct / 100.0) : 1.0;
                DealBrandSpellHitLocked(remote, stream, ciphers, conn, brand.SkillId, brand.Gem, player, id, m,
                    now, energyHitMore * brandedMore, energyAilmentMore, label: "PenanceBrandAltX");
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
            int spreadRadius = 18 + GolemStat(brand.Gem.Stats, "penance_brand_base_spread_radius_+");
            var spreadTargets = conn.Monsters
                .Where(kv => kv.Key != targetId
                    && kv.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    && (!BrandMonsterMarks.TryGetValue(kv.Value, out var existing) || existing.PenanceEnergy == 0)
                    && kv.Value.OwnerId is null && !conn.AsleepMonsters.Contains(kv.Key)
                    && Dist(kv.Value.Walk.Position(now), at) <= spreadRadius)
                .OrderBy(kv => Dist(kv.Value.Walk.Position(now), at))
                .Take(2);
            foreach (var (otherId, other) in spreadTargets)
            {
                var otherMark = BrandMonsterMarks.GetOrCreateValue(other);
                if (otherMark.PenanceEnergy < PenanceMaxEnergy) otherMark.PenanceEnergy++;
                brand.EnergizedMonsters.Add(otherId);
                SpawnBeamLocked(conn, state, at, targetId, other.Walk.Position(now), otherId,
                    PenanceBrandChargeBeamRow, now + 0.25, bytes);
            }
        }

        // Base Penance Brand explodes & removes itself when reaching max energy (20)
        if (!brand.PenanceExplodesOnDetach && targetMark.PenanceEnergy >= PenanceMaxEnergy)
        {
            TriggerPenanceExplosionLocked(remote, stream, ciphers, conn, state, brand, targetId, target, at, player, now, bytes);
            RemoveBrandCleanlyLocked(state, brand, bytes);
        }
    }

    static void TriggerPenanceExplosionLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, BrandState state, ActiveBrand brand, uint primaryId, SpawnedMonster primary,
        (int X, int Y) primaryAt, IReadOnlyDictionary<uint, int> player, double now, WorldRecordBuilder bytes)
    {
        int attachedMorePct = GolemStat(brand.Gem.Stats, "sigil_attached_target_hit_damage_+%_final");

        void ExplodeAt(uint centreId, SpawnedMonster? centreMonster, (int X, int Y) centreAt, int energy)
        {
            if (energy <= 0) return;
            bool full = energy >= PenanceMaxEnergy;
            int radius = BrandEffectiveRadius(conn, brand, player, extraBaseRadius: energy / 2);
            SpawnTempEffectLocked(conn, state, centreAt, full ? PenanceExplosionBigAo : PenanceExplosionSmallAo,
                now + 0.55, bytes, (float)Math.Clamp(radius / 22.0, 0.6, 3.0));

            foreach (var (id, m) in conn.Monsters.ToArray())
            {
                if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    || m.OwnerId is not null || conn.AsleepMonsters.Contains(id)
                    || (brand.PenanceSpreads ? id != centreId : Dist(m.Walk.Position(now), centreAt) > radius)) continue;
                double brandedMore = id == primaryId ? Math.Max(0, 1 + attachedMorePct / 100.0) : 1.0;
                DealBrandSpellHitLocked(remote, stream, ciphers, conn, brand.SkillId, brand.Gem, player, id, m,
                    now, brandedMore, 1.0, label: brand.EffectId);
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
                if (!conn.Monsters.TryGetValue(otherId, out var other)) continue;
                if (!BrandMonsterMarks.TryGetValue(other, out var om) || om.PenanceEnergy <= 0) continue;
                ExplodeAt(otherId, other, other.Walk.Position(now), om.PenanceEnergy);
            }
        }
        brand.EnergizedMonsters.Clear();
        brand.Energy = 0;
    }

    // ---- 4. Wintertide Brand (Stacking Cold DoT + Wintertide's End) -----------------------------------------------------

    static double WintertideDpsFor(SupportedGem gem, IReadOnlyDictionary<uint, int> player, int stages)
    {
        var merged = MergeSkillStats(player, gem.Stats);
        int Get(string name) => GolemStat(merged, name);
        int Own(string name) => GolemStat(gem.Stats, name);
        double baseDps = Get("base_cold_damage_to_deal_per_minute") / 60.0;
        if (!(baseDps > 0) || DamageStats.Prohibited(DamageType.Cold, Get)) return 0;

        double increased = (double)Get("damage_+%") + Get("cold_damage_+%") + Get("elemental_damage_+%")
            + Get("damage_over_time_+%") + Get("cold_damage_over_time_+%") + Get("area_damage_+%")
            + Get("sigil_damage_+%") + Get("winter_brand_damage_+%")
            + (Get("spell_damage_modifiers_apply_to_skill_dot") != 0 ? Get("spell_damage_+%") : 0);
        double more = WintertideDotSupportMore.Aggregate(1.0, (m, name) => m * Math.Max(0, 1 + Own(name) / 100.0));
        if (Get("keystone_elemental_overload_damage_+%_final") is int eo and not 0)
            more *= Math.Max(0, 1 + eo / 100.0);
        int perStage = Math.Max(0, Own("immolation_brand_burn_damage_+%_final_per_stage"));
        more *= 1.0 + Math.Max(0, stages) * perStage / 100.0;
        double dotMult = Math.Max(0, 1.0 + ((double)Get("dot_multiplier_+") + Get("cold_dot_multiplier_+")) / 100.0);
        return baseDps * Math.Max(0, 1 + increased / 100.0) * more * dotMult;
    }

    static void ActivateWintertideBrandLocked(ClientState conn, BrandState state, ActiveBrand brand,
        IReadOnlyDictionary<uint, int> player, double now)
    {
        if (!brand.Attached || brand.AttachedMonster is not { } target) return;
        int maxStages = Math.Max(1, GolemStat(brand.Gem.Stats, "winter_brand_max_number_of_stages")
            + GolemStat(player, "winter_brand_max_number_of_stages_+"));
        if (brand.Stages < maxStages) brand.Stages++;

        brand.WintertidePrimaryDps = WintertideDpsFor(brand.Gem, player, brand.Stages);

        // Inflict Chill on the attached enemy
        int chillEffect = GolemStat(player, "winter_brand_chill_effect_+%") + GolemStat(brand.Gem.Stats, "winter_brand_chill_effect_+%");
        var merged = MergeSkillStats(player, brand.Gem.Stats);
        int Get(string n) => GolemStat(merged, n) + (n == "chill_effect_+%" ? chillEffect : 0);
        InflictedLocked(conn, target, target.Ailments.WithoutDamage(Kind.Chill, 2.0, now, Get, n => target.Effects[n], 1.0));
    }

    static void TriggerWintertideEndLocked(ClientState conn, BrandState state, ActiveBrand brand,
        (int X, int Y) at, IReadOnlyDictionary<uint, int> player, double now, WorldRecordBuilder bytes)
    {
        int radius = BrandEffectiveRadius(conn, brand, player);
        SpawnTempEffectLocked(conn, state, at, WintertideNovaAo, now + 0.5, bytes, (float)Math.Clamp(radius / 22.0, 0.6, 3.0));

        double dps = WintertideDpsFor(brand.Gem, player, Math.Max(1, brand.Stages));
        if (!(dps > 0)) return;
        int baseEndMs = GolemStat(brand.Gem.Stats, "base_tertiary_skill_effect_duration");
        if (baseEndMs <= 0) baseEndMs = 2000;
        double duration = Math.Max(0.2, SkillDurationModifiers.Seconds(baseEndMs,
            n => GolemStat(player, n), n => GolemStat(brand.Gem.Stats, n), SkillDurationModifiers.Kinds.Brand));
        double until = now + duration;

        var merged = MergeSkillStats(player, brand.Gem.Stats);
        int chillEffect = GolemStat(merged, "winter_brand_chill_effect_+%");
        int Get(string n) => GolemStat(merged, n) + (n == "chill_effect_+%" ? chillEffect : 0);

        foreach (var (id, m) in conn.Monsters)
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || m.OwnerId is not null || Dist(m.Walk.Position(now), at) > radius) continue;
            if (!state.WintertideEnds.TryGetValue(id, out var end) || !ReferenceEquals(end.Monster, m))
                state.WintertideEnds[id] = end = new(m, now);
            end.Apply(now, until, dps);
            InflictedLocked(conn, m, m.Ailments.WithoutDamage(Kind.Chill, duration, now, Get, n => m.Effects[n], 1.0));
        }
    }

    static void TickWintertideDotsLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, BrandState state, double now, double dt, WorldRecordBuilder bytes)
    {
        // Primary attached Wintertide Brand DoTs (multiple attached brands stack on the same enemy!)
        var targets = state.Brands.Where(b => b.Attached && b.WintertidePrimaryDps > 0).GroupBy(b => b.AttachedMonsterId);
        foreach (var group in targets)
        {
            uint id = group.Key;
            if (!conn.Monsters.TryGetValue(id, out var m) || m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }) continue;
            double totalDps = group.Where(b => ReferenceEquals(b.AttachedMonster, m)).Sum(b => b.WintertidePrimaryDps);
            if (!(totalDps > 0)) continue;

            var vis = state.MonsterVisuals.TryGetValue(id, out var existing) ? existing : state.MonsterVisuals[id] = new();
            double step = vis.WintertideLastAt > 0 ? Math.Clamp(now - vis.WintertideLastAt, 0, 0.5) : dt;
            vis.WintertideLastAt = now;
            double total = vis.WintertideCarry + totalDps * step * MonsterDotTaken(m, DamageType.Cold, now);
            int damage = (int)Math.Min(int.MaxValue, Math.Floor(total));
            vis.WintertideCarry = total - damage;
            if (damage <= 0 || ApplyMonsterHitLocked(conn, m, damage) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, id, m, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(conn, id, m, hit, damage, now, "Wintertide Brand", playerKill: true);
        }

        // Secondary Wintertide's End DoTs (after detach/expiry)
        foreach (var (id, end) in state.WintertideEnds.ToArray())
        {
            var m = end.Monster;
            bool alive = conn.Monsters.TryGetValue(id, out var live) && ReferenceEquals(live, m)
                && m is { Hostile: true, Dead: false, Life: > 0 };
            int damage = alive ? end.Advance(now, m.Invulnerable ? 0 : MonsterDotTaken(m, DamageType.Cold, now)) : 0;
            if (!alive || end.Until <= now)
            {
                if (end.BuffInstance != 0)
                {
                    if (conn.Monsters.ContainsKey(id)) bytes.World(id, InstanceEntities.RemoveBuff(id, end.BuffInstance));
                    end.BuffInstance = 0;
                }
                state.WintertideEnds.Remove(id);
            }
            else if (end.BuffInstance == 0 || end.ShownUntil != end.Until)
            {
                if (end.BuffInstance == 0) end.BuffInstance = conn.NextBuff++;
                end.ShownUntil = end.Until;
                bytes.World(id, InstanceEntities.AddBuff(id, end.BuffInstance, WintertideSecondaryBuffRow, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0.1, end.Until - now))));
            }
            if (damage <= 0 || ApplyMonsterHitLocked(conn, m, damage) is not { } hit) continue;
            if (hit.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, id, m, now); hit = hit with { StageStarts = false }; }
            CommitAcceptedHitLocked(conn, id, m, hit, damage, now, "Wintertide's End", playerKill: true);
        }
    }

    // ---- 5. Arcanist Brand (Linked Spell Triggering) --------------------------------------------------------------------

    static List<(ushort Id, SupportedGem Gem)> ArcanistBrandLinkedSpellsLocked(ClientState conn, SupportedGem brand)
    {
        var result = new List<(ushort, SupportedGem)>();
        foreach (var source in PlayerSkillSourcesLocked(conn).Where(s => s.Active && s.SourceGem is not null
            && s.Identity.Host == brand.Host && s.Effect != brand.Effect).OrderBy(s => s.Identity.Socket))
        {
            var linked = SupportedSkill(conn, source.Id);
            if (linked.Group == brand.Group && linked.RequirementsMet && linked.Supports.Any(s => s.Effect.Id == "SupportBrandSupport")
                && TriggeredSpellExecutable(conn, source.Id, linked))
                result.Add((source.Id, linked));
        }
        return result;
    }

    static void ActivateArcanistBrandLocked(ClientState conn, ProjectileSession session,
        ActiveBrand brand, IReadOnlyDictionary<uint, int> player, double now)
    {
        var liveBrand = SupportedSkill(conn, brand.SkillId);
        if (liveBrand.Effect != brand.Effect || !liveBrand.RequirementsMet) return;
        var linked = ArcanistBrandLinkedSpellsLocked(conn, liveBrand);
        if (linked.Count == 0) return;

        var catalog = SkillsFor(conn);
        var aim = brand.At;
        var origin = brand.At;
        if (!brand.Attached)
        {
            var near = conn.Monsters.Values
                .Where(m => m is { Hostile: true, Dead: false, Life: > 0 } && Dist(m.Walk.Position(now), origin) <= 60)
                .OrderBy(m => Dist(m.Walk.Position(now), origin)).FirstOrDefault();
            aim = near?.Walk.Position(now) ?? (origin.X + 20, origin.Y);
        }

        var triggerState = LinkedTriggersOf(conn);
        var rules = CombatLifetime;
        for (int i = 0; i < linked.Count; i++)
        {
            int idx = (brand.TriggerRotation + i) % linked.Count;
            var (spellSkillId, spellGem) = linked[idx];
            if (now < triggerState.ReadyAt.GetValueOrDefault(spellGem.Effect)) continue;
            if (GemSkillInstanceDisabled(conn, spellGem.Effect, spellSkillId, triggered: true) is not null) continue;
            bool Current() => session.Active && CurrentGameRules && ReferenceEquals(CombatLifetime, rules)
                && !conn.Closed && !conn.LeavingArea && !conn.Paused && conn.PlayerLife > 0;

            var release = PrepareTriggeredPayload(conn, session, spellSkillId, aim, Current, origin, brand.AttachedMonster);
            if (release is null) continue;
            if (!PayMana(conn, spellSkillId, out uint mana, out uint life))
            {
                brand.TriggerRotation = (idx + 1) % linked.Count;
                return;
            }
            int cdMs = catalog.CooldownMs(spellGem.Effect, spellGem.Level);
            if (cdMs > 0)
            {
                double rate = Math.Max(0.05, 1 + (GolemStat(player, "virtual_cooldown_speed_+%")
                    + GolemStat(player, "base_spell_cooldown_speed_+%") + GolemStat(spellGem.Stats, "base_cooldown_speed_+%")) / 100.0);
                triggerState.ReadyAt[spellGem.Effect] = now + cdMs / 1000.0 / rate;
            }
            brand.TriggerRotation = (idx + 1) % linked.Count;

            if (mana > 0 || life > 0)
                session.Enqueue(() => SendSkillCost(session.remote, session.stream, session.ciphers, conn, life, "Arcanist Brand trigger cost"));

            release();
            return;
        }
    }

    // ---- 6. Brandsurge Explosion ----------------------------------------------------------------------------------------

    static void TriggerBrandsurgeExplosionLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, BrandState state, ActiveBrand brand, (int X, int Y) at,
        IReadOnlyDictionary<uint, int> player, double now, WorldRecordBuilder bytes)
    {
        if (brand.OverchargeGem is not { } surgeGem) return;
        int radius = (int)Math.Round(SkillRadius(conn, surgeGem.Stats, spell: true, surgeGem.Effect) * brand.OverchargeAreaMore);
        SpawnTempEffectLocked(conn, state, at, PresentationOf(brand.Family).ExplosionAo, now + 0.55, bytes,
            (float)Math.Clamp(radius / 24.0, 0.7, 3.0));

        foreach (var (id, m) in conn.Monsters.ToArray())
        {
            if (m is not { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                || m.OwnerId is not null || conn.AsleepMonsters.Contains(id)
                || Dist(m.Walk.Position(now), at) > radius) continue;
            DealBrandSpellHitLocked(remote, stream, ciphers, conn, brand.OverchargeSkillId, surgeGem, player, id, m,
                now, brand.OverchargeDamageMore, brand.OverchargeDamageMore, label: "Brandsurge");
        }
    }

    // ---- Shared Brand Spell Hit Executor --------------------------------------------------------------------------------

    static int BrandEffectiveRadius(ClientState conn, ActiveBrand brand, IReadOnlyDictionary<uint, int> player, int extraBaseRadius = 0)
    {
        var own = brand.Gem.Stats;
        int extraAreaPct = AttachedFraction(brand) >= 0.5
            ? GolemStat(player, "brand_area_of_effect_+%_if_50%_attached_duration_expired") : 0;
        if (brand.Family == BrandFamily.Magma)
            extraAreaPct += GolemStat(player, "penance_brand_area_of_effect_+%");
        if (extraAreaPct != 0 && Stats.Key(SkillArea.BaseIncreased) is uint baseKey)
            own = new Dictionary<uint, int>(own) { [baseKey] = own.GetValueOrDefault(baseKey) + extraAreaPct };
        int radius = SkillRadius(conn, own, spell: true, brand.Effect);
        return Math.Max(4, radius + extraBaseRadius);
    }

    static void DealBrandSpellHitLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, SupportedGem gem, IReadOnlyDictionary<uint, int> player,
        uint monsterId, SpawnedMonster m, double now, double hitMore, double ailmentMore,
        (DamageType Type, int Pct)? extraPenetrationType = null, string label = "Brand")
    {
        if (m.Invulnerable || MonsterBlocks(m, Random.Shared, spell: true, now, gem.Stats)) return;
        if (SupportedHitInputsFor(conn, skillId, capturedGem: gem) is not { } inputs) return;

        long vsBlinded = Blinded(conn, monsterId) ? inputs.Get(PlayerHitDamage.VsBlindedDamageStat) : 0;
        long brandSpecific = BrandFamilyOf(SkillsFor(conn).EffectId(gem.Effect)) switch
        {
            BrandFamily.Conduit => GolemStat(player, "storm_brand_damage_+%"),
            BrandFamily.Cataclysm => GolemStat(player, "armageddon_brand_damage_+%"),
            BrandFamily.Magma => GolemStat(player, "penance_brand_damage_+%"),
            _ => 0,
        };
        if (BrandMonsterMarks.TryGetValue(m, out var bm) && bm.AttachedCount > 0)
            brandSpecific += GolemStat(player, "sigil_attached_target_damage_+%");
        var hit = SupportedHitFrom(inputs, suppress: true, additionalIncreased: vsBlinded + brandSpecific);
        if (hitMore != 1.0) hit = hit.Scale(hitMore);

        var gambler = GamblerFor(player);
        var rolled = WithConditionalSupportDamageLocked(conn, gem.Stats, attack: false, m,
            GameEvents.WithOutgoingDamage(conn, player, m, hit.Roll(Random.Shared)), now);

        int critChance = CriticalChanceOn(conn, monsterId, SupportedCriticalChance(conn, skillId),
            SupportedCriticalChanceVsBlinded(conn, skillId, release: false),
            SupportedCriticalChanceVsGround(conn, skillId, release: false));
        bool critical = RollCriticalStrike(conn, critChance, gem.Stats, attack: false, player: player);
        int critMult = CriticalMultiplierOn(m, SupportedCriticalMultiplier(conn, skillId), player, gem.Stats);

        var pen = Penetration.Of(player, Stats, attack: false, gem.Stats);
        if (extraPenetrationType is { } ep)
            pen = ep.Type switch
            {
                DamageType.Fire => pen with { Fire = pen.Fire + ep.Pct },
                DamageType.Cold => pen with { Cold = pen.Cold + ep.Pct },
                DamageType.Lightning => pen with { Lightning = pen.Lightning + ep.Pct },
                _ => pen,
            };
        var resistMods = HitResistMods(player, gem.Stats);

        var (damage, _, mitigated) = MonsterHitDamage(m, rolled, critical, attack: false, now, resistMods, pen,
            gambler.InvertsResistance(Random.Shared));
        if (critical) damage = DamageModel.CriticalDamage(damage, critMult);
        if (damage <= 0) return;

        var taken = MonsterTakenComponents(m, mitigated, spell: true, now);
        var leech = HitLeechSources(player, attack: false, gem.Stats, null, melee: false);
        LeechOnHitLocked(conn, m, taken, damage, leech);

        var stun = OwnerStun(conn, m, damage, mitigated, melee: false, spell: true, critical, player, gem.Stats);
        if (ApplyMonsterHitLocked(conn, m, damage, null, null, allyAt: null,
                cull: Cull.Of(player, gem.Stats, critical, spell: true), cullPlayer: player)
            is not { } applied) return;
        if (applied.StageStarts) { CommitBossStage(remote, stream, ciphers, conn, monsterId, m, now); applied = applied with { StageStarts = false }; }

        CrueltyOnHitLocked(conn, skillId, damage, m.Life + (uint)damage, m.MaxLife, gem.Stats, now);
        ArcaneSurgeOnSpellHitLocked(conn, player, m);
        if (ElementalOverloadCritLocked(conn, skillId, SkillsFor(conn).EffectId(gem.Effect), player, critical) is { } eoPacket)
            conn.Minions.OutputQueue.Add(new(eoPacket, "Elemental Overload from brand crit", WorldId: PlayerId));

        if (!m.Dead)
        {
            var poison = SupportedPoison(conn, skillId, spell: true, player, additionalIncreased: vsBlinded, capturedInputs: inputs);
            if (ailmentMore != 1.0)
                poison = poison with { Min = poison.Min * ailmentMore, Max = poison.Max * ailmentMore,
                    CriticalMin = poison.CriticalMin * ailmentMore, CriticalMax = poison.CriticalMax * ailmentMore };
            AilmentsOnHitLocked(conn, monsterId, m, mitigated, spell: true, critical, critMult, player, gem.Stats, now,
                Random.Shared, poison: poison, sourceSkill: skillId, blind: BlindHitInput.Capture(player, gem.Stats), playerHit: true);
            var ignite = SupportedIgnite(conn, skillId, spell: true, player, additionalIncreased: vsBlinded, capturedInputs: inputs);
            if (ignite.Max > 0)
            {
                if (ailmentMore != 1.0)
                    ignite = ignite with { Min = ignite.Min * ailmentMore, Max = ignite.Max * ailmentMore,
                        CriticalMin = ignite.CriticalMin * ailmentMore, CriticalMax = ignite.CriticalMax * ailmentMore };
                foreach (var packet in IgniteOnHitLocked(conn, monsterId, m, ignite, critical, now, playerIgnite: true) ?? [])
                    conn.Minions.OutputQueue.Add(new(packet, $"{label} ignite", monsterId, m));
            }
        }
        CommitAcceptedHitLocked(conn, monsterId, m, applied, damage, now, label, playerKill: true, stun: stun);
    }

    // ---- Client Presentation: Effects, Beams, Monster Buffs, HUD Counters -----------------------------------------------

    static void SpawnDetachedVisualLocked(ClientState conn, ActiveBrand brand, WorldRecordBuilder bytes)
    {
        if (brand.GroundObjectId != 0) return;
        uint id = conn.TakeObjectId();
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

    static void SpawnTempEffectLocked(ClientState conn, BrandState state, (int X, int Y) at, string ao,
        double removeAt, WorldRecordBuilder bytes, float scale = 1f)
    {
        uint id = conn.TakeObjectId();
        bytes.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(at.X, at.Y, ao) { Flags0 = EffectObject.FixedRotation, Scale = scale }.Blob()));
        bytes.Presentation(id, InstanceProtocol.EnterWakeZone(id));
        state.TempObjects.Add((id, removeAt));
    }

    static void SpawnBeamLocked(ClientState conn, BrandState state, (int X, int Y) start, uint startObj,
        (int X, int Y) end, uint endObj, ushort beamRow, double removeAt, WorldRecordBuilder bytes)
    {
        uint id = conn.TakeObjectId();
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

    internal static byte[] PenanceEnergyBuff(uint monsterId, ushort instance, ushort energy) =>
        InstanceEntities.AddBuff(monsterId, instance, PenanceEnergyBuffRow, PlayerId,
            stacks: energy, list: [PenanceMaxEnergy]);

    static void ReconcileBrandPresentationLocked(ClientState conn, BrandState state, double now, WorldRecordBuilder bytes)
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
            if (b.Attached && conn.Monsters.ContainsKey(b.AttachedMonsterId))
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
            bool exists = conn.Monsters.TryGetValue(mid, out var monster) && monster is { Dead: false, Life: > 0 };
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
                    if (conn.Monsters.ContainsKey(mid))
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
                        ushort buffId = cur.Buff != 0 ? cur.Buff : conn.NextBuff++;
                        uint? dur = info.MaxRemaining > 0 ? BitConverter.SingleToUInt32Bits((float)info.MaxRemaining) : null;
                        bytes.World(mid, InstanceEntities.AddBuff(mid, buffId, pres.AttachedBuffRow, PlayerId, info.SkillId,
                            u68: dur, stacks: (ushort)info.Count, list: [pres.AttachedMiscAnimatedRow, 0]));
                        vis.Attached[fam] = (buffId, info.Count, now + info.MaxRemaining);
                    }
                }
            }

            // Penance Brand energy stacks (BuffDefinitions 1551 magma_pustule)
            int energy = exists && BrandMonsterMarks.TryGetValue(monster!, out var mark) ? mark.PenanceEnergy : 0;
            if (energy <= 0 && vis.PenanceBuff != 0)
            {
                if (conn.Monsters.ContainsKey(mid)) bytes.World(mid, InstanceEntities.RemoveBuff(mid, vis.PenanceBuff));
                vis.PenanceBuff = 0;
                vis.PenanceStacks = 0;
            }
            else if (energy > 0 && energy != vis.PenanceStacks)
            {
                if (vis.PenanceBuff == 0) vis.PenanceBuff = conn.NextBuff++;
                bytes.World(mid, PenanceEnergyBuff(mid, vis.PenanceBuff, (ushort)energy));
                vis.PenanceStacks = energy;
            }

            // Wintertide Brand primary DoT visual (BuffDefinitions 1550 immolation_brand_burn)
            bool hasWinterPrimary = exists && desiredAttached.TryGetValue(mid, out var winterFamilies) && winterFamilies.ContainsKey(BrandFamily.Immolation);
            if (!hasWinterPrimary && vis.WintertidePrimaryBuff != 0)
            {
                if (conn.Monsters.ContainsKey(mid)) bytes.World(mid, InstanceEntities.RemoveBuff(mid, vis.WintertidePrimaryBuff));
                vis.WintertidePrimaryBuff = 0;
                vis.WintertideLastAt = 0;
            }
            else if (hasWinterPrimary && vis.WintertidePrimaryBuff == 0)
            {
                vis.WintertidePrimaryBuff = conn.NextBuff++;
                bytes.World(mid, InstanceEntities.AddBuff(mid, vis.WintertidePrimaryBuff, WintertidePrimaryBuffRow, PlayerId));
            }

            if (vis.Attached.Count == 0 && vis.PenanceBuff == 0 && vis.WintertidePrimaryBuff == 0)
                state.MonsterVisuals.Remove(mid);
        }

        // 3. Publish changed SigilDisplay HUD counter stats on PlayerId via 0x1b3
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
            bytes.World(PlayerId, InstanceEntities.StatsUpdate(PlayerId, statDelta));
    }
}
