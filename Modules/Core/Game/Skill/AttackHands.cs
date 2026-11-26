using System.Runtime.CompilerServices;
using GameServer.Core.World;

/// <summary>Which weapon an attack use swings: the main hand, the off hand, or both at once.</summary>
public enum AttackHand : byte { Main, Off, Both }

/// <summary>
/// The hand each dual-wielded attack use swings (Agents/docs/protocol/11-skills.md, "Which hand swings").
/// The client picks it itself from the action: native melee selector <c>0x141b725a0</c> (the same rule as the projectile
/// selectors in Agents/docs/re/projectiles/hand-selection.md) asks the availability slot (<c>0x1419e7570</c>: bit 0 the
/// main hand's weapon class fits the skill, bit 1 the off hand's) and, when both fit, takes the main hand when the
/// action's sequence plus its result stage is even. The use's sequence is the client's own (C→S 0x24 / 0x190, echoed in
/// 0x1ab), so the server picks the same hand from it: no hand field is sent. The choice is recorded per connection at
/// the use's first question (its timing gate) so its attack time, hit, critical chance, accuracy and local modifiers
/// all come from one hand even if the equipment changes before the hit.
/// </summary>
public static class AttackHands
{
    const int Remembered = 16;

    sealed class Log
    {
        public readonly (ushort Skill, ushort Sequence, AttackHand Hand)[] Uses = new (ushort, ushort, AttackHand)[Remembered];
        public int Next, Count;
    }

    // Per connection, never on a shared object: the last few uses' hands.
    static readonly ConditionalWeakTable<Player, Log> Logs = new();

    /// <summary>A sequence the client allocated for its own action. The server's own uses (triggers, totems, an echo
    /// it forces) carry 0 or 0xffff: they do not alternate (the main hand when both fit) and are not recorded.</summary>
    public static bool ClientSequence(ushort sequence) => sequence is not (0 or 0xffff);

    /// <summary>The native parity rule for an action whose both hands fit: main hand when sequence + stage is even
    /// (<c>~(stage + low8(sequence)) &amp; 1</c>). A new use starts at stage 0.</summary>
    public static AttackHand Alternate(ushort sequence, int stage = 0) =>
        ((sequence + stage) & 1) == 0 ? AttackHand.Main : AttackHand.Off;

    /// <summary>The hand recorded for this use, or <paramref name="select"/>'s answer (given the sequence to alternate
    /// on, 0 for a server use) recorded now. The log's lock is a leaf: <paramref name="select"/> runs outside it, since
    /// it reads the skill and equipment under the connection's own locks, which a caller may already
    /// hold (a contact under its combat lock) or take later (a use's admission). When two threads select the same use
    /// at once, the first to record wins and both return its hand.</summary>
    public static AttackHand Resolve(Player player, ushort skill, ushort sequence, Func<ushort, AttackHand> select)
    {
        if (!ClientSequence(sequence)) return select(0);
        var log = Logs.GetValue(player, _ => new Log());
        lock (log)
            if (Recorded(log, skill, sequence) is { } known) return known;
        var hand = select(sequence);
        lock (log)
        {
            if (Recorded(log, skill, sequence) is { } raced) return raced;
            log.Uses[log.Next] = (skill, sequence, hand);
            log.Next = (log.Next + 1) % Remembered;
            log.Count = Math.Min(Remembered, log.Count + 1);
            return hand;
        }

        static AttackHand? Recorded(Log log, ushort skill, ushort sequence)
        {
            for (int i = 0; i < log.Count; i++)
                if (log.Uses[i].Skill == skill && log.Uses[i].Sequence == sequence) return log.Uses[i].Hand;
            return null;
        }
    }

