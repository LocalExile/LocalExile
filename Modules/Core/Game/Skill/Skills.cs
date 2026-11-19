using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.World;
using System.Collections.Frozen;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;

public static partial class Game
{
    // The player's granted skills: the default attack with the unarmed weapon's stats and every stat the server set on the
    // player since (a skill's stats include the character's; the client reads the skill's). A gem whose requirements the
    // character does not meet makes its item inactive (EquipmentStatSources) like a worn item the character is too low for,
    // so GemSkillEffects leaves its skill out: the client has nothing to cast. A use that arrives anyway (a stale client) is
    // refused (GemDisabled).
    public static IReadOnlyList<GrantedSkill> PlayerSkills(Player conn)
    {
        lock (conn.Monsters) ReconcileBeastCompanionsLocked(conn);
        // Headhunter, Rage, Fortification and charge changes call this many times a second. A short cache keeps the
        // client's skill records at most a frame or two stale while preventing a full build inside the send gate per
        // change (2026-11-05: 180 builds in one fight, 0x0025 handlers at 100-200 ms).
        // Reused until its inputs change (the character's stats, equipment, skill cosmetics, pools and life:
        // SkillSnapshotInputs), not for a fixed time; a full refresh (SendCharacterStats: weapons, gems, items, passives)
        // drops it at once. The stats carry Rage, charges, Onslaught and the auras, whose attack and cast times the
        // client's actions follow, so those rebuild it too.
        double now = AreaTime(conn);
        var currentStats = CharacterPlayerStats(conn);
        if (conn.PlayerSkillsCache is { } cached && PlayerSkillsInputs.TryGetValue(conn, out var was) && ReferenceEquals(was.Skills, cached)
            && was.Inputs.Matches(conn, currentStats)) return cached;
        var inputs = SkillSnapshotInputs.Capture(conn, currentStats);
        // One build reads one snapshot of the character's stats, final stats and supported gems: every gem's cast time,
        // hit, crit and panel rows used to recompute them (CharacterPlayerStats, FinalPlayerStats, SupportedSkill) five or
        // six times per gem, 180-270 ms per build in the Debug server, inside the send gate. The snapshot also captures
        // the panel's raw sources for the selected-skill aliases (GemSkills).
        var character = CharacterPlayerStatsSnapshot(conn, out _, out var panelSources, capturePanelSources: true);
        var outer = StatsScope.Value;
        var build = new SkillBuildScope(conn, character);
        StatsScope.Value = build;
        try
        {
            var player = FinalPlayerStatsFrom(conn, new(character)).ToList();
            var skills = new List<GrantedSkill>([.. PlayerDefaultSkills(conn, player, character),
                .. GemSkills(conn, player, character, panelSources!), .. GameEvents.ExtraSkills(conn)]);
            skills.AddRange(SentinelSourceSkills(conn, skills).ToArray());
            conn.PlayerSkillsCache = skills;
            conn.PlayerSkillsAt = now;
            PlayerSkillsInputs.AddOrUpdate(conn, new(inputs, skills));
            return skills;
        }
        finally { build.Active = false; StatsScope.Value = outer; }
    }

    /// <summary>The PlayerSkills build running on this flow: what it computed once and reuses instead of recomputing.</summary>
    public sealed class SkillBuildScope(Player conn, Dictionary<uint, int>? stats = null)
    {
        Dictionary<uint, int>? character = stats;
        public Player Conn => conn;
        public volatile bool Active = true;
        /// <summary>The one character snapshot of this flow's work: a client message's whole validation, payment and
        /// launch, or one PlayerSkills build. Built on the first read, so a message that reads no stats pays nothing.
        /// (2026-11-05: a cast message built it five or six times, and 154 builds a second cost a full core.)
        /// In an area's frames that is the graph's last Stats phase (a change made in this flow reaches the stats, and the
        /// client, in the next one); off them a fresh build.</summary>
        public Dictionary<uint, int> Stats => character ??= CharacterPlayerStatsCore(conn, out _);
        public List<(uint, int)>? Final;
        public readonly Dictionary<ushort, SupportedGem> Supported = [];
    }

    public static readonly AsyncLocal<SkillBuildScope?> StatsScope = new();

    /// <summary>One character snapshot for this flow's work (<c>using var</c> scope). A client message's validation,
    /// payment and launch reuse the first build; a fresh one starts outside any scope. Nested scopes keep the outermost.</summary>
    public readonly struct PlayerStatScope : IDisposable
    {
        readonly SkillBuildScope? outer;
        readonly SkillBuildScope? owned;
        public PlayerStatScope(Player conn, bool fresh = false)
        {
            outer = StatsScope.Value;
            owned = fresh || outer is null || !outer.Active || !ReferenceEquals(outer.Conn, conn) ? new(conn) : null;
            if (owned is not null) StatsScope.Value = owned;
        }
        public void Dispose()
        {
            // AsyncLocal restoration affects only this flow. A frame loop or delayed action can retain the same
            // scope, so retire its snapshot too. Native attack timing reads the current skill stats (0x141b58740).
            if (owned is not null) owned.Active = false;
            StatsScope.Value = outer;
        }
    }

    public static SkillBuildScope? BuildScopeOf(Player conn) =>
        StatsScope.Value is { Active: true } scope && ReferenceEquals(scope.Conn, conn) ? scope : null;

    // The player's stats as the server sets them: what /set_stat set, then the attack's final values computed from them
    // (the client derives nothing itself: 11-skills.md §4).
    // FinalPlayerStats as a full 0x1b3, which only sets the stats it lists: every stat sent before that is gone now (a
    // stat only an unequipped item or a refunded passive gave) goes with 0, so the client does not keep its old value.
    public static List<(uint, int)> PlayerStatsWithCleared(Player conn)
    {
        RefreshSingleUseCooldowns(conn);
        var stats = FinalPlayerStats(conn).ToList();
        var now = stats.Select(s => s.Item1).ToHashSet();
        lock (conn.Monsters)
        {
            stats.AddRange(conn.SentStatKeys.Where(k => !now.Contains(k)).Select(k => (k, 0)));
            conn.SentStatKeys = now;
            RefreshPeerScale(conn, stats.Single(s => s.Item1 == ActorScale.Final).Item2);
        }
        // The ground around a stationary player follows the stats the client was told (the stat presentations).
        int Sent(uint key) => stats.FirstOrDefault(s => s.Item1 == key).Item2;
        conn.FlaskGrounds.Configure(FlaskGrounds.StationaryRadius(Sent), Sent(FlaskGrounds.LingerStat),
            Sent(FlaskGrounds.ProfaneLingerStat));
        // So do the player's debuffs on monsters (EnemyDebuffsTick): his passives' enemy auras and his taunt's increase.
        ushort[] nodes;
        lock (conn.Monsters) nodes = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
        conn.EnemyDebuffs = new(Sent(Stat.TauntedEnemiesDamageTakenPlusPct));
        return stats;
    }

    public const uint LowestAttributeDamage = Stat.DamagePlusPctPer5OfYourLowestAttribute;
    public static readonly (uint Conditional, uint Plain, uint Higher, uint Lower)[] AttributeAilmentConditions =
    [
        (Stat.CannotBeIgnitedWithStrengthHigherThanDex, Stat.CannotBeIgnited, Stat.Strength, Stat.Dexterity),
        (Stat.CannotBeFrozenWithDexHigherThanInt, Stat.CannotBeFrozen, Stat.Dexterity, Stat.Intelligence),
        (Stat.CannotBeShockedWithIntHigherThanStrength, Stat.CannotBeShocked, Stat.Intelligence, Stat.Strength),
    ];

    public static IEnumerable<(uint, int)> FinalPlayerStats(Player conn)
    {
        if (BuildScopeOf(conn) is not { } scope) return FinalPlayerStatsCore(conn);
        return [.. scope.Final ??= [.. FinalPlayerStatsCore(conn)]];
    }

    public static IEnumerable<(uint, int)> FinalPlayerStatsCore(Player conn) =>
        FinalPlayerStatsFrom(conn, CharacterPlayerStats(conn));

    // The final stats from a character snapshot already worked out (PlayerSkills reuses its own).
    public static IEnumerable<(uint, int)> FinalPlayerStatsFrom(Player conn, Dictionary<uint, int> stats)
    {
        KeepExpandedInventoryVisible(conn, stats);
        // Both hands' native weapon types and whether they dual wield (a value /set_stat set stays). The client derives none
        // of them from network stats: quivers need a bow's 9 in the main hand, and without is_dual_wielding it held 1,
        // which greyed out Shield Charge beside a one-handed weapon (13-items.md "Client weapon-type stats").
        var mainHand = ActiveMainHand(conn);
        int mainType = WeaponTypeStats.Of(mainHand?.Type?.Class), offType = WeaponTypeStats.Of(ActiveOffHand(conn)?.Type?.Class);
        if (Stat.MainHandWeaponType is var mainTypeKey) stats.TryAdd(mainTypeKey, mainType);
        if (Stat.OffHandWeaponType is var offTypeKey) stats.TryAdd(offTypeKey, offType);
        if (Stat.IsDualWielding is var dualKey)
            stats.TryAdd(dualKey, WeaponTypeStats.IsDualWielding(mainType, offType,
                Stat.UnarmedCountsAsDualWielding is var unarmedKey && stats.GetValueOrDefault(unarmedKey) != 0) ? 1 : 0);
        // Both hands' weapon stats. A dual-wielded attack swings the off hand as well, and the client sets that strike's
        // weapon up from the off hand's own final stats (it derives nothing itself, 11-skills.md §4).
        foreach (var (k, v) in WeaponStats(conn, WeaponOf(conn, mainHand), stats)) stats[k] = v;
        foreach (var (k, v) in CosmeticStats(conn).Where(s => !ActorScale.IsInput(s.Stat))) stats.TryAdd(k, v);
        foreach (var build in GameEvents.FinalStatsBuilding.For(conn)) build(conn, stats);
        return stats.Select(kv => (kv.Key, kv.Value));
    }

    // The main hand's final weapon stats and, when the off hand holds a weapon, the off hand's too (off_hand_… instead of
    // main_hand_…). Until issue #167 the Gateway sent no off-hand stat at all, so the second weapon of a dual-wielded attack
    // had no attack time, damage or reach on the client; the off-hand animation of a Holy Strike on the ground then ran
    // with a null animation set and the next action crashed (0x141c9b351, AnimationController+0x180 = 0). Frida 2026-10-25:
    // while it creates the attack action the client reads off_hand_minimum/maximum_attack_distance.
    // Each hand's local speed is already in its own duration; both receive only the character's shared speed bucket.
    public static List<(uint, int)> WeaponStats(Player conn, SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> stats)
    {
        var conversion = DamageStats.Conversion(stats, Stats, attack: true);
        var mainHit = AttackHitConverted(weapon, stats, MeleeAttack(conn), conversion);
        var accuracy = OrdinaryAccuracy(conn, stats, weapon.LocalSources);
        double damageFactor = CriticalStrikePolicy.DamageFactor(CriticalProhibitedBy(stats, attack: true, own: null)
            ? 0 : conn.Pools.AttackCriticalStrikeChance, AttackCriticalMultiplier(conn, stats), CriticalExtraRollsFor(conn, stats),
            accuracy.Chance(Monsters.BaseEvasion(conn.Pools.Level) ?? 0, false, false));
        var list = weapon.Stats(stats, mainHit, damageFactor).ToList();
        AddHand(mainHit, "main_hand");
        if (OffhandWeaponFor(conn) is { } off)
        {
            var offHit = AttackHitConverted(off, stats, MeleeAttack(conn), conversion);
            list.AddRange(off.OffStats(stats, offHit));
            AddHand(offHit, "off_hand");
            // The hands alternate: the panel's per-use, per-second and attacks-per-second rows average both, each hand
            // weighted by its own critical chance and chance to hit (the Off Hand rows, OffHandPanelStats).
            int offSpeed = list.First(s => s.Item1 == Stat.OffHandAttackSpeedPlusPct).Item2;
            var (perUse, perSecond, attacksPerSecond) = DualWieldPanel.Average(mainHit, weapon.AttackSeconds(stats),
                offHit, DualWieldPanel.OffHandSeconds(off, offSpeed), damageFactor,
                OffHandDamageFactor(conn, stats, n => n is var key ? stats.GetValueOrDefault(key) : 0, own: null));
            Put(Stat.DisplayHundredTimesDamagePerSkillUse, perUse);
            Put(Stat.HundredTimesDamagePerSecond, perSecond);
            Put(Stat.HundredTimesAttacksPerSecond, attacksPerSecond);
        }
        return list;

        void Put(uint key, int value)
        {
            int index = list.FindIndex(s => s.Item1 == key);
            if (index >= 0) list[index] = (key, value);
            else list.Add((key, value));
        }

        void AddHand(GameServer.Core.World.DamagePacket hit, string hand)
        {
            foreach (var (stat, value) in PanelStats.HandDamage(hit, hand))
                if (stat is var key) Put(key, value);
        }
    }

