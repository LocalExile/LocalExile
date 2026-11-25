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
    public static IReadOnlyList<GrantedSkill> PlayerSkills(Player player)
    {
        ReconcileBeastCompanionsLocked(player);
        // Headhunter, Rage, Fortification and charge changes call this many times a second. A short cache keeps the
        // client's skill records at most a frame or two stale while preventing a full build inside the send gate per
        // change (2026-11-05: 180 builds in one fight, 0x0025 handlers at 100-200 ms).
        // Reused until its inputs change (the character's stats, equipment, skill cosmetics, pools and life:
        // SkillSnapshotInputs), not for a fixed time; a full refresh (SendCharacterStats: weapons, gems, items, passives)
        // drops it at once. The stats carry Rage, charges, Onslaught and the auras, whose attack and cast times the
        // client's actions follow, so those rebuild it too.
        double now = AreaTime(player);
        var currentStats = PlayerStatValues(player);
        if (player.PlayerSkillsCache is { } cached && PlayerSkillsInputs.TryGetValue(player, out var was) && ReferenceEquals(was.Skills, cached)
            && was.Inputs.Matches(player, currentStats))
        {
            KeepActiveSkills(player);
            return cached;
        }
        var inputs = SkillSnapshotInputs.Capture(player, currentStats);
        // One build reads one snapshot of the character's stats, final stats and supported gems: every gem's cast time,
        // hit, crit and panel rows used to recompute them (CharacterPlayerStats, FinalPlayerStats, SupportedSkill) five or
        // six times per gem, 180-270 ms per build in the Debug server, inside the send gate. The snapshot also captures
        // the panel's raw sources for the selected-skill aliases (GemSkills).
        var character = CachedPlayerStats(player, out _, out var panelSources, capturePanelSources: true);
        var outer = StatsScope.Value;
        var build = new SkillBuildScope(player, character) { Building = true };
        StatsScope.Value = build;
        try
        {
            var playerStats = FinalPlayerStatsFrom(player, new(character)).ToList();
            var skills = new List<GrantedSkill>([.. PlayerDefaultSkills(player, playerStats, character),
                .. GemSkills(player, playerStats, character, panelSources!), .. GameEvents.ExtraSkills(player)]);
            skills.AddRange(SentinelSourceSkills(player, skills).ToArray());
            player.PlayerSkillsCache = skills;
            player.PlayerSkillsAt = now;
            KeepActiveSkills(player, build, playerStats);
            PlayerSkillsInputs.AddOrUpdate(player, new(inputs, skills));
            return skills;
        }
        finally { build.Active = false; StatsScope.Value = outer; }
    }

    /// <summary>The PlayerSkills build running on this flow: what it computed once and reuses instead of recomputing.</summary>
    public sealed class SkillBuildScope(Player player, Dictionary<uint, int>? stats = null)
    {
        IReadOnlyDictionary<uint, int>? character = stats;
        public Player Player => player;
        public volatile bool Active = true;
        /// <summary>The one character snapshot of this flow's work: a client message's whole validation, payment and
        /// launch, or one PlayerSkills build. Built on the first read, so a message that reads no stats pays nothing.
        /// (2026-11-05: a cast message built it five or six times, and 154 builds a second cost a full core.)
        /// In an area's frames that is the graph's last Stats phase (a change made in this flow reaches the stats, and the
        /// client, in the next one); off them a fresh build.</summary>
        public IReadOnlyDictionary<uint, int> Stats => character ??= PlayerStatValues(player);
        public List<(uint, int)>? Final;
        public readonly Dictionary<ushort, SupportedGem> Supported = [];
        /// <summary>The PlayerSkills build itself (not a message's scope): it works every skill out anew.</summary>
        public bool Building;
        /// <summary>The build's gem skills (GemSkillEffects), kept with the skills (ActiveSkills).</summary>
        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)[]? Gems;
    }

    public static readonly AsyncLocal<SkillBuildScope?> StatsScope = new();

    /// <summary>One character snapshot for this flow's work (<c>using var</c> scope). A client message's validation,
    /// payment and launch reuse the first build; a fresh one starts outside any scope. Nested scopes keep the outermost.</summary>
    public readonly struct PlayerStatScope : IDisposable
    {
        readonly SkillBuildScope? outer;
        readonly SkillBuildScope? owned;
        public PlayerStatScope(Player player, bool fresh = false)
        {
            outer = StatsScope.Value;
            owned = fresh || outer is null || !outer.Active || !ReferenceEquals(outer.Player, player) ? new(player) : null;
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

    public static SkillBuildScope? BuildScopeOf(Player player) =>
        StatsScope.Value is { Active: true } scope && ReferenceEquals(scope.Player, player) ? scope : null;

    // The player's stats as the server sets them: what /set_stat set, then the attack's final values computed from them
    // (the client derives nothing itself: 11-skills.md §4). They are the player's whole list in the Stats component
    // (AreaStats), which writes what changed at the frame's start; nothing is sent here.
    public static byte[] PublishPlayerStats(Player player)
    {
        RefreshSingleUseCooldowns(player);
        var stats = ClientStats(player, FinalPlayerStats(player).ToList());
        player.Area.Stats.SetAll(player.Id, stats);
        RefreshPeerScale(player, stats.Single(s => s.Item1 == ActorScale.Final).Item2);
        // The ground around a stationary player follows the stats the client was told (the stat presentations).
        int Sent(uint key) => stats.FirstOrDefault(s => s.Item1 == key).Item2;
        player.FlaskGrounds.Configure(FlaskGrounds.StationaryRadius(Sent), Sent(FlaskGrounds.LingerStat),
            Sent(FlaskGrounds.ProfaneLingerStat));
        // So do the player's debuffs on monsters (EnemyDebuffsTick): his passives' enemy auras and his taunt's increase.
        ushort[] nodes;
        nodes = [.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated];
        player.EnemyDebuffs = new(Sent(Stat.TauntedEnemiesDamageTakenPlusPct));
        return [];
    }

    public const uint LowestAttributeDamage = Stat.DamagePlusPctPer5OfYourLowestAttribute;
    public static readonly (uint Conditional, uint Plain, uint Higher, uint Lower)[] AttributeAilmentConditions =
    [
        (Stat.CannotBeIgnitedWithStrengthHigherThanDex, Stat.CannotBeIgnited, Stat.Strength, Stat.Dexterity),
        (Stat.CannotBeFrozenWithDexHigherThanInt, Stat.CannotBeFrozen, Stat.Dexterity, Stat.Intelligence),
        (Stat.CannotBeShockedWithIntHigherThanStrength, Stat.CannotBeShocked, Stat.Intelligence, Stat.Strength),
    ];

    public static IEnumerable<(uint, int)> FinalPlayerStats(Player player)
    {
        var buildScope = BuildScopeOf(player);
        if (buildScope is not { Building: true } && CurrentActiveSkills(player) is { } current)
            return current.Final ??= [.. FinalPlayerStatsCore(player)];
        if (buildScope is not { } scope) return FinalPlayerStatsCore(player);
        return [.. scope.Final ??= [.. FinalPlayerStatsCore(player)]];
    }

    public static IEnumerable<(uint, int)> FinalPlayerStatsCore(Player player) =>
        FinalPlayerStatsFrom(player, new Dictionary<uint, int>(CharacterPlayerStats(player)));

    // The final stats from a character snapshot already worked out (PlayerSkills reuses its own).
    public static IEnumerable<(uint, int)> FinalPlayerStatsFrom(Player player, Dictionary<uint, int> stats)
    {
        KeepExpandedInventoryVisible(player, stats);
        // Both hands' native weapon types and whether they dual wield (a value /set_stat set stays). The client derives none
        // of them from network stats: quivers need a bow's 9 in the main hand, and without is_dual_wielding it held 1,
        // which greyed out Shield Charge beside a one-handed weapon (13-items.md "Client weapon-type stats").
        var mainHand = ActiveMainHand(player);
        int mainType = WeaponTypeStats.Of(mainHand?.Type?.Class), offType = WeaponTypeStats.Of(ActiveOffHand(player)?.Type?.Class);
        if (Stat.MainHandWeaponType is var mainTypeKey) stats.TryAdd(mainTypeKey, mainType);
        if (Stat.OffHandWeaponType is var offTypeKey) stats.TryAdd(offTypeKey, offType);
        if (Stat.IsDualWielding is var dualKey)
            stats.TryAdd(dualKey, WeaponTypeStats.IsDualWielding(mainType, offType,
                Stat.UnarmedCountsAsDualWielding is var unarmedKey && stats.GetValueOrDefault(unarmedKey) != 0) ? 1 : 0);
        // Both hands' weapon stats. A dual-wielded attack swings the off hand as well, and the client sets that strike's
        // weapon up from the off hand's own final stats (it derives nothing itself, 11-skills.md §4).
        foreach (var (k, v) in WeaponStats(player, WeaponOf(player, mainHand), stats)) stats[k] = v;
        foreach (var (k, v) in CosmeticStats(player).Where(s => !ActorScale.IsInput(s.Stat))) stats.TryAdd(k, v);
        foreach (var build in GameEvents.FinalStatsBuilding.For(player)) build(player, stats);
        return stats.Select(kv => (kv.Key, kv.Value));
    }

    // The main hand's final weapon stats and, when the off hand holds a weapon, the off hand's too (off_hand_… instead of
    // main_hand_…). Until issue #167 the Gateway sent no off-hand stat at all, so the second weapon of a dual-wielded attack
    // had no attack time, damage or reach on the client; the off-hand animation of a Holy Strike on the ground then ran
    // with a null animation set and the next action crashed (0x141c9b351, AnimationController+0x180 = 0). Frida 2026-10-25:
    // while it creates the attack action the client reads off_hand_minimum/maximum_attack_distance.
    // Each hand's local speed is already in its own duration; both receive only the character's shared speed bucket.
    public static List<(uint, int)> WeaponStats(Player player, SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        var conversion = SupportedAttackDamage.WithWeaponGains(DamageStats.Conversion(stats, Stats, attack: true), weapon, Get);
        var accuracy = OrdinaryAccuracy(player, stats, weapon.LocalSources);
        var mainHit = AttackHitConverted(weapon, stats, MeleeAttack(player), conversion, accuracy);
        double damageFactor = CriticalStrikePolicy.DamageFactor(CriticalProhibitedBy(stats, attack: true, own: null)
            ? 0 : player.Pools.AttackCriticalStrikeChance, AttackCriticalMultiplier(player, stats), CriticalExtraRollsFor(player, stats),
            accuracy.Chance(Monsters.BaseEvasion(player.Pools.Level) ?? 0, false, false));
        var list = weapon.Stats(stats, mainHit, damageFactor).ToList();
        AddHand(mainHit, "main_hand");
        if (OffhandWeaponFor(player) is { } off)
        {
            var offConversion = SupportedAttackDamage.WithWeaponGains(DamageStats.Conversion(stats, Stats, attack: true), off, Get);
            var offHit = AttackHitConverted(off, stats, MeleeAttack(player), offConversion, new(WeaponAccuracyFor(player, off, stats)));
            list.AddRange(off.OffStats(stats, offHit));
            AddHand(offHit, "off_hand");
            // The hands alternate: the panel's per-use, per-second and attacks-per-second rows average both, each hand
            // weighted by its own critical chance and chance to hit (the Off Hand rows, OffHandPanelStats).
            int offSpeed = list.First(s => s.Item1 == Stat.OffHandAttackSpeedPlusPct).Item2;
            var (perUse, perSecond, attacksPerSecond) = DualWieldPanel.Average(mainHit, weapon.AttackSeconds(stats),
                offHit, DualWieldPanel.OffHandSeconds(off, offSpeed), damageFactor,
                OffHandDamageFactor(player, stats, n => n is var key ? stats.GetValueOrDefault(key) : 0, own: null));
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
    public static double? OffHandDamageFactor(Player player, IReadOnlyDictionary<uint, int> playerStats, Func<uint, int> rows,
        IReadOnlyDictionary<uint, int>? own)
    {
        if (rows(Stat.SkillUsesOffHand) == 0) return null;
        return CriticalStrikePolicy.DamageFactor(CriticalProhibitedBy(playerStats, attack: true, own) ? 0 : rows(Stat.OffHandCriticalStrikeChance),
            rows(Stat.OffHandCriticalStrikeMultiplier), CriticalExtraRollsFor(player, playerStats, own), rows(Stat.OffHandChanceToHitPct));
    }

    public static AttackAccuracyProfile OrdinaryAccuracy(Player player, IReadOnlyDictionary<uint, int> stats,
        IReadOnlyDictionary<uint, int>? local) =>
        OrdinaryAccuracy(SkillsFor(player), SkillEffectId(player, SkillCatalog.FirstSkillId), player.Pools, stats, local)
            with { Rating = PlayerAccuracy(player, stats) };

    // From the default attack's GrantedEffects Id (SkillEffectId of the first skill) and the pools as read once.
    public static AttackAccuracyProfile OrdinaryAccuracy(SkillCatalog skills, string? attackEffectId, CharacterStats.Pools pools,
        IReadOnlyDictionary<uint, int> stats, IReadOnlyDictionary<uint, int>? local)
    {
        int effect = attackEffectId is { } id ? skills.EffectRow(id) : -1;
        var own = skills.EffectStats(effect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        return AttackAccuracyProfile.From(pools, stats, Stats, own, local);
    }

    // The player's stats (PlayerStatSteps.cs): in an area's frames the stat graph's last step.
    public static IReadOnlyDictionary<uint, int> CharacterPlayerStats(Player player)
    {
        // Not to be changed: in an area's frames the stat graph's own values (a caller that changes them copies them).
        if (BuildScopeOf(player) is { } scope) return scope.Stats;
        return PlayerStatValues(player);
    }

    // spell_effective_total_chance_permyriad_for_hit_to_be_critical / _double_critical: the spell chance after the
    // luck policy (CriticalExtraRollsFor) and the Assassin's bifurcation (spell_critical_strikes_bifurcate); zero when
    // critical strikes are prohibited, as the chance row itself is.
    public static IEnumerable<(uint Stat, int Value)> SpellEffectiveCritical(Player player, IReadOnlyDictionary<uint, int> playerStats,
        int chance, IReadOnlyDictionary<uint, int>? own) =>
        SpellEffectiveCritical(() => CriticalOnLowLife(player, playerStats), playerStats, chance, own);

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
    public static bool MeleeAttack(Player player, int gemEffect = -1) =>
        gemEffect >= 0 && SkillsFor(player).IsAttack(gemEffect) ? SkillsFor(player).IsMelee(gemEffect)
            : WeaponProfileFor(player, activeSet: true).Melee;

    // Conversion for one hit: the player's stats plus the skill's own stats (base/skill conversion). Player stats win on
    // a key collision; the normal case is that the skill adds stats the player does not have.
    public static DamagePacket AttackHit(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> stats, bool melee,
        AttackAccuracyProfile accuracy) => AttackHitConverted(weapon, stats, melee, null, accuracy);

    /// <summary>Elemental Overload's skill-icon diamond: the client draws it from the <c>elemental_overload</c> buff
    /// bound to the skill (AddBuff bit3, <c>activeSkill</c>). The buff is Invisible — no buff-bar icon — but its presence
    /// drives the indicator below the skill's icon (poewiki). Sent on the skill's critical strike and refreshed for the
    /// eight-second window; the player tick removes it when the window ends. One instance, bound to the skill that last
    /// crit.</summary>
    public const ushort ElementalOverloadBuffRow = 606;   // BuffDefinitions.elemental_overload

    public static bool HasElementalOverload(IReadOnlyDictionary<uint, int> stats) =>
        stats.GetValueOrDefault(Stat.KeystoneElementalOverload) != 0;

    /// <summary>A landed hit of this skill: a critical strike with the keystone opens the skill's window and returns the
    /// indicator packet. Everything else returns null before any lookup, as this runs on every hit.</summary>
    public static byte[]? ElementalOverloadCritLocked(Player player, ushort skillId, string? effect,
        IReadOnlyDictionary<uint, int> playerStats, bool critical)
    {
        if (!critical || !HasElementalOverload(playerStats)) return null;
        player.RecentEvents.RecordSkillCrit(effect ?? SkillEffectId(player, skillId) ?? "", AreaTime(player), true);
        if (SkillsFor(player).ActionHash(SupportedSkill(player, skillId).Effect) is not ushort action) return null;
        ushort buff = player.ElementalOverloadBuff;
        if (buff == 0) buff = player.ElementalOverloadBuff = player.NextBuff++;
        player.ElementalOverloadBuffUntil = AreaTime(player) + RecentEvents.EightSecondWindow;
        return InstanceEntities.AddBuff(player.Id, buff, ElementalOverloadBuffRow, player.Id, skillId,
            u68: BitConverter.SingleToUInt32Bits((float)RecentEvents.EightSecondWindow), activeSkill: (skillId, action));
    }

    /// <summary>Elemental Overload's per-skill window on a damage snapshot: while the keystone is allocated and this
    /// skill dealt a critical strike in the past eight seconds, its hits and ailments deal 40% more Elemental Damage
    /// (the client's carrier stat, <c>keystone_elemental_overload_damage_+%_final</c>). The window is per skill, not per
    /// character (poewiki); PoB models it as a global flag because it cannot track per-skill crits. The activating
    /// critical strike does not benefit — its damage is built before the crit is recorded.</summary>
    public static IReadOnlyDictionary<uint, int> WithElementalOverload(Player player, ushort skillId,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (!HasElementalOverload(stats)) return stats;
        var result = new Dictionary<uint, int>(stats);
        return AddElementalOverload(player, SkillEffectId(player, skillId), result) ? result : stats;
    }

    /// <summary>Adds the window's 40% more to <paramref name="stats"/> when the keystone is in them and the skill's
    /// window is open; true when it did.</summary>
    public static bool AddElementalOverload(Player player, string? effect, Dictionary<uint, int> stats)
    {
        if (!HasElementalOverload(stats) || effect is not { Length: > 0 }
            || !player.RecentEvents.SkillCritRecently(effect, AreaTime(player))) return false;
        stats[Stat.KeystoneElementalOverloadDamagePlusPctFinal] = 40;
        return true;
    }

    // Resolve conversion history and modifiers from raw local/added damage, then apply Rage once.
    public static DamagePacket AttackHitConverted(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> stats,
        bool melee, GameServer.Core.World.DamageConversion? conversion, AttackAccuracyProfile accuracy, long additionalIncreased = 0)
    {
        var Get = WeaponAttributeDamage.Read(WeaponAttributeDamage.Armed(weapon), name => RageStat(stats, name));
        var hit = PlayerHitDamage.Resolve(SupportedAttackDamage.Raw(weapon, Get, accuracy, melee: melee), SupportedAttackDamage.HitModifiers(weapon, Get),
            conversion ?? new(), spell: false, melee: melee, projectile: !melee, wieldingBow: weapon.Bow, wieldingWand: weapon.WieldingWand,
            additionalIncreased: additionalIncreased + SupportedAttackDamage.WeaponIncrease(weapon, Get),
            additional: WeaponTypeDamage.Of(weapon, Get));
        if (melee) hit = WithVaalWarchiefBuff(hit, Get);
        return MaximumAttackDamage.Apply(RageStats.ApplyAttack(hit, Get), Get).Scale(GamblerFor(stats).DamageFactor);
    }

    public static double AddedEffectiveness(Player player, int effect, int level) => SkillsFor(player).DamageEffectiveness(effect, level) is int value and > 0 ? value / 10000.0 : 1;

    public static GameServer.Core.World.DamageConversion ConversionFor(Player player, int effectRow, int level, bool attack)
    {
        return DamageStats.Conversion(CharacterPlayerStats(player),
            effectRow >= 0 ? SkillsFor(player).EffectStats(effectRow, level) : [], Stats, attack);
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
            (Stat.AttackBlockLuck, pools.AttackBlockLucky == pools.AttackBlockUnlucky ? 0 : pools.AttackBlockLucky ? 1 : -1),
            (Stat.SpellBlockLuck, pools.SpellBlockLucky == pools.SpellBlockUnlucky ? 0 : pools.SpellBlockLucky ? 1 : -1),
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
        foreach (var (stat, value) in new[] { (Stat.Strength, str), (Stat.Dexterity, dex), (Stat.Intelligence, intel) })
            if (stat is var key) yield return (key, value);
    }

    // The skills of the gems socketed in the equipped items: each gem's GrantedEffects row at the gem's level with its own stats
    // (SkillCatalog.EffectStats: damage, cast time, mana cost) after the player's, with ids after the default attack's that
    // no basic action uses (SkillCatalog.SkillIds).
    public static IEnumerable<GrantedSkill> GemSkills(Player player, IEnumerable<(uint, int)> playerStats2,
        IReadOnlyDictionary<uint, int> characterInputs, PanelSourceSnapshot panelSources)
    {
        string? character = player.Character.Name;
        var playerStats = new Dictionary<uint, int>();
        foreach (var (key, value) in playerStats2) playerStats[key] = value;
        var gems = GemSkillEffects(player).ToArray();
        if (BuildScopeOf(player) is { Building: true } building) building.Gems = gems;
        RemoveMissingSealSources(player, gems.Select(g => g.Id));
        var skillCosmetics = CharacterSaves.SkillCosmetics(string.IsNullOrEmpty(character) ? "Exile" : character);
        foreach (var (id, effect, name, _, quality, gem) in gems)
        {
            var supported = SupportedSkill(player, id);
            int level = supported.Level;
            var own = WithManaPoolCost(player, supported, WithBlessingCost(player, supported), ManaCostPool(player, supported));
            UnleashSeals(player, id, supported, ProjectileWorldTime(player));
            var skills = SkillsFor(player);
            var stats = MergeSkillStats(playerStats, own, skills.IsChannelled(effect), SkillCostMore(player, own),
                CostEfficiencyFor(skills, effect));
            // A shield attack swings the shield in the off hand, which OffStats leaves out (not a weapon): the client then
            // read off_hand_total_base_weapon_attack_duration_ms 0, and Shield Charge's end-of-charge attack never finished
            // (Frida 2026-11-01, 11-skills.md "Shield Charge"). Its base time is the gem's own AttackTime.
            if (SkillsFor(player).IsAttack(effect) && SkillsFor(player).RequiresShield(effect)
                && ActiveOffHand(player)?.Type?.Class == "Shield" && SkillsFor(player).AttackTimeMs(effect, level) is int shieldMs and > 0)
            {
                stats[Stat.OffHandBaseWeaponAttackDurationMs] = shieldMs;
                stats[Stat.OffHandTotalBaseWeaponAttackDurationMs] = shieldMs;
            }
            if (Stat.VirtualExtraCriticalRolls is var criticalRolls)
                stats[criticalRolls] = CriticalExtraRollsFor(player, playerStats, own);
            if (CooldownPlan(player, supported, playerStats) is { } cooldown
                && Stat.VirtualCooldownSpeedPlusPct is var recoveryKey)
            {
                stats[recoveryKey] = cooldown.RecoveryPercent;
                // Otherwise the client propagates the use to the GEPL group before the second key can send its request.
                if (SkillsFor(player).CooldownGroup(effect, level) is 1 or 11 && Stat.SkillCooldownGroupOverride is var group)
                    stats[group] = cooldown.SharedGroup ?? 36;
                if (Stat.VirtualTotalCooldownCount is var countKey) stats[countKey] = cooldown.Uses;
            }
            int additionalAttackMs = SkillsFor(player).IsAttack(effect) ? SkillAttackTiming.AdditionalMilliseconds(stats) : 0;
            SkillAttackTiming.Apply(SkillsFor(player), effect, level, stats);
            // Spark's slot 0x310 selects its tornado art and repeat policy from this final stat. The Twister stat
            // set does not contain it; without the projection the native triggered action creates ordinary Spark.
            if (SkillsFor(player).EffectId(effect) == TwisterEffect && Stat.SkillBehaviourVariation is var tornadoMode)
                stats[tornadoMode] = 4;
            // The network skill record does not run the client's GrantedEffects.SkillID calculator. LeapSlam reads
            // this value for Groundbreaking's shorter range and its unskipped startup (0x1412fafd0 / 0x1419fda80).
            if (IsLeapSlam(player, effect))
                stats[Stat.SkillBehaviourVariation] = SkillsFor(player).BehaviourVariation(effect);
            // The client reads the final duration, not its base: its own Spark sparks live skill_effect_duration ms
            // (0x141b3860a) and without it died on their first tick, so none was drawn.
            if (Stat.BaseSkillEffectDuration is var baseDurationKey && own.GetValueOrDefault(baseDurationKey) > 0
                && Stat.SkillEffectDuration is var durationKey)
                stats[durationKey] = (int)Math.Round(1000 * SkillDurationModifiers.Seconds(own[baseDurationKey],
                    name => playerStats.GetValueOrDefault(name),
                    name => own.GetValueOrDefault(name), DurationKinds(player, effect)));
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
            if (SkillsFor(player).IsSpell(effect) && !SkillsFor(player).IsAttack(effect) && Stat.SpellCriticalStrikeChance is var critKey)
                stats[critKey] = SupportedCriticalChance(player, id);
            // The skill effects worn in the gem's Cosmetics tab slots: their stats (skill_art_variation …) on this skill only.
            foreach (var (key, worn) in skillCosmetics)
                if (CharacterSaves.SkillCosmeticGem(key) == gem && Mtx.Resolve(worn) is { } record && MtxEffects.Of(record.MtxTypeHash) is { } fx)
                    foreach (var (k, v) in fx.Stats) stats[k] = v;
            // The derived stats the client reads in place of them (a hit effect, a surge, an ailment's art).
            foreach (var (k, v) in SkillCosmeticDerivedStats(player, stats)) stats[k] = v;
            // A Crucible weapon's "Flicker Strike and Vigilant Strike's Cooldown can be bypassed by Power Charges": the client
            // reads only the skill's own override (CooldownBypass.FlickerVigilantPowerOverride), so it goes on these skills.
            if (CooldownBypass.FlickerVigilantPowerOverride is var crucibleKey && playerStats.GetValueOrDefault(crucibleKey) != 0
                && CooldownBypass.FlickerVigilantOverride(SkillsFor(player).EffectId(effect), SkillsFor(player).CooldownBypassType(effect, level))
                && CooldownBypass.PowerOverride is var overrideKey)
                stats[overrideKey] = 1;
            // The worn effect's skill_art_variation counts from the skill's own (SkillArtBase: Holy Sweep's is 2).
            if (Stat.SkillArtVariation is var artKey
                && SkillArtBase.Variation(SkillsFor(player).EffectId(effect), stats.GetValueOrDefault(artKey)) is int art and not 0)
                stats[artKey] = art;
            // The native spell action divides virtual BASE time by cast speed itself. Keep the catalog base
            // intact; only the panel's final casts-per-second stat is adjusted here.
            int supportCastSpeed = own.GetValueOrDefault(Stat.BaseCastSpeedPlusPct)
                + BrandCastSpeedBonusPct(player, effect, playerStats, own)
                + TaggedCastSpeedBonusPct(player, effect, playerStats.GetValueOrDefault, own.GetValueOrDefault);
            int castSpeedTotal = FinalCastSpeedPct(player, stats, CastSpeedPct(stats) + supportCastSpeed);
            var supportedTiming = GolemStat(own, Stat.BaseSpellCastTimeMsOverride) is int castBasis and > 0
                ? SpellCastTiming.Calculate(castBasis, castSpeedTotal) : SpellCastTiming.ForSkill(SkillsFor(player), effect, castSpeedTotal);
            if (supportedTiming is { } spellTiming)
            {
                spellTiming.ApplyTo(stats, Stats);
                // The native action divides by this skill's cast_speed_+% (0x1419e9f10): the total the server wait uses.
                if (Stat.CastSpeedPlusPct is var castKey && castSpeedTotal != CastSpeedPct(stats))
                    stats[castKey] = castSpeedTotal;
            }
            // Preserve the legacy non-spell/attack display path; attack timing is outside this fix.
            else if ((CastSpeedPct(stats) + supportCastSpeed) is int castSpeed and not 0 && SkillsFor(player).CastMs(effect) is int castMs and > 0)
            {
                int ms = (int)Math.Round(castMs / (1 + castSpeed / 100.0));
                if (Stat.VirtualBaseSpellCastTimeMs is var vk) stats[vk] = ms;
                if (Stat.HundredTimesCastsPerSecond is var hk) stats[hk] = (int)Math.Round(100_000.0 / Math.Max(1, ms));
            }
            // A spell's tooltip DPS is its own hit (SpellDamage) × casts per second, not the default attack's values that
            // came with the player's stats.
            int Get(uint k) => stats.GetValueOrDefault(k);
            // The client's spell action repeats the cast this many times (SpellRepeats); the server repeats its hits as many.
            if (SpellRepeats(player, effect, Get) is > 0 and int repeats && Stat.SpellRepeatCount is var repeatKey)
                stats[repeatKey] = repeats;
            // Network stats run no calculators. The native melee action reads this final count (0x141b71600).
            if (SkillsFor(player).IsAttack(effect) && Stat.AttackRepeatCount is var attackRepeatKey)
                stats[attackRepeatKey] = MeleeAttackRepeats(player, id);
            // Cyclone's client effect reads the final radius, not the increased-area input. Match AreaSkillHit.
            if (SkillArea.IsCyclone(SkillsFor(player).EffectId(effect)))
            {
                // The player's base area is in the panel total already (AreaPlayerStats): only the gem's own counts here.
                int AreaGet(uint name) => name == SkillArea.BaseIncreased ? own.GetValueOrDefault(name) : Get(name);
                int baseRadius = SkillArea.BaseRadius(SkillsFor(player).EffectId(effect), AreaGet);
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
            else if (GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(player).EffectId(effect)))
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
            // Other area skills (slams, sweeps, area spells): the client reads final radius (0x4876) as well as
            // total_skill_area_of_effect_radius_+%_including_final (0x1591) and skill_area_of_effect_+% (0xd7) for
            // visual scaling; publishing all three aligns client visuals with server SkillRadius.
            else if (Get(Stat.ActiveSkillBaseAreaOfEffectRadius) > 0 || SkillsFor(player).IsArea(effect))
            {
                bool spellArea = SkillsFor(player).IsSpell(effect) && !SkillsFor(player).IsAttack(effect);
                var areaStats = SkillAreaStats(player, own, effect, playerStats);
                int AreaGet(uint key) => areaStats.GetValueOrDefault(key);
                int baseRadius = SkillArea.BaseRadius(SkillsFor(player).EffectId(effect), AreaGet);
                if (Stat.ActiveSkillAreaOfEffectRadius is var finalRadius)
                    stats[finalRadius] = SkillArea.Radius(baseRadius, AreaGet, spellArea);
                if (Stat.TotalSkillAreaOfEffectRadiusPlusPctIncludingFinal is var scaleKey)
                    stats[scaleKey] = SkillArea.RadiusPercent(AreaGet, spellArea);
                if (Stat.SkillAreaOfEffectPlusPct is var areaKey)
                    stats[areaKey] = SkillArea.Increased(AreaGet, spellArea);
            }
            // The final secondary radius too (0x4879): Holy Sweep's hammer art scales to it over its base size
            // (0x1414f44b0, 0x1414f50e0), and at 0 the hammers were drawn at scale 0, never seen (live 2026-11-09).
            if (Get(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius) > 0
                && Stat.ActiveSkillSecondaryAreaOfEffectRadius is var secondaryRadius)
                stats[secondaryRadius] = SkillSecondaryRadius(player, own, SkillsFor(player).IsSpell(effect), effect);
            // Hammerfalls' controller times its waves by the skill's combined_main_hand_attack_speed_+% (0x141b1a270); with
            // none sent its waves kept 0.7 s whatever the attack speed (live 2026-11-11). The server's waves read the same.
            if (Get(Stat.HolySweepHammerfallRateMs) > 0 && Stat.CombinedMainHandAttackSpeedPlusPct is var combinedSpeed)
                stats[combinedSpeed] = SupportedMainHandAttackSpeedPct(player, id);
            // A trap or herald that deals secondary damage (Bear Trap carries the Spell type but no spell base damage,
            // Herald of Ice's shatter): its own secondary rows, before the attack/spell hit rows.
            if (!SkillsFor(player).IsAttack(effect) && Get(Stat.DisplaySkillDealsSecondaryDamage) != 0
                && SkillsFor(player).SecondaryHitDamage(effect, supported.Level, supported.Quality) is { IsZero: false } secondaryBase)
            {
                foreach (var (stat, value) in SecondaryPanelRows(player, Get, playerStats, supported, secondaryBase))
                    if (stat is var key)
                        stats[key] = value;
            }
            else if (SkillsFor(player).IsAttack(effect) || SkillsFor(player).IsSpell(effect))
            {
                // One read of the gem's hit inputs serves its hit, its ailment rows and (dual-wielded) the off hand's.
                // Reap's more damage per Blood Charge held scales the hit itself, as AreaSkillHit does, so the damage
                // ranges, the ailment rows and the per-use/per-second rows agree.
                double bloodMore = BloodChargeDamageMore(player, name => name is var k ? own.GetValueOrDefault(k) : 0);
                // A shield skill's rows are the shield's hit and swing (ShieldWeapon), as its hits are: the gem's own
                // AttackTime at the off hand's speed, without the main weapon's local attack speed.
                var shieldWeapon = ShieldWeapon(player, id);
                var inputs = SupportedHitInputsFor(player, id, weapon: shieldWeapon);
                SupportedHitInputs? offInputs = null;
                if (inputs is not null && bloodMore != 1) inputs = inputs with { Raw = inputs.Raw.Scale(bloodMore) };
                var supportedHit = inputs is null ? GameServer.Core.World.DamagePacket.Empty : SupportedHitFrom(inputs, suppress: true);
                // The skill's per-hand more multipliers, as its hits take them (SupportedSwingHit).
                var (mainHandMore, dualWieldMore) = SkillsFor(player).IsAttack(effect) && shieldWeapon is null
                    ? HandDamageMore(player, own) : (1, 1);
                if (mainHandMore * dualWieldMore != 1) supportedHit = supportedHit.Scale(mainHandMore * dualWieldMore);
                var shieldSpeed = SkillsFor(player).IsAttack(effect) && shieldWeapon is not null
                    ? SupportedOffHandAttackSpeed(player, id, shieldWeapon) : ((double Seconds, int IncreasedPct, int MorePct, int FinalPct)?)null;
                double seconds = SkillsFor(player).IsAttack(effect)
                    ? shieldSpeed?.Seconds ?? SupportedAttackSeconds(player, id)
                    : Math.Max(.001, (CastMs(player, effect, id) ?? 0) / 1000.0);
                var finalPlan = SkillHitPlan.ForSkill(SkillsFor(player), effect, supported.Level, supported.Quality);
                int criticalChance = SupportedCriticalChance(player, id);
                int criticalMultiplier = SupportedCriticalMultiplier(player, id);
                var accuracy = SkillsFor(player).IsAttack(effect) ? SupportedAccuracy(player, id) : default;
                double damageFactor = CriticalStrikePolicy.DamageFactor(
                    CriticalProhibitedBy(characterInputs, SkillsFor(player).IsAttack(effect), own) ? 0 : criticalChance,
                    criticalMultiplier, CriticalExtraRollsFor(player, characterInputs, own),
                    SkillsFor(player).IsAttack(effect) ? accuracy.Chance(Monsters.BaseEvasion(player.Pools.Level) ?? 0, false, false) : 100);
                var ranges = SkillsFor(player).IsAttack(effect)
                    ? AttackPanelStats.Compute(finalPlan, supportedHit, 1 / seconds, damageFactor)
                    : SpellPanelStats.Compute(finalPlan, supportedHit, 1 / seconds,
                        includeMainHand: true, damageFactor: damageFactor);
                foreach (var (key, value) in ranges)
                    if (key is var statKey) stats[statKey] = value;
                if (SkillsFor(player).IsAttack(effect))
                {
                    // Additional base time belongs in the duration row, not in an equivalent attack-speed penalty.
                    int attackBaseMs = (shieldWeapon ?? WeaponFor(player)).AttackMs;
                    if (additionalAttackMs != 0) attackBaseMs = SkillAttackTiming.BaseMilliseconds(attackBaseMs, additionalAttackMs);
                    stats[Stat.MainHandAttackSpeedPlusPct] = (int)Math.Round((attackBaseMs / (seconds * 1000) - 1) * 100);
                    stats[Stat.HundredTimesAttacksPerSecond] = (int)Math.Round(100 / seconds);
                    // Shield Charge (and shield attacks, +0x311 = 0) read off_hand_attack_speed_+% (0x127, 0x141b58740) for
                    // their end-attack time and combined_off_hand_attack_speed_+% / _final (0x211e / 0x531e, 0x141a70760)
                    // for charge travel speed. Network skill stats run no calculators, so publish all three explicitly.
                    if (shieldSpeed is { } shieldOffSpeed)
                    {
                        stats[Stat.OffHandAttackSpeedPlusPct] = shieldOffSpeed.FinalPct;
                        stats[Stat.CombinedOffHandAttackSpeedPlusPct] = shieldOffSpeed.IncreasedPct;
                        stats[Stat.CombinedOffHandAttackSpeedPlusPctFinal] = shieldOffSpeed.MorePct;
                    }
                    foreach (var (field, value) in new[]
                    {
                        (Stat.MainHandAccuracyRating, accuracy.Rating),
                        (Stat.MainHandChanceToHitPct, accuracy.Chance(Monsters.BaseEvasion(player.Pools.Level) ?? 0, false, false)),
                        (Stat.MainHandChanceToHitEvasiveMonstersPct, accuracy.Chance(Monsters.BaseEvasiveEvasion(player.Pools.Level) ?? 0, false, false)),
                    })
                        if (field is var key)
                            stats[key] = value;
                    // A dual-wielded attack gem whose weapon restriction the off hand fits swings it too: its own rows,
                    // with the same supports, and the per-use / per-second / attacks-per-second rows combine both hands
                    // (alternating, or both at once for skill_double_hits_when_dual_wielding: AttackHands).
                    var panelHands = shieldWeapon is null ? PanelHands(player, id) : AttackHand.Main;
                    if (panelHands != AttackHand.Main && OffHandPanelStats(player, playerStats, panelSources.MainHand, panelSources.OffHand,
                            stats.GetValueOrDefault(Stat.MainHandAttackSpeedPlusPct), own, effect) is { } offHand
                        && OffhandWeaponFor(player) is { } offWeapon)
                    {
                        offInputs = SupportedHitInputsFor(player, id, weapon: offWeapon);
                        var offHit = offInputs is null ? GameServer.Core.World.DamagePacket.Empty : SupportedHitFrom(offInputs, suppress: true);
                        if (dualWieldMore != 1) offHit = offHit.Scale(dualWieldMore);
                        var offRows = offHand.ToDictionary(r => r.Stat, r => r.Value);
                        double offSeconds = SupportedOffHandAttackSeconds(player, id, offWeapon);
                        if (additionalAttackMs != 0)
                        {
                            int offBaseMs = SkillAttackTiming.BaseMilliseconds(offWeapon.AttackMs, additionalAttackMs);
                            int offSpeed = (int)Math.Round((offBaseMs / (offSeconds * 1000) - 1) * 100);
                            offHand[offHand.FindIndex(row => row.Stat == Stat.OffHandAttackSpeedPlusPct)] = (Stat.OffHandAttackSpeedPlusPct, offSpeed);
                        }
                        var (perUse, perSecond, attacksPerSecond) = DualWieldPanel.Average(supportedHit, seconds, offHit,
                            offSeconds, damageFactor,
                            OffHandDamageFactor(player, characterInputs, n => offRows.GetValueOrDefault(n), own),
                            both: panelHands == AttackHand.Both,
                            bothSeconds: panelHands == AttackHand.Both ? SupportedAttackSeconds(player, id, AttackHand.Both) : null);
                        foreach (var (field, value) in offHand.Concat(OffHandDamageRows(offHit)))
                            if (field is var key)
                                stats[key] = value;
                        stats[Stat.DisplayHundredTimesDamagePerSkillUse] = perUse;
                        stats[Stat.HundredTimesDamagePerSecond] = perSecond;
                        stats[Stat.HundredTimesAttacksPerSecond] = attacksPerSecond;
                    }
                    // A skill only the main hand swings draws no Off Hand rows, whatever the character's default attack has.
                    else if (panelHands == AttackHand.Main) stats[Stat.SkillUsesOffHand] = 0;
                }
                stats[SkillsFor(player).IsAttack(effect) ? Stat.MainHandCriticalStrikeChance : Stat.SpellCriticalStrikeChance] = criticalChance;
                stats[SkillsFor(player).IsAttack(effect) ? Stat.MainHandCriticalStrikeMultiplier : Stat.SpellCriticalStrikeMultiplier] = criticalMultiplier;
                if (!SkillsFor(player).IsAttack(effect))
                    foreach (var (stat, value) in SpellEffectiveCritical(player, characterInputs, criticalChance, own))
                        if (stat is var key) stats[key] = value;
                // A spell flagged as dealing secondary damage (Vaal Detonate Dead) fills the secondary rows with its
                // hit and critical values; the panel then draws those instead of the spell rows.
                bool secondary = !SkillsFor(player).IsAttack(effect) && Get(Stat.DisplaySkillDealsSecondaryDamage) != 0;
                if (secondary)
                {
                    foreach (var (stat, value) in PanelStats.HandDamage(supportedHit, "secondary"))
                        if (stat is var key) stats[key] = value;
                    if (Stat.SecondaryCriticalStrikeChance is var sc) stats[sc] = criticalChance;
                    if (Stat.SecondaryCriticalStrikeMultiplier is var sm) stats[sm] = criticalMultiplier;
                }
                // The damaging-ailment rows of this skill's hit, per hand for a (dual-wielded) attack, from the same inputs.
                string ailmentHand = SkillsFor(player).IsAttack(effect) ? "main_hand" : secondary ? "secondary" : "spell";
                var ailmentRows = inputs is null ? [] : SupportedAilmentRowsFrom(inputs, ailmentHand);
                // A spell inherits the default attack's hand rows with the player's stats: clear them, as the hit rows are.
                if (!SkillsFor(player).IsAttack(effect))
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
            OwnAuraRestriction.PublishSkill(stats, OwnAuraRestrictionValue(player));
            if (OwnAuraRestriction.IsAura(stats) && SublimeDisables(player, effect, playerStats))
                stats[OwnAuraRestriction.SkillDisabled] = 1;
            // Published like the own-aura reason (never zero over another); the client also derives it (FUN_1428d6330).
            if (CastingSpell(player, effect) && Stat.YourSpellsAreDisabled is var spellsOff
                && playerStats.GetValueOrDefault(spellsOff) != 0) stats[OwnAuraRestriction.SkillDisabled] = 1;
            // Crafted trigger-wand spells carry spell_has_trigger_from_crafted_item_mod without
            // spell_uncastable_if_triggerable in GrantedEffectStatSets; publish both disable flags so the client greys
            // out manual slots and avoids playing bogus local cast animations for server-rejected triggers.
            if (GolemStat(own, Stat.SpellHasTriggerFromCraftedItemMod) != 0
                || supported.Supports.Any(s => s.Effect.Id == "SupportTriggerSpellOnSkillUse"))
            {
                stats[Stat.SpellUncastableIfTriggerable] = 1;
                stats[OwnAuraRestriction.SkillDisabled] = 1;
            }
            else if (TriggerOnlyReason(supported) is not null)
                stats[OwnAuraRestriction.SkillDisabled] = 1;
            // These display aliases use the same raw snapshot as the character derivation. Apply them after
            // runtime radius calculations so an already-derived selected AoE alias cannot count its base twice.
            int RawPanel(uint key) => panelSources.Raw.GetValueOrDefault(key);
            int OwnPanel(uint key) => own.GetValueOrDefault(key);
            bool projectileAttack = SkillsFor(player).IsAttack(effect) && ResolveProjectilePanelParameters(effect) is not null;
            foreach (var (stat, value) in PanelStats.Selected(RawPanel, OwnPanel, panelSources.PlayerLevel,
                panelSources.MainHand.GetValueOrDefault, panelSources.OffHand.GetValueOrDefault,
                panelSources.MainArmed, panelSources.OffArmed, projectileAttack, projectileAttack && panelSources.OffArmed))
                if (stat is var key)
                    stats[key] = value;
            if (VaalSoulCost.Aura is var aura)
                stats[aura] = VaalSoulInput(player, playerStats, own, VaalSoulCost.Aura);
            if (SkillsFor(player).VaalBudget(effect, supported.Level) is not null)
                PublishVaalSoulInputs(player, playerStats, own, stats);
            ApplyProjectilePanelStats(effect, stats);
            if (SkillsFor(player).VaalBudget(effect, supported.Level) is not null)
                PublishVaalRageCost(player, id, stats);
            else if (ChainbreakerRageCost(Get) is > 0 and var rageCost)
                stats[Stat.RageCost] = ActorScale.Saturate((long)stats.GetValueOrDefault(Stat.RageCost) + rageCost);
            // Native CanStart reads final limits, not the base inputs in the gem's stat set.
            // Network stat snapshots do not run the client's stat calculators.
            void Publish(uint stat, int value) { if (stat is var key) stats[key] = value; }
            // A cost of a share of maximum mana over time (Bladefall of Trarthus): its final, virtual per-minute amount.
            bool paysLife = Lifetap.ConvertsCostToLife(own, playerStats);
            if (Get(Stat.BaseManaCostPctPerMinute) is int costPct and > 0)
                Publish(Stat.ManaCostPerMinute, BladefallVolleys.ManaPerMinute(costPct, paysLife ? player.Pools.Life : player.Pools.Mana));
            // Network skill stats skip the client's cost-conversion calculators (0x141eedb80 reads mana_cost and
            // life_cost directly). Publish the final admitted Mana and Life costs on the wire so the client matches
            // server admission (Lifetap/Blood Magic, Deathmarked, Cannibalistic Rite), while leaving
            // SupportedSkill/SupportedStats unchanged for server cost accounting.
            if (paysLife)
            {
                Publish(Stat.SkillCostLifeInsteadOfMana, 1);
                int manaCost = Get(Stat.ManaCost);
                if (manaCost > 0)
                {
                    Publish(Stat.LifeCost, (int)Math.Min(int.MaxValue, (long)Math.Max(0, Get(Stat.LifeCost)) + manaCost));
                    Publish(Stat.ManaCost, 0);
                }
                int manaPerMinute = Get(Stat.ManaCostPerMinute);
                if (manaPerMinute > 0)
                {
                    Publish(Stat.LifeCostPerMinute, (int)Math.Min(int.MaxValue, (long)Math.Max(0, Get(Stat.LifeCostPerMinute)) + manaPerMinute));
                    Publish(Stat.ManaCostPerMinute, 0);
                }
            }
            else if (Get(Stat.MarkSkillsCostNoMana) != 0 && skills.HasType(effect, "Mark"))
            {
                Publish(Stat.ManaCost, 0);
            }
            else if (Math.Clamp(Get(Stat.BaseAttackSkillCostLifeInsteadOfManaPct), 0, 100) is int split and > 0
                && skills.IsAttack(effect) && own.TryGetValue(Stat.ManaCost, out int baseMana))
            {
                var (remainingMana, _, convertedLife) = ManaRecoveryRules.SplitCostToLife(
                    baseMana, Get, skills.IsChannelled(effect), SkillCostMore(player, own), split);
                Publish(Stat.ManaCost, remainingMana);
                if (convertedLife > 0)
                    Publish(Stat.LifeCost, (int)Math.Min(int.MaxValue, (long)Math.Max(0, Get(Stat.LifeCost)) + convertedLife));
            }
            if (Get(Stat.NoManaCost) != 0)
            {
                Publish(Stat.ManaCost, 0);
                Publish(Stat.ManaCostPerMinute, 0);
            }
            // Bladefall of Trarthus' volley interval: 0 hung the client on its first use (#584).
            if (Get(Stat.BladefallBaseVolleyFrequencyMs) is int volleyMs and > 0)
                Publish(Stat.BladefallVolleyFrequencyMs, BladefallVolleys.FrequencyMs(volleyMs,
                    Get(Stat.BladefallVolleyFrequencyPlusPctPer100MaximumMana), player.Pools.Mana));
            if (SummonData.Find(SkillsFor(player).EffectId(effect)) is { LimitStat: > 0 } summon)
            {
                int limit = SummonLimit(player, summon, supported);
                Publish(summon.FinalLimitStat, limit);
                Publish(summon.LimitStat, limit);
            }
            if (SkillsFor(player).EffectId(effect) is "DominatingBlow" or "DominatingBlowAltX")
                foreach (var rarity in new[] { MonsterRarity.Normal, MonsterRarity.Magic, MonsterRarity.Rare })
                    if (HitSummonDefinition(SkillsFor(player).EffectId(effect), rarity) is { } sentinel)
                        Publish(sentinel.FinalLimitStat, SummonLimit(player, sentinel, supported));
            if (SkillsFor(player).EffectId(effect) is "Absolution" or "AbsolutionAltX" or "VaalAbsolution"
                or "DominatingBlow" or "DominatingBlowAltX" or "VaalDomination")
            {
                Publish(Stat.SkillBehaviourVariation, SkillsFor(player).BehaviourVariation(effect));
                bool absolution = SkillsFor(player).EffectId(effect)!.Contains("Absolution", StringComparison.Ordinal);
                string family = absolution ? "absolution_sentinels" : "dominated_monsters";
                uint display = absolution ? Stat.DisplayMaxUpgradedSentinelsOfAbsolution : Stat.DisplayMaxUpgradedSentinelsOfDominance;
                int basis = GolemStat(own, Stat.Of("base_max_number_of_vaal_" + family));
                if (basis == 0) basis = GolemStat(own, display);
                Publish(Stat.Of("max_number_of_vaal_" + family), ApplyDarkMonarchLimit(Math.Clamp(basis
                    + GolemStat(playerStats, Stat.Of("base_max_number_of_vaal_" + family))
                    + GolemStat(playerStats, Stat.Of("max_number_of_vaal_" + family)), 0, 200),
                    n => GolemStat(playerStats, n), absolution ? "VaalAbsolution" : "VaalDomination"));
                foreach (uint count in new[] { Stat.NumberOfActiveAbsolutionSentinels, Stat.NumberOfActiveVaalAbsolutionSentinels,
                    Stat.NumberOfDominatedMonsters, Stat.NumberOfDominatedVaalMonsters }) Publish(count, GolemStat(playerStats, count));
            }
            if (GolemData.Find(SkillsFor(player).EffectId(effect)) is { IsGolem: true })
                Publish(Stat.NumberOfGolemsAllowed, GolemRosterLimit(own, playerStats));
            var placed = PlacedKind(supported);
            if (placed.Trap || placed.Mine)
            {
                int limit = PlacedSkillLimit(player, supported, placed.Mine);
                Publish(placed.Mine ? Stat.NumberOfRemoteMinesAllowed : Stat.NumberOfTrapsAllowed, limit);
                Publish(placed.Mine ? Stat.SkillDisplayNumberOfRemoteMinesAllowed : Stat.SkillDisplayNumberOfTrapsAllowed, limit);
                Publish(placed.Mine ? Stat.SkillIsMined : Stat.SkillIsTrapped, 1);
                Publish(placed.Mine ? Stat.NumberOfMinesToPlace : Stat.NumberOfTrapsToThrow,
                    PlacedSkillCount(player, supported, placed.Mine));
                int frenzy = ChargesOf(player).Effective(ChargeType.Frenzy);
                int throwSpeed = Get(placed.Mine ? Stat.MineLayingSpeedPlusPct : Stat.TrapThrowingSpeedPlusPct)
                    + Get(Stat.TrapAndMineThrowingSpeedPlusPct)
                    + frenzy * Get(placed.Mine ? Stat.MineThrowingSpeedPlusPctPerFrenzyCharge : Stat.TrapThrowingSpeedPlusPctPerFrenzyCharge);
                Publish(placed.Mine ? Stat.VirtualMineThrowingSpeedPlusPct : Stat.VirtualTrapThrowingSpeedPlusPct, throwSpeed);
                if (Get(Stat.TrapTriggerRadiusPlusPct) is int triggerPct and not 0)
                    Publish(Stat.VirtualTrapTriggerRadiusPlusPct, triggerPct);
                if (Get(Stat.ThrowTrapsInCircleRadius) is int circleRadius and > 0)
                    Publish(Stat.VirtualThrowTrapsInCircleRadius, circleRadius);
                bool spellArea = !SkillsFor(player).IsAttack(effect);
                var areaStats = SkillAreaStats(player, own, effect, playerStats);
                int AreaGet(uint key) => areaStats.GetValueOrDefault(key);
                int baseRadius = SkillArea.BaseRadius(SkillsFor(player).EffectId(effect), Get);
                Publish(Stat.ActiveSkillBaseAreaOfEffectRadius, baseRadius);
                Publish(Stat.ActiveSkillAreaOfEffectRadius, SkillArea.Radius(baseRadius, AreaGet, spellArea));
                Publish(Stat.TotalSkillAreaOfEffectRadiusPlusPctIncludingFinal, SkillArea.RadiusPercent(AreaGet, spellArea));
                Publish(Stat.SkillAreaOfEffectPlusPct, SkillArea.Increased(AreaGet, spellArea));
            }
            if (GolemStat(own, Stat.IsTotem) != 0)
            {
                // Native PlacementWrap::CanStart (0x141962e30) refuses any totem skill when number_of_totems_allowed
                // (0x3be) is 0, and network stat snapshots skip its calculator (0x1427b75c0).
                int totemLimit = TotemLimit(player, id);
                Publish(Stat.NumberOfTotemsAllowed, totemLimit);
                Publish(Stat.SkillDisplayNumberOfTotemsAllowed, totemLimit);
                Publish(Stat.SkillIsTotemified, 1);
                Publish(Stat.TotemRange, (int)Math.Round(TotemTargetRange(player, id)));
                if (Get(Stat.BaseTotemDuration) is int baseTotemMs and > 0)
                    Publish(Stat.TotemDuration, (int)Math.Round(baseTotemMs * Math.Max(0, 100 + Get(Stat.TotemDurationPlusPct)) / 100.0));
            }
            if (SkillsFor(player).IsBrand(effect))
            {
                Publish(Stat.BrandsPerTargetLimit, BrandMaxPerTarget(playerStats, own));
                if (GolemStat(own, Stat.BaseSigilRepeatFrequencyMs) > 0)
                    Publish(Stat.SigilRepeatFrequencyMs, (int)Math.Round(1000 * BrandActivationSeconds(player, supported, playerStats)));
                if (GolemStat(own, Stat.BaseSecondarySkillEffectDuration) > 0)
                    Publish(Stat.SecondarySkillEffectDuration, (int)Math.Round(1000 * BrandDetachedDurationSeconds(playerStats, own)));
            }
            Publish(Stat.SkillIsInstant, GolemStat(own, Stat.BaseSkillIsInstant) != 0 ? 1 : 0);
            if (CurseCatalog.Hex(SkillsFor(player).EffectId(effect) ?? ""))
            {
                Publish(Stat.ActiveSkillBaseAreaOfEffectRadius, 22);
                Publish(Stat.ActiveSkillAreaOfEffectRadius, CurseRadius(player, id, OwnAuraRestriction.IsAura(own), supported, playerStats));
            }
            if (SkillsFor(player).FindByRow(id, effect, supported.Level, [.. stats.Select(kv => (kv.Key, kv.Value))]) is { } skill)
            {
                // Per gem, every time a stat rebuild walks the bar: 1264 lines (up to 64 in one second) in a 3-minute
                // fight, ~45 KB/s of log, which the rebuild then waited on. Only under Game_TRACE=skills.
                if (ServerTrace.Skills)
                    Console.WriteLine($"[gateway] gem skill 0x{skill.Id:x4} {name} level {level} quality {quality} (effect {effect}): GEPL {skill.EffectsPerLevelRow}, " +
                        $"GESSPL {skill.StatSetsPerLevelRow}, " + string.Join(", ", own.Select(kv => $"{Stats.Name(kv.Key)} {kv.Value}")));
                yield return skill with { Supports = [.. supported.Supports.Select(s => SkillsFor(player).FindByRow(0, s.Effect.Row, s.Gem.Level))
                    .OfType<GrantedSkill>().Select(s => new GrantedSupport(s.EffectsPerLevelRow, s.StatSetsPerLevelRow)
                    { ExtraStatSets = [.. s.ExtraStatSets.Select(e => e.Row)] })] };
            }
        }
    }

    // What a skill bar slot holds, as saved (CharacterSaves): a gem or the default attack by GrantedEffects Id (skill ids
    // change with the socketed gems), a basic action as "Action/<ActionTypes Id>" ("Action/Move": "Move only" on a mouse
    // button), "" for a cleared slot. Null for an id the server does not know.
    public static string? SkillBarEntry(Player player, ushort skillId) =>
        skillId == 0 ? "" : SkillEffectId(player, skillId) ?? (SkillsFor(player).ActionName(skillId) is { } a ? "Action/" + a : null);

    // The saved skill bar as the player's current skill ids, sent with S→C 0x53 together with the per-slot option
    // masks (0x53 sets them, so 0 would clear "Always Attack Without Moving": a slot the player never touched is sent
    // its skill's own default, AttackInPlaceDefaults). The default attack goes on slot 0 (left mouse button) only when
    // nothing at all was saved for that slot; nothing is sent without a saved bar, so the client keeps its own default.
    public static Task SendSkillBar(Player player) =>
        Send(player, [], "0x0053 current skill bar", atGate: () => SkillBarRecord(player));

    // A 0x2bc Actor skill update is queued into the current lockstep frame (WorldOutput) and processed by the client
    // when it steps that frame, whereas 0x0053 is an instance packet handled immediately on receipt. Wait two lockstep
    // frames on wall-clock time (coalesced via SkillBarRestoreGeneration, working both while playing and while paused)
    // so 0x0053 always arrives after the client has applied 0x2bc and cannot be overwritten by it.
    public static Task RestoreSkillBarAfterActorUpdate(Player player)
    {
        if (player.Connection.LockstepClient)
        {
            int gen = Interlocked.Increment(ref player.SkillBarRestoreGeneration);
            _ = Task.Delay(2 * FrameMs + 16).ContinueWith(_ =>
            {
                if (!player.Closed && !player.LeavingArea && player.SkillBarRestoreGeneration == gen)
                    _ = player.Frames.Run(() => SendSkillBarHoldingGuard(player));
            });
            return Task.CompletedTask;
        }
        return SendSkillBarHoldingGuard(player);
    }

    // The bar re-creates every slot's cooldown entry, so a held guard's frozen entry goes out again behind it, or its
    // cooldown counts while the buff runs (issue #1127). Built at the send gate, as SendSkills' re-hold is.
    static async Task SendSkillBarHoldingGuard(Player player)
    {
        await SendSkillBar(player);
        if (TriggeredGuards.TryGetValue(player, out _))
            await Send(player, [], "0x02bc guard cooldown re-held after the skill bar",
                atGate: () => HeldGuardCooldownRefresh(player));
    }

    // A bind received while an earlier refresh waits for the send gate must win over that refresh.
    // The skill ids the saved bar names, slot by slot: a slot's save names a GrantedEffects Id or a basic action, since
    // the ids themselves change with the socketed gems. Taken out of SkillBarRecord so the slot options can be read
    // against the same bar (AttackInPlaceDefaults).
    public static ushort[] SkillBarIds(Player player, IReadOnlyDictionary<int, string>? saved)
    {
        var ids = new ushort[InstanceProtocol.SkillBarSlots];
        var byEffect = GemSkillEffects(player).Select(g => (g.Id, Effect: SkillsFor(player).EffectId(g.Effect)))
            .Append((Id: SkillCatalog.FirstSkillId, Effect: SkillEffectId(player, SkillCatalog.FirstSkillId)))
            .Where(e => e.Effect is not null).GroupBy(e => e.Effect!).ToDictionary(g => g.Key, g => g.First().Id);
        if (saved is { Count: > 0 })
            foreach (var (slot, entry) in saved)
            {
                if (slot < 0 || slot >= ids.Length) continue;
                if (entry.StartsWith("Action/") && SkillsFor(player).ActionSkillId(entry["Action/".Length..]) is ushort action) ids[slot] = action;
                else if (byEffect.TryGetValue(entry, out ushort id)) ids[slot] = id;
            }
        if (saved is not { Count: > 0 } || !saved.ContainsKey(0)) ids[0] = SkillCatalog.FirstSkillId;
        foreach (var build in GameEvents.SkillBarBuilding.For(player)) build(player, ids);
        return ids;
    }

    // The "Always Attack Without Moving" bits a slot carries by its skill's own data: ActiveSkillType
    // AttackInPlaceIsDefault (row 82; artillery_ballista's ActiveSkills row carries it, the default attack's does
    // not). The client ticks such a box itself, and 0x53 overwrites both masks (instance +0xcfd0 / +0xcfd2), so a bar
    // sent with 0 unticks it -- and then the client's ground press carries no flag 0x0001, its action asks MoveTo for
    // the aim with the weapon's range instead of for its own cell, and it walks up to a far aim while every following
    // action start puts it back. The saved mask, once the player has touched a box, always wins.
    public static ushort AttackInPlaceDefaults(Player player, IReadOnlyList<ushort> ids)
    {
        ushort mask = 0;
        for (int slot = 0; slot < ids.Count && slot < 16; slot++)
            if (ids[slot] != 0 && GemSkillEffects(player).FirstOrDefault(g => g.Id == ids[slot]) is { Name: not null } gem
                && SkillsFor(player).HasType(gem.Effect, "AttackInPlaceIsDefault")) mask |= (ushort)(1 << slot);
        return mask;
    }

    internal static byte[]? SkillBarRecord(Player player)
    {
        FillSkillBar(player);
        var save = CharacterSaves.Load(CharacterName(player));
        var saved = save?.SkillBar;
        if (saved is not { Count: > 0 } && GameEvents.ExtraSkills(player).Count == 0) return null;
        var ids = SkillBarIds(player, saved);
        ushort attackInPlace = save?.AttackInPlaceSlots ?? AttackInPlaceDefaults(player, ids), mask52 = save?.SkillBarMask52 ?? 0;
        return InstanceProtocol.SkillBar(ids, attackInPlace, mask52);
    }

    // Puts the skills that are nowhere on the saved bar into its empty slots (SkillBarLayout), leaving every other slot as
    // it is: the default attack on the left mouse button of a new bar, each gem skill of the equipment on the right mouse
    // button or the next empty slot. True when the bar changed (saved).
    public static bool FillSkillBar(Player player)
    {
        if (player.Closed || player.LeavingArea || player.RageRetired || HardcoreProgressBlocked(player)) return false;
        string character = CharacterName(player);
        var save = CharacterSaves.Load(character);
        var bar = save?.SkillBar ?? [];
        var unassigned = SkillBarLayout.Unassigned(save);
        string attack = SkillEffectId(player, SkillCatalog.FirstSkillId)!;
        var gems = GemSkillEffects(player).Select(g => SkillsFor(player).EffectId(g.Effect)).OfType<string>().ToList();
        var added = SkillBarLayout.Fill(bar, attack, gems.Where(g => !unassigned.Contains(g)), entry => entry == attack || entry.StartsWith("Action/") || gems.Contains(entry));
        foreach (var (slot, entry) in added) CharacterSaves.SetSkillBar(character, (byte)slot, entry);
        if (added.Count > 0)
            Console.WriteLine($"[gateway] {character} skill bar: {string.Join(", ", added.Select(a => $"{a.Entry} on slot {a.Slot}"))}");
        return added.Count > 0;
    }

    // The GrantedEffects Id behind a skill id of the player (the default attack or a gem skill), or null.
    public static string? SkillEffectId(Player player, ushort skillId) =>
        skillId == SkillCatalog.FirstSkillId ? Environment.GetEnvironmentVariable("Game_PLAYER_ATTACK") ?? "PlayerMelee"
        : GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId) is { Name: not null } g ? SkillsFor(player).EffectId(g.Effect) : null;

    // Why the held item may not go into an equipment row: the character is below its level requirement
    // (ItemModStats.LevelRequirement). The client also checks it (0x1421f33c0) against the level we send in the item data,
    // but the server does not trust that.
    public static string? LevelTooLow(Player player, byte row, Item? held)
    {
        if (held is null || !(ItemModStats.EquipmentRows.Contains(row) || row is 14 or 15)) return null;
        if (ItemsFor(player).ByHash(held.TypeHash) is not { } t) return null;
        int needs = ItemModStats.LevelRequirement(held, t.DropLevel, ItemsFor(player).Mods);
        return player.Pools.Level < needs ? $"{t.Name} requires level {needs} (character is {player.Pools.Level})" : null;
    }

    // Why a gem may not go into a socket: the character's level is below the gem's level requirement (GrantedEffectStatSetsPerLevel
    // PlayerLevelReq at the gem's level) or an attribute requirement is not met. The client checks both (0x142476ac0 for the
    // level, 0x142476710 per attribute) and refuses the move itself, but the server does not trust that.
    public static string? GemRequirementsUnmet(Player player, Item? gem)
    {
        // From the gem, the level and the attributes: kept with the skills (ActiveSkills).
        if (gem is not null && BuildScopeOf(player) is not { Building: true } && CurrentActiveSkills(player) is { } current)
        {
            if (!current.Requirements.TryGetValue(gem, out var known)) current.Requirements[gem] = known = GemRequirementsUnmetAt(player, gem, null);
            return known;
        }
        return GemRequirementsUnmetAt(player, gem, null);
    }

    public static string? GemRequirementsUnmetAt(Player player, Item? gem, GemRequirementSnapshot? snapshot, uint? targetItemId = null)
    {
        if (gem is null || ItemsFor(player).ByHash(gem.TypeHash) is not { } baseType || ItemsFor(player).Gem(baseType) is not { } type) return null;
        if (!LeagueFor(player).Rules.AllowsGemBase(baseType.Id))
            return $"{baseType.Name} is unavailable in {LeagueFor(player).Name}";
        // Gems socketed in (or being socketed into) a worn item whose requirements are waived by a league system
        // (GameEvents.RequirementExemptions, e.g. Survivors items and test fixtures using TestRequirements.Waive)
        // inherit that exemption so pool rebuilds do not silently drop socketed supports on low-attribute characters.
        if (GameEvents.RequirementExemptions.Any && GameEvents.ExemptItems(player) is { Count: > 0 } exemptions)
        {
            if (targetItemId is uint hostId && exemptions.Contains(hostId)) return null;
            var places = snapshot?.Places ?? GemRequirementSnapshot.PlacesIn(ItemsOf(player).EquipmentSnapshot());
            if (places.Any(p => ReferenceEquals(p.Gem, gem) && exemptions.Contains(p.ItemId)))
                return null;
        }
        int level = ItemsFor(player).GemLevel(gem);
        int effect = type.Variant(gem.GemVariant).Effect;
        if (effect < 0) return null;
        string name = ItemsFor(player).GemName(gem);
        int characterLevel = snapshot?.Level ?? player.Pools.Level;
        if (SkillsFor(player).PlayerLevelReq(effect, level) is int needs and > 0 && characterLevel < needs)
            return $"{name} at level {level} requires level {needs} (character is {characterLevel})";
        if (snapshot?.IgnoreAttributeRequirements ?? player.Pools.IgnoreAttributeRequirements) return null;
        // A gem's attributes come from its percentage shares and the level requirement, at the gem's level clamped to its cap
        // (a corrupted level 21 gem keeps level 20's requirement; 0x142476710). The client checks them against
        // virtual_*_for_requirements, which are the attributes unless something overrides them (/set_stat).
        var (str, dex, intel) = GemAttributeRequirements(player, gem, type, effect);
        foreach (var (need, attr, has) in new[] { (str, Stat.Strength, snapshot?.Strength ?? RequirementAttribute(player, Stat.Strength, player.Pools.Strength)),
            (dex, Stat.Dexterity, snapshot?.Dexterity ?? RequirementAttribute(player, Stat.Dexterity, player.Pools.Dexterity)),
            (intel, Stat.Intelligence, snapshot?.Intelligence ?? RequirementAttribute(player, Stat.Intelligence, player.Pools.Intelligence)) })
            if (need > 0 && has < need)
                return GemRequirementHost(player, gem, snapshot, targetItemId) is { Item: { } host }
                    && SocketedGemAttributeRequirementsIgnored(ItemsFor(player), host)
                    ? null : $"{name} at level {level} requires {need} {attr} (character has {has})";
        return null;
    }

    // A gem's attribute requirements at its level: each SkillGems percentage through SkillCatalog.GemAttributeRequirement.
    public static (int Str, int Dex, int Int) GemAttributeRequirements(Player player, Item gem, ItemCatalog.GemType type, int effect)
        => GemAttributeRequirements(player, gem, type, effect, AttributeRequirementsPct(player));

    public static (int Str, int Dex, int Int) GemAttributeRequirements(Player player, Item gem, ItemCatalog.GemType type, int effect, int pct)
    {
        int level = Math.Clamp(ItemsFor(player).GemLevel(gem), 1, Math.Max(1, type.MaxLevel));
        int needs = SkillsFor(player).PlayerLevelReq(effect, level);
        return (ScaledRequirement(SkillCatalog.GemAttributeRequirement(type.StrengthPercent, needs, type.IsSupport), pct),
            ScaledRequirement(SkillCatalog.GemAttributeRequirement(type.DexterityPercent, needs, type.IsSupport), pct),
            ScaledRequirement(SkillCatalog.GemAttributeRequirement(type.IntelligencePercent, needs, type.IsSupport), pct));
    }

    // The requirements a gem socketed in a worn item adds to that item (the client shows the item red when they are not
    // met): the level its skill needs at the gem's level, and its attribute requirements. Pure catalog data, so the
    // equipment resolver can read it before the pools exist.
    public static (int Level, int Str, int Dex, int Int) SocketedGemRequirements(Player player, Item gem, int attributePct) =>
        SocketedGemRequirements(player, gem, attributePct, ignoreAttributes: false);

    public static (int Level, int Str, int Dex, int Int) SocketedGemRequirements(Player player, Item gem,
        int attributePct, bool ignoreAttributes)
    {
        if (ItemsFor(player).ByHash(gem.TypeHash) is not { } baseType || ItemsFor(player).Gem(baseType) is not { } type) return default;
        int effect = type.Variant(gem.GemVariant).Effect;
        if (effect < 0) return default;
        int level = SkillsFor(player).PlayerLevelReq(effect, ItemsFor(player).GemLevel(gem));
        if (ignoreAttributes) return (level, 0, 0, 0);
        var (str, dex, intel) = GemAttributeRequirements(player, gem, type, effect, attributePct);
        return (level, str, dex, intel);
    }

    // The value the client's requirement checks compare (virtual_*_for_requirements, 13-items.md §2d): what /set_stat set,
    // else the character's attribute.
    public static int RequirementAttribute(Player player, uint attribute, int value)
    {
        return value;
    }

    // Actor restrictions use the actual caster's current sources, never a skill overlay (which may replace a key).
    // CharacterPlayerStats does not build PlayerSkills or call GemDisabled, so this cannot recurse through admission.
    public static string? ActorSkillDisabled(Player player, int effect)
    {
        // Legacy effect-only callers can use support metadata only when the instance is unambiguous.
        // Production admission always supplies the requested skill ID through ActorSkillInstanceDisabled.
        var copies = GemSkillEffects(player).Where(g => g.Effect == effect).Take(2).ToArray();
        return ActorSkillDisabledCore(player, effect, copies.Length == 1 ? copies[0].Id : null);
    }

    public static string? ActorSkillInstanceDisabled(Player player, int effect, ushort skillId) =>
        ActorSkillDisabledCore(player, effect, skillId);

    public static bool OwnAuraSkillRestricted(Player player, int effect, ushort? skillId)
    {
        if (effect == 0 && (skillId is not ushort sid || !SkillsFor(player).IsGemSkill(sid))) return false;
        var sources = StatSourceValues(player);
        if (OwnAuraRestrictionValue(player, new(sources)) == 0 && !SublimeDisables(player, effect, sources)) return false;
        if (SkillsFor(player).IsAura(effect)) return true;
        if (skillId is not ushort id) return false;
        var gem = SupportedSkill(player, id);
        return gem.Effect == effect && OwnAuraRestriction.IsAura(gem.Stats);
    }

    public static string? ActorSkillDisabledCore(Player player, int effect, ushort? skillId)
    {
        if (SkillsFor(player).EffectId(effect) is "AnimateGuardian" or "AnimateGuardianAltX"
            && AnimatedGuardianUnavailable(player)) return "the Animated Guardian died in this area";
        if (EternalBlessingDisables(player, skillId)) return "Eternal Blessing disables mana reservations";
        if (skillId is ushort blessingSkill && GuardianBlessingDisables(player, effect, blessingSkill))
            return "Guardian's Blessing requires a living minion linked to its support";
        // What it reads (the skill's gem, the stat sources, the stats) changes only with the skills (ActiveSkills).
        if (BuildScopeOf(player) is not { Building: true } && CurrentActiveSkills(player) is { } current)
        {
            if (!current.ActorDisabled.TryGetValue((effect, skillId), out var known))
                current.ActorDisabled[(effect, skillId)] = known = ActorSkillDisabledNow(player, effect, skillId);
            return known;
        }
        return ActorSkillDisabledNow(player, effect, skillId);
    }

    static string? ActorSkillDisabledNow(Player player, int effect, ushort? skillId)
    {
        if (VaalMinionProxyDisabled(player, effect, skillId) is { } proxy) return proxy;
        if (SkillMinionFamily(SkillsFor(player).EffectId(effect)) is { } family && !CanHaveMinion(PlayerStatValues(player), family))
            return "equipment prevents summoning this type of minion";
        if (DarkMonarchCastFamily(SkillsFor(player).EffectId(effect)) is { } darkFamily
            && !DarkMonarchAllows(darkFamily, StatSourceValues(player)))
            return "The Dark Monarch prevents this minion type";
        if (OwnAuraSkillRestricted(player, effect, skillId)) return "own aura skills are disabled";
        if (skillId is ushort beastSkill && BeastKindFor(SkillsFor(player).EffectId(effect)) is { } beastKind)
        {
            var gem = SupportedSkill(player, beastSkill);
            if (gem.Effect != effect || !gem.RequirementsMet
                || gem.WornGrant is not { } grant || grant.EffectId != beastKind.EffectId)
                return "the beast companion source is unavailable";
        }
        // Devout of Gruthkul: every spell, auras and triggered spells too (wiki: they cannot be cast or triggered).
        if (CastingSpell(player, effect) && SpellsDisabled(player)) return "spells are disabled";
        return SkillsFor(player).IsWarcry(effect) && Stat.YourWarcrySkillsAreDisabled is var key
            && CharacterPlayerStats(player).GetValueOrDefault(key) != 0 ? "warcry skills are disabled" : null;
    }

    // Why a gem skill may not be used: an actor restriction, unmet gem requirements or provider equipment restrictions.
    // The client reds the gem but still sends the use, so this is the backstop for a client whose item is not inactive yet.
    // Null when the actor restriction and requirements allow the effect.
    public static string? GemDisabled(Player player, int effect)
    {
        if (ActorSkillDisabled(player, effect) is { } restriction) return restriction;
        if (WeaponRestricted(player, effect) is { } weapon) return weapon;
        if (!LeagueFor(player).Rules.AllowsGemEffect(SkillsFor(player).EffectId(effect)))
            return $"This skill is unavailable in {LeagueFor(player).Name}";
        if (WornGrantedSkillsOf(player).Any(g => g.Effect == effect && IsInactive(player, g.Host)))
            return "the granting item is inactive";
        foreach (var place in PlayerInventories.For(CharacterName(player), ItemsFor(player)).SocketedGemPlaces())
            if (ItemsFor(player).GemEffects(place.Gem).Contains(effect) && GemRequirementsUnmet(player, place.Gem) is { } missing)
                return missing;
        if (SkillsFor(player).EffectId(effect) is { } effectId
            && ProjectileDefinition(effectId)?.Unavailable?.Invoke(player) is { } unavailable) return unavailable;
        return null;
    }

    // Keep source-gem requirements and accepted supports local to the actual requested instance.
    // A second gem with the same GrantedEffects row may have a different level or link group.
    public static string? GemSkillInstanceDisabled(Player player, int effect, ushort skillId, bool triggered = false)
    {
        var gem = SupportedSkill(player, skillId);
        if (gem.Effect != effect || gem.SourceGem is null && !gem.Granted && gem.WornGrant is null)
            return "skill is unavailable";
        if (gem.WornGrant is not null && !gem.RequirementsMet) return "the granting item is inactive";
        if (!triggered && gem.WornGrant is { } grant && TriggeredItemMinion(grant) is not null)
            return "this minion is triggered by its item";
        if (GolemStat(gem.Stats, Stat.CannotCastCurses) != 0) return "this curse is triggered by Hextouch";
        if (!triggered && CurseCatalog.Hex(SkillsFor(player).EffectId(effect)) && IsBaneLinkedHex(player, gem))
            return "this curse is triggered by Bane";
        if (TravelSkillsExceptDashDisabled(player, effect, skillId) is { } travel) return travel;
        if (gem.Supports.Any(s => s.Effect.Added.Contains(36))
            && !TriggeredSpellExecutable(player, skillId, gem)
            && !CooldownTriggerExecutable(player, gem)
            && !SnipeTriggeredAttackExecutable(player, skillId, gem)
            && !ManaforgedAttackExecutable(player, skillId, gem)) return "this trigger payload has no server executor";
        if (!triggered && TriggerOnlyReason(gem) is { } trigger) return trigger;
        if ((ActorSkillInstanceDisabled(player, effect, skillId) ?? WeaponRestricted(player, effect)
                ?? GemRequirementsUnmet(player, gem.SourceGem)) is { } disabled)
            return disabled;
        return SkillsFor(player).EffectId(effect) is { } effectId ? ProjectileDefinition(effectId)?.Unavailable?.Invoke(player) : null;
    }

    static string? TravelSkillsExceptDashDisabled(Player player, int effect, ushort skillId)
    {
        if (!SkillsFor(player).IsTravel(effect) || IsDashEffect(SkillsFor(player).EffectId(effect))
            || !(Stat.YourTravelSkillsExceptDashAreDisabled is var travelKey)) return null;
        var gem = SupportedSkill(player, skillId);
        if (gem.Effect != effect || gem.SourceGem is null && !gem.Granted && gem.WornGrant is null
            || gem.WornGrant is not null && !gem.RequirementsMet) return null;
        return CharacterPlayerStats(player).GetValueOrDefault(travelKey) == 0
            ? null : "travel skills except Dash are disabled";
    }

    // ItemClasses row 36: the hand type the client gives an empty main hand (11-skills.md §10a, Unarmed 37 = row + 1).
    public const int UnarmedClassRow = 36;

    // A skill neither active hand can use (its ActiveSkills weapon restriction, the client's FUN_141eeec70): the client
    // greys it out, and a use that still arrives is refused here. An empty or inactive main hand is Unarmed; an inactive
    // weapon is not in the hands (HandType), as the model the client checks does not show it (Looks).
    public static string? WeaponRestricted(Player player, int effect)
    {
        var skills = SkillsFor(player);
        var allowed = skills.WeaponRestrictions(effect);
        if (allowed.Count == 0) return null;
        bool Fits((Item Item, ItemCatalog.BaseType? Type)? hand, int? emptyClass) =>
            WeaponIdentity.Allows(allowed, hand?.Type?.ClassRow ?? emptyClass,
                hand is { } held && WeaponIdentity.AllOneHandMelee(held.Item, ItemsFor(player).Mods));
        if (Fits(ActiveMainHand(player), UnarmedClassRow))
        {
            // An empty main hand fits Doryani's Touch only if the off hand also holds no weapon.
            if (!allowed.Contains(UnarmedClassRow) || skills.EffectId(effect) != "DoryanisTouch"
                || ActiveOffHand(player)?.Type is not { } offType || skills.WeaponBase(offType.Row) is null) return null;
        }
        else if (Fits(ActiveOffHand(player), null)) return null;
        return "the equipped weapon cannot use this skill";
    }

    // A worn item whose requirements are not met (CharacterStatSources): its mods and gems do nothing.
    public static bool IsInactive(Player player, uint itemId) => InactiveItems.GetValueOrDefault(CharacterName(player))?.Contains(itemId) == true;

    // The skills of the active gems socketed in the equipped items in skill-list order: skill id, GrantedEffects row, gem
    // name, the gem's own level (from its experience; the experience stalls at the character's level requirement instead,
    // GainGemExperience) and quality. A transfigured or Trarthan gem grants its variant's skill, a Vaal gem its Vaal skill and
    // its base skill (ItemCatalog.GemEffects). Each skill keeps its id while its gem stays socketed (GemSkillIds): the client
    // binds skill bar keys by skill id. A gem whose requirements the character does not meet makes its item inactive
    // (EquipmentStatSources), so it grants nothing here; a use that still arrives is refused (GemDisabled).
    public static IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)> GemSkillEffects(Player player)
    {
        if (BuildScopeOf(player) is not { Building: true } && CurrentActiveSkills(player) is { } current) return current.Gems;
        string name = CharacterName(player);
        var equipment = ItemsOf(player).EquipmentSnapshot();
        return GemSkillEffectsAt(name, equipment, InactiveItems.GetValueOrDefault(name), LeagueFor(player),
            GrantedSkillsOf(player), WornGrantedSkillsOf(player, equipment));
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

    public static PassiveLevelChange CaptureLevelChange(Player player, int level)
    {
        var next = new List<ushort>(player.Character.Passives);
        int points = Progression.PassivePoints(level) + player.Character.ExtraPassivePoints + QuestPassivePoints(player) + AscendancyOrdinaryPoints(player);
        TrimOrdinaryPassives(player, next, points);
        var effects = player.Character.MasteryEffects.Where(kv => next.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return new(level, player.Pools.Level, player.Character.ExtraPassivePoints, next, effects, player.Character.MainAscendancy,
            player.Character.RefundPoints, player.Character.SavedPassiveProgress, next.Count != player.Character.Passives.Count);
    }

    public static int CommitLevelChange(Player player, PassiveLevelChange change)
    {
        {
            using var availability = AscendancyPolicies.Enter(player, player.Character.MainAscendancy.Selection);
            if (HardcoreProgressBlocked(player)) return player.Pools.Level;
            // An allocation/refund may have committed after capture, even when this level did not need a trim.
            // Rebuild against the current runtime snapshot; keep the durable comparison for other connections.
            if (!ReferenceEquals(change.Expected, player.Character.SavedPassiveProgress) || change.Before != player.Pools.Level ||
                change.ExtraPoints != player.Character.ExtraPassivePoints || change.RefundPoints != player.Character.RefundPoints)
                change = CaptureLevelChange(player, change.Level);
            if (change.Trimmed)
                CharacterSaves.SetMainAscendancy(CharacterName(player), change.Ascendancy, change.Nodes, change.Masteries,
                    expected: change.Expected);
            return PublishCommittedLevel(player, change);
        }
    }

    // XP is shared by overlapping area connections. Reconcile from the durable tree, not a stale connection's
    // candidate; the read, optional trim and comparison stay in one transaction before runtime publication.
    public static int CommitExperienceLevel(Player player, int level)
    {
        {
            using var availability = AscendancyPolicies.Enter(player, player.Character.MainAscendancy.Selection);
            if (HardcoreProgressBlocked(player)) return player.Pools.Level;
            var change = Saves.Atomically(() =>
            {
                var save = CharacterSaves.Load(CharacterName(player)) ?? new CharacterSaves.Save([]);
                AscendancyPolicies.RequireCurrentSelection(player.Character.MainAscendancy.Selection, save.MainAscendancy?.Selection);
                ValidateSavedProgress(player, save);
                var ascendancy = save.MainAscendancy ?? new();
                var nodes = new List<ushort>(save.Passives ?? []);
                ushort extra = save.ExtraPassivePoints ?? 0, refunds = save.RefundPoints ?? 0;
                int points = Progression.PassivePoints(level) + extra + QuestStaticRewards.Default.PassivePoints(save.QuestFlags ?? []) + Game.AscendancyRules.OrdinaryPointsGranted(
                    (byte)SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId, ascendancy);
                TrimOrdinaryPassives(player, nodes, points, ascendancy);
                var masteries = (save.MasteryEffects ?? []).Where(kv => nodes.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var current = new PassiveLevelChange(level, player.Pools.Level, extra, nodes, masteries, ascendancy,
                    refunds, CharacterSaves.PassiveProgressSnapshot.From(save), nodes.Count != (save.Passives?.Count ?? 0));
                if (current.Trimmed)
                    CharacterSaves.SetMainAscendancy(CharacterName(player), ascendancy, nodes, masteries,
                        expected: current.Expected);
                return current;
            });
            return PublishCommittedLevel(player, change);
        }
    }

    public static int PublishCommittedLevel(Player player, PassiveLevelChange change)
    {
        player.Character.Passives = [.. change.Nodes];
        player.Character.MasteryEffects = new(change.Masteries);
        player.Character.MainAscendancy = change.Ascendancy;
        AscendancyPolicies.SelectionChanged(player);
        player.Character.ExtraPassivePoints = change.ExtraPoints;
        player.Character.RefundPoints = change.RefundPoints;
        player.Character.SavedPassiveProgress = new(change.Ascendancy, change.Nodes, change.Masteries, change.RefundPoints);
        player.Pools = PoolsFor(player, change.Level);
        player.ApplyMovementSpeed();
        return change.Before;
    }

    // A new level in place, after the Player component's experience (0x2bc; the client derives the level from it and runs
    // its level change): the level's stats (0x1b3: level, life, mana, accuracy, ...), the skills with them (0x2bc Actor),
    // full life, as a level up gives, and the passive skill points (0x33). Passives beyond a lower level's points are
    // taken back, last allocated first.
    public static async Task ApplyLevel(Player player, int level)
    {
        if (HardcoreProgressBlocked(player)) return;
        int before = CommitLevelChange(player, CaptureLevelChange(player, level));
        await PublishLevelChange(player, before, level);
    }

    public static string ClassDisplayName(Player player)
    {
        var cls = SkillsFor(player).ClassFor(player.Character.ClassRow);
        if (player.Character.MainAscendancy?.Selection is { } sel
            && Ascendancies?.Find(sel) is { } asc
            && asc.BaseClass == (byte)cls.IntegerId)
            return asc.Name;
        var baseClass = (GameServer.Core.Accounts.CharacterClass)cls.IntegerId;
        return Enum.IsDefined(baseClass) ? baseClass.ToString() : "Exile";
    }

    public static string ClassDisplayName(GameServer.Core.Accounts.CharacterClass baseClass, string? ascendancyId, string? ascendancyFamily = null)
    {
        if (!string.IsNullOrEmpty(ascendancyId) && Ascendancies is { } catalog)
        {
            AscendancyFamily? family = Enum.TryParse<AscendancyFamily>(ascendancyFamily, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed) ? parsed : null;
            if (catalog.Definitions.FirstOrDefault(d => d.BaseClass == (byte)baseClass
                && (family is null || d.Identity.Family == family)
                && d.Identity.Id.Equals(ascendancyId, StringComparison.OrdinalIgnoreCase)) is { } asc)
                return asc.Name;
        }
        return Enum.IsDefined(baseClass) ? baseClass.ToString() : "Exile";
    }

    internal static async Task BroadcastLevelChange(Player player, int level)
    {
        if (player.Instance is not { Presence: { } presence }) return;
        // BackendErrors LevelUp (143): "{0} ({1}) is now level {2}". Each recipient gets its own bytes (in-place cipher).
        string text = $"{CharacterName(player)} ({ClassDisplayName(player)}) is now level {level}";
        var seen = new HashSet<Player>(ReferenceEqualityComparer.Instance);
        foreach (var recipient in InGame.Keys.Append(player))
        {
            if (!seen.Add(recipient)
                || recipient is not { Controlled: false, Closed: false, LeavingArea: false, Connection: { PlayerSent: true, Outbox: { } outbox } }
                || !ReferenceEquals(recipient.Instance?.Presence, presence)) continue;
            try { await outbox(InstanceChat.ChatLine(text), $"0x0017 level up \"{text}\""); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    public static async Task PublishLevelChange(Player player, int before, int level)
    {
        if (HardcoreProgressBlocked(player)) return;
        Console.WriteLine($"[gateway] {player.Connection.Remote} {CharacterName(player)}: level {before} â†’ {level} (life {player.Pools.Life}, mana {player.Pools.Mana}, {Progression.PassivePoints(level)} passive point(s))");
        await SendCharacterStats(player, PlayerStatChange.Build);
        int mana = UnreservedMana(player);
        // A lethal hit while SendCharacterStats awaited must not turn a Hardcore death into a level-up heal.
        if (player.Character.Hardcore && (player.PlayerLife == 0 || player.Character.HardcoreDeathRecorded)) return;
        (player.PlayerLife, player.PlayerMana) = (LifeCap(player), (uint)mana);
        RefillPlayerEnergyShield(player);
        Note($"0x01b1 player life {player.PlayerLife}");
        await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana}");
        await SendPassives(player);
        await CheckLevelAchievements(player, level);
        await BroadcastLevelChange(player, level);
    }

    // The player's stats (0x1b3) and the skills computed with them (0x2bc Actor), after the level or the passives changed.
    // The inputs the cached skill list (Player.PlayerSkillsCache) was built from.
    sealed record PlayerSkillsBuild(SkillSnapshotInputs Inputs, IReadOnlyList<GrantedSkill> Skills);
    static readonly Store<Player, PlayerSkillsBuild> PlayerSkillsInputs = new();

    public static async Task SendCharacterStats(Player player, PlayerStatChange why)
    {
        // The skill list is built again when its inputs changed (PlayerSkills), and at once by the full publication.
        if (QuestKillCommitOutput is not null)
        {
            player.PlayerSkillsCache = null;
            ActiveSkillSets.Remove(player);
            player.Minions.StatRevision++;
            return;
        }
        // In an area's frames the next Stats phase builds and publishes them, once for all the changes of a frame.
        if (PublishByFrame(player, why)) return;
        await PublishCharacterStats(player, why);
    }

    // The character panel, the skills and the Energy Shield as they are now.
    internal static async Task PublishCharacterStats(Player player, PlayerStatChange why)
    {
        player.PlayerSkillsCache = null;
        ActiveSkillSets.Remove(player);
        await SendPlayerStatSnapshot(player,
            () => PublishPlayerStats(player), $"0x01b3 player stats ({why})");
        await SendSkills(player);
        await SendPlayerEnergyShield(player);
    }

    // The player's skills again, and the removal of the ones the client still has whose gems left the equipment, in one
    // Actor 0x20 update (PlayerSkillsChange): the 0x40 list neither removes skills nor tells the character panel.
    public static async Task SendSkills(Player player)
    {
        if (!CurrentGameRules) return;
        ReconcileBeastCompanionsLocked(player);
        RefreshVaalSouls(player);
        // Named, because it is a large block (auras, pools, cooldowns) that runs on every skill refresh, and a waiter that
        // found the combat lock busy here had no name to show for it.
        if (!CurrentGameRules || player.Closed || player.LeavingArea || player.RageRetired) return;
        RefreshVaalSouls(player);
        RefreshSingleUseCooldowns(player);
        EnsureAntiquarianPools(player);
        EnsureOwnAuraPools(player);
        foreach (var id in player.Auras.Keys.ToList())
        {
            var gem = SupportedSkill(player, id);
            if (gem.Effect < 0 || SupportedReservationCost(player, id) is not { } reservation) continue;
            var before = player.Auras[id];
            var context = ReservationContext(player, id);
            var skillStats = gem.Stats.Select(kv => (kv.Key, kv.Value)).ToArray();
            player.Auras[id] = before with
            {
                ManaFlat = reservation.ManaFlat, ManaPercent = reservation.ManaPercent,
                LifeFlat = reservation.LifeFlat, LifePercent = reservation.LifePercent,
                ReservesLife = context.Life, ReservationEfficiencyPct = context.Efficiency,
                Level = gem.Level, Quality = gem.Quality, WornGrant = gem.WornGrant,
                SkillStats = skillStats
            };
            player.AuraStatsDirty |= before.ManaFlat != reservation.ManaFlat || before.ManaPercent != reservation.ManaPercent
                || before.LifeFlat != reservation.LifeFlat || before.LifePercent != reservation.LifePercent
                || before.ReservesLife != context.Life || before.ReservationEfficiencyPct != context.Efficiency
                || before.Level != gem.Level || before.Quality != gem.Quality || before.WornGrant != gem.WornGrant
                || before.SkillStats is null || !before.SkillStats.SequenceEqual(skillStats);
        }
        if (player.PetrifiedBlood.Active)
        {
            var id = player.PetrifiedBlood.SkillId;
            var gem = SupportedSkill(player, id);
            if (gem.Effect < 0 || SkillsFor(player).EffectId(gem.Effect) != PetrifiedBlood.EffectId
                || SupportedReservationCost(player, id) is not { } reservation) player.PetrifiedBlood.Suspend();
            else
            {
                var stats = PetrifiedBloodEffectStats(player, gem.Effect, gem.Level, gem.Quality);
                var context = ReservationContext(player, id);
                player.PetrifiedBlood.Refresh(gem.Effect, Get(stats, PetrifiedBlood.LoseOverTimeSource),
                    Get(stats, PetrifiedBlood.PreventSource), Get(stats, PetrifiedBlood.LifeCostSource),
                    reservation.Percent, LowLifeThresholdPct(CharacterPlayerStats(player)), context.Life, context.Efficiency);
                if (!ReservationBudgetFits(player)) player.PetrifiedBlood.Suspend();
                ClampReservedResources(player);
                player.AuraStatsDirty = true;
            }
        }
        if (player.Auras.Count > 0 || player.PetrifiedBlood.Active)
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
        }
        List<ushort> gone = [], added = [];
        // Built at the send gate, as every full stat and Actor refresh is (SendPlayerStatSnapshot): the packet that is
        // written last is then the one built last, the own-aura records it depends on go with it, and a lockstep client
        // gets it as a world record of the player.
        await SendPlayerStatSnapshot(player, () =>
        {
            var skills = PlayerSkills(player);
            gone = player.ClientSkillIds.Except(skills.Select(s => s.Id)).Order().ToList();
            added = skills.Select(s => s.Id).Where(id => !player.ClientSkillIds.Contains(id)).ToList();
            player.ClientSkillIds = [.. skills.Select(s => s.Id)];
            RecordPlayerSkills(player, skills);
            return PlayerSkillsBatchesWithVaalSouls(player, skills, gone);
        }, "0x02bc player Actor: current skill stats");
        if (!CurrentGameRules) return;
        await SendNativeCooldownRefresh(player);
        // The skill list above replaces every skill record, and the client's replacement refreshes an existing zero-rate
        // cooldown entry (0x141ef1573): a guard's held entry would start counting again while its buff runs (issue #1127,
        // seen on movement, which refreshes the conditional item stats). Re-publish the held rate-0 entry behind the list
        // so the bar stays frozen; a zero-rate refresh replaces the entry without restarting its animation.
        // Built at the send gate: a guard that ended while this send waited has its counting entry queued behind, and a
        // held entry built before would go out after it and freeze the bar with no buff.
        if (TriggeredGuards.TryGetValue(player, out _))
            await Send(player, [], "0x02bc guard cooldown re-held after its skill refresh",
                atGate: () => HeldGuardCooldownRefresh(player));
        if (gone.Count > 0) await EndSkillEffects(player, gone);

        // The update can take a bound skill off the skill bar (live 2026-10-21: Haste's slot emptied when its aura went on,
        // and came back with the area change's 0x53), and a gem socketed again does not get its slot back by itself, so the
        // saved bar goes out again. Not when a skill the saved bar never had was added: the client binds a new skill to a free
        // slot itself (Câ†’S 0x4e), and the bar sent now would clear it there. A new gem skill gets an empty slot of the saved
        // bar first (FillSkillBar: the right mouse button, then Q W E R T …), so it goes out with the bar.
        FillSkillBar(player);
        var saved = CharacterSaves.Load(CharacterName(player))?.SkillBar?.Values;
        // Only a real skill change rebuilds the bar: a 0x53 re-creates every slot and resets its cooldown entry to the
        // skill's own recovery rate, replaying the bar's cooldown animation (issue #1127: a guard's held entry was reset
        // by the rebuild its own stat-only start sent). A pure stat refresh (a guard or self buff starting/ending, an
        // aura's stats recalculated) adds and removes nothing, so the slots keep their entries. `added.All` is true on an
        // empty list, so the change gate comes first. Transient league skills are not part of the saved bar.
        if (GameEvents.ExtraSkills(player).Count > 0
            || (gone.Count > 0 || added.Count > 0)
                && added.All(id => SkillEffectId(player, id) is { } effect && saved?.Contains(effect) == true))
            await RestoreSkillBarAfterActorUpdate(player);
        // The same recalculation may have changed which worn weapons qualify.
        await SendModelIfHandsChanged(player);
    }

    // Skills the player no longer has (their gems left the equipment): their auras and Righteous Fire end with them, and the
    // reserved mana comes back.
    public static async Task EndSkillEffects(Player player, IReadOnlyCollection<ushort> gone)
    {
        if (!CurrentGameRules || !CurrentGameWork) return;
        (List<AuraCatalog.Aura> EndedAuras, bool PetrifiedEnded) ending;
        RighteousFireState? expectedRf = null;
        HashSet<ushort> ended = [];
        if (!CurrentGameRules || !CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired) return;
        // This notification can outlive the source mutation that produced it. Resolve the canonical rows again so a
        // current replacement for the same numeric id keeps its owners and saved activation. Actual direct source
        // retirement calls the shared cleanup eagerly and never uses this stale-notification authority check.
        using (new PlayerStatScope(player, fresh: true))
            foreach (ushort id in gone.Distinct())
            {
                if (CurrentPlayerSkillSourceLocked(player, id) is not { Active: true } source)
                {
                    ended.Add(id);
                    continue;
                }
                var supported = SupportedSkill(player, id);
                if (supported.Effect < 0 || supported.Effect != source.Effect || !supported.RequirementsMet)
                    ended.Add(id);
            }
        if (ended.Count > 0)
        {
            ending = EndNonRighteousFireSkillEffectsLocked(player, ended);
            // Non-Righteous-Fire cleanup can change the stats that qualify RF, so evaluate its exact lease after that
            // dependency. The check still uses the original gone id, independently of the current-row filter.
            if (player.RighteousFire is { } rf && gone.Contains(rf.Skill) && !RighteousFireCurrentLocked(player, rf))
                expectedRf = rf;
        }
        else
        {
            // RF-only stale cleanup must not call the shared helper: it also reconciles unrelated owners.
            if (player.RighteousFire is { } rf && gone.Contains(rf.Skill) && !RighteousFireCurrentLocked(player, rf))
                expectedRf = rf;
            if (expectedRf is null) return;
            ending = (new List<AuraCatalog.Aura>(), false);
        }
        SaveSummons(player);
        SaveGolems(player);
        await RefreshGolemBuffs(player);
        if (ending.EndedAuras.Count > 0 || ending.PetrifiedEnded)
        {
            await SendOwnAuraChanges(player);
            string why = ending.EndedAuras.Count > 0
                ? $"aura {string.Join(", ", ending.EndedAuras.Select(a => a.Name))} off (gem left)"
                : "Petrified Blood off (gem left)";
            Console.WriteLine($"[gateway] {player.Connection.Remote} {why}");
            await SendCharacterStats(player, PlayerStatChange.Auras);
            await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana}/{player.Pools.Mana}, {ReservedMana(player)} reserved");
        }
        if (expectedRf is not null) await EndRighteousFire(player, "its gem left", expectedRf);
    }

    // The non-Righteous-Fire owners that end with an actual source can share this synchronous mutation. The caller holds
    // Monsters; only immutable cleanup packets are queued here and every async presentation remains in a wrapper.
    private static (List<AuraCatalog.Aura> EndedAuras, bool PetrifiedEnded) EndNonRighteousFireSkillEffectsLocked(
        Player player, IReadOnlyCollection<ushort> gone)
    {
        List<AuraCatalog.Aura> ended = [.. player.Auras.Values.Where(a => gone.Contains(a.SkillId))];
        foreach (var aura in ended)
        {
            player.Auras.Remove(aura.SkillId);
            EndReservationRuntimeLocked(player, aura);
            CharacterSaves.SetAuraActive(CharacterName(player), aura.EffectId, false);
        }
        if (ended.Any(a => StanceCatalog.IsStance(a.EffectId)))
            ReconcileStanceSelectionLocked(player, persist: true);
        bool petrifiedEnded = player.PetrifiedBlood.Active && gone.Contains(player.PetrifiedBlood.SkillId);
        if (petrifiedEnded)
        {
            player.PetrifiedBlood.Suspend();
            CharacterSaves.SetPetrifiedBlood(CharacterName(player), false);
        }
        double now = AreaTime(player);
        foreach (var fighter in player.Summons.Fighters.Values.Where(g => gone.Contains(g.Source)).ToArray())
            RemoveSummonLocked(player, fighter, now);
        foreach (var golem in player.Golems.All.Values.Where(g => gone.Contains(g.Source)).ToArray())
            RemoveGolemLocked(player, golem, now);
        player.Golems.Waiting.RemoveAll(w => gone.Contains(w.Saved.Skill));
        foreach (var placed in player.Placed.All.Values.Where(p => gone.Contains(p.Skill)).ToArray())
        {
            player.Placed.All.Remove(placed.Id);
            if (!player.Placed.Grounds.Any(g => g.Object == placed.Id))
                player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(placed.Id), "placed skill source removed", WorldId: placed.Id, RemovesObject: true));
            player.Placed.Dirty = true;
            player.Minions.StatRevision++;
        }
        foreach (var ground in player.Placed.Grounds.Where(g => gone.Contains(g.Source)).ToArray())
        {
            player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ground.Object), "placed ground source removed", WorldId: ground.Object, RemovesObject: true));
            player.Placed.Grounds.Remove(ground);
        }
        if (!player.Summons.Fighters.Values.Any(g => g.Definition.Kind == "HolyRelic") && player.Summons.HolyBuff != 0)
        {
            player.Area.Buffs.ClearRow(player.Id, 1181);
            player.Summons.HolyBuff = 0;
            player.Summons.HolyRegenerationUntil = 0;
            player.Summons.HolyRegenerationPerMinute = 0;
            player.Minions.StatRevision++;
        }
        if (ended.Count > 0 || petrifiedEnded)
        {
            player.AuraStatsDirty = true;
            player.Minions.StatRevision++;
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
        }
        return (ended, petrifiedEnded);
    }


}