    /// <summary>
    /// Whether a weapon class fits a skill, in the client's order (0x141ee8410 in the 3.29.3b notes): the skill's
    /// bow/non-melee/wand stats, the supports' weapon-family stats, then the ActiveSkills restriction list unless
    /// <c>ignore_skill_weapon_restrictions</c>. A shield or quiver is never a weapon here (the caller passes weapons only).
    /// </summary>
    public static bool Fits(string itemClass, int classRow, IReadOnlySet<int> allowed, Func<string, int> skill)
    {
        if (skill("skill_can_only_use_non_melee_weapons") != 0 && itemClass is not ("Wand" or "Bow")) return false;
        if (skill("skill_can_only_use_bow") != 0 && itemClass != "Bow") return false;
        if (skill("supported_skill_can_only_use_wand") != 0 && itemClass != "Wand") return false;
        if (skill("supported_skill_can_only_use_axe_and_sword") != 0 && itemClass is not ("One Hand Sword"
            or "Thrusting One Hand Sword" or "One Hand Axe" or "Two Hand Sword" or "Two Hand Axe")) return false;
        if (skill("supported_skill_can_only_use_dagger_and_claw") != 0 && itemClass is not ("Claw" or "Dagger" or "Rune Dagger"))
            return false;
        if (skill("supported_skill_can_only_use_mace_and_staff") != 0 && itemClass is not ("One Hand Mace" or "Staff"
            or "Two Hand Mace" or "Sceptre" or "Warstaff")) return false;
        if (skill("supported_skill_can_only_use_axe_mace_and_staff") != 0 && itemClass is not ("One Hand Axe" or "One Hand Mace"
            or "Staff" or "Two Hand Axe" or "Two Hand Mace" or "Sceptre" or "Warstaff")) return false;
        return allowed.Count == 0 || skill("ignore_skill_weapon_restrictions") != 0 || allowed.Contains(classRow);
    }

    /// <summary>The hand a use swings from which hands fit: both fit → <see cref="Alternate"/>, or
    /// <see cref="AttackHand.Both"/> for a skill with <c>skill_double_hits_when_dual_wielding</c>; one fits → that one.</summary>
    public static AttackHand Choose(bool mainFits, bool offFits, bool doubleHits, ushort sequence) =>
        mainFits && offFits ? doubleHits ? AttackHand.Both : Alternate(sequence)
        : offFits ? AttackHand.Off : AttackHand.Main;
}

public static partial class Game
{
    /// <summary>The hand an attack gem's use swings (<see cref="AttackHands"/>), recorded at its first question.</summary>
    public static AttackHand AttackHandFor(Player player, ushort skillId, ushort sequence) =>
        skillId == SkillCatalog.FirstSkillId ? DefaultAttackHand(player, sequence)
        : AttackHands.Resolve(player, skillId, sequence, at => SelectAttackHand(player, skillId, at));

    /// <summary>The default attack's hand: it has no weapon restriction, so two melee weapons or two wands alternate.
    /// A wand beside a melee weapon keeps the main hand: the ranged and melee executors are separate.</summary>
    public static AttackHand DefaultAttackHand(Player player, ushort sequence) =>
        AttackHands.Resolve(player, SkillCatalog.FirstSkillId, sequence, at =>
        {
            if (WeaponIn(player, ActiveMainHand(player)) is not { } main || OffhandWeaponFor(player) is not { } off) return AttackHand.Main;
            return main.WieldingWand == off.WieldingWand ? AttackHands.Alternate(at) : AttackHand.Main;
        });

    static AttackHand SelectAttackHand(Player player, ushort skillId, ushort sequence)
    {
        var gem = SupportedSkill(player, skillId);
        // A shield skill swings the shield (ShieldWeapon); a spell or a non-attack has no weapon hand.
        if (!SkillsFor(player).IsAttack(gem.Effect) || SkillsFor(player).IsSpell(gem.Effect) || ShieldWeapon(player, skillId) is not null)
            return AttackHand.Main;
        var (mainFits, offFits) = HandsFit(player, gem.Effect, gem.Stats);
        int Own(string name) => Stats.Key(name) is uint key ? gem.Stats.GetValueOrDefault(key) : 0;
        // A channel's stages (Cyclone) share one use; which stage swings which hand is not traced, so a channel keeps the
        // main hand when both fit, as its interval does (SupportedChannelSeconds).
        return AttackHands.Choose(mainFits, offFits, LacerateEffect(SkillsFor(player).EffectId(gem.Effect))
            || Own("skill_double_hits_when_dual_wielding") != 0,
            SkillsFor(player).IsChannelled(gem.Effect) ? (ushort)0 : sequence);
    }

    /// <summary>Whether each active hand's weapon fits the skill (AttackHands.Fits). No main-hand weapon: the fists fit
    /// and the off hand does not swing. The off hand needs a weapon (not a shield or quiver) beside a main-hand weapon.</summary>
    public static (bool Main, bool Off) HandsFit(Player player, int effect, IReadOnlyDictionary<uint, int> skillStats)
    {
        var main = ActiveMainHand(player);
        if (WeaponIn(player, main) is null || main?.Type is not { } mainType) return (true, false);
        var off = ActiveOffHand(player);
        int Own(string name) => Stats.Key(name) is uint key ? skillStats.GetValueOrDefault(key) : 0;
        var allowed = SkillsFor(player).WeaponRestrictions(effect);
        bool mainFits = AttackHands.Fits(mainType.Class, mainType.ClassRow, allowed, Own);
        bool offFits = WeaponIn(player, off) is not null && off?.Type is { } offType
            && AttackHands.Fits(offType.Class, offType.ClassRow, allowed, Own);
        return (mainFits, offFits);
    }

