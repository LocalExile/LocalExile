using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>
    /// The Offence tab's Off Hand rows (issue #75) for an attack that swings the off-hand weapon of a dual-wielded pair:
    /// <c>skill_uses_off_hand</c> makes the panel draw them, and the hand's accuracy rating, chance to hit (plain and
    /// evasive monsters), attack speed, critical strike chance and multiplier are that weapon's own, from the shared
    /// character buckets with the off-hand item's local modifiers in the main hand's place. The client derives none
    /// of them (11-skills.md §4). Null when nothing is swung with the off hand (empty, a shield, a quiver, no main-hand
    /// weapon): the rows then keep the main hand's values the class attributes send, and the flag clears.
    /// <paramref name="player"/> is the character's final stats; <paramref name="own"/> a selected attack gem's own
    /// stats, whose flat/increased accuracy and critical increases join as they do for the main hand.
    /// </summary>
    public static List<(uint Stat, int Value)>? OffHandPanelStats(Player player, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int> mainLocal, IReadOnlyDictionary<uint, int> offLocal,
        int mainHandAttackSpeedPct, IReadOnlyDictionary<uint, int>? own = null, int effect = -1) =>
        OffHandPanelStats(() => OffhandWeaponFor(player), () => ActiveOffHand(player), () => player.Pools,
            () => WeaponProfileFor(player, activeSet: true), ItemsFor(player), () => AttackCriticalMultiplier(player, playerStats, effect, own),
            playerStats, mainLocal, offLocal, mainHandAttackSpeedPct, own, PlayerBlindValue(player, playerStats).Less);

    // The same rows from inputs read once (the stat snapshot's tail inputs), each asked where the line above asks conn.
    public static List<(uint Stat, int Value)>? OffHandPanelStats(Func<SkillCatalog.Unarmed?> offhandWeapon,
        Func<(Item Item, ItemCatalog.BaseType? Type)?> activeOffHand, Func<CharacterStats.Pools> currentPools,
        Func<WeaponProfile> weaponProfile, ItemCatalog catalog, Func<int> attackCriticalMultiplier,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> mainLocal, IReadOnlyDictionary<uint, int> offLocal,
        int mainHandAttackSpeedPct, IReadOnlyDictionary<uint, int>? own, int blindLess = 0)
    {
        if (offhandWeapon() is null || activeOffHand() is not { Type: { } offType }) return null;
        var pools = currentPools();
        int Player(uint key) => player.GetValueOrDefault(key);
        int Own(uint key) => own?.GetValueOrDefault(key) ?? 0;
        var profile = weaponProfile();
        // Accuracy: the pools' unrounded basis carries the main weapon's local accuracy; swap in the off hand's. As
        // AccuracyRating.Selected does, keep the pools' final rating when nothing differs from the main hand.
        long localDifference = (long)offLocal.GetValueOrDefault(Stat.LocalAccuracyRating) - mainLocal.GetValueOrDefault(Stat.LocalAccuracyRating);
        long ownFlat = AccuracyRating.Flat(profile, n => Own(n), includeLocal: false), ownIncreased = AccuracyRating.Increased(profile, n => Own(n));
        int rating = localDifference == 0 && ownFlat == 0 && ownIncreased == 0 ? pools.Accuracy
            : AccuracyRating.Calculate(pools.AccuracyBase + localDifference + ownFlat, pools.AccuracyPct + ownIncreased);
        rating = TimedBlind.Apply(rating, blindLess);
        var accuracy = AttackAccuracyProfile.From(pools, player, Stats, own, offLocal) with { Rating = rating };
        // Critical chance: the off-hand weapon's base with its local mods, the same override and global increases.
        int weaponBase = AddedBaseCriticalStrike.LocalWeaponChance(catalog.WeaponCritical(offType),
            offLocal.GetValueOrDefault(Stat.LocalCriticalStrikeChance), offLocal.GetValueOrDefault(Stat.LocalCriticalStrikeChancePlusPct));
        int chance = AttackBaseCriticalStrike.Chance(true, weaponBase,
            pools.CriticalStrikeChancePct + (own is null ? 0 : AttackBaseCriticalStrike.IncreasedSum(profile, n => Own(n))),
            Player, own is null ? null : Own);
        int multiplier = CriticalHitMultiplier((long)attackCriticalMultiplier()
            + Own(Stat.BaseCriticalStrikeMultiplierPlus), player, own);
        return
        [
            (Stat.SkillUsesOffHand, 1),
            (Stat.OffHandAccuracyRating, rating),
            (Stat.OffHandChanceToHitPct, accuracy.Chance(Monsters.BaseEvasion(pools.Level) ?? 0, false, false)),
            (Stat.OffHandChanceToHitEvasiveMonstersPct, accuracy.Chance(Monsters.BaseEvasiveEvasion(pools.Level) ?? 0, false, false)),
            // The attack's final main-hand speed (the default attack's shared bucket, or the gem's with its supports)
            // without the main weapon's own local speed, which belongs to that weapon alone.
            (Stat.OffHandAttackSpeedPlusPct, SkillCatalog.Unarmed.OffHandAttackSpeedPct(mainHandAttackSpeedPct, mainLocal.GetValueOrDefault(Stat.LocalAttackSpeedPlusPct))),
            (Stat.OffHandCriticalStrikeChance, chance),
            (Stat.OffHandCriticalStrikeMultiplier, multiplier),
        ];
    }

    /// <summary>The off hand's twelve damage rows for a selected attack gem: the same supported hit swung with the
    /// off-hand weapon, every type written (zero clears a type the conversion no longer deals).</summary>
    public static IEnumerable<(uint Stat, int Value)> OffHandDamageRows(DamagePacket hit)
    {
        var ranges = PanelStats.HandDamage(hit, "off_hand").ToDictionary(r => r.Stat, r => r.Value);
        foreach (uint stat in PanelStats.DamageRows("off_hand")) yield return (stat, ranges.GetValueOrDefault(stat));
    }
}
