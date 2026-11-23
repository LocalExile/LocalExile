using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SlayerNodesTests
{
    static Func<uint, int> With(params (uint Name, int Value)[] stats)
    {
        var values = stats.ToDictionary(s => s.Name, s => s.Value);
        return name => values.GetValueOrDefault(name);
    }

    static Dictionary<uint, int> Keyed(params (uint Name, int Value)[] stats) =>
        stats.ToDictionary(s => s.Name, s => s.Value);

    // The Slayer's ordinary nodes with a consumer of their own (Agents/docs/game/slayer.md), by the values PassiveSkills
    // gives them: Overwhelm +10 per nearby enemy up to +100 and the client's -30 aura on monsters, Impact 5% area per kill
    // up to 50%, Bane of Legends' 20% and 10% more, Endless Hunger's 20% overkill leech, attack speed and defences while
    // leeching, Brutal Fervour's 10% reduced damage taken while leeching and Masterful Form.
    [Fact]
    public void Slayer_nodes_follow_their_stats_and_the_clients_caps()
    {
        // Overwhelm: min(100, X x nearby), unbounded below (client 0x14276e1f0).
        Assert.Equal(0, Slayer.CritMultiplierFromNearby(With((Slayer.CritPerNearbyEnemy, 10))));
        Assert.Equal(70, Slayer.CritMultiplierFromNearby(With((Slayer.CritPerNearbyEnemy, 10), (Slayer.NearbyEnemies, 7))));
        Assert.Equal(100, Slayer.CritMultiplierFromNearby(With((Slayer.CritPerNearbyEnemy, 10), (Slayer.NearbyEnemies, 15))));
        Assert.Equal(-30, Slayer.CritMultiplierFromNearby(With((Slayer.CritPerNearbyEnemy, -10), (Slayer.NearbyEnemies, 3))));
        // ... and it reaches both the attack and the spell multiplier of the pools.
        var bonus = Game.PassiveBonus(Keyed((Slayer.CritPerNearbyEnemy, 10), (Slayer.NearbyEnemies, 4)), new WeaponProfile(),
            AttributeModifiers.FromWide(_ => 0));
        Assert.Equal(40, bonus.CriticalStrikeMultiplier);
        Assert.Equal(40, bonus.SpellCriticalStrikeMultiplier);

        // Impact: min(50, X x killed recently) (client 0x14274c820).
        Assert.Equal(15, Slayer.AreaFromKills(With((Slayer.AreaPerEnemyKilled, 5), (Slayer.EnemiesKilledRecently, 3))));
        Assert.Equal(50, Slayer.AreaFromKills(With((Slayer.AreaPerEnemyKilled, 5), (Slayer.EnemiesKilledRecently, 40))));
        // The tick's counts stop where the capped products do, so a count past the cap resends nothing.
        Assert.Equal(10, Slayer.UpToCap(15, 10, 100));
        Assert.Equal(7, Slayer.UpToCap(7, 10, 100));
        Assert.Equal(17, Slayer.UpToCap(40, 3, 50));
        var state = new Slayer.State();
        foreach (double at in new[] { 0.0, 1, 3.9 }) state.Kills.Enqueue(at);
        Assert.Equal(3, state.KilledWithin(3.95));
        Assert.Equal(2, state.KilledWithin(4));   // "recently" is four seconds

        // Bane of Legends: 20% more against a unique, 10% more after a kill, each its own multiplier.
        var bane = With((Slayer.MoreVsUnique, 20), (Slayer.MoreIfKilledRecently, 10), (Slayer.HaveKilledRecently, 1));
        Assert.Equal(1.32, Slayer.DamageMore(bane, uniqueTarget: true), 9);
        Assert.Equal(1.1, Slayer.DamageMore(bane, uniqueTarget: false), 9);
        Assert.Equal(1, Slayer.DamageMore(With((Slayer.MoreIfKilledRecently, 10)), uniqueTarget: false));

        // Endless Hunger: 20% of what the hit deals beyond the target's Life, nothing for a hit that does not kill.
        var leech = LeechSources.From(With((Stat.LifeLeechOnOverkillDamagePct, 20)), attack: true);
        Assert.Equal(100, leech.OverkillLife(1000, 500), 9);
        Assert.Equal(0, leech.OverkillLife(400, 500));

        // While leeching: 10% reduced damage taken, cannot be stunned, unaffected by bleeding; nothing otherwise.
        var taken = With((Stat.DamageTakenPlusPctWhileLeeching, -10), (Stat.BaseDamageTakenPlusPct, 5));
        Assert.Equal(5, PlayerDamageTaken.IncreasedPct(DamageType.Physical, false, taken));
        Assert.Equal(-5, PlayerDamageTaken.IncreasedPct(DamageType.Physical, false,
            With((Stat.DamageTakenPlusPctWhileLeeching, -10), (Stat.BaseDamageTakenPlusPct, 5), (Stat.IsLeeching, 1))));
        foreach (bool leeching in new[] { false, true })
        {
            var stats = Keyed((Stat.CannotBeStunnedWhileLeeching, 1), (Stat.UnaffectedByBleedingWhileLeeching, 1),
                (Stat.IsLeeching, leeching ? 1 : 0));
            Game.WithLeechingDefences(new PlainStatMap(stats));
            Assert.Equal(leeching ? 1 : 0, stats.GetValueOrDefault(Stat.CannotBeStunned));
            Assert.Equal(leeching ? 1 : 0, stats[Stat.VirtualUnaffectedByBleeding]);
            // Endless Hunger's flag no longer turns the stun check off: the player is stunned only when not leeching.
            Assert.DoesNotContain(stats, s => s.Value != 0 && PlayerHitRecovery.UnsupportedStat(s.Key));
            double chance = PlayerHitRecovery.Chance(1000, 3000,
                name => stats.GetValueOrDefault(name));
            Assert.Equal(leeching, chance == 0);
        }

        // Masterful Form: the endurance maximum is the frenzy one (3 + 1), whatever its own stats say.
        var charges = ChargePanelStats.Compute(With((Stat.MaxFrenzyCharges, 1), (Stat.MaxEnduranceCharges, 2),
            (ChargePanelStats.EnduranceEqualsFrenzy, 1)));
        Assert.Equal(4, charges.Single(c => c.Stat == Stat.VirtualMaximumEnduranceCharges).Value);
        Assert.Equal(5, ChargePanelStats.Compute(With((Stat.MaxEnduranceCharges, 2)))
            .Single(c => c.Stat == Stat.VirtualMaximumEnduranceCharges).Value);
    }
}