    /// <summary>The weapon a hand swings; the off hand falls back to the main hand when it no longer holds a weapon.</summary>
    public static SkillCatalog.Unarmed HandWeapon(Player player, AttackHand hand) =>
        hand == AttackHand.Off && OffhandWeaponFor(player) is { } off ? off : WeaponFor(player);

    /// <summary>The weapon override an off-hand swing passes where the main hand's is the default (null otherwise).</summary>
    public static SkillCatalog.Unarmed? OffHandSwing(Player player, AttackHand hand) =>
        hand == AttackHand.Off ? OffhandWeaponFor(player) : null;

    /// <summary>The swinging hand's weapon base critical chance with its local modifiers (WeaponProfile.Critical for
    /// the main hand).</summary>
    public static int HandWeaponCritical(Player player, AttackHand hand)
    {
        if (hand != AttackHand.Off || OffhandWeaponFor(player) is not { } off || ActiveOffHand(player)?.Type is not { } type)
            return WeaponProfileFor(player, activeSet: true).Critical;
        // The off-hand weapon's sources are already its qualified local stats (WeaponIn), as HandLocalStats gives them.
        var local = off.LocalSources;
        return AddedBaseCriticalStrike.LocalWeaponChance(ItemsFor(player).WeaponCritical(type),
            local?.GetValueOrDefault(Stat.LocalCriticalStrikeChance) ?? 0, local?.GetValueOrDefault(Stat.LocalCriticalStrikeChancePlusPct) ?? 0);
    }

    /// <summary>The off-hand swing's weapon base critical chance for the target-conditional chances (null: the main
    /// hand's, also for both hands at once).</summary>
    public static int? SwingWeaponCritical(Player player, AttackHand hand) =>
        hand == AttackHand.Off && OffhandWeaponFor(player) is not null ? HandWeaponCritical(player, hand) : null;

    /// <summary>The default attack's accuracy for the swinging hand, Blind included (PlayerAccuracy for the main hand);
    /// the off hand swaps in its local accuracy (WeaponAccuracyFor), as its panel row does.</summary>
    public static int HandAccuracy(Player player, AttackHand hand, IReadOnlyDictionary<uint, int>? playerStats = null) =>
        hand == AttackHand.Off && OffhandWeaponFor(player) is { } off
            ? WeaponAccuracyFor(player, off, playerStats ?? CharacterPlayerStats(player)) : PlayerAccuracy(player, playerStats);

    /// <summary>The default attack's critical chance with the swinging hand (Pools.AttackCriticalStrikeChance is the
    /// main hand's): the off-hand weapon's own base and local modifiers with the same increases and override.</summary>
    public static int HandAttackCriticalChance(Player player, AttackHand hand, IReadOnlyDictionary<uint, int> playerStats)
    {
        if (hand != AttackHand.Off || OffhandWeaponFor(player) is null) return player.Pools.AttackCriticalStrikeChance;
        int PlayerStat(uint key) => playerStats.GetValueOrDefault(key);
        int RawCritical(uint key) => IsCriticalProhibition(key) ? 0 : PlayerStat(key);
        return DisplayedCriticalChance(player, AttackBaseCriticalStrike.Chance(true, HandWeaponCritical(player, hand),
            player.Pools.CriticalStrikeChancePct, RawCritical), RawCritical);
    }

    /// <summary>The default attack's attack time with the swinging hand, before the action factor: the off hand's own
    /// duration at the off-hand increased speed (the main weapon's local speed taken back out), as its panel row.</summary>
    public static double DefaultAttackSeconds(Player player, AttackHand hand, IReadOnlyDictionary<uint, int> playerStats)
    {
        var main = WeaponFor(player);
        if (hand != AttackHand.Off || OffhandWeaponFor(player) is not { } off) return main.AttackSeconds(playerStats);
        return DualWieldPanel.OffHandSeconds(off, SkillCatalog.Unarmed.OffHandAttackSpeedPct(main.IncreasedAttackSpeed(playerStats),
            WeaponProfileFor(player, activeSet: true).LocalAttackSpeedPct));
    }

    /// <summary>The default attack's time for this use (its recorded hand), before the action factor.</summary>
    public static double DefaultAttackSeconds(Player player, ushort sequence, IReadOnlyDictionary<uint, int> playerStats) =>
        DefaultAttackSeconds(player, DefaultAttackHand(player, sequence), playerStats);

