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
        public int CursedLifePct { get; init; }
        public int CursedEnergyShieldPct { get; init; }
        public int LifePctPerWither { get; init; }
    }
    public Ailing When { get; init; }

    public static KillRecovery From(Func<uint, int> get, int level)
    {
        bool life = !LifeRecovery.NonLeechBlocked(get), shield = get(Stat.CannotRecoverEnergyShield) == 0;
        int flatLife = ActorScale.Saturate((long)get(Stat.BaseLifeGainedOnEnemyDeath) + (long)get(Stat.LifeGainedOnEnemyDeathPerLevel) * level);
        return new(
            life ? flatLife : Math.Min(0, flatLife),
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
                CursedLifePct = life ? get(Stat.RecoverPctMaximumLifeOnKillingCursedEnemy) : 0,
                CursedEnergyShieldPct = shield ? get(Stat.RecoverPctMaximumEnergyShieldOnKillingCursedEnemy) : 0,
                LifePctPerWither = life ? get(Stat.LifeGainedOnKillPerWitherStackOnSlainEnemyPct) : 0,
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
    public static void RecoverOnKill(Player player, SpawnedMonster m)
    {
        if (RecoverOnKill(player, m, null) is int deathEpoch)
            _ = AreaFrames.Start(() => PlayerDiedAtEpoch(player, "death from life lost on kill", deathEpoch));
    }

    static int? RecoverOnKill(Player player, SpawnedMonster m, TinctureKillBonus? tincture)
    {
        var session = LeechOf(player);
        if (player.PlayerLife == 0) return null;
        // The corpse keeps its killing hit's ailments (wiki Ailment: they count as killing an affected enemy); a
        // shattered one kept only that it was frozen.
        double now = ProjectileWorldTime(player);
        var k = session.OnKill.For(m.Ignites.Burning || m.Ignites.DiedBurning, m.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now) > 0,
            m.Ailments.ShockedAtDeath(now), m.Ailments.FrozenAtDeath(now));
        var conditions = session.OnKill.When;
        if (m.CursedAtDeath)
            k = k with
            {
                LifePct = ActorScale.Saturate((long)k.LifePct + conditions.CursedLifePct),
                EnergyShieldPct = ActorScale.Saturate((long)k.EnergyShieldPct + conditions.CursedEnergyShieldPct),
            };
        int poisonStacks = conditions.PoisonedLifePct != 0 || conditions.LifePermyriadPerPoison != 0
            ? m.Poisons.StacksAtDeath(now) : 0;
        if (poisonStacks > 0)
            k = k with { LifePct = ActorScale.Saturate((long)k.LifePct + conditions.PoisonedLifePct) };
        if (conditions.LifePctPerWither != 0 && m.WitherStacksAtDeath > 0)
            k = k with { LifePct = ActorScale.Saturate((long)k.LifePct + (long)m.WitherStacksAtDeath * conditions.LifePctPerWither) };
        k = k with { Life = k.Life + (session.RecoversLife ? m.Curses[Stat.LifeGrantedWhenKilled] + (tincture?.Life ?? 0) : 0),
            Mana = k.Mana + m.Curses[Stat.ManaGrantedWhenKilled] + (tincture?.Mana ?? 0) };
        static double Amount(int flat, int pct, uint maximum) => Math.Floor(flat + maximum * (double)pct / 100);
        uint life = player.PlayerLife, mana = player.PlayerMana;
        double lifeChange = conditions.LifePermyriadPerPoison == 0 || poisonStacks == 0
            ? Amount(k.Life, k.LifePct, player.PlayerMaxLife)
            : (double)decimal.Floor(k.Life + player.PlayerMaxLife
                * (k.LifePct * 100m + poisonStacks * (decimal)conditions.LifePermyriadPerPoison) / 10_000m);
        player.PlayerLife = lifeChange < 0 ? VaalImmortalCallLife(player, (uint)Math.Max(0, life + lifeChange), now)
            : PetrifiedBlood.RecoverLife(player, life, lifeChange, ref session.LifeInstantCarry, minimum: 1);
        // Sent with the next vitals update, as instant leech is.
        session.InstantLifeChanged |= life != player.PlayerLife;
        if (player.PlayerLife == 0)
        {
            player.InvalidateIncomingHits();
            ClearWardBreakOnDeath(player);
            RecordHardcoreDeath(player);
            return player.IncomingHitEpoch;
        }
        player.PlayerMana = RecoveryPool.Advance(mana, (uint)UnreservedMana(player), Math.Max(0, Amount(k.Mana, k.ManaPct, player.Pools.Mana)),
            ref session.ManaInstantCarry);
        player.EnergyShield.Gain(Math.Max(0, Amount(k.EnergyShield, k.EnergyShieldPct, player.EnergyShield.Maximum)));
        session.InstantManaChanged |= mana != player.PlayerMana;
        return null;
    }
}
