using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class RecoveryKeystoneTests
{
    [Fact]
    public void Eternal_Youth_halves_regeneration_and_leech_and_recharges_life_after_life_damage()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        bool allocated = false;
        using var source = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (!ReferenceEquals(owner, player)) return stats;
            stats[Stat.BaseLifeRegenerationRatePerMinute] = 600;
            if (allocated) stats[Stat.KeystoneEternalYouth] = 1;
            return stats;
        });
        Game.CachedPlayerStats(player, out double unmodified);
        Assert.Equal(10, unmodified, 6);
        allocated = true;
        var values = Game.CachedPlayerStats(player, out double regeneration);
        int Get(uint key) => values.GetValueOrDefault(key);
        Assert.Equal(5, regeneration, 6);
        Assert.Equal(0, EnergyShield.RechargePerMinute(300, Get), 6);
        var leech = LeechLimits.Resolve(Get, 1000, 600, 300);
        Assert.Equal(100, leech.Resources[0].MaximumPerSecond, 6);
        Assert.Equal(120, leech.Resources[1].MaximumPerSecond, 6);

        player.EnergyShield.SetMaximum(300, refill: true);
        Assert.Equal(0, Game.DamagePlayerHitTyped(player, 60, 0, 0, 1, values, DamagePacket.Of(DamageType.Physical, 60, 60)));
        double rate = player.PlayerMaxLife / 3d;
        Assert.Equal(rate, Game.AdvanceKeystoneRecovery(player, 1, 2, true, Get), 6);
        Assert.Equal(60, Game.DamagePlayerHitTyped(player, 0, 0, 60, 2, values, DamagePacket.Of(DamageType.Chaos, 60, 60)));
        Assert.Equal(0, Game.AdvanceKeystoneRecovery(player, 2, 4, true, Get), 6);
        Assert.Equal(rate, Game.AdvanceKeystoneRecovery(player, 4, 5, true, Get), 6);
    }

    [Fact]
    public void Immortal_Ambition_starts_empty_loses_shield_and_routes_life_leech_into_the_shared_shield_cap()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        using var source = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (!ReferenceEquals(owner, player)) return stats;
            stats[Stat.KeystoneSoulTether] = 1;
            stats[Stat.BaseEnergyShieldRegenerationRatePerMinute] = 6000;
            return stats;
        });
        player.Pools = player.Pools with { EnergyShield = 300 };
        Game.InitializeCharacterVitals(player);
        Assert.Equal(300u, player.EnergyShield.Maximum);
        Assert.Equal(0u, player.EnergyShield.Current);

        var values = Game.CachedPlayerStats(player, out _);
        int Get(uint key) => values.GetValueOrDefault(key);
        player.EnergyShield.Recover(300);
        Assert.True(player.EnergyShield.Spend(150));
        Game.AdvanceKeystoneRecovery(player, 0, 1, false, Get);
        player.EnergyShield.Advance(0, 1, Get, true);
        Assert.Equal(135u, player.EnergyShield.Current);
        Assert.False(player.EnergyShield.Recharging);

        var leech = new LeechSession();
        leech.Configure(0, LeechLimits.Resolve(Get, 1000, 400, 300), Get);
        leech.Hit(0, (100, 0, 30), lifeFull: true, manaFull: true, shieldFull: false);
        leech.Hit(0, (100, 0, 30), lifeFull: true, manaFull: true, shieldFull: false);
        var full = leech.TakeRecovery(1);
        Assert.Equal(0, full.Life, 6);
        Assert.Equal(30, full.EnergyShield, 6);
        leech.ClearFull(1, life: false, mana: true, shield: false);
        var injured = leech.TakeRecovery(2);
        Assert.Equal(40, injured.Life, 6);
        Assert.Equal(12, injured.EnergyShield, 6);
    }
}
