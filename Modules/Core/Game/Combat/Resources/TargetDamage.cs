using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const uint PhysicalVsIgnited = Stat.PhysicalDamagePlusPctVsIgnitedEnemies;
    public const uint DamageVsLowLifePerFrenzy = Stat.DamagePlusPctVsEnemiesOnLowLifePerFrenzyCharge;
    public const uint TinctureDamageVsLowLife = Stat.TincturedWeaponDamagePlusPctVsEnemiesThatAreOnLowLife;
    public static bool EnemyOnLowLife(SpawnedMonster target) => target.Hostile && MonsterOnLowLife(target);

    // Use the target's life before this contact for both damage and the shared critical roll.
    readonly record struct DualStrikeBonuses(int DamagePct, int CriticalPct)
    {
        internal static DualStrikeBonuses From(IReadOnlyDictionary<uint, int> skill) =>
            new(skill.GetValueOrDefault(Stat.DualStrikeDamagePlusPctFinalAgainstEnemiesOnFullLife),
                skill.GetValueOrDefault(Stat.DualStrikeCriticalStrikeChancePlusPctFinalAgainstEnemiesOnFullLife));

        internal DualStrikeBonuses Against(SpawnedMonster target) =>
            target.MaxLife > 0 && target.Life >= target.MaxLife ? this : default;

        internal double DamageMore => DamageFactor(DamagePct);

        internal int CriticalChance(int chance) => CriticalPct == 0 ? chance
            : (int)Math.Clamp(chance * (100L + CriticalPct) / 100, 0, 10_000);
    }

    const uint FireVsBleeding = Stat.FireDamagePlusPctVsBleedingEnemies;

    static bool HasTypedTargetDamage(Func<uint, int> get) => get(PhysicalVsIgnited) != 0 || get(FireVsBleeding) != 0
        || get(DamageVsLowLifePerFrenzy) != 0
        || get(Stat.TinctureApplied) != 0 && get(TinctureDamageVsLowLife) != 0;

    internal static bool ProjectileTargetChangesDamage(SpiderDamageSource source, bool blinded, int webs, bool ignited,
        bool bleeding, bool lowLife, bool maimed, TargetContact contact)
    {
        if (contact != default) return true;
        int Get(uint key) => source.Stats.GetValueOrDefault(key);
        bool attack = source.Attack && !source.Secondary;
        return blinded && Get(PlayerHitDamage.VsBlindedDamageStat) != 0
            || webs > 0 && HasSpiderDamage(Get)
            || ignited && Get(PhysicalVsIgnited) != 0
            || bleeding && Get(FireVsBleeding) != 0
            || lowLife && (HasLowLifeDamage(Get) || Get(DamageVsLowLifePerFrenzy) != 0
                || attack && Get(Stat.TinctureApplied) != 0 && Get(TinctureDamageVsLowLife) != 0)
            || maimed && attack && Get(Stat.AttackDamagePlusPctVsMaimedEnemies) != 0;
    }

    static bool HasLowLifeDamage(Func<uint, int> get) => get(Stat.DamageVsEnemiesOnLowLifePlusPct) != 0
        || get(Stat.DamageVsEnemiesOnLowLifePlusPctFinal) != 0
        || get(Stat.ClawDamageAgainstEnemiesOnLowLifePlusPct) != 0
        || get(Stat.AxeMasteryHitAndAilmentDamagePlusPctFinalVsEnemiesOnLowLife) != 0
        || get(Stat.GladiatorDamagePlusPctFinalAgainstEnemiesOnLowLifeWhileWieldingAxe) != 0;

    static bool HasConditionalHitDamage(SupportedHitInputs inputs) => HasLowLifeDamage(inputs.Get)
        || inputs.Melee && inputs.Get(Stat.MeleeDamagePlusPctAtCloseRange) != 0
        || inputs.Attack && !inputs.Secondary && inputs.Get(Stat.AttackDamagePlusPctVsMaimedEnemies) != 0
        || HasContactDamage(inputs.Get);

    static bool InCloseRange(Player player, SpawnedMonster target, double at) =>
        TargetAccuracy.InCloseRange(Dist(player.Walk.Position(at), target.Walk.Position(at)));

    static bool HasLocalTargetDamage(IReadOnlyDictionary<uint, int>? local) => local is not null
        && (local.GetValueOrDefault(Stat.LocalHitDamagePlusPctVsIgnitedEnemies) != 0
            || local.GetValueOrDefault(Stat.LocalHitDamagePlusPctVsFrozenEnemies) != 0
            || local.GetValueOrDefault(Stat.LocalHitDamagePlusPctVsShockedEnemies) != 0);

    static bool HasContactDamage(Func<uint, int> get) => get(Stat.DamagePlusPctToRareAndUniqueEnemies) != 0
        || get(Stat.DamagePlusPctVsRareMonsters) != 0 || get(Stat.DamagePlusPctVsMagicMonsters) != 0
        || get(Stat.DamagePlusPctVsIgnitedEnemies) != 0 || get(Stat.DamagePlusPctVsFrozenEnemies) != 0
        || get(Stat.DamageVsShockedEnemiesPlusPct) != 0 || get(Stat.DamagePlusPctVsHinderedEnemies) != 0
        || get(Stat.HitAndAilmentDamagePlusPctVsBleedingEnemies) != 0 || get(Stat.HitAndAilmentDamagePlusPctVsCursedEnemies) != 0
        || get(Stat.DamagePlusPctFinalIfThereIsAtMost1RareOrUniqueEnemyNearby) != 0
        || get(Stat.DamagePlusPctVsChilledEnemies) != 0 || get(Stat.HitDamagePlusPctVsIgnitedEnemies) != 0
        || get(Stat.HitAndAilmentDamagePlusPctVsEnemiesAffectedByAilments) != 0
        || get(Stat.ColdHitDamagePlusPctVsShockedEnemies) != 0 || get(Stat.LightningHitDamagePlusPctVsChilledEnemies) != 0
        || get(Stat.DamagePlusPctAgainstEnemiesMarkedByYou) != 0
        || get(Stat.LeechMasteryHitDamagePlusPctFinalVsLeechImmune) != 0
        || get(Stat.PassiveMasteryHitAilmentDamagePlusPctFinalVsEnemiesWith5PlusPoisons) != 0;

    internal readonly record struct TargetContact(long HitIncrease, long AilmentIncrease, int ColdHitIncrease,
        int LightningHitIncrease, int OpportunisticMore, int PoisonMore, int LeechImmuneMore)
    {
        internal double AilmentMore => DamageFactor(OpportunisticMore) * DamageFactor(PoisonMore);
        internal double HitMore => AilmentMore * DamageFactor(LeechImmuneMore);
    }

    // A release keeps the source stats. Each contact reads the target before the hit can inflict anything.
    internal static TargetContact ContactDamage(Player player, SpawnedMonster target, double at,
        IReadOnlyDictionary<uint, int> stats, IReadOnlyDictionary<uint, int>? local = null)
    {
        int Get(uint name) => GolemStat(stats, name);
        int Local(uint name) => local?.GetValueOrDefault(name) ?? 0;
        long hit = target.Rarity switch
        {
            MonsterRarity.Rare => (long)Get(Stat.DamagePlusPctToRareAndUniqueEnemies) + Get(Stat.DamagePlusPctVsRareMonsters),
            MonsterRarity.Unique => Get(Stat.DamagePlusPctToRareAndUniqueEnemies),
            MonsterRarity.Magic => Get(Stat.DamagePlusPctVsMagicMonsters),
            _ => 0,
        };
        int chillHit = Get(Stat.DamagePlusPctVsChilledEnemies);
        int lightningHit = Get(Stat.LightningHitDamagePlusPctVsChilledEnemies);
        bool chilled = (chillHit != 0 || lightningHit != 0) && AilmentOn(target, AilmentType.Chill, at);
        if (chilled) hit += chillHit;
        else lightningHit = 0;
        int coldHit = Get(Stat.ColdHitDamagePlusPctVsShockedEnemies);
        long shockHit = (long)Get(Stat.DamageVsShockedEnemiesPlusPct) + Local(Stat.LocalHitDamagePlusPctVsShockedEnemies);
        bool shocked = (coldHit != 0 || shockHit != 0) && AilmentOn(target, AilmentType.Shock, at);
        if (shocked) hit += shockHit;
        else coldHit = 0;
        long frozenHit = (long)Get(Stat.DamagePlusPctVsFrozenEnemies) + Local(Stat.LocalHitDamagePlusPctVsFrozenEnemies);
        if (frozenHit != 0 && AilmentOn(target, AilmentType.Freeze, at)) hit += frozenHit;

        long igniteHit = (long)Get(Stat.HitDamagePlusPctVsIgnitedEnemies) + Local(Stat.LocalHitDamagePlusPctVsIgnitedEnemies);
        int ignited = Get(Stat.DamagePlusPctVsIgnitedEnemies);
        bool isIgnited = (igniteHit != 0 || ignited != 0) && AilmentOn(target, AilmentType.Ignite, at);
        if (isIgnited) hit += igniteHit;

        long ailment = Get(Stat.HitAndAilmentDamagePlusPctVsEnemiesAffectedByAilments);
        if (ailment != 0 && !chilled && !HasDamageAilment(target, at)) ailment = 0;
        if (isIgnited) ailment += ignited;
        int hindered = Get(Stat.DamagePlusPctVsHinderedEnemies);
        if (hindered != 0 && (target.HinderedUntil > at || target.TotemHinderedUntil > at
            || target.Effects[Stat.IsHindered] != 0 || SpiderHinder(target, at) > 0 || StormbindHinder(player.Area, target, at) > 0))
            ailment += hindered;
        int bleeding = Get(Stat.HitAndAilmentDamagePlusPctVsBleedingEnemies);
        if (bleeding != 0 && AilmentOn(target, AilmentType.Bleed, at)) ailment += bleeding;
        int cursed = Get(Stat.HitAndAilmentDamagePlusPctVsCursedEnemies);
        if (cursed != 0 && target.HasCurses && target.Curses.HasActive(at)) ailment += cursed;
        int marked = Get(Stat.DamagePlusPctAgainstEnemiesMarkedByYou);
        if (marked != 0 && target.Curses.HasMark(player.Id, at)) ailment += marked;
        int opportunistic = Get(Stat.DamagePlusPctFinalIfThereIsAtMost1RareOrUniqueEnemyNearby);
        if (opportunistic != 0 && !AtMostOneRareEnemy(player, at)) opportunistic = 0;
        int poison = Get(Stat.PassiveMasteryHitAilmentDamagePlusPctFinalVsEnemiesWith5PlusPoisons);
        if (poison != 0 && target.Poisons.Stacks(at) < 5) poison = 0;
        int leechImmune = Get(Stat.LeechMasteryHitDamagePlusPctFinalVsLeechImmune);
        if (leechImmune != 0 && target.Effects[Stat.CannotHaveLifeLeechedFrom] == 0) leechImmune = 0;
        return new(hit + ailment, ailment, coldHit, lightningHit, opportunistic, poison, leechImmune);
    }

    static bool HasDamageAilment(SpawnedMonster target, double at)
    {
        for (var type = AilmentType.Ignite; type <= AilmentType.Poison; type++)
            if (AilmentOn(target, type, at)) return true;
        return false;
    }

    static bool AtMostOneRareEnemy(Player player, double at)
    {
        const int radius = 60;
        var here = player.Walk.Position(at);
        int count = 0;
        foreach (var (_, m) in player.Area.MonstersNear(here, radius))
            if (m.Rarity is (MonsterRarity.Rare or MonsterRarity.Unique) && m.Hostile && !m.Dead && m.Life > 0 && !m.WorldHidden
                && Dist(here, m.Walk.Position(at)) <= radius && ++count == 2) return false;
        return true;
    }

    static double DamageFactor(int percent) => percent == 0 ? 1 : Math.Max(0, 1 + percent / 100d);

    readonly record struct TargetDamageModifiers(long HitIncrease, long AilmentIncrease, double HitMore, double AilmentMore);

    static TargetDamageModifiers TargetModifiers(Func<uint, int> get, bool lowLife, bool maimed = false,
        bool close = false, bool claw = false, TargetContact contact = default)
    {
        long hitIncrease = contact.HitIncrease + (close ? (long)get(Stat.MeleeDamagePlusPctAtCloseRange) : 0)
            + (maimed ? get(Stat.AttackDamagePlusPctVsMaimedEnemies) : 0);
        if (!lowLife) return new(hitIncrease, contact.AilmentIncrease, contact.HitMore, contact.AilmentMore);
        int clawIncrease = claw ? get(Stat.ClawDamageAgainstEnemiesOnLowLifePlusPct) : 0;
        hitIncrease += (long)get(Stat.DamageVsEnemiesOnLowLifePlusPct) + clawIncrease;
        double more = DamageFactor(get(Stat.DamageVsEnemiesOnLowLifePlusPctFinal))
            * DamageFactor(get(Stat.AxeMasteryHitAndAilmentDamagePlusPctFinalVsEnemiesOnLowLife));
        int axeMore = get(Stat.GladiatorDamagePlusPctFinalAgainstEnemiesOnLowLifeWhileWieldingAxe);
        if (axeMore != 0 && (WeaponTypeDamage.FamilyOf(get(Stat.MainHandWeaponType)) == "axe"
                || WeaponTypeDamage.FamilyOf(get(Stat.OffHandWeaponType)) == "axe"
                || get(WeaponIdentity.MainAllOneHandMelee) != 0 || get(WeaponIdentity.OffAllOneHandMelee) != 0))
            more *= DamageFactor(axeMore);
        return new(hitIncrease, contact.AilmentIncrease + clawIncrease, more * contact.HitMore, more * contact.AilmentMore);
    }

    static int TargetHitStat(uint key, int value, TargetContact contact) => key switch
    {
        Stat.ColdDamagePlusPct when contact.ColdHitIncrease != 0 => ActorScale.Saturate((long)value + contact.ColdHitIncrease),
        Stat.LightningDamagePlusPct when contact.LightningHitIncrease != 0 => ActorScale.Saturate((long)value + contact.LightningHitIncrease),
        _ => value,
    };

    static IReadOnlyDictionary<uint, int> TargetHitStats(IReadOnlyDictionary<uint, int> stats, TargetContact contact)
    {
        if (contact.ColdHitIncrease == 0 && contact.LightningHitIncrease == 0) return stats;
        var hit = new Dictionary<uint, int>(stats);
        if (contact.ColdHitIncrease != 0)
            hit[Stat.ColdDamagePlusPct] = TargetHitStat(Stat.ColdDamagePlusPct, GolemStat(stats, Stat.ColdDamagePlusPct), contact);
        if (contact.LightningHitIncrease != 0)
            hit[Stat.LightningDamagePlusPct] = TargetHitStat(Stat.LightningDamagePlusPct, GolemStat(stats, Stat.LightningDamagePlusPct), contact);
        return hit;
    }

    // Project into the original type's increased bucket before conversion. The converted part inherits that
    // bucket once, and the damaging ailments use the same pre-contact target state independently of the hit.
    static int TypedTargetDamageStat(Func<uint, int> get, uint name, bool ignited, bool bleeding, bool lowLife = false,
        bool attack = true) =>
        ActorScale.Saturate((long)get(name) + (name == Stat.PhysicalDamagePlusPct && ignited ? get(PhysicalVsIgnited) : 0)
            + (name == Stat.FireDamagePlusPct && bleeding ? get(FireVsBleeding) : 0)
            + (name == Stat.DamagePlusPct && lowLife
                ? (long)Math.Max(0, get(Stat.CurrentFrenzyCharges)) * get(DamageVsLowLifePerFrenzy)
                    + (attack && get(Stat.TinctureApplied) != 0 ? get(TinctureDamageVsLowLife) : 0)
                : 0));

    static IReadOnlyDictionary<uint, int> TypedTargetDamageStats(IReadOnlyDictionary<uint, int> stats,
        bool ignited, bool bleeding, bool lowLife = false, bool attack = true)
    {
        int Get(uint name) => GolemStat(stats, name);
        if (!HasTypedTargetDamage(Get) || !ignited && !bleeding && !lowLife) return stats;
        var result = new Dictionary<uint, int>(stats);
        foreach (uint name in new[] { Stat.PhysicalDamagePlusPct, Stat.FireDamagePlusPct, Stat.DamagePlusPct })
            result[name] = TypedTargetDamageStat(Get, name, ignited, bleeding, lowLife, attack);
        return result;
    }
}
