using GameServer.Core.Protocol.Instance;
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

    [Fact]
    public void Zealots_oath_and_pious_path_convert_and_mirror_life_regeneration_to_energy_shield()
    {
        // Sorrow of the Divine's local flask mod grants RegenerateEnergyShieldInsteadOfLife during flask effect.
        var flaskLocal = new Dictionary<uint, int> { [Stat.LocalFlaskZealotsOath] = 1 };
        var during = UtilityFlasks.DuringEffectSources(flaskLocal, 1.5);
        Assert.Contains(during, s => s is { Stat: Stat.RegenerateEnergyShieldInsteadOfLife, Value: 1 });
        Assert.Contains(UtilityFlasks.RawStats(UtilityFlasks.Strongest(during), Program.Stats),
            kv => kv.Key == Stat.RegenerateEnergyShieldInsteadOfLife && kv.Value == 1);

        // Zeel's Amplifier grants KeystoneZealotsOath only while not hit recently.
        var zeel = ItemCatalog.Create(Program.Items.Find("Polished Spiked Shield")!) with
        {
            Rarity = ItemRarity.Unique,
            Mods = [new(ModSection.Explicit, Program.Items.Mods.ById("ZealotsOathIfHaventBeenHitRecentlyUnique__1")!.Hash, [1])],
        };
        var unhitStats = ItemModStats.Global([zeel], Program.Items.Mods, Program.Stats,
            new(Recent: new HashSet<string>(), KnownRecent: new HashSet<string>([StatConditions.BeenHit])));
        Assert.Equal(1, unhitStats.GetValueOrDefault(Stat.KeystoneZealotsOath));
        var hitStats = ItemModStats.Global([zeel], Program.Items.Mods, Program.Stats,
            new(Recent: new HashSet<string>([StatConditions.BeenHit]), KnownRecent: new HashSet<string>([StatConditions.BeenHit])));
        Assert.Equal(0, hitStats.GetValueOrDefault(Stat.KeystoneZealotsOath));

        {
            using var f = new TriggeredSummonTests.Fixture();
            var player = f.Player;
            var custom = new Dictionary<uint, int>();
            using var source = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
            {
                if (!ReferenceEquals(owner, player)) return stats;
                foreach (var (k, v) in custom) stats[k] = v;
                return stats;
            });
            player.Pools = player.Pools with { Life = 2000, EnergyShield = 400 };
            Game.InitializeCharacterVitals(player);

            custom[Stat.BaseLifeRegenerationRatePerMinute] = 600;
            custom[Stat.LifeRegenerationRatePerMinutePct] = 300;
            custom[Stat.LifeRegenerationRatePlusPct] = 100;
            custom[Stat.LifeRecoveryRatePlusPct] = 100;
            custom[Stat.EnergyShieldRegenerationRatePlusPct] = 50;
            custom[Stat.EnergyShieldRecoveryRatePlusPct] = 20;
            custom[Stat.MapPlayerLifeAndEsRecoverySpeedPlusPctFinal] = -10;
            custom[Stat.RegenerateEnergyShieldInsteadOfLife] = 1;
            custom[Stat.KeystoneEternalYouth] = 1;
            custom[Stat.KeystoneVaalPact] = 1;

            // Flat (600) + 5%/s of 400 max ES (1200) = 1800 base/min, scaled by ES multipliers (1.5 * 1.2 * 0.9 = 1.62) = 2916/min.
            // Life regen is 0; Life rates and Eternal Youth do not apply; Vaal Pact leaves converted ES regen intact.
            var values = Game.CharacterPlayerStatsCore(player, out double lifeRate, out double esRate);
            Assert.Equal(2916, values.GetValueOrDefault(Stat.TotalEnergyShieldRecoveryPerMinuteFromRegeneration));
            Assert.Equal(2916, values.GetValueOrDefault(Stat.EnergyShieldRecoveryPerMinute));
            Assert.Equal(0, values.GetValueOrDefault(Stat.TotalLifeRecoveryPerMinuteFromRegeneration));
            Assert.Equal(0, values.GetValueOrDefault(Stat.LifeRecoveryPerMinute));
            Assert.Equal(2916d / 60, lifeRate, 6);
            Assert.Equal(2916d / 60, esRate, 6);

            // Untiring's flat life regen scales by ES multiplier under Zealot's Oath.
            custom[Stat.GainPctTotalPhysDamagePreventedInThePast10SecAsLifeRegenPerSec] = 1000;
            var untiring = Game.CachedPlayerStats(player, out _);
            Assert.Equal(1.62, PreventedDamageRecovery.PerSecond(untiring.GetValueOrDefault), 6);
            custom.Remove(Stat.GainPctTotalPhysDamagePreventedInThePast10SecAsLifeRegenPerSec);

            // NoEnergyShieldRegeneration / NoLifeRegeneration drop converted ES regen to 0.
            foreach (uint block in new[] { Stat.CannotRegenerateEnergyShield, Stat.NoEnergyShieldRechargeOrRegeneration, Stat.NoLifeRegeneration })
            {
                custom[block] = 1;
                var blocked = Game.CachedPlayerStats(player, out _);
                Assert.Equal(0, blocked.GetValueOrDefault(Stat.TotalEnergyShieldRecoveryPerMinuteFromRegeneration));
                custom.Remove(block);
            }

            // Without Zealot's Oath, x%_life_regeneration_applies_to_energy_shield applies 20% to ES (maxES + ES multipliers)
            // while keeping Life regen (maxLife + Life multipliers, blocked from Life display by Vaal Pact).
            custom.Remove(Stat.RegenerateEnergyShieldInsteadOfLife);
            custom[Stat.KeystoneEternalYouth] = 0;
            custom[Stat.XPctLifeRegenerationAppliesToEnergyShield] = 20;
            var split = Game.CachedPlayerStats(player, out double splitLifeRate);
            // Life: (600 + 2000 * 3) * 2.0 * 2.0 * 0.9 = 6600 * 3.6 = 23760/min -> 396/s.
            Assert.Equal(396, splitLifeRate, 6);
            Assert.Equal(0, split.GetValueOrDefault(Stat.LifeRecoveryPerMinute));
            // ES: (600*0.2 + 400*0.6) * 1.62 = 360 * 1.62 = 583.2 -> 583/min.
            Assert.Equal(583, split.GetValueOrDefault(Stat.TotalEnergyShieldRecoveryPerMinuteFromRegeneration));

            // Periodic 4s % life regen (Time of Need / Hunter / Life Mastery) uses maxES * ES multiplier under Zealot's Oath.
            Assert.Equal(1, Game.PeriodicRegenSeconds(5) - Game.PeriodicRegenSeconds(4));
            Assert.Equal(1.62, EnergyShield.RegenerationMultiplier(values.GetValueOrDefault), 6);
        }

        // Pious Path mirrors active Life regeneration to Energy Shield on Consecrated Ground and stops under Zealot's Oath.
        {
            using var f = new TriggeredSummonTests.Fixture();
            var player = f.Player;
            var custom = new Dictionary<uint, int>();
            using var source = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
            {
                if (!ReferenceEquals(owner, player)) return stats;
                foreach (var (k, v) in custom) stats[k] = v;
                return stats;
            });
            player.Pools = player.Pools with { Life = 2000, EnergyShield = 400 };
            Game.InitializeCharacterVitals(player);

            custom[Stat.BaseLifeRegenerationRatePerMinute] = 600;
            custom[Stat.LifeRegenerationRatePerMinutePct] = 300;
            custom[Stat.LifeRegenerationRatePlusPct] = 50;
            custom[Stat.LifeRecoveryRatePlusPct] = 40;
            custom[Stat.EnergyShieldRegenerationRatePlusPct] = 80;
            custom[Stat.EnergyShieldRecoveryRatePlusPct] = 20;
            custom[Stat.MapPlayerLifeAndEsRecoverySpeedPlusPctFinal] = -10;
            custom[Stat.ConsecratedGroundAlliesRecoverEsAsWellAsLifeFromLifeRegeneration] = 1;
            custom[Stat.YourConsecratedGroundEffectLingersForMsAfterLeavingTheArea] = 4000;

            // Off Consecrated Ground: Pious Path is inactive.
            var offGround = Game.CachedPlayerStats(player, out _);
            Assert.Equal(0, offGround.GetValueOrDefault(Stat.TotalEnergyShieldRecoveryPerMinuteFromRegeneration));

            // Standing on Consecrated Ground (adds 300%/min = 5%/s life regen + activates Pious Path via PlayerStatSteps):
            // Base life = 600 + 2000 * (300 + 300)/100 = 12600/min; * 1.5 LifeRegenRate = 18900/min before recovery rate.
            // Life = 18900 * 1.4 * 0.9 = 23814/min; Pious Path ES = 18900 * 1.2 * 0.9 = 20412/min (ignores ES regen rate 80%).
            player.FlaskGrounds.Configure(0, 4000);
            player.FlaskGrounds.SetConsecrated(true, () => 1);
            var onGround = Game.CachedPlayerStats(player, out _);
            Assert.Equal(1, onGround.GetValueOrDefault(Stat.RecoverEsAsWellAsLifeFromLifeRegeneration));
            Assert.Equal(23814, onGround.GetValueOrDefault(Stat.TotalLifeRecoveryPerMinuteFromRegeneration));
            Assert.Equal(20412, onGround.GetValueOrDefault(Stat.TotalEnergyShieldRecoveryPerMinuteFromRegeneration));

            // Eternal Youth halves active Life regeneration and thus halves Pious Path's ES mirror.
            custom[Stat.KeystoneEternalYouth] = 1;
            var youth = Game.CachedPlayerStats(player, out _);
            Assert.Equal(11907, youth.GetValueOrDefault(Stat.TotalLifeRecoveryPerMinuteFromRegeneration));
            Assert.Equal(10206, youth.GetValueOrDefault(Stat.TotalEnergyShieldRecoveryPerMinuteFromRegeneration));
            custom.Remove(Stat.KeystoneEternalYouth);

            // CannotRegenerateEnergyShield stops Pious Path's ES recovery while Life regeneration keeps running.
            custom[Stat.CannotRegenerateEnergyShield] = 1;
            var blockedEs = Game.CachedPlayerStats(player, out _);
            Assert.Equal(23814, blockedEs.GetValueOrDefault(Stat.TotalLifeRecoveryPerMinuteFromRegeneration));
            Assert.Equal(0, blockedEs.GetValueOrDefault(Stat.TotalEnergyShieldRecoveryPerMinuteFromRegeneration));
            custom.Remove(Stat.CannotRegenerateEnergyShield);

            // When Zealot's Oath is also active, active Life regeneration becomes 0 so Pious Path stops mirroring to ES;
            // only Zealot's Oath's ES conversion remains: (600 + 400 * 6) * 1.8 * 1.2 * 0.9 = 3000 * 1.944 = 5832/min.
            custom[Stat.KeystoneZealotsOath] = 1;
            var withOath = Game.CachedPlayerStats(player, out _);
            Assert.Equal(0, withOath.GetValueOrDefault(Stat.TotalLifeRecoveryPerMinuteFromRegeneration));
            Assert.Equal(5832, withOath.GetValueOrDefault(Stat.TotalEnergyShieldRecoveryPerMinuteFromRegeneration));
        }
    }
}