    // The off hand's critical-weighted, hit-chance-weighted damage factor for the dual-wield average, from the Off Hand
    // rows already worked out (OffHandPanelStats; rows: the published ones, or a selected gem's); null when no off hand
    // is swung, so the average falls back to the main hand's factor.
    public static double? OffHandDamageFactor(Player conn, IReadOnlyDictionary<uint, int> player, Func<uint, int> rows,
        IReadOnlyDictionary<uint, int>? own)
    {
        if (rows(Stat.SkillUsesOffHand) == 0) return null;
        return CriticalStrikePolicy.DamageFactor(CriticalProhibitedBy(player, attack: true, own) ? 0 : rows(Stat.OffHandCriticalStrikeChance),
            rows(Stat.OffHandCriticalStrikeMultiplier), CriticalExtraRollsFor(conn, player, own), rows(Stat.OffHandChanceToHitPct));
    }

    public static AttackAccuracyProfile OrdinaryAccuracy(Player conn, IReadOnlyDictionary<uint, int> stats,
        IReadOnlyDictionary<uint, int>? local) =>
        OrdinaryAccuracy(SkillsFor(conn), SkillEffectId(conn, SkillCatalog.FirstSkillId), conn.Pools, stats, local)
            with { Rating = PlayerAccuracy(conn, stats) };

    // From the default attack's GrantedEffects Id (SkillEffectId of the first skill) and the pools as read once.
    public static AttackAccuracyProfile OrdinaryAccuracy(SkillCatalog skills, string? attackEffectId, CharacterStats.Pools pools,
        IReadOnlyDictionary<uint, int> stats, IReadOnlyDictionary<uint, int>? local)
    {
        int effect = attackEffectId is { } id ? skills.EffectRow(id) : -1;
        var own = skills.EffectStats(effect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        return AttackAccuracyProfile.From(pools, stats, Stats, own, local);
    }

    // The player's stats (PlayerStatSteps.cs): in an area's frames the stat graph's last step.
    public static Dictionary<uint, int> CharacterPlayerStats(Player conn)
    {
        // Temporary lag instrumentation: how often the full stat build runs and what it costs (per connection).
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (BuildScopeOf(conn) is { } scope) return new(scope.Stats);
            return CharacterPlayerStatsCore(conn, out _);
        }
        finally { conn.Performance.StatBuild(started); }
    }

    // spell_effective_total_chance_permyriad_for_hit_to_be_critical / _double_critical: the spell chance after the
    // luck policy (CriticalExtraRollsFor) and the Assassin's bifurcation (spell_critical_strikes_bifurcate); zero when
    // critical strikes are prohibited, as the chance row itself is.
    public static IEnumerable<(uint Stat, int Value)> SpellEffectiveCritical(Player conn, IReadOnlyDictionary<uint, int> player,
        int chance, IReadOnlyDictionary<uint, int>? own) =>
        SpellEffectiveCritical(() => CriticalOnLowLife(conn, player), player, chance, own);

    // onLowLife: CriticalOnLowLife of the same player stats, asked when the rows are enumerated, as the line above does.
    public static IEnumerable<(uint Stat, int Value)> SpellEffectiveCritical(Func<bool> onLowLife, IReadOnlyDictionary<uint, int> player,
        int chance, IReadOnlyDictionary<uint, int>? own)
    {
        bool Flag(uint key) => (player.GetValueOrDefault(key) != 0 || (own?.GetValueOrDefault(key) ?? 0) != 0);
        int allowed = CriticalProhibitedBy(player, attack: false, own) ? 0 : chance;
        int extraRolls = CriticalExtraRollsFor(onLowLife(), player, own);
        bool bifurcate = Flag(Stat.SpellCriticalStrikesBifurcate);
        yield return (Stat.SpellEffectiveTotalChancePermyriadForHitToBeCritical,
            CriticalStrikePolicy.EffectiveChance(allowed, extraRolls, bifurcate));
        yield return (Stat.SpellEffectiveTotalChancePermyriadForHitToBeDoubleCritical,
            CriticalStrikePolicy.DoubleCriticalChance(allowed, extraRolls, bifurcate));
    }

    // The melee context of an attack's damage modifiers. An attack gem classifies
    // its own ActiveSkillType Melee; the default attack and the panel swing the active weapon set's weapon where an
    // inactive weapon is not there at all (WeaponProfileFor) and unarmed swings are melee.
    public static bool MeleeAttack(Player conn, int gemEffect = -1) =>
        gemEffect >= 0 && SkillsFor(conn).IsAttack(gemEffect) ? SkillsFor(conn).IsMelee(gemEffect)
            : WeaponProfileFor(conn, activeSet: true).Melee;

    // Conversion for one hit: the player's stats plus the skill's own stats (base/skill conversion). Player stats win on
    // a key collision; the normal case is that the skill adds stats the player does not have.
    public static DamagePacket AttackHit(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> stats, bool melee) =>
        AttackHitConverted(weapon, stats, melee, null);

    // Resolve conversion history and modifiers from raw local/added damage, then apply Rage once.
    public static DamagePacket AttackHitConverted(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> stats,
        bool melee, GameServer.Core.World.DamageConversion? conversion, long additionalIncreased = 0)
    {
        var Get = WeaponAttributeDamage.Read(WeaponAttributeDamage.Armed(weapon), name => RageStat(stats, name));
        var hit = PlayerHitDamage.Resolve(SupportedAttackDamage.Raw(weapon, Get), Get,
            conversion ?? new(), spell: false, melee: melee, projectile: !melee, wieldingBow: weapon.Bow, wieldingWand: weapon.WieldingWand,
            additionalIncreased: additionalIncreased + SupportedAttackDamage.WeaponIncrease(weapon, Get));
        return MaximumAttackDamage.Apply(RageStats.ApplyAttack(hit, Get), Get).Scale(GamblerFor(stats).DamageFactor);
    }

    public static double AddedEffectiveness(Player conn, int effect, int level) => SkillsFor(conn).DamageEffectiveness(effect, level) is int value and > 0 ? value / 10000.0 : 1;

    public static GameServer.Core.World.DamageConversion ConversionFor(Player conn, int effectRow, int level, bool attack)
    {
        return DamageStats.Conversion(CharacterPlayerStats(conn),
            effectRow >= 0 ? SkillsFor(conn).EffectStats(effectRow, level) : [], Stats, attack);
    }

    // The class's attributes (Characters.dat BaseStrength / BaseDexterity / BaseIntelligence) as final stats: strength,
    // dexterity and intelligence for the character panel, and virtual_*_for_requirements, which item requirements are
    // checked against (0x1402ceb80 via the Player cache 0x141e86a30, keys 0x3d1b-0x3d1d; global_no_attribute_requirements
    // skips the check, global_item_attribute_requirements_+% scales it).
    // With the level-based pools (CharacterStats): level, maximum_life / maximum_mana (and their base_ forms),
    // accuracy_rating, physical_damage_reduction_rating (armour) and evasion_rating (base_evasion_rating 15), as final
    // values, and the attributes' increases: armour/evasion_rating_+%, maximum_energy_shield_+% (intelligence) and
    // melee_physical_damage_+% (strength). The Offence panel's accuracy row reads main_hand_accuracy_rating
    // (CharacterPanelStats MH_Accuracy); its Chance to
    // Hit rows read main_hand_chance_to_hit_% and main_hand_chance_to_hit_evasive_monsters_% directly. The reference is a
    // monster of the character's level (the normal and high-dexterity evasion rows of DefaultMonsterStats).
    public static IEnumerable<(uint, int)> ClassAttributes(int classRow, CharacterStats.Pools pools)
    {
        int referenceEvasion = Monsters.BaseEvasion(pools.Level) ?? 0;
        int referenceEvasiveEvasion = Monsters.BaseEvasiveEvasion(pools.Level) ?? 0;
        int chanceToHit = DamageModel.AccuracyHitChance(referenceEvasion, pools.Accuracy);
        int evasiveChanceToHit = DamageModel.AccuracyHitChance(referenceEvasiveEvasion, pools.Accuracy);
        foreach (var (stat, value) in new[] { (Stat.Level, pools.Level), (Stat.BaseMaximumLife, (int)pools.Life), (Stat.MaximumLife, (int)pools.Life),
            (Stat.BaseMaximumMana, (int)pools.Mana), (Stat.MaximumMana, (int)pools.Mana), (Stat.AccuracyRating, pools.Accuracy),
            (Stat.MainHandAccuracyRating, pools.Accuracy), (Stat.OffHandAccuracyRating, pools.Accuracy),
            (Stat.MainHandChanceToHitPct, chanceToHit), (Stat.OffHandChanceToHitPct, chanceToHit),
            (Stat.MainHandChanceToHitEvasiveMonstersPct, evasiveChanceToHit),
            (Stat.OffHandChanceToHitEvasiveMonstersPct, evasiveChanceToHit),
            (Stat.CharacterSheetReferenceEvasion, referenceEvasion),
            (Stat.CharacterSheetReferenceHighDexEvasion, referenceEvasiveEvasion),
            (Stat.PhysicalDamageReductionRating, pools.Armour), (Stat.PhysicalDamageReductionRatingPlusPct, pools.ArmourPct),
            (Stat.BaseEvasionRating, CharacterStats.BaseEvasion), (Stat.EvasionRating, pools.Evasion), (Stat.EvasionRatingPlusPct, pools.EvasionPct),
            (Stat.MaximumEnergyShield, (int)pools.EnergyShield), (Stat.MaximumEnergyShieldPlusPct, pools.EnergyShieldPct), (Stat.MeleePhysicalDamagePlusPct, pools.MeleePhysicalDamagePct),
            (Stat.MovementVelocityPlusPct, pools.MovementVelocityPct),
            (Stat.CombinedBaseBlockPct, pools.BlockChanceBase), (Stat.AttackBlockPct, pools.BlockChance),
            // The character panel prints attack block as "capped% (uncapped%)"; the client does not derive the second value.
            (Stat.UncappedAttackBlockPct, pools.BlockChanceUncapped),
            // A separate spell block row is only drawn when display_spell_block_chance is non-zero.
            (Stat.SpellBlockPct, pools.SpellBlockChance), (Stat.DisplaySpellBlockChance, pools.SpellBlockChance),
            (Stat.BaseSpellBlockPct, 0),
            // Resistance display: the client shows fire_damage_resistance_% as the capped value and
            // uncapped_fire_damage_resistance_% (and the other three) in parentheses as the overcap. The passives' base
            // resistances are already summed into the pools, so zero the all-elements input to avoid counting it twice. The
            // maximum-resistance passives stay in BaseStats: the client adds them to its own 75 base. Do not send maximum_*
            // here; those keys are additive maximum-resistance modifiers, so sending 75 would count the base twice.
            (Stat.BaseResistAllElementsPct, 0),
            (Stat.BaseFireDamageResistancePct, pools.FireResistance),
            (Stat.FireDamageResistancePct, DamageModel.Capped(pools.FireResistance, pools.FireMaxResistance)),
            (Stat.UncappedFireDamageResistancePct, pools.FireResistance),
            (Stat.BaseColdDamageResistancePct, pools.ColdResistance),
            (Stat.ColdDamageResistancePct, DamageModel.Capped(pools.ColdResistance, pools.ColdMaxResistance)),
            (Stat.UncappedColdDamageResistancePct, pools.ColdResistance),
            (Stat.BaseLightningDamageResistancePct, pools.LightningResistance),
            (Stat.LightningDamageResistancePct, DamageModel.Capped(pools.LightningResistance, pools.LightningMaxResistance)),
            (Stat.UncappedLightningDamageResistancePct, pools.LightningResistance),
            (Stat.BaseChaosDamageResistancePct, pools.ChaosResistance),
            (Stat.ChaosDamageResistancePct, DamageModel.Capped(pools.ChaosResistance, pools.ChaosMaxResistance)),
            (Stat.UncappedChaosDamageResistancePct, pools.ChaosResistance),
            // The character panel reads the final suppression values, not only the raw passive input.
            (Stat.SpellSuppressionChancePct, pools.SpellSuppressionChance),
            (Stat.BaseSpellSuppressionChancePct, pools.SpellSuppressionChance),
            (Stat.CombinedSpellSuppressionChancePct, pools.SpellSuppressionChance),
            (Stat.SpellDamagePctSuppressed, pools.SpellSuppressionEffect),
            (Stat.BaseSpellDamagePctSuppressed, pools.SpellSuppressionEffect),
            // The Offence panel's crit rows read these final values (permyriad for chance, percent for multiplier).
            (Stat.MainHandCriticalStrikeChance, pools.AttackCriticalStrikeChance),
            (Stat.MainHandCriticalStrikeMultiplier, pools.CriticalStrikeMultiplier),
            (Stat.SpellCriticalStrikeChance, pools.SpellCriticalStrikeChance),
            (Stat.SpellCriticalStrikeMultiplier, pools.SpellCriticalStrikeMultiplier) })
            if (stat is var key) yield return (key, value);
        // Calculated attributes include legitimate all-zero values. Legacy partial pool records without attributes
        // still fall back to the class's own; record clones preserve the explicit resolved marker.
        var c = Skills.ClassFor(classRow);
        var (str, dex, intel) = pools.AttributesResolved || pools.Strength != 0 || pools.Dexterity != 0 || pools.Intelligence != 0
            ? (pools.Strength, pools.Dexterity, pools.Intelligence) : (c.Strength, c.Dexterity, c.Intelligence);
        foreach (var (stat, value) in new[] { (Stat.Strength, str), (Stat.Dexterity, dex), (Stat.Intelligence, intel),
            (Stat.VirtualStrengthForRequirements, str), (Stat.VirtualDexterityForRequirements, dex),
            (Stat.VirtualIntelligenceForRequirements, intel) })
            if (stat is var key) yield return (key, value);
    }

