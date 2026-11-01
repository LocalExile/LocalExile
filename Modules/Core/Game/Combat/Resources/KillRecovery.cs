/// <summary>
/// Life, Mana and Energy Shield the player recovers when they kill an enemy: the flat "X Life gained on Kill" (base plus
/// per character level) and "Recover X% of Life on Kill" of the maximum pool. PoB (CalcOffence "LifeOnKill") floors the
/// sum and applies no recovery rate. The cannot-recover Life/ES flags stop their pool's share. <see cref="When"/> holds the
/// amounts that need the killed enemy's ailments, which <see cref="For"/> adds.
/// </summary>
readonly record struct KillRecovery(int Life, int LifePct, int Mana, int ManaPct, int EnergyShield, int EnergyShieldPct)
{
    // "Gain X Life per Ignited Enemy Killed", "Recover X% of Life on Killing a Chilled Enemy", "+X Energy Shield gained on
    // Killing a Shocked Enemy", "+X Mana gained on Killing a Frozen Enemy".
    public readonly record struct Ailing(int IgnitedLife, int ChilledLifePct, int ShockedEnergyShield, int FrozenMana);
    public Ailing When { get; init; }

    public static KillRecovery From(Func<string, int> get, int level)
    {
        bool life = get("cannot_recover_life") == 0, shield = get("cannot_recover_energy_shield") == 0;
        return new(
            life ? get("base_life_gained_on_enemy_death") + get("life_gained_on_enemy_death_per_level") * level : 0,
            life ? get("recover_%_maximum_life_on_kill") : 0,
            get("base_mana_gained_on_enemy_death") + get("mana_gained_on_enemy_death_per_level") * level,
            get("recover_%_maximum_mana_on_kill") + get("maximum_mana_%_gained_on_kill"),
            shield ? get("base_energy_shield_gained_on_enemy_death") + get("energy_shield_gained_on_enemy_death_per_level") * level : 0,
            shield ? get("recover_energy_shield_%_on_kill") : 0)
        {
            When = new(life ? get("life_gained_on_killing_ignited_enemies") : 0,
                life ? get("recover_%_maximum_life_on_killing_chilled_enemy") : 0,
                shield ? get("gain_X_energy_shield_on_killing_shocked_enemy") : 0, get("recover_X_mana_on_killing_frozen_enemy")),
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

static partial class Game
{
    // A hostile monster the player killed (MonsterKilled, CommitProjectileKill): its "on kill" recovery, at once. The
    // amounts follow the last player stat snapshot (ConfigureLeech), so a pack's deaths do not each rebuild the stats.
    internal static void RecoverOnKill(ClientState conn, SpawnedMonster m)
    {
        lock (conn.Monsters)
        {
            var session = LeechOf(conn);
            if (conn.PlayerLife == 0 || session.OnKill == default) return;
            // The corpse keeps its killing hit's ailments (wiki Ailment: they count as killing an affected enemy); a
            // shattered one kept only that it was frozen.
            double now = ProjectileWorldTime(conn);
            var k = session.OnKill.For(m.Ignites.Burning || m.Ignites.DiedBurning, m.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now) > 0,
                m.Ailments.ShockedAtDeath(now),
                m.Ailments.Shattered || m.Ailments.Magnitude(MonsterElementalAilments.Kind.Freeze, now) > 0);
            static double Amount(int flat, int pct, uint maximum) => Math.Max(0, Math.Floor(flat + maximum * (double)pct / 100));
            uint life = conn.PlayerLife, mana = conn.PlayerMana;
            conn.PlayerLife = RecoveryPool.Advance(life, conn.PlayerMaxLife, Amount(k.Life, k.LifePct, conn.PlayerMaxLife),
                ref session.LifeInstantCarry, minimum: 1);
            conn.PlayerMana = RecoveryPool.Advance(mana, (uint)UnreservedMana(conn), Amount(k.Mana, k.ManaPct, conn.Pools.Mana),
                ref session.ManaInstantCarry);
            conn.EnergyShield.Gain(Amount(k.EnergyShield, k.EnergyShieldPct, conn.EnergyShield.Maximum));
            // Sent with the next vitals update, as instant leech is.
            session.InstantLifeChanged |= life != conn.PlayerLife;
            session.InstantManaChanged |= mana != conn.PlayerMana;
        }
    }
}