    /// <summary>A supported attack's time with a hand. Both hands at once: the average of the two weapons' times
    /// (Path of Building's harmonic mean of the two speeds), or the off hand's alone with
    /// <c>dual_strike_off_hand_weapon_determines_attack_time</c>, then <c>viper_strike_dual_wield_attack_speed_+%_final</c>.</summary>
    public static double SupportedAttackSeconds(Player player, ushort skillId, AttackHand hand) =>
        AttackSecondsForHand(player, skillId, hand, startingPair: false);

    static double StartingAttackSeconds(Player player, ushort skillId, ushort sequence) =>
        AttackSecondsForHand(player, skillId, AttackHandFor(player, skillId, sequence), startingPair: true);

    static double AttackSecondsForHand(Player player, ushort skillId, AttackHand hand, bool startingPair,
        int? doubleStrikeStages = null)
    {
        double Seconds(SkillCatalog.Unarmed? weapon) =>
            SupportedAttackTiming(player, skillId, weapon, clampToTick: true, proxy: false,
                doubleStrikeStages: doubleStrikeStages, startingPair: startingPair).Seconds;
        if (hand == AttackHand.Main || OffhandWeaponFor(player) is not { } off) return Seconds(null);
        double offSeconds = Seconds(off);
        if (hand == AttackHand.Off) return offSeconds;
        int Own(string name) => Stats.Key(name) is uint key ? SupportedSkill(player, skillId).Stats.GetValueOrDefault(key) : 0;
        double seconds = Own("dual_strike_off_hand_weapon_determines_attack_time") != 0 ? offSeconds
            : (Seconds(null) + offSeconds) / 2;
        return Math.Max(TickSeconds, seconds / Math.Max(.01, 1 + Own("viper_strike_dual_wield_attack_speed_+%_final") / 100.0));
    }

    /// <summary>A supported attack's time for this use (its recorded hand).</summary>
    public static double SupportedAttackSeconds(Player player, ushort skillId, ushort sequence) =>
        SupportedAttackSeconds(player, skillId, AttackHandFor(player, skillId, sequence));

    /// <summary>The swing's hit for a supported attack with a hand. Both hands: each weapon's hit, the main hand's with
    /// <c>active_skill_main_hand_weapon_damage_+%_final</c>, added into one hit ("dealing the damage of both in one
    /// strike", ActiveSkills dual_strike), then the skill's dual-wield more multipliers. One hand while dual wielding still
    /// takes <c>active_skill_merged_damage_+%_final_while_dual_wielding</c> (Cleave's 40% less).</summary>
    public static DamagePacket SupportedSwingHit(Player player, ushort skillId, AttackHand hand,
        Func<SkillCatalog.Unarmed?, DamagePacket> hit)
    {
        var gem = SupportedSkill(player, skillId);
        if (!SkillsFor(player).IsAttack(gem.Effect) || SkillsFor(player).IsSpell(gem.Effect)) return hit(null);
        var (mainMore, dualMore) = HandDamageMore(player, gem.Stats);
        var off = OffhandWeaponFor(player);
        DamagePacket swing;
        if (hand == AttackHand.Both && off is not null)
        {
            swing = Scaled(hit(null), mainMore);
            foreach (var (type, range) in hit(off).Components) swing = swing.Add(type, range);
        }
        else swing = hand == AttackHand.Off && off is not null ? hit(off) : Scaled(hit(null), mainMore);
        return Scaled(swing, dualMore);

        static DamagePacket Scaled(DamagePacket packet, double more) => more == 1 ? packet : packet.Scale(more);
    }

    /// <summary>A skill's more multipliers by hand: <c>active_skill_main_hand_weapon_damage_+%_final</c> on the main
    /// hand's hit, and while dual wielding <c>active_skill_merged_damage_+%_final_while_dual_wielding</c> and
    /// <c>viper_strike_dual_wield_damage_+%_final</c> on the whole use (Path of Building's MainHandAttack and DualWielding
    /// conditions).</summary>
    public static (double MainHand, double DualWielding) HandDamageMore(Player player, IReadOnlyDictionary<uint, int> skillStats)
    {
        int Own(string name) => Stats.Key(name) is uint key ? skillStats.GetValueOrDefault(key) : 0;
        double main = Math.Max(0, 1 + Own("active_skill_main_hand_weapon_damage_+%_final") / 100.0);
        double dual = WeaponProfileFor(player, activeSet: true).DualWield
            ? Math.Max(0, 1 + Own("active_skill_merged_damage_+%_final_while_dual_wielding") / 100.0)
                * Math.Max(0, 1 + Own("viper_strike_dual_wield_damage_+%_final") / 100.0) : 1;
        return (main, dual);
    }

