/// <summary>
/// Life, Mana and Energy Shield the player recovers when they kill an enemy: the flat "X Life gained on Kill" (base plus
/// per character level) and "Recover X% of Life on Kill" of the maximum pool. PoB (CalcOffence "LifeOnKill") floors the
/// sum and applies no recovery rate. The cannot-recover Life/ES flags stop their pool's share. <see cref="When"/> holds the
/// amounts that need the killed enemy's ailments, which <see cref="For"/> adds.
/// </summary>
public readonly record struct KillRecovery(int Life, int LifePct, int Mana, int ManaPct, int EnergyShield, int EnergyShieldPct)
{
    // "Gain X Life per Ignited Enemy Killed", "Recover X% of Life on Killing a Chilled Enemy", "+X Energy Shield gained on
    // Killing a Shocked Enemy", "+X Mana gained on Killing a Frozen Enemy".
    public readonly record struct Ailing(int IgnitedLife, int ChilledLifePct, int ShockedEnergyShield, int FrozenMana)
    {
        public int PoisonedLifePct { get; init; }
        public int LifePermyriadPerPoison { get; init; }
    }
    public Ailing When { get; init; }

    public static KillRecovery From(Func<uint, int> get, int level)
    {
        bool life = !LifeRecovery.NonLeechBlocked(get), shield = get(Stat.CannotRecoverEnergyShield) == 0;
        return new(
            life ? get(Stat.BaseLifeGainedOnEnemyDeath) + get(Stat.LifeGainedOnEnemyDeathPerLevel) * level : 0,
            life ? get(Stat.RecoverPctMaximumLifeOnKill) : 0,
            get(Stat.BaseManaGainedOnEnemyDeath) + get(Stat.ManaGainedOnEnemyDeathPerLevel) * level,
            get(Stat.RecoverPctMaximumManaOnKill) + get(Stat.MaximumManaPctGainedOnKill),
            shield ? get(Stat.BaseEnergyShieldGainedOnEnemyDeath) + get(Stat.EnergyShieldGainedOnEnemyDeathPerLevel) * level : 0,
            shield ? get(Stat.RecoverEnergyShieldPctOnKill) : 0)
        {
            When = new(life ? get(Stat.LifeGainedOnKillingIgnitedEnemies) : 0,
                life ? get(Stat.RecoverPctMaximumLifeOnKillingChilledEnemy) : 0,
                shield ? get(Stat.GainXEnergyShieldOnKillingShockedEnemy) : 0, get(Stat.RecoverXManaOnKillingFrozenEnemy))
            {
                PoisonedLifePct = life ? get(Stat.RecoverPctMaximumLifeOnKillingPoisonedEnemy) : 0,
                LifePermyriadPerPoison = life ? get(Stat.RecoverPermyriadMaximumLifePerPoisonOnEnemyOnKill) : 0,
            },
        };
    }

    // What a kill of an enemy in this state recovers.
    public KillRecovery For(bool ignited, bool chilled, bool shocked, bool frozen) => this with
    {
        Life = Life + (ignited ? When.IgnitedLife : 0), LifePct = LifePct + (chilled ? When.ChilledLifePct : 0),
        EnergyShield = EnergyShield + (shocked ? When.ShockedEnergyShield : 0), Mana = Mana + (frozen ? When.FrozenMana : 0),
        When = default,
    };
}

public static partial class Game
{
    // A hostile monster the player killed (MonsterKilled, CommitProjectileKill): its "on kill" recovery, at once. The
    // amounts follow the last player stat snapshot (ConfigureLeech), so a pack's deaths do not each rebuild the stats.
    public static void RecoverOnKill(Player player, SpawnedMonster m) => RecoverOnKill(player, m, null);

    static void RecoverOnKill(Player player, SpawnedMonster m, TinctureKillBonus? tincture)
    {
        var session = LeechOf(player);
        if (player.PlayerLife == 0) return;
        // The corpse keeps its killing hit's ailments (wiki Ailment: they count as killing an affected enemy); a
        // shattered one kept only that it was frozen.
        double now = ProjectileWorldTime(player);
        var k = session.OnKill.For(m.Ignites.Burning || m.Ignites.DiedBurning, m.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now) > 0,
            m.Ailments.ShockedAtDeath(now), m.Ailments.FrozenAtDeath(now));
        var poisonRecovery = session.OnKill.When;
        int poisonStacks = poisonRecovery.PoisonedLifePct != 0 || poisonRecovery.LifePermyriadPerPoison != 0
            ? m.Poisons.StacksAtDeath(now) : 0;
        if (poisonStacks > 0)
            k = k with { LifePct = ActorScale.Saturate((long)k.LifePct + poisonRecovery.PoisonedLifePct) };
        k = k with { Life = k.Life + (session.RecoversLife ? m.Curses[Stat.LifeGrantedWhenKilled] + (tincture?.Life ?? 0) : 0),
            Mana = k.Mana + m.Curses[Stat.ManaGrantedWhenKilled] + (tincture?.Mana ?? 0) };
        static double Amount(int flat, int pct, uint maximum) => Math.Max(0, Math.Floor(flat + maximum * (double)pct / 100));
        uint life = player.PlayerLife, mana = player.PlayerMana;
        double lifeRecovery = poisonRecovery.LifePermyriadPerPoison == 0 || poisonStacks == 0
            ? Amount(k.Life, k.LifePct, player.PlayerMaxLife)
            : (double)Math.Max(0m, decimal.Floor(k.Life + player.PlayerMaxLife
                * (k.LifePct * 100m + poisonStacks * (decimal)poisonRecovery.LifePermyriadPerPoison) / 10_000m));
        player.PlayerLife = PetrifiedBlood.RecoverLife(player, life, lifeRecovery, ref session.LifeInstantCarry, minimum: 1);
        player.PlayerMana = RecoveryPool.Advance(mana, (uint)UnreservedMana(player), Amount(k.Mana, k.ManaPct, player.Pools.Mana),
            ref session.ManaInstantCarry);
        player.EnergyShield.Gain(Amount(k.EnergyShield, k.EnergyShieldPct, player.EnergyShield.Maximum));
        // Sent with the next vitals update, as instant leech is.
        session.InstantLifeChanged |= life != player.PlayerLife;
        session.InstantManaChanged |= mana != player.PlayerMana;
    }
}
