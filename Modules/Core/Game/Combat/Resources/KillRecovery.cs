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
    public readonly record struct Ailing(int IgnitedLife, int ChilledLifePct, int ShockedEnergyShield, int FrozenMana);
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
                shield ? get(Stat.GainXEnergyShieldOnKillingShockedEnemy) : 0, get(Stat.RecoverXManaOnKillingFrozenEnemy)),
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
        k = k with { Life = k.Life + (session.RecoversLife ? m.Curses[Stat.LifeGrantedWhenKilled] + (tincture?.Life ?? 0) : 0),
            Mana = k.Mana + m.Curses[Stat.ManaGrantedWhenKilled] + (tincture?.Mana ?? 0) };
        static double Amount(int flat, int pct, uint maximum) => Math.Floor(flat + maximum * (double)pct / 100);
        uint life = player.PlayerLife, mana = player.PlayerMana;
        double lifeChange = Amount(k.Life, k.LifePct, player.PlayerMaxLife);
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

    /// <summary>An ailment taken off the player at once (the effect rules' "remove"), with the helpers a flask's own
    /// removal uses; <paramref name="seconds"/> of protection after it, 0 for none.</summary>
    public static void RemoveByRuleLocked(Player player, string what, double seconds, double now)
    {
        bool all = what == "ailments", elemental = all || what == "elemental";
        if (all || what == "bleeding") player.IncomingAilments.RemoveOrProtectBleeding(now, seconds);
        if (all || what == "poison") player.IncomingAilments.RemoveOrProtectPoison(now, seconds);
        if ((all || what == "corrupted_blood") && CorruptedBloodStacks(player, now) > 0) RemoveCorruptedBlood(player, now, seconds);
        if ((elemental || what == "chill_freeze") && player.ElementalAilments.RemoveOrProtectChillFreeze(now, seconds))
        {
            if (player.FreezeHeldUntil > now) player.FreezeHeldUntil = now;
            player.ApplyMovementSpeedAt(now);
        }
        if (elemental || what == "ignite")
        {
            player.ElementalAilments.RemoveOrProtectIgnite(now, seconds);
            EndFlameTouched(player, now);
        }
        if (elemental || what == "shock") player.ElementalAilments.RemoveOrProtectShock(now, seconds);
    }

    /// <summary>Life, mana or energy shield gained or lost at once (the effect rules' "recover"): <paramref name="flat"/>
    /// plus <paramref name="percent"/> of the pool's maximum. A gain goes through the same recovery as a kill's; a
    /// loss is not damage (nothing mitigates it, and it does not interrupt the recharge) and leaves 1 life.</summary>
    public static void RecoverByRuleLocked(Player player, string pool, bool lose, double flat, double percent)
    {
        if (player.PlayerLife == 0) return;
        var session = LeechOf(player);
        double Amount(uint maximum) => Math.Max(0, Math.Floor(flat + maximum * percent / 100));
        switch (pool)
        {
            case "life":
                uint life = player.PlayerLife;
                if (lose) player.PlayerLife = (uint)Math.Max(1, life - Math.Min(life - 1, Amount(player.PlayerMaxLife)));
                else player.PlayerLife = PetrifiedBlood.RecoverLife(player, life, Amount(player.PlayerMaxLife), ref session.LifeInstantCarry, minimum: 1);
                session.InstantLifeChanged |= life != player.PlayerLife;
                break;
            case "mana":
                uint mana = player.PlayerMana;
                if (lose) player.PlayerMana = (uint)Math.Max(0, mana - Math.Min(mana, Amount(player.Pools.Mana)));
                else player.PlayerMana = RecoveryPool.Advance(mana, (uint)UnreservedMana(player), Amount(player.Pools.Mana), ref session.ManaInstantCarry);
                session.InstantManaChanged |= mana != player.PlayerMana;
                break;
            case "energy_shield":
                double carry = 0;
                if (lose) player.EnergyShield.Lose(Amount(player.EnergyShield.Maximum), ref carry);
                else player.EnergyShield.Gain(Amount(player.EnergyShield.Maximum));
                break;
        }
    }
}