    // The skills of the gems socketed in the equipped items: each gem's GrantedEffects row at the gem's level with its own stats
    // (SkillCatalog.EffectStats: damage, cast time, mana cost) after the player's, with ids after the default attack's that
    // no basic action uses (SkillCatalog.SkillIds).
    public static IEnumerable<GrantedSkill> GemSkills(Player conn, IEnumerable<(uint, int)> player,
        IReadOnlyDictionary<uint, int> characterInputs, PanelSourceSnapshot panelSources)
    {
        string? character = conn.Character;
        var playerStats = new Dictionary<uint, int>();
        foreach (var (key, value) in player) playerStats[key] = value;
        var gems = GemSkillEffects(conn).ToArray();
        RemoveMissingSealSources(conn, gems.Select(g => g.Id));
        var skillCosmetics = CharacterSaves.SkillCosmetics(string.IsNullOrEmpty(character) ? "Exile" : character);
        foreach (var (id, effect, name, _, quality, gem) in gems)
        {
            var supported = SupportedSkill(conn, id);
            int level = supported.Level;
            var own = supported.Stats;
            UnleashSeals(conn, id, supported, ProjectileWorldTime(conn));
            var stats = MergeSkillStats(playerStats, own, SkillsFor(conn).IsChannelled(effect), SkillCostMore(conn, own));
            // A shield attack swings the shield in the off hand, which OffStats leaves out (not a weapon): the client then
            // read off_hand_total_base_weapon_attack_duration_ms 0, and Shield Charge's end-of-charge attack never finished
            // (Frida 2026-11-01, 11-skills.md "Shield Charge"). Its base time is the gem's own AttackTime.
            if (SkillsFor(conn).IsAttack(effect) && SkillsFor(conn).RequiresShield(effect)
                && ActiveOffHand(conn)?.Type?.Class == "Shield" && SkillsFor(conn).AttackTimeMs(effect, level) is int shieldMs and > 0)
            {
                stats[Stat.OffHandBaseWeaponAttackDurationMs] = shieldMs;
                stats[Stat.OffHandTotalBaseWeaponAttackDurationMs] = shieldMs;
            }
            if (Stat.VirtualExtraCriticalRolls is var criticalRolls)
                stats[criticalRolls] = CriticalExtraRollsFor(conn, playerStats, own);
            if (CooldownPlan(conn, supported, playerStats) is { } cooldown
                && Stat.VirtualCooldownSpeedPlusPct is var recoveryKey)
            {
                stats[recoveryKey] = cooldown.RecoveryPercent;
                // Otherwise the client propagates the use to the GEPL group before the second key can send its request.
                if (SkillsFor(conn).CooldownGroup(effect, level) is 1 or 11 && Stat.SkillCooldownGroupOverride is var group)
                    stats[group] = cooldown.SharedGroup ?? 36;
                if (Stat.VirtualTotalCooldownCount is var countKey) stats[countKey] = cooldown.Uses;
            }
            int additionalAttackMs = SkillsFor(conn).IsAttack(effect) ? SkillAttackTiming.AdditionalMilliseconds(stats) : 0;
            SkillAttackTiming.Apply(SkillsFor(conn), effect, level, stats);
            // Spark's slot 0x310 selects its tornado art and repeat policy from this final stat. The Twister stat
            // set does not contain it; without the projection the native triggered action creates ordinary Spark.
            if (SkillsFor(conn).EffectId(effect) == TwisterEffect && Stat.SkillBehaviourVariation is var tornadoMode)
                stats[tornadoMode] = 4;
            // The network skill record does not run the client's GrantedEffects.SkillID calculator. LeapSlam reads
            // this value for Groundbreaking's shorter range and its unskipped startup (0x1412fafd0 / 0x1419fda80).
            if (IsLeapSlam(conn, effect))
                stats[Stat.SkillBehaviourVariation] = SkillsFor(conn).BehaviourVariation(effect);
            // The client reads the final duration, not its base: its own Spark sparks live skill_effect_duration ms
            // (0x141b3860a) and without it died on their first tick, so none was drawn.
            if (Stat.BaseSkillEffectDuration is var baseDurationKey && own.GetValueOrDefault(baseDurationKey) > 0
                && Stat.SkillEffectDuration is var durationKey)
                stats[durationKey] = (int)Math.Round(1000 * SkillDurationModifiers.Seconds(own[baseDurationKey],
                    name => name is var k ? playerStats.GetValueOrDefault(k) : 0,
                    name => name is var k ? own.GetValueOrDefault(k) : 0));
            // Devout of Lunaris: the client aims a self-targeted Nova at the cursor only on
            // virtual_self_targeted_skills_target_location_instead, which its calculator (FUN_14289d210) derives from these
            // three stats; network stats run no calculators (04 §4.1a), so without it the Nova stayed on the caster (live).
            if (new[] { Stat.NovaSpellsCastAtTargetLocation, Stat.CastingSpell, Stat.SkillIsNovaSkill }
                    .All(n => n is var k && stats.GetValueOrDefault(k) != 0)
                && Stat.VirtualSelfTargetedSkillsTargetLocationInstead is var novaKey)
                stats[novaKey] = 1;
            // Parallel volley (0x141bbb460): the client reads virtual_additional_projectiles_fire_parallel_x_dist;
            // without it the distance is zero and parallel projectiles collapse onto each other.
            if (Stat.VirtualAdditionalProjectilesFireParallelXDist is var virtualParallelXKey
                && !stats.ContainsKey(virtualParallelXKey))
            {
                int parallelX = 0;
                if (Stat.VolleyAdditionalProjectilesFireParallelXDist is var volleyX && stats.TryGetValue(volleyX, out int vx) && vx > 0)
                    parallelX = vx;
                else if (Stat.GreaterVolleyAdditionalProjectilesFireParallelXDist is var gVolleyX && stats.TryGetValue(gVolleyX, out int gvx) && gvx > 0)
                    parallelX = gvx;
                if (parallelX > 0) stats[virtualParallelXKey] = parallelX;
            }
            if (SkillsFor(conn).IsSpell(effect) && !SkillsFor(conn).IsAttack(effect) && Stat.SpellCriticalStrikeChance is var critKey)
                stats[critKey] = SupportedCriticalChance(conn, id);
            // The skill effects worn in the gem's Cosmetics tab slots: their stats (skill_art_variation …) on this skill only.
            foreach (var (key, worn) in skillCosmetics)
                if (CharacterSaves.SkillCosmeticGem(key) == gem && Mtx.Resolve(worn) is { } record && MtxEffects.Of(record.MtxTypeHash) is { } fx)
                    foreach (var (k, v) in fx.Stats) stats[k] = v;
            // The derived stats the client reads in place of them (a hit effect, a surge, an ailment's art).
            foreach (var (k, v) in SkillCosmeticDerivedStats(conn, stats)) stats[k] = v;
            // A Crucible weapon's "Flicker Strike and Vigilant Strike's Cooldown can be bypassed by Power Charges": the client
            // reads only the skill's own override (CooldownBypass.FlickerVigilantPowerOverride), so it goes on these skills.
            if (CooldownBypass.FlickerVigilantPowerOverride is var crucibleKey && playerStats.GetValueOrDefault(crucibleKey) != 0
                && CooldownBypass.FlickerVigilantOverride(SkillsFor(conn).EffectId(effect), SkillsFor(conn).CooldownBypassType(effect, level))
                && CooldownBypass.PowerOverride is var overrideKey)
                stats[overrideKey] = 1;
            // The worn effect's skill_art_variation counts from the skill's own (SkillArtBase: Holy Sweep's is 2).
            if (Stat.SkillArtVariation is var artKey
                && SkillArtBase.Variation(SkillsFor(conn).EffectId(effect), stats.GetValueOrDefault(artKey)) is int art and not 0)
                stats[artKey] = art;
            // The native spell action divides virtual BASE time by cast speed itself. Keep the catalog base
            // intact; only the panel's final casts-per-second stat is adjusted here.
            int supportCastSpeed = Stat.BaseCastSpeedPlusPct is var cs ? own.GetValueOrDefault(cs) : 0;
            int castSpeedTotal = FinalCastSpeedPct(conn, stats, CastSpeedPct(stats) + supportCastSpeed);
            if (SpellCastTiming.ForSkill(SkillsFor(conn), effect, castSpeedTotal) is { } spellTiming)
            {
                spellTiming.ApplyTo(stats, Stats);
                // The native action divides by this skill's cast_speed_+% (0x1419e9f10): the total the server wait uses.
                if (Stat.CastSpeedPlusPct is var castKey && castSpeedTotal != CastSpeedPct(stats))
                    stats[castKey] = castSpeedTotal;
            }
            // Preserve the legacy non-spell/attack display path; attack timing is outside this fix.
            else if ((CastSpeedPct(stats) + supportCastSpeed) is int castSpeed and not 0 && SkillsFor(conn).CastMs(effect) is int castMs and > 0)
            {
                int ms = (int)Math.Round(castMs / (1 + castSpeed / 100.0));
                if (Stat.VirtualBaseSpellCastTimeMs is var vk) stats[vk] = ms;
                if (Stat.HundredTimesCastsPerSecond is var hk) stats[hk] = (int)Math.Round(100_000.0 / Math.Max(1, ms));
            }
            // A spell's tooltip DPS is its own hit (SpellDamage) × casts per second, not the default attack's values that
            // came with the player's stats.
            int Get(uint k) => stats.GetValueOrDefault(k);
            // The client's spell action repeats the cast this many times (SpellRepeats); the server repeats its hits as many.
            if (SpellRepeats(conn, effect, Get) is > 0 and int repeats && Stat.SpellRepeatCount is var repeatKey)
                stats[repeatKey] = repeats;
            // Network stats run no calculators. The native melee action reads this final count (0x141b71600).
            if (SkillsFor(conn).IsAttack(effect) && Stat.AttackRepeatCount is var attackRepeatKey)
                stats[attackRepeatKey] = MeleeAttackRepeats(conn, id);
            // Cyclone's client effect reads the final radius, not the increased-area input. Match AreaSkillHit.
            if (SkillArea.IsCyclone(SkillsFor(conn).EffectId(effect)))
            {
                // The player's base area is in the panel total already (AreaPlayerStats): only the gem's own counts here.
                int AreaGet(uint name) => name == SkillArea.BaseIncreased ? own.GetValueOrDefault(name) : Get(name);
                int baseRadius = SkillArea.BaseRadius(SkillsFor(conn).EffectId(effect), AreaGet);
                int increased = SkillArea.Increased(AreaGet, spell: false);
                int radiusPercent = SkillArea.RadiusPercent(AreaGet, spell: false);
                int radius = SkillArea.Radius(baseRadius, AreaGet, spell: false);
                if (Stat.ActiveSkillBaseAreaOfEffectRadius is var baseKey) stats[baseKey] = baseRadius;
                if (Stat.ActiveSkillAreaOfEffectRadius is var radiusKey)
                    stats[radiusKey] = radius;
                // Channelled Cyclone's conditional-radius path reads these derived stats instead of 0x4876.
                // Compute them before writing skill_area_of_effect_+%, or the base increase would count twice.
                if (Stat.TotalSkillAreaOfEffectRadiusPlusPctIncludingFinal is var scaleKey) stats[scaleKey] = radiusPercent;
                if (Stat.SkillAreaOfEffectPlusPct is var areaKey) stats[areaKey] = increased;
                Console.WriteLine($"[gateway] Cyclone area: base {baseRadius}, radius {radius}, increased area {increased}%, radius scale {radiusPercent}%");
            }
            // Molten Strike's client ballistic/explosion builders read 0x1591 (0x14193efd0) for both scatter range and
            // explosion radius; its StatSet omits active_skill_base_area_of_effect_radius, whose base is 9 (0x141b6baa0).
            else if (GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(conn).EffectId(effect)))
            {
                int AreaGet(uint name) => name == SkillArea.BaseIncreased
                    ? own.GetValueOrDefault(name) + Get(Stat.MoltenStrikeRadiusPlusPct)
                    : Get(name);
                int baseRadius = Get(Stat.ActiveSkillBaseAreaOfEffectRadius) is > 0 and var br ? br : 9;
                int increased = SkillArea.Increased(AreaGet, spell: false);
                int radiusPercent = SkillArea.RadiusPercent(AreaGet, spell: false);
                int radius = SkillArea.Radius(baseRadius, AreaGet, spell: false);
                if (Stat.ActiveSkillAreaOfEffectRadius is var radiusKey) stats[radiusKey] = radius;
                if (Stat.TotalSkillAreaOfEffectRadiusPlusPctIncludingFinal is var scaleKey) stats[scaleKey] = radiusPercent;
                if (Stat.SkillAreaOfEffectPlusPct is var areaKey) stats[areaKey] = increased;
            }
            // Other area skills: the client's effect builders read the final radius (0x4876) too; without it Ice Nova drew
            // nothing (live 2026-10-30). The radius the server's hit uses (SkillRadius, AreaSkillHit).
            else if (Get(Stat.ActiveSkillBaseAreaOfEffectRadius) > 0 && Stat.ActiveSkillAreaOfEffectRadius is var finalRadius)
                stats[finalRadius] = SkillRadius(conn, own, SkillsFor(conn).IsSpell(effect), effect);
            // The final secondary radius too (0x4879): Holy Sweep's hammer art scales to it over its base size
            // (0x1414f44b0, 0x1414f50e0), and at 0 the hammers were drawn at scale 0, never seen (live 2026-11-09).
            if (Get(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius) > 0
                && Stat.ActiveSkillSecondaryAreaOfEffectRadius is var secondaryRadius)
                stats[secondaryRadius] = SkillSecondaryRadius(conn, own, SkillsFor(conn).IsSpell(effect));
            // Hammerfalls' controller times its waves by the skill's combined_main_hand_attack_speed_+% (0x141b1a270); with
            // none sent its waves kept 0.7 s whatever the attack speed (live 2026-11-11). The server's waves read the same.
            if (Get(Stat.HolySweepHammerfallRateMs) > 0 && Stat.CombinedMainHandAttackSpeedPlusPct is var combinedSpeed)
                stats[combinedSpeed] = SupportedMainHandAttackSpeedPct(conn, id);
            // A trap or herald that deals secondary damage (Bear Trap carries the Spell type but no spell base damage,
            // Herald of Ice's shatter): its own secondary rows, before the attack/spell hit rows.
            if (!SkillsFor(conn).IsAttack(effect) && Get(Stat.DisplaySkillDealsSecondaryDamage) != 0
                && SkillsFor(conn).SecondaryHitDamage(effect, supported.Level, supported.Quality) is { IsZero: false } secondaryBase)
            {
                foreach (var (stat, value) in SecondaryPanelRows(conn, Get, playerStats, supported, secondaryBase))
                    if (stat is var key)
                        stats[key] = value;
            }
            else if (SkillsFor(conn).IsAttack(effect) || SkillsFor(conn).IsSpell(effect))
            {
                // One read of the gem's hit inputs serves its hit, its ailment rows and (dual-wielded) the off hand's.
                // Reap's more damage per Blood Charge held scales the hit itself, as AreaSkillHit does, so the damage
                // ranges, the ailment rows and the per-use/per-second rows agree.
                double bloodMore = BloodChargeDamageMore(conn, name => name is var k ? own.GetValueOrDefault(k) : 0);
                // A shield skill's rows are the shield's hit and swing (ShieldWeapon), as its hits are: the gem's own
                // AttackTime at the off hand's speed, without the main weapon's local attack speed.
                var shieldWeapon = ShieldWeapon(conn, id);
                var inputs = SupportedHitInputsFor(conn, id, weapon: shieldWeapon);
                SupportedHitInputs? offInputs = null;
                if (inputs is not null && bloodMore != 1) inputs = inputs with { Raw = inputs.Raw.Scale(bloodMore) };
                var supportedHit = inputs is null ? GameServer.Core.World.DamagePacket.Empty : SupportedHitFrom(inputs, suppress: true);
                double seconds = SkillsFor(conn).IsAttack(effect)
                    ? shieldWeapon is null ? SupportedAttackSeconds(conn, id) : SupportedOffHandAttackSeconds(conn, id, shieldWeapon)
                    : Math.Max(.001, (CastMs(conn, effect, id) ?? 0) / 1000.0);
                var finalPlan = SkillHitPlan.ForSkill(SkillsFor(conn), effect, supported.Level, supported.Quality);
                int criticalChance = SupportedCriticalChance(conn, id);
                int criticalMultiplier = SupportedCriticalMultiplier(conn, id);
                var accuracy = SkillsFor(conn).IsAttack(effect) ? SupportedAccuracy(conn, id) : default;
                double damageFactor = CriticalStrikePolicy.DamageFactor(
                    CriticalProhibitedBy(characterInputs, SkillsFor(conn).IsAttack(effect), own) ? 0 : criticalChance,
                    criticalMultiplier, CriticalExtraRollsFor(conn, characterInputs, own),
                    SkillsFor(conn).IsAttack(effect) ? accuracy.Chance(Monsters.BaseEvasion(conn.Pools.Level) ?? 0, false, false) : 100);
                var ranges = SkillsFor(conn).IsAttack(effect)
                    ? AttackPanelStats.Compute(finalPlan, supportedHit, 1 / seconds, damageFactor)
                    : SpellPanelStats.Compute(finalPlan, supportedHit, 1 / seconds,
                        includeMainHand: true, damageFactor: damageFactor);
                foreach (var (key, value) in ranges)
                    if (key is var statKey) stats[statKey] = value;
                if (SkillsFor(conn).IsAttack(effect))
                {
                    // Additional base time belongs in the duration row, not in an equivalent attack-speed penalty.
                    int attackBaseMs = (shieldWeapon ?? WeaponFor(conn)).AttackMs;
                    if (additionalAttackMs != 0) attackBaseMs = SkillAttackTiming.BaseMilliseconds(attackBaseMs, additionalAttackMs);
                    stats[Stat.MainHandAttackSpeedPlusPct] = (int)Math.Round((attackBaseMs / (seconds * 1000) - 1) * 100);
                    stats[Stat.HundredTimesAttacksPerSecond] = (int)Math.Round(100 / seconds);
                    foreach (var (field, value) in new[]
                    {
                        (Stat.MainHandAccuracyRating, accuracy.Rating),
                        (Stat.MainHandChanceToHitPct, accuracy.Chance(Monsters.BaseEvasion(conn.Pools.Level) ?? 0, false, false)),
                        (Stat.MainHandChanceToHitEvasiveMonstersPct, accuracy.Chance(Monsters.BaseEvasiveEvasion(conn.Pools.Level) ?? 0, false, false)),
                    })
                        if (field is var key)
                            stats[key] = value;
                    // A dual-wielded attack gem swings the off hand too: its own rows, with the same supports, and the
                    // per-use / per-second / attacks-per-second rows average both hands.
                    if (OffHandPanelStats(conn, playerStats, panelSources.MainHand, panelSources.OffHand,
                            stats.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct), own, effect) is { } offHand
                        && OffhandWeaponFor(conn) is { } offWeapon)
                    {
                        offInputs = SupportedHitInputsFor(conn, id, weapon: offWeapon);
                        var offHit = offInputs is null ? GameServer.Core.World.DamagePacket.Empty : SupportedHitFrom(offInputs, suppress: true);
                        var offRows = offHand.ToDictionary(r => r.Stat, r => r.Value);
                        double offSeconds = SupportedOffHandAttackSeconds(conn, id, offWeapon);
                        if (additionalAttackMs != 0)
                        {
                            int offBaseMs = SkillAttackTiming.BaseMilliseconds(offWeapon.AttackMs, additionalAttackMs);
                            int offSpeed = (int)Math.Round((offBaseMs / (offSeconds * 1000) - 1) * 100);
                            offHand[offHand.FindIndex(row => row.Stat == Stat.OffHandAttackSpeedPlusPct)] = (Stat.OffHandAttackSpeedPlusPct, offSpeed);
                        }
                        var (perUse, perSecond, attacksPerSecond) = DualWieldPanel.Average(supportedHit, seconds, offHit,
                            offSeconds, damageFactor,
                            OffHandDamageFactor(conn, characterInputs, n => offRows.GetValueOrDefault(n), own));
                        foreach (var (field, value) in offHand.Concat(OffHandDamageRows(offHit)))
                            if (field is var key)
                                stats[key] = value;
                        stats[Stat.DisplayHundredTimesDamagePerSkillUse] = perUse;
                        stats[Stat.HundredTimesDamagePerSecond] = perSecond;
                        stats[Stat.HundredTimesAttacksPerSecond] = attacksPerSecond;
                    }
                }
                stats[SkillsFor(conn).IsAttack(effect) ? Stat.MainHandCriticalStrikeChance : Stat.SpellCriticalStrikeChance] = criticalChance;
                stats[SkillsFor(conn).IsAttack(effect) ? Stat.MainHandCriticalStrikeMultiplier : Stat.SpellCriticalStrikeMultiplier] = criticalMultiplier;
                if (!SkillsFor(conn).IsAttack(effect))
                    foreach (var (stat, value) in SpellEffectiveCritical(conn, characterInputs, criticalChance, own))
                        if (stat is var key) stats[key] = value;
                // A spell flagged as dealing secondary damage (Vaal Detonate Dead) fills the secondary rows with its
                // hit and critical values; the panel then draws those instead of the spell rows.
                bool secondary = !SkillsFor(conn).IsAttack(effect) && Get(Stat.DisplaySkillDealsSecondaryDamage) != 0;
                if (secondary)
                {
                    foreach (var (stat, value) in PanelStats.HandDamage(supportedHit, "secondary"))
                        if (stat is var key) stats[key] = value;
                    if (Stat.SecondaryCriticalStrikeChance is var sc) stats[sc] = criticalChance;
                    if (Stat.SecondaryCriticalStrikeMultiplier is var sm) stats[sm] = criticalMultiplier;
                }
                // The damaging-ailment rows of this skill's hit, per hand for a (dual-wielded) attack, from the same inputs.
                string ailmentHand = SkillsFor(conn).IsAttack(effect) ? "main_hand" : secondary ? "secondary" : "spell";
                var ailmentRows = inputs is null ? [] : SupportedAilmentRowsFrom(inputs, ailmentHand);
                // A spell inherits the default attack's hand rows with the player's stats: clear them, as the hit rows are.
                if (!SkillsFor(conn).IsAttack(effect))
                    ailmentRows.InsertRange(0, AilmentPanelStats.Rows("main_hand").Concat(AilmentPanelStats.Rows("off_hand")).Select(n => (n, 0)));
                if (offInputs is not null) ailmentRows.AddRange(SupportedAilmentRowsFrom(offInputs, "off_hand"));
                foreach (var (stat, value) in ailmentRows)
                    if (stat is var key)
                        stats[key] = value;
            }
            // Movement and other skills whose base flag asks the panel for Damage per Use rather than per second.
            if (Stat.SkillShowAverageDamageInsteadOfDps is var showAverage)
                stats[showAverage] = Get(Stat.BaseSkillShowAverageDamageInsteadOfDps) != 0 ? 1 : 0;
            stats[OwnAuraRestriction.BaseAura] = own.GetValueOrDefault(OwnAuraRestriction.BaseAura);
            OwnAuraRestriction.PublishSkill(stats, OwnAuraRestrictionValue(conn));
            if (OwnAuraRestriction.IsAura(stats) && SublimeDisables(conn, effect, playerStats))
                stats[OwnAuraRestriction.SkillDisabled] = 1;
            // Published like the own-aura reason (never zero over another); the client also derives it (FUN_1428d6330).
            if (CastingSpell(conn, effect) && Stat.YourSpellsAreDisabled is var spellsOff
                && playerStats.GetValueOrDefault(spellsOff) != 0) stats[OwnAuraRestriction.SkillDisabled] = 1;
            // These display aliases use the same raw snapshot as the character derivation. Apply them after
            // runtime radius calculations so an already-derived selected AoE alias cannot count its base twice.
            int RawPanel(uint key) => panelSources.Raw.GetValueOrDefault(key);
            int OwnPanel(uint key) => own.GetValueOrDefault(key);
            bool projectileAttack = SkillsFor(conn).IsAttack(effect) && ResolveProjectilePanelParameters(effect) is not null;
            foreach (var (stat, value) in PanelStats.Selected(RawPanel, OwnPanel, panelSources.PlayerLevel,
                panelSources.MainHand.GetValueOrDefault, panelSources.OffHand.GetValueOrDefault,
                panelSources.MainArmed, panelSources.OffArmed, projectileAttack, projectileAttack && panelSources.OffArmed))
                if (stat is var key)
                    stats[key] = value;
            if (VaalSoulCost.Aura is var aura)
                stats[aura] = VaalSoulInput(conn, playerStats, own, VaalSoulCost.Aura);
            if (SkillsFor(conn).VaalBudget(effect, supported.Level) is not null)
                PublishVaalSoulInputs(conn, playerStats, own, stats);
            ApplyProjectilePanelStats(effect, stats);
            if (SkillsFor(conn).VaalBudget(effect, supported.Level) is not null)
                PublishVaalRageCost(conn, id, stats);
            // Native CanStart reads final limits, not the base inputs in the gem's stat set.
            // Network stat snapshots do not run the client's stat calculators.
            void Publish(uint stat, int value) { if (stat is var key) stats[key] = value; }
            // A cost of a share of maximum mana over time (Bladefall of Trarthus): its final, virtual per-minute amount.
            if (Get(Stat.BaseManaCostPctPerMinute) is int costPct and > 0)
                Publish(Stat.ManaCostPerMinute, BladefallVolleys.ManaPerMinute(costPct, conn.Pools.Mana));
            // Bladefall of Trarthus' volley interval: 0 hung the client on its first use (#584).
            if (Get(Stat.BladefallBaseVolleyFrequencyMs) is int volleyMs and > 0)
                Publish(Stat.BladefallVolleyFrequencyMs, BladefallVolleys.FrequencyMs(volleyMs,
                    Get(Stat.BladefallVolleyFrequencyPlusPctPer100MaximumMana), conn.Pools.Mana));
            if (SummonData.Find(SkillsFor(conn).EffectId(effect)) is { LimitStat: > 0 } summon)
                Publish(summon.FinalLimitStat, SummonLimit(conn, summon, supported));
            if (SkillsFor(conn).EffectId(effect) is "DominatingBlow" or "DominatingBlowAltX")
                foreach (var rarity in new[] { MonsterRarity.Normal, MonsterRarity.Magic, MonsterRarity.Rare })
                    if (HitSummonDefinition(SkillsFor(conn).EffectId(effect), rarity) is { } sentinel)
                        Publish(sentinel.FinalLimitStat, SummonLimit(conn, sentinel, supported));
            if (SkillsFor(conn).EffectId(effect) is "Absolution" or "AbsolutionAltX" or "VaalAbsolution"
                or "DominatingBlow" or "DominatingBlowAltX" or "VaalDomination")
            {
                Publish(Stat.SkillBehaviourVariation, SkillsFor(conn).BehaviourVariation(effect));
                bool absolution = SkillsFor(conn).EffectId(effect)!.Contains("Absolution", StringComparison.Ordinal);
                string family = absolution ? "absolution_sentinels" : "dominated_monsters";
                uint display = absolution ? Stat.DisplayMaxUpgradedSentinelsOfAbsolution : Stat.DisplayMaxUpgradedSentinelsOfDominance;
                int basis = GolemStat(own, Stat.Of("base_max_number_of_vaal_" + family));
                if (basis == 0) basis = GolemStat(own, display);
                Publish(Stat.Of("max_number_of_vaal_" + family), Math.Clamp(basis
                    + GolemStat(playerStats, Stat.Of("base_max_number_of_vaal_" + family))
                    + GolemStat(playerStats, Stat.Of("max_number_of_vaal_" + family)), 0, 200));
                foreach (uint count in new[] { Stat.NumberOfActiveAbsolutionSentinels, Stat.NumberOfActiveVaalAbsolutionSentinels,
                    Stat.NumberOfDominatedMonsters, Stat.NumberOfDominatedVaalMonsters }) Publish(count, GolemStat(playerStats, count));
            }
            if (GolemData.Find(SkillsFor(conn).EffectId(effect)) is { IsGolem: true })
                Publish(Stat.NumberOfGolemsAllowed, GolemCatalog.Limit(OwnPanel(Stat.BaseNumberOfGolemsAllowed),
                    n => GolemStat(playerStats, n)));
            var placed = PlacedKind(supported);
            if (placed.Trap || placed.Mine)
            {
                int limit = PlacedSkillLimit(conn, supported, placed.Mine);
                Publish(placed.Mine ? Stat.NumberOfRemoteMinesAllowed : Stat.NumberOfTrapsAllowed, limit);
                Publish(placed.Mine ? Stat.SkillDisplayNumberOfRemoteMinesAllowed : Stat.SkillDisplayNumberOfTrapsAllowed, limit);
                Publish(placed.Mine ? Stat.SkillIsMined : Stat.SkillIsTrapped, 1);
                Publish(placed.Mine ? Stat.NumberOfMinesToPlace : Stat.NumberOfTrapsToThrow,
                    PlacedSkillCount(conn, supported, placed.Mine));
                Publish(Stat.ActiveSkillBaseAreaOfEffectRadius, SkillArea.BaseRadius(SkillsFor(conn).EffectId(effect), Get));
                Publish(Stat.ActiveSkillAreaOfEffectRadius, (int)SkillRadius(conn, own, true, effect));
            }
            Publish(Stat.SkillIsInstant, GolemStat(own, Stat.BaseSkillIsInstant) != 0 ? 1 : 0);
            if (CurseCatalog.Hex(SkillsFor(conn).EffectId(effect) ?? ""))
            {
                Publish(Stat.ActiveSkillBaseAreaOfEffectRadius, 22);
                Publish(Stat.ActiveSkillAreaOfEffectRadius, CurseRadius(conn, id, OwnAuraRestriction.IsAura(own), supported, playerStats));
            }
            if (SkillsFor(conn).FindByRow(id, effect, supported.Level, [.. stats.Select(kv => (kv.Key, kv.Value))]) is { } skill)
            {
                // Per gem, every time a stat rebuild walks the bar: 1264 lines (up to 64 in one second) in a 3-minute
                // fight, ~45 KB/s of log, which the rebuild then waited on. Only under Game_TRACE=skills.
                if (ServerTrace.Skills)
                    Console.WriteLine($"[gateway] gem skill 0x{skill.Id:x4} {name} level {level} quality {quality} (effect {effect}): GEPL {skill.EffectsPerLevelRow}, " +
                        $"GESSPL {skill.StatSetsPerLevelRow}, " + string.Join(", ", own.Select(kv => $"{Stats.Name(kv.Key)} {kv.Value}")));
                yield return skill with { Supports = [.. supported.Supports.Select(s => SkillsFor(conn).FindByRow(0, s.Effect.Row, s.Gem.Level))
                    .OfType<GrantedSkill>().Select(s => new GrantedSupport(s.EffectsPerLevelRow, s.StatSetsPerLevelRow)
                    { ExtraStatSets = [.. s.ExtraStatSets.Select(e => e.Row)] })] };
            }
        }
    }

    // What a skill bar slot holds, as saved (CharacterSaves): a gem or the default attack by GrantedEffects Id (skill ids
    // change with the socketed gems), a basic action as "Action/<ActionTypes Id>" ("Action/Move": "Move only" on a mouse
    // button), "" for a cleared slot. Null for an id the server does not know.
    public static string? SkillBarEntry(Player conn, ushort skillId) =>
        skillId == 0 ? "" : SkillEffectId(conn, skillId) ?? (SkillsFor(conn).ActionName(skillId) is { } a ? "Action/" + a : null);

    // The saved skill bar as the player's current skill ids, sent with S→C 0x53 together with the saved per-slot option
    // masks (0x53 sets them, so 0 would clear "Always Attack Without Moving"). The default attack goes on slot 0 (left
    // mouse button) only when nothing at all was saved for that slot; nothing is sent without a saved bar, so the client
    // keeps its own default.
    public static Task SendSkillBar(Player conn) =>
        Send(conn, [], "0x0053 current skill bar", atGate: () => SkillBarRecord(conn));

    // A bind received while an earlier refresh waits for the send gate must win over that refresh.
    internal static byte[]? SkillBarRecord(Player conn)
    {
        lock (conn.Monsters)
        {
            FillSkillBar(conn);
            var save = CharacterSaves.Load(CharacterName(conn));
            var saved = save?.SkillBar;
            if (saved is not { Count: > 0 } && GameEvents.ExtraSkills(conn).Count == 0) return null;
            var ids = new ushort[InstanceProtocol.SkillBarSlots];
            var byEffect = GemSkillEffects(conn).Select(g => (g.Id, Effect: SkillsFor(conn).EffectId(g.Effect)))
                .Append((Id: SkillCatalog.FirstSkillId, Effect: SkillEffectId(conn, SkillCatalog.FirstSkillId)))
                .Where(e => e.Effect is not null).GroupBy(e => e.Effect!).ToDictionary(g => g.Key, g => g.First().Id);
            if (saved is { Count: > 0 })
                foreach (var (slot, entry) in saved)
                {
                    if (slot < 0 || slot >= ids.Length) continue;
                    if (entry.StartsWith("Action/") && SkillsFor(conn).ActionSkillId(entry["Action/".Length..]) is ushort action) ids[slot] = action;
                    else if (byEffect.TryGetValue(entry, out ushort id)) ids[slot] = id;
                }
            if (saved is not { Count: > 0 } || !saved.ContainsKey(0)) ids[0] = SkillCatalog.FirstSkillId;
            foreach (var build in GameEvents.SkillBarBuilding.For(conn)) build(conn, ids);
            ushort attackInPlace = save?.AttackInPlaceSlots ?? 0, mask52 = save?.SkillBarMask52 ?? 0;
            return InstanceProtocol.SkillBar(ids, attackInPlace, mask52);
        }
    }

    // Puts the skills that are nowhere on the saved bar into its empty slots (SkillBarLayout), leaving every other slot as
    // it is: the default attack on the left mouse button of a new bar, each gem skill of the equipment on the right mouse
    // button or the next empty slot. True when the bar changed (saved).
    public static bool FillSkillBar(Player conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || HardcoreProgressBlocked(conn)) return false;
            string character = CharacterName(conn);
            var save = CharacterSaves.Load(character);
            var bar = save?.SkillBar ?? [];
            var unassigned = SkillBarLayout.Unassigned(save);
            string attack = SkillEffectId(conn, SkillCatalog.FirstSkillId)!;
            var gems = GemSkillEffects(conn).Select(g => SkillsFor(conn).EffectId(g.Effect)).OfType<string>().ToList();
            var added = SkillBarLayout.Fill(bar, attack, gems.Where(g => !unassigned.Contains(g)), entry => entry == attack || entry.StartsWith("Action/") || gems.Contains(entry));
            foreach (var (slot, entry) in added) CharacterSaves.SetSkillBar(character, (byte)slot, entry);
            if (added.Count > 0)
                Console.WriteLine($"[gateway] {character} skill bar: {string.Join(", ", added.Select(a => $"{a.Entry} on slot {a.Slot}"))}");
            return added.Count > 0;
        }
    }

    // The GrantedEffects Id behind a skill id of the player (the default attack or a gem skill), or null.
    public static string? SkillEffectId(Player conn, ushort skillId) =>
        skillId == SkillCatalog.FirstSkillId ? Environment.GetEnvironmentVariable("Game_PLAYER_ATTACK") ?? "PlayerMelee"
        : GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId) is { Name: not null } g ? SkillsFor(conn).EffectId(g.Effect) : null;

    // Why the held item may not go into an equipment row: the character is below its level requirement
    // (ItemModStats.LevelRequirement). The client also checks it (0x1421f33c0) against the level we send in the item data,
    // but the server does not trust that.
    public static string? LevelTooLow(Player conn, byte row, Item? held)
    {
        if (held is null || !(ItemModStats.EquipmentRows.Contains(row) || row is 14 or 15)) return null;
        if (ItemsFor(conn).ByHash(held.TypeHash) is not { } t) return null;
        int needs = ItemModStats.LevelRequirement(held, t.DropLevel, ItemsFor(conn).Mods);
        return conn.Pools.Level < needs ? $"{t.Name} requires level {needs} (character is {conn.Pools.Level})" : null;
    }

    // Why a gem may not go into a socket: the character's level is below the gem's level requirement (GrantedEffectStatSetsPerLevel
    // PlayerLevelReq at the gem's level) or an attribute requirement is not met. The client checks both (0x142476ac0 for the
    // level, 0x142476710 per attribute) and refuses the move itself, but the server does not trust that.
    public static string? GemRequirementsUnmet(Player conn, Item? gem)
        => GemRequirementsUnmetAt(conn, gem, null);

    public static string? GemRequirementsUnmetAt(Player conn, Item? gem, GemRequirementSnapshot? snapshot)
    {
        if (gem is null || ItemsFor(conn).ByHash(gem.TypeHash) is not { } baseType || ItemsFor(conn).Gem(baseType) is not { } type) return null;
        if (!LeagueFor(conn).Rules.AllowsGemBase(baseType.Id))
            return $"{baseType.Name} is unavailable in {LeagueFor(conn).Name}";
        int level = ItemsFor(conn).GemLevel(gem);
        int effect = type.Variant(gem.GemVariant).Effect;
        if (effect < 0) return null;
        string name = ItemsFor(conn).GemName(gem);
        int characterLevel = snapshot?.Level ?? conn.Pools.Level;
        if (SkillsFor(conn).PlayerLevelReq(effect, level) is int needs and > 0 && characterLevel < needs)
            return $"{name} at level {level} requires level {needs} (character is {characterLevel})";
        // A gem's attributes come from its percentage shares and the level requirement, at the gem's level clamped to its cap
        // (a corrupted level 21 gem keeps level 20's requirement; 0x142476710). The client checks them against
        // virtual_*_for_requirements, which are the attributes unless something overrides them (/set_stat).
        var (str, dex, intel) = GemAttributeRequirements(conn, gem, type, effect);
        foreach (var (need, attr, has) in new[] { (str, Stat.Strength, snapshot?.Strength ?? RequirementAttribute(conn, Stat.Strength, conn.Pools.Strength)),
            (dex, Stat.Dexterity, snapshot?.Dexterity ?? RequirementAttribute(conn, Stat.Dexterity, conn.Pools.Dexterity)),
            (intel, Stat.Intelligence, snapshot?.Intelligence ?? RequirementAttribute(conn, Stat.Intelligence, conn.Pools.Intelligence)) })
            if (need > 0 && has < need)
                return $"{name} at level {level} requires {need} {attr} (character has {has})";
        return null;
    }

    // A gem's attribute requirements at its level: each SkillGems percentage through SkillCatalog.GemAttributeRequirement.
    public static (int Str, int Dex, int Int) GemAttributeRequirements(Player conn, Item gem, ItemCatalog.GemType type, int effect)
        => GemAttributeRequirements(conn, gem, type, effect, AttributeRequirementsPct(conn));

    public static (int Str, int Dex, int Int) GemAttributeRequirements(Player conn, Item gem, ItemCatalog.GemType type, int effect, int pct)
    {
        int level = Math.Clamp(ItemsFor(conn).GemLevel(gem), 1, Math.Max(1, type.MaxLevel));
        int needs = SkillsFor(conn).PlayerLevelReq(effect, level);
        return (ScaledRequirement(SkillCatalog.GemAttributeRequirement(type.StrengthPercent, needs, type.IsSupport), pct),
            ScaledRequirement(SkillCatalog.GemAttributeRequirement(type.DexterityPercent, needs, type.IsSupport), pct),
            ScaledRequirement(SkillCatalog.GemAttributeRequirement(type.IntelligencePercent, needs, type.IsSupport), pct));
    }

    // The requirements a gem socketed in a worn item adds to that item (the client shows the item red when they are not
    // met): the level its skill needs at the gem's level, and its attribute requirements. Pure catalog data, so the
    // equipment resolver can read it before the pools exist.
    public static (int Level, int Str, int Dex, int Int) SocketedGemRequirements(Player conn, Item gem, int attributePct)
    {
        if (ItemsFor(conn).ByHash(gem.TypeHash) is not { } baseType || ItemsFor(conn).Gem(baseType) is not { } type) return default;
        int effect = type.Variant(gem.GemVariant).Effect;
        if (effect < 0) return default;
        int level = SkillsFor(conn).PlayerLevelReq(effect, ItemsFor(conn).GemLevel(gem));
        var (str, dex, intel) = GemAttributeRequirements(conn, gem, type, effect, attributePct);
        return (level, str, dex, intel);
    }

    // The value the client's requirement checks compare (virtual_*_for_requirements, 13-items.md §2d): what /set_stat set,
    // else the character's attribute.
    public static int RequirementAttribute(Player conn, uint attribute, int value)
    {
        return value;
    }

    // Actor restrictions use the actual caster's current sources, never a skill overlay (which may replace a key).
    // CharacterPlayerStats does not build PlayerSkills or call GemDisabled, so this cannot recurse through admission.
    public static string? ActorSkillDisabled(Player conn, int effect)
    {
        // Legacy effect-only callers can use support metadata only when the instance is unambiguous.
        // Production admission always supplies the requested skill ID through ActorSkillInstanceDisabled.
        var copies = GemSkillEffects(conn).Where(g => g.Effect == effect).Take(2).ToArray();
        return ActorSkillDisabledCore(conn, effect, copies.Length == 1 ? copies[0].Id : null);
    }

    public static string? ActorSkillInstanceDisabled(Player conn, int effect, ushort skillId) =>
        ActorSkillDisabledCore(conn, effect, skillId);

    public static bool OwnAuraSkillRestricted(Player conn, int effect, ushort? skillId)
    {
        var sources = CharacterStatSources(conn).Stats;
        if (OwnAuraRestrictionValue(conn, new(sources)) == 0 && !SublimeDisables(conn, effect, sources)) return false;
        if (SkillsFor(conn).IsAura(effect)) return true;
        if (skillId is not ushort id) return false;
        var gem = SupportedSkill(conn, id);
        return gem.Effect == effect && OwnAuraRestriction.IsAura(gem.Stats);
    }

    public static string? ActorSkillDisabledCore(Player conn, int effect, ushort? skillId)
    {
        if (VaalMinionProxyDisabled(conn, effect, skillId) is { } proxy) return proxy;
        if (OwnAuraSkillRestricted(conn, effect, skillId)) return "own aura skills are disabled";
        if (skillId is ushort beastSkill && BeastKindFor(SkillsFor(conn).EffectId(effect)) is { } beastKind)
        {
            var gem = SupportedSkill(conn, beastSkill);
            if (gem.Effect != effect || !gem.RequirementsMet
                || gem.WornGrant is not { } grant || grant.EffectId != beastKind.EffectId)
                return "the beast companion source is unavailable";
        }
        // Devout of Gruthkul: every spell, auras and triggered spells too (wiki: they cannot be cast or triggered).
        if (CastingSpell(conn, effect) && SpellsDisabled(conn)) return "spells are disabled";
        return SkillsFor(conn).IsWarcry(effect) && Stat.YourWarcrySkillsAreDisabled is var key
            && CharacterPlayerStats(conn).GetValueOrDefault(key) != 0 ? "warcry skills are disabled" : null;
    }

    // Why a gem skill may not be used: an actor restriction, unmet gem requirements or provider equipment restrictions.
    // The client reds the gem but still sends the use, so this is the backstop for a client whose item is not inactive yet.
    // Null when the actor restriction and requirements allow the effect.
    public static string? GemDisabled(Player conn, int effect)
    {
        if (ActorSkillDisabled(conn, effect) is { } restriction) return restriction;
        if (WeaponRestricted(conn, effect) is { } weapon) return weapon;
        if (!LeagueFor(conn).Rules.AllowsGemEffect(SkillsFor(conn).EffectId(effect)))
            return $"This skill is unavailable in {LeagueFor(conn).Name}";
        if (WornGrantedSkillsOf(conn).Any(g => g.Effect == effect && IsInactive(conn, g.Host)))
            return "the granting item is inactive";
        foreach (var place in PlayerInventories.For(CharacterName(conn), ItemsFor(conn)).SocketedGemPlaces())
            if (ItemsFor(conn).GemEffects(place.Gem).Contains(effect) && GemRequirementsUnmet(conn, place.Gem) is { } missing)
                return missing;
        if (SkillsFor(conn).EffectId(effect) is { } effectId
            && ProjectileDefinition(effectId)?.Unavailable?.Invoke(conn) is { } unavailable) return unavailable;
        return null;
    }

    // Keep source-gem requirements and accepted supports local to the actual requested instance.
    // A second gem with the same GrantedEffects row may have a different level or link group.
    public static string? GemSkillInstanceDisabled(Player conn, int effect, ushort skillId, bool triggered = false)
    {
        var gem = SupportedSkill(conn, skillId);
        if (gem.Effect != effect || gem.SourceGem is null && !gem.Granted && gem.WornGrant is null)
            return "skill is unavailable";
        if (gem.WornGrant is not null && !gem.RequirementsMet) return "the granting item is inactive";
        if (GolemStat(gem.Stats, Stat.CannotCastCurses) != 0) return "this curse is triggered by Hextouch";
        if (TravelSkillsExceptDashDisabled(conn, effect, skillId) is { } travel) return travel;
        if (gem.Supports.Any(s => s.Effect.Added.Contains(36))
            && !TriggeredSpellExecutable(conn, skillId, gem)) return "this trigger payload has no server executor";
        if (!triggered && TriggerOnlyReason(gem) is { } trigger) return trigger;
        if ((ActorSkillInstanceDisabled(conn, effect, skillId) ?? WeaponRestricted(conn, effect)
                ?? GemRequirementsUnmet(conn, gem.SourceGem)) is { } disabled)
            return disabled;
        return SkillsFor(conn).EffectId(effect) is { } effectId ? ProjectileDefinition(effectId)?.Unavailable?.Invoke(conn) : null;
    }

    static string? TravelSkillsExceptDashDisabled(Player conn, int effect, ushort skillId)
    {
        if (!SkillsFor(conn).IsTravel(effect) || IsDashEffect(SkillsFor(conn).EffectId(effect))
            || !(Stat.YourTravelSkillsExceptDashAreDisabled is var travelKey)) return null;
        var gem = SupportedSkill(conn, skillId);
        if (gem.Effect != effect || gem.SourceGem is null && !gem.Granted && gem.WornGrant is null
            || gem.WornGrant is not null && !gem.RequirementsMet) return null;
        return CharacterPlayerStats(conn).GetValueOrDefault(travelKey) == 0
            ? null : "travel skills except Dash are disabled";
    }

    // ItemClasses row 36: the hand type the client gives an empty main hand (11-skills.md §10a, Unarmed 37 = row + 1).
    public const int UnarmedClassRow = 36;

    // A skill neither active hand can use (its ActiveSkills weapon restriction, the client's FUN_141eeec70): the client
    // greys it out, and a use that still arrives is refused here. An empty or inactive main hand is Unarmed; an inactive
    // weapon is not in the hands (HandType), as the model the client checks does not show it (Looks).
    public static string? WeaponRestricted(Player conn, int effect)
    {
        var allowed = SkillsFor(conn).WeaponRestrictions(effect);
        if (allowed.Count == 0) return null;
        int main = HandType(conn, PlayerInventories.Weapon1)?.ClassRow ?? UnarmedClassRow;
        return allowed.Contains(main) || HandType(conn, PlayerInventories.Offhand1)?.ClassRow is int off && allowed.Contains(off)
            ? null : "the equipped weapon cannot use this skill";
    }

    // A worn item whose requirements are not met (CharacterStatSources): its mods and gems do nothing.
    public static bool IsInactive(Player conn, uint itemId) => InactiveItems.GetValueOrDefault(CharacterName(conn))?.Contains(itemId) == true;

    // The skills of the active gems socketed in the equipped items in skill-list order: skill id, GrantedEffects row, gem
    // name, the gem's own level (from its experience; the experience stalls at the character's level requirement instead,
    // GainGemExperience) and quality. A transfigured or Trarthan gem grants its variant's skill, a Vaal gem its Vaal skill and
    // its base skill (ItemCatalog.GemEffects). Each skill keeps its id while its gem stays socketed (GemSkillIds): the client
    // binds skill bar keys by skill id. A gem whose requirements the character does not meet makes its item inactive
    // (EquipmentStatSources), so it grants nothing here; a use that still arrives is refused (GemDisabled).
    public static IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)> GemSkillEffects(Player conn)
    {
        string name = CharacterName(conn);
        var equipment = ItemsOf(conn).EquipmentSnapshot();
        return GemSkillEffectsAt(name, equipment, InactiveItems.GetValueOrDefault(name), LeagueFor(conn),
            GrantedSkillsOf(conn), WornGrantedSkillsOf(conn, equipment));
    }

    // Worn-item grants follow socketed gems; passive grants (GrantedSkillsOf) follow those. Gem 0 marks both virtual
    // source kinds, while PlayerSkillSourcesAt carries their explicit host identities for admission and supports.
    public static IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)> GemSkillEffectsAt(
        string? character, IReadOnlyList<(byte Row, InventoryItem Item)> equipment, IReadOnlySet<uint>? inactive,
        LeagueRuntime league, IReadOnlyList<PassiveGrantedSkills.Grant> granted,
        IReadOnlyList<WornGrantedSkills.Grant> worn)
    {
        // An inactive source keeps its assigned id while present, but grants no published skill.
        return PlayerSkillSourcesAt(character, equipment, inactive, league, granted, worn)
            .Where(s => s.Active)
            .Select(s => (s.Id, s.Effect, s.Name, s.Level, s.Quality, s.Gem));
    }

    public static double Dist((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // An attack gem used at the ground: a point beyond the weapon's reach is an attack-move (the client walks there);
    // within reach it is a swing at the ground that pays the skill's mana even with nothing standing there (§5 of
    // 11-skills.md; live 2026-10-26: every empty point used to be walked, so swinging cost nothing).
    public static bool GroundAttackWalks((int X, int Y) here, (int X, int Y) aim, double reach) => Dist(here, aim) > reach;

    public sealed record PassiveLevelChange(int Level, int Before, ushort ExtraPoints,
        IReadOnlyList<ushort> Nodes, IReadOnlyDictionary<ushort, ushort> Masteries, AscendancyProgress Ascendancy,
        ushort RefundPoints, CharacterSaves.PassiveProgressSnapshot? Expected, bool Trimmed);

    public static PassiveLevelChange CaptureLevelChange(Player conn, int level)
    {
        lock (conn.Monsters)
        {
            var next = new List<ushort>(conn.Passives);
            int points = Progression.PassivePoints(level) + conn.ExtraPassivePoints + QuestPassivePoints(conn) + AscendancyOrdinaryPoints(conn);
            TrimOrdinaryPassives(conn, next, points);
            var effects = conn.MasteryEffects.Where(kv => next.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            return new(level, conn.Pools.Level, conn.ExtraPassivePoints, next, effects, conn.MainAscendancy,
                conn.RefundPoints, conn.SavedPassiveProgress, next.Count != conn.Passives.Count);
        }
    }

    public static int CommitLevelChange(Player conn, PassiveLevelChange change)
    {
        lock (conn.Monsters)
        {
            using var availability = AscendancyPolicies.Enter(conn, conn.MainAscendancy.Selection);
            if (HardcoreProgressBlocked(conn)) return conn.Pools.Level;
            // An allocation/refund may have committed after capture, even when this level did not need a trim.
            // Rebuild against the current runtime snapshot; keep the durable comparison for other connections.
            if (!ReferenceEquals(change.Expected, conn.SavedPassiveProgress) || change.Before != conn.Pools.Level ||
                change.ExtraPoints != conn.ExtraPassivePoints || change.RefundPoints != conn.RefundPoints)
                change = CaptureLevelChange(conn, change.Level);
            if (change.Trimmed)
                CharacterSaves.SetMainAscendancy(CharacterName(conn), change.Ascendancy, change.Nodes, change.Masteries,
                    expected: change.Expected);
            return PublishCommittedLevel(conn, change);
        }
    }

    // XP is shared by overlapping area connections. Reconcile from the durable tree, not a stale connection's
    // candidate; the read, optional trim and comparison stay in one transaction before runtime publication.
    public static int CommitExperienceLevel(Player conn, int level)
    {
        lock (conn.Monsters)
        {
            using var availability = AscendancyPolicies.Enter(conn, conn.MainAscendancy.Selection);
            if (HardcoreProgressBlocked(conn)) return conn.Pools.Level;
            var change = Saves.Atomically(() =>
            {
                var save = CharacterSaves.Load(CharacterName(conn)) ?? new CharacterSaves.Save([]);
                AscendancyPolicies.RequireCurrentSelection(conn.MainAscendancy.Selection, save.MainAscendancy?.Selection);
                ValidateSavedProgress(conn, save);
                var ascendancy = save.MainAscendancy ?? new();
                var nodes = new List<ushort>(save.Passives ?? []);
                ushort extra = save.ExtraPassivePoints ?? 0, refunds = save.RefundPoints ?? 0;
                int points = Progression.PassivePoints(level) + extra + QuestStaticRewards.Default.PassivePoints(save.QuestFlags ?? []) + Game.AscendancyRules.OrdinaryPointsGranted(
                    (byte)SkillsFor(conn).ClassFor(conn.ClassRow).IntegerId, ascendancy);
                TrimOrdinaryPassives(conn, nodes, points, ascendancy);
                var masteries = (save.MasteryEffects ?? []).Where(kv => nodes.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var current = new PassiveLevelChange(level, conn.Pools.Level, extra, nodes, masteries, ascendancy,
                    refunds, CharacterSaves.PassiveProgressSnapshot.From(save), nodes.Count != (save.Passives?.Count ?? 0));
                if (current.Trimmed)
                    CharacterSaves.SetMainAscendancy(CharacterName(conn), ascendancy, nodes, masteries,
                        expected: current.Expected);
                return current;
            });
            return PublishCommittedLevel(conn, change);
        }
    }

    public static int PublishCommittedLevel(Player conn, PassiveLevelChange change)
    {
        conn.Passives = [.. change.Nodes];
        conn.MasteryEffects = new(change.Masteries);
        conn.MainAscendancy = change.Ascendancy;
        AscendancyPolicies.SelectionChanged(conn);
        conn.ExtraPassivePoints = change.ExtraPoints;
        conn.RefundPoints = change.RefundPoints;
        conn.SavedPassiveProgress = new(change.Ascendancy, change.Nodes, change.Masteries, change.RefundPoints);
        conn.Pools = PoolsFor(conn, change.Level);
        conn.ApplyMovementSpeed();
        return change.Before;
    }

    // A new level in place, after the Player component's experience (0x2bc; the client derives the level from it and runs
    // its level change): the level's stats (0x1b3: level, life, mana, accuracy, ...), the skills with them (0x2bc Actor),
    // full life, as a level up gives, and the passive skill points (0x33). Passives beyond a lower level's points are
    // taken back, last allocated first.
    public static async Task ApplyLevel(Player conn, int level)
    {
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return;
        int before = CommitLevelChange(conn, CaptureLevelChange(conn, level));
        await PublishLevelChange(conn, before, level);
    }

    public static async Task PublishLevelChange(Player conn, int before, int level)
    {
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return;
        Console.WriteLine($"[gateway] {conn.Remote} {CharacterName(conn)}: level {before} â†’ {level} (life {conn.Pools.Life}, mana {conn.Pools.Mana}, {Progression.PassivePoints(level)} passive point(s))");
        await SendCharacterStats(conn, PlayerStatChange.Build);
        int mana = UnreservedMana(conn);
        lock (conn.Monsters)
        {
            // A lethal hit while SendCharacterStats awaited must not turn a Hardcore death into a level-up heal.
            if (conn.Hardcore && (conn.PlayerLife == 0 || conn.HardcoreDeathRecorded)) return;
            (conn.PlayerLife, conn.PlayerMana) = (LifeCap(conn), (uint)mana);
            conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, true);
        }
        await SendWorldRecord(conn, PlayerId, PlayerLifeUpdate(conn), $"0x01b1 player life {conn.PlayerLife}");
        await SendPlayerMana(conn, $"0x01b1 player mana {conn.PlayerMana}");
        await SendPassives(conn);
    }

    // The player's stats (0x1b3) and the skills computed with them (0x2bc Actor), after the level or the passives changed.
    // The inputs the cached skill list (Player.PlayerSkillsCache) was built from.
    sealed record PlayerSkillsBuild(SkillSnapshotInputs Inputs, IReadOnlyList<GrantedSkill> Skills);
    static readonly Store<Player, PlayerSkillsBuild> PlayerSkillsInputs = new();

    public static async Task SendCharacterStats(Player conn, PlayerStatChange why)
    {
        conn.PlayerSkillsCache = null;   // a build change: the skill list is built again
        if (QuestKillCommitOutput is not null)
        {
            conn.Minions.StatRevision++;
            return;
        }
        // In an area's frames the next Stats phase builds and publishes them, once for all the changes of a frame.
        if (PublishByFrame(conn, why)) return;
        await PublishCharacterStats(conn, why);
    }

    // The character panel, the skills and the Energy Shield as they are now.
    internal static async Task PublishCharacterStats(Player conn, PlayerStatChange why)
    {
        conn.PlayerSkillsCache = null;
        await SendPlayerStatSnapshot(conn,
            () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), $"0x01b3 player stats ({why})");
        await SendSkills(conn);
        await SendPlayerEnergyShield(conn);
    }

    // The player's skills again, and the removal of the ones the client still has whose gems left the equipment, in one
    // Actor 0x20 update (PlayerSkillsChange): the 0x40 list neither removes skills nor tells the character panel.
    public static async Task SendSkills(Player conn)
    {
        if (!CurrentGameRules) return;
        lock (conn.Monsters) ReconcileBeastCompanionsLocked(conn);
        RefreshVaalSouls(conn);
        // Named, because it is a large block (auras, pools, cooldowns) that runs on every skill refresh, and a waiter that
        // found the combat lock busy here had no name to show for it.
        using (conn.Performance.Lock(conn.Monsters, "skills"))
        {
            if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.RageRetired) return;
            RefreshVaalSouls(conn);
            RefreshSingleUseCooldowns(conn);
            EnsureAntiquarianPools(conn);
            EnsureOwnAuraPools(conn);
            foreach (var id in conn.Auras.Keys.ToList())
            {
                var gem = SupportedSkill(conn, id);
                if (gem.Effect < 0 || SupportedReservationCost(conn, id) is not { } reservation) continue;
                var before = conn.Auras[id];
                var skillStats = gem.Stats.Select(kv => (kv.Key, kv.Value)).ToArray();
                conn.Auras[id] = before with
                {
                    ManaFlat = reservation.ManaFlat, ManaPercent = reservation.ManaPercent,
                    LifeFlat = reservation.LifeFlat, LifePercent = reservation.LifePercent,
                    Level = gem.Level, Quality = gem.Quality, WornGrant = gem.WornGrant,
                    SkillStats = skillStats
                };
                conn.AuraStatsDirty |= before.ManaFlat != reservation.ManaFlat || before.ManaPercent != reservation.ManaPercent
                    || before.LifeFlat != reservation.LifeFlat || before.LifePercent != reservation.LifePercent
                    || before.Level != gem.Level || before.Quality != gem.Quality || before.WornGrant != gem.WornGrant
                    || before.SkillStats is null || !before.SkillStats.SequenceEqual(skillStats);
            }
            if (conn.PetrifiedBlood.Active)
            {
                var id = conn.PetrifiedBlood.SkillId;
                var gem = SupportedSkill(conn, id);
                if (gem.Effect < 0 || SkillsFor(conn).EffectId(gem.Effect) != PetrifiedBlood.EffectId
                    || SupportedReservationCost(conn, id) is not { } reservation) conn.PetrifiedBlood.Suspend();
                else
                {
                    var stats = PetrifiedBloodEffectStats(conn, gem.Effect, gem.Level, gem.Quality);
                    var context = ReservationContext(conn, id);
                    conn.PetrifiedBlood.Refresh(gem.Effect, Get(stats, PetrifiedBlood.LoseOverTimeSource),
                        Get(stats, PetrifiedBlood.PreventSource), Get(stats, PetrifiedBlood.LifeCostSource),
                        reservation.ManaPercent, LowLifeThresholdPct(CharacterPlayerStats(conn)), context.Life, context.Efficiency);
                    if (!ReservationBudgetFits(conn)) conn.PetrifiedBlood.Suspend();
                    ClampReservedResources(conn);
                    conn.AuraStatsDirty = true;
                }
            }
            if (conn.Auras.Count > 0 || conn.PetrifiedBlood.Active)
            {
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
            }
        }
        List<ushort> gone = [], added = [];
        // Built at the send gate, as every full stat and Actor refresh is (SendPlayerStatSnapshot): the packet that is
        // written last is then the one built last, the own-aura records it depends on go with it, and a lockstep client
        // gets it as a world record of the player.
        await SendPlayerStatSnapshot(conn, () =>
        {
            var skills = PlayerSkills(conn);
            gone = conn.ClientSkillIds.Except(skills.Select(s => s.Id)).Order().ToList();
            added = skills.Select(s => s.Id).Where(id => !conn.ClientSkillIds.Contains(id)).ToList();
            conn.ClientSkillIds = [.. skills.Select(s => s.Id)];
            RecordPlayerSkills(conn, skills);
            return PlayerSkillsWithVaalSouls(conn, skills, gone);
        }, "0x02bc player Actor: current skill stats");
        if (!CurrentGameRules) return;
        await SendNativeCooldownRefresh(conn);
        if (gone.Count > 0) await EndSkillEffects(conn, gone);

        // The update can take a bound skill off the skill bar (live 2026-10-21: Haste's slot emptied when its aura went on,
        // and came back with the area change's 0x53), and a gem socketed again does not get its slot back by itself, so the
        // saved bar goes out again. Not when a skill the saved bar never had was added: the client binds a new skill to a free
        // slot itself (Câ†’S 0x4e), and the bar sent now would clear it there. A new gem skill gets an empty slot of the saved
        // bar first (FillSkillBar: the right mouse button, then Q W E R T …), so it goes out with the bar.
        FillSkillBar(conn);
        var saved = CharacterSaves.Load(CharacterName(conn))?.SkillBar?.Values;
        // Transient league skills are not part of the saved bar.
        if (GameEvents.ExtraSkills(conn).Count > 0
            || added.All(id => SkillEffectId(conn, id) is { } effect && saved?.Contains(effect) == true))
            await SendSkillBar(conn);
        // The same recalculation may have changed which worn weapons qualify.
        await SendModelIfHandsChanged(conn);
    }

    // Skills the player no longer has (their gems left the equipment): their auras and Righteous Fire end with them, and the
    // reserved mana comes back.
    public static async Task EndSkillEffects(Player conn, IReadOnlyCollection<ushort> gone)
    {
        if (!CurrentGameRules || !CurrentGameWork) return;
        (List<AuraCatalog.Aura> EndedAuras, bool PetrifiedEnded) ending;
        RighteousFireState? expectedRf = null;
        HashSet<ushort> ended = [];
        lock (conn.Monsters)
        {
            if (!CurrentGameRules || !CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired) return;
            // This notification can outlive the source mutation that produced it. Resolve the canonical rows again so a
            // current replacement for the same numeric id keeps its owners and saved activation. Actual direct source
            // retirement calls the shared cleanup eagerly and never uses this stale-notification authority check.
            using (new PlayerStatScope(conn, fresh: true))
                foreach (ushort id in gone.Distinct())
                {
                    if (CurrentPlayerSkillSourceLocked(conn, id) is not { Active: true } source)
                    {
                        ended.Add(id);
                        continue;
                    }
                    var supported = SupportedSkill(conn, id);
                    if (supported.Effect < 0 || supported.Effect != source.Effect || !supported.RequirementsMet)
                        ended.Add(id);
                }
            if (ended.Count > 0)
            {
                ending = EndNonRighteousFireSkillEffectsLocked(conn, ended);
                // Non-Righteous-Fire cleanup can change the stats that qualify RF, so evaluate its exact lease after that
                // dependency. The check still uses the original gone id, independently of the current-row filter.
                if (conn.RighteousFire is { } rf && gone.Contains(rf.Skill) && !RighteousFireCurrentLocked(conn, rf))
                    expectedRf = rf;
            }
            else
            {
                // RF-only stale cleanup must not call the shared helper: it also reconciles unrelated owners.
                if (conn.RighteousFire is { } rf && gone.Contains(rf.Skill) && !RighteousFireCurrentLocked(conn, rf))
                    expectedRf = rf;
                if (expectedRf is null) return;
                ending = (new List<AuraCatalog.Aura>(), false);
            }
        }
        SaveSummons(conn);
        SaveGolems(conn);
        await RefreshGolemBuffs(conn);
        if (ending.EndedAuras.Count > 0 || ending.PetrifiedEnded)
        {
            await SendOwnAuraChanges(conn);
            string why = ending.EndedAuras.Count > 0
                ? $"aura {string.Join(", ", ending.EndedAuras.Select(a => a.Name))} off (gem left)"
                : "Petrified Blood off (gem left)";
            Console.WriteLine($"[gateway] {conn.Remote} {why}");
            await SendCharacterStats(conn, PlayerStatChange.Auras);
            await SendPlayerMana(conn, $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
        }
        if (expectedRf is not null) await EndRighteousFire(conn, "its gem left", expectedRf);
    }

    // The non-Righteous-Fire owners that end with an actual source can share this synchronous mutation. The caller holds
    // Monsters; only immutable cleanup packets are queued here and every async presentation remains in a wrapper.
    private static (List<AuraCatalog.Aura> EndedAuras, bool PetrifiedEnded) EndNonRighteousFireSkillEffectsLocked(
        Player conn, IReadOnlyCollection<ushort> gone)
    {
        List<AuraCatalog.Aura> ended = [.. conn.Auras.Values.Where(a => gone.Contains(a.SkillId))];
        foreach (var aura in ended)
        {
            conn.Auras.Remove(aura.SkillId);
            EndReservationRuntimeLocked(conn, aura);
            CharacterSaves.SetAuraActive(CharacterName(conn), aura.EffectId, false);
        }
        if (ended.Any(a => StanceCatalog.IsStance(a.EffectId)))
            ReconcileStanceSelectionLocked(conn, persist: true);
        bool petrifiedEnded = conn.PetrifiedBlood.Active && gone.Contains(conn.PetrifiedBlood.SkillId);
        if (petrifiedEnded)
        {
            conn.PetrifiedBlood.Suspend();
            CharacterSaves.SetPetrifiedBlood(CharacterName(conn), false);
        }
        double now = AreaTime(conn);
        foreach (var fighter in conn.Summons.Fighters.Values.Where(g => gone.Contains(g.Source)).ToArray())
            RemoveSummonLocked(conn, fighter, now);
        foreach (var golem in conn.Golems.All.Values.Where(g => gone.Contains(g.Source)).ToArray())
            RemoveGolemLocked(conn, golem, now);
        conn.Golems.Waiting.RemoveAll(w => gone.Contains(w.Saved.Skill));
        foreach (var placed in conn.Placed.All.Values.Where(p => gone.Contains(p.Skill)).ToArray())
        {
            conn.Placed.All.Remove(placed.Id);
            if (!conn.Placed.Grounds.Any(g => g.Object == placed.Id))
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(placed.Id), "placed skill source removed", WorldId: placed.Id, RemovesObject: true));
            conn.Placed.Dirty = true;
            conn.Minions.StatRevision++;
        }
        foreach (var ground in conn.Placed.Grounds.Where(g => gone.Contains(g.Source)).ToArray())
        {
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ground.Object), "placed ground source removed", WorldId: ground.Object, RemovesObject: true));
            conn.Placed.Grounds.Remove(ground);
        }
        if (!conn.Summons.Fighters.Values.Any(g => g.Definition.Kind == "HolyRelic") && conn.Summons.HolyBuff != 0)
        {
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, conn.Summons.HolyBuff), "Holy Relic source removed", WorldId: PlayerId));
            conn.Summons.HolyBuff = 0;
            conn.Summons.HolyRegenerationUntil = 0;
            conn.Summons.HolyRegenerationPerMinute = 0;
            conn.Minions.StatRevision++;
        }
        if (ended.Count > 0 || petrifiedEnded)
        {
            conn.AuraStatsDirty = true;
            conn.Minions.StatRevision++;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        return (ended, petrifiedEnded);
    }


}
