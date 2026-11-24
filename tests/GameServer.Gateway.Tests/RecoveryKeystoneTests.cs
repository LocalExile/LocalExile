using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class RecoveryKeystoneTests
{
    [Fact]
    public void Eternal_Youth_halves_regeneration_and_leech_and_recharges_life_after_life_damage()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        bool allocated = false, zealotsOath = false, wickedWard = false;
        int mapRecoveryPct = 0, mapNoRegen = 0;
        using var source = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (!ReferenceEquals(owner, player)) return stats;
            stats[Stat.BaseLifeRegenerationRatePerMinute] = 600;
            if (allocated) stats[Stat.KeystoneEternalYouth] = 1;
            if (zealotsOath) stats[Stat.KeystoneZealotsOath] = 1;
            if (wickedWard) stats[Stat.KeystoneWickedWard] = 1;
            if (mapRecoveryPct != 0) stats[Stat.MapPlayerLifeAndEsRecoverySpeedPlusPctFinal] = mapRecoveryPct;
            if (mapNoRegen != 0) stats[Stat.MapPlayersNoRegenerationIncludingEs] = mapNoRegen;
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

        // Zealot's Oath converts Life Regeneration to Energy Shield without Eternal Youth halving it.
        zealotsOath = true;
        var oathValues = Game.CachedPlayerStats(player, out double oathRegen);
        Assert.Equal(10, oathRegen, 6);
        Assert.Equal(600, oathValues.GetValueOrDefault(Stat.TotalEnergyShieldRecoveryPerMinuteFromRegeneration));
        Assert.Equal(0, oathValues.GetValueOrDefault(Stat.TotalLifeRecoveryPerMinuteFromRegeneration));
        zealotsOath = false;

        player.EnergyShield.SetMaximum(300, refill: true);
        Assert.Equal(0, Game.DamagePlayerHitTyped(player, 60, 0, 0, 1, values, DamagePacket.Of(DamageType.Physical, 60, 60)));
        double rate = player.PlayerMaxLife / 3d;
        Assert.Equal(rate, Game.AdvanceKeystoneRecovery(player, 1, 2, true, Get), 6);
        Assert.Equal(60, Game.DamagePlayerHitTyped(player, 0, 0, 60, 2, values, DamagePacket.Of(DamageType.Chaos, 60, 60)));
        Assert.Equal(0, Game.AdvanceKeystoneRecovery(player, 2, 4, true, Get), 6);
        Assert.Equal(rate, Game.AdvanceKeystoneRecovery(player, 4, 5, true, Get), 6);

        // Wicked Ward scales Eternal Youth Life Recharge by 0.6x and protects it from interruption for 4s after start (t=4..8).
        wickedWard = true;
        var wickedValues = Game.CachedPlayerStats(player, out _);
        int GetWicked(uint key) => wickedValues.GetValueOrDefault(key);
        Assert.Equal(rate * 0.6, Game.AdvanceKeystoneRecovery(player, 5, 6, true, GetWicked), 6);
        Assert.Equal(60, Game.DamagePlayerHitTyped(player, 0, 0, 60, 6, wickedValues, DamagePacket.Of(DamageType.Chaos, 60, 60)));
        Assert.Equal(rate * 0.6, Game.AdvanceKeystoneRecovery(player, 6, 7, true, GetWicked), 6);
        Assert.Equal(60, Game.DamagePlayerHitTyped(player, 0, 0, 60, 8, wickedValues, DamagePacket.Of(DamageType.Chaos, 60, 60)));
        Assert.Equal(0, Game.AdvanceKeystoneRecovery(player, 8, 9, true, GetWicked), 6);
        wickedWard = false;

        // Map less recovery rate scales Eternal Youth Life Recharge once; map no-regeneration does not block recharge.
        mapRecoveryPct = -40;
        mapNoRegen = 1;
        var mapValues = Game.CachedPlayerStats(player, out double mapRegen);
        int GetMap(uint key) => mapValues.GetValueOrDefault(key);
        Assert.Equal(0, mapRegen, 6);
        Assert.Equal(rate * 0.6, Game.AdvanceKeystoneRecovery(player, 10, 11, true, GetMap), 6);
        // Without Eternal Youth, ES Recharge applies Wicked Ward (0.6x) and map less recovery rate (0.6x) once each.
        allocated = false;
        wickedWard = true;
        var esValues = Game.CachedPlayerStats(player, out _);
        Assert.Equal(300 * 20.0 * 0.6 * 0.6, EnergyShield.RechargePerMinute(300, key => esValues.GetValueOrDefault(key)), 6);
    }

    [Fact]
    public void Immortal_Ambition_starts_empty_loses_shield_and_routes_life_leech_into_the_shared_shield_cap()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        bool soulTether = true;
        using var source = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (!ReferenceEquals(owner, player)) return stats;
            if (soulTether) stats[Stat.KeystoneSoulTether] = 1;
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
        // Level-up / refill does not snap Energy Shield to full under Immortal Ambition.
        Game.RefillPlayerEnergyShield(player);
        Assert.Equal(135u, player.EnergyShield.Current);

        var leech = new LeechSession();
        int displayChanges = 0;
        leech.DisplayChanged = () => displayChanges++;
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

        // Transitioning to Full Life with Life-only leech activates IsEsLeeching and fires DisplayChanged;
        // unequipping Soul Tether clears pendingShieldRecovery.
        leech.Clear(2);
        leech.Hit(2, (100, 0, 0), lifeFull: false, manaFull: true, shieldFull: false);
        Assert.True(leech.Active(2).Life);
        Assert.False(leech.Active(2).EnergyShield);
        int beforeFull = displayChanges;
        leech.ClearFull(2.5, life: true, mana: true, shield: false);
        Assert.True(displayChanges > beforeFull);
        Assert.True(leech.Active(2.5).EnergyShield);
        int beforeFullShield = displayChanges;
        leech.ClearFull(2.6, life: true, mana: true, shield: true);
        Assert.True(displayChanges > beforeFullShield);
        Assert.False(leech.Active(2.6).EnergyShield);

        leech.ClearFull(2.7, life: true, mana: true, shield: false);
        Assert.True(leech.Active(3.2).EnergyShield);
        soulTether = false;
        var plainValues = Game.CachedPlayerStats(player, out _);
        int GetPlain(uint key) => plainValues.GetValueOrDefault(key);
        leech.Configure(3.2, LeechLimits.Resolve(GetPlain, 1000, 400, 300), GetPlain);
        Assert.Equal(0, leech.TakeRecovery(3.2).EnergyShield, 6);
    }

    [Fact]
    public void Full_life_preserves_vampiric_link_leech_with_immortal_ambition()
    {
        var leech = new LeechSession();
        int Get(uint key) => key == Stat.KeystoneSoulTether ? 1 : 0;
        leech.Configure(0, LeechLimits.Resolve(Get, 1000, 400, 300), Get);
        leech.SetLifeRecoveryRedirected(0, true);
        leech.Hit(0, (100, 0, 0), lifeFull: true, manaFull: true, shieldFull: false);

        leech.ClearFull(1, life: true, mana: true, shield: false);
        var recovery = leech.TakeRecovery(1);

        Assert.Equal(20, recovery.Life, 6);
        Assert.Equal(0, recovery.EnergyShield, 6);
    }
}