    /// <summary>Which hands a skill's panel shows (its Off Hand rows only when the off hand swings it).</summary>
    public static AttackHand PanelHands(Player player, ushort skillId)
    {
        var gem = SupportedSkill(player, skillId);
        var (mainFits, offFits) = HandsFit(player, gem.Effect, gem.Stats);
        int Own(string name) => Stats.Key(name) is uint key ? gem.Stats.GetValueOrDefault(key) : 0;
        return !offFits ? AttackHand.Main : !mainFits ? AttackHand.Off
            : Own("skill_double_hits_when_dual_wielding") != 0 ? AttackHand.Both : AttackHand.Off;
    }

    /// <summary>A supported attack's critical chance with a hand; both hands at once average the two (Path of
    /// Building's CritChance AVERAGE for a both-weapon attack).</summary>
    public static int SupportedCriticalChance(Player player, ushort skillId, AttackHand hand) =>
        SupportedCriticalChance(player, skillId, hand, hand != AttackHand.Main && OffhandWeaponFor(player) is not null
            ? HandWeaponCritical(player, AttackHand.Off) : null);

    /// <summary><see cref="SupportedCriticalChance(Player, ushort, AttackHand)"/> with the off-hand weapon's base
    /// critical chance read once by the caller (null: no off-hand weapon), for a sweep that asks per victim.</summary>
    public static int SupportedCriticalChance(Player player, ushort skillId, AttackHand hand, int? offCritical) =>
        SupportedCriticalChance(player, skillId, hand, offCritical, 0);

    static int SupportedCriticalChance(Player player, ushort skillId, AttackHand hand, int? offCritical,
        long extraIncrease) => (hand, offCritical) switch
        {
            (AttackHand.Off, int off) => SupportedCriticalChanceFor(player, skillId, false, extraIncrease, off),
            (AttackHand.Both, int off) => (int)Math.Round((SupportedCriticalChanceFor(player, skillId, false, extraIncrease)
                + SupportedCriticalChanceFor(player, skillId, false, extraIncrease, off)) / 2.0, MidpointRounding.AwayFromZero),
            _ => SupportedCriticalChanceFor(player, skillId, false, extraIncrease),
        };

    /// <summary>A supported attack's accuracy with a hand: the off hand's local accuracy in the main hand's place.
    /// Both hands at once use the main hand's (one hit, one evasion roll).</summary>
    public static AttackAccuracyProfile SupportedAccuracy(Player player, ushort skillId, AttackHand hand)
    {
        var accuracy = SupportedAccuracy(player, skillId);
        if (hand != AttackHand.Off || OffhandWeaponFor(player) is not { } off) return accuracy;
        var gem = SupportedSkill(player, skillId);
        var playerStats = CharacterPlayerStats(player);
        return AttackAccuracyProfile.From(player.Pools, playerStats, Stats, gem.Stats, off.LocalSources)
            with { Rating = WeaponAccuracyFor(player, off, playerStats, gem.Stats, gem.Effect) };
    }

    /// <summary>The swinging hand's local modifiers (on-hit chances, leech, penetration): the main hand's for both
    /// hands at once.</summary>
    /// <summary>The hand's weapon's fixed critical strike chance in hundredths of a percent ("This Weapon's Critical
    /// Strike Chance is 100%", "is #%"), or 0 without such a line. The panel, a swing and a release all read it.</summary>
    public static int FixedWeaponCriticalChance(Player player, AttackHand hand) =>
        HandLocalSources(player, hand) is not { } local ? 0
        : local.GetValueOrDefault(Stat.LocalWeaponCritChanceIs100) != 0 ? 10000
        : Math.Max(0, local.GetValueOrDefault(Stat.LocalWeaponCritChanceIsPct)) * 100;

    public static IReadOnlyDictionary<uint, int>? HandLocalSources(Player player, AttackHand hand) =>
        HandWeapon(player, hand).LocalSources;

    /// <summary>", main hand" / ", off hand" / ", both hands" for the gateway log while the off hand holds a weapon;
    /// empty otherwise, so one-weapon logs read as before.</summary>
    public static string HandNote(Player player, AttackHand hand) =>
        OffhandWeaponFor(player) is null ? "" : ", " + HandLog(hand);

    /// <summary>The hand's word in the gateway log.</summary>
    public static string HandLog(AttackHand hand) => hand switch
    {
        AttackHand.Off => "off hand",
        AttackHand.Both => "both hands",
        _ => "main hand",
    };
}
