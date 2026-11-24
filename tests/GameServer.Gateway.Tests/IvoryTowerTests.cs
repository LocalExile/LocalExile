using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class IvoryTowerTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    [Fact]
    public void Ivory_Tower_routes_chaos_DoT_and_hits_to_Mana_and_respects_LifeReservation_ES()
    {
        using (var f = new MinionUtilityTests.Fixture())
        {
            var player = f.Player;
            player.PlayerLife = 100;
            player.PlayerMana = 1000;
            player.Pools = new(80, 100, 1000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000);
            var stats = new Dictionary<uint, int>
            {
                [Key("chaos_damage_%_taken_from_mana_before_life")] = 100,
            };

            // 1. Chaos hit goes to Mana first, protecting Low-Life pool
            int hitLife = Game.DamagePlayerHitTyped(player, 0, 0, 250, 0, stats,
                DamagePacket.Empty.Add(DamageType.Chaos, new(250, 250)));
            Assert.Equal(0, hitLife);
            Assert.Equal(750u, player.PlayerMana);

            // 2. Chaos DoT (Poison / Decay / Caustic) ALSO goes to Mana before Life!
            int dotLife = Game.DamagePlayerChaosOverTime(player, 300, 1.0, stats);
            Assert.Equal(0, dotLife);
            Assert.Equal(450u, player.PlayerMana);

            // 3. Overflow past remaining Mana hits Life
            int overflowLife = Game.DamagePlayerChaosOverTime(player, 600, 2.0, stats);
            Assert.Equal(150, overflowLife);
            Assert.Equal(0u, player.PlayerMana);

            // 4. With Shavronne's Wrappings / Coruscating Elixir, Chaos Hit does NOT bypass ES
            player.EnergyShield.SetMaximum(500, refill: true);
            stats[Key("chaos_damage_does_not_bypass_energy_shield")] = 1;
            int shavsHit = Game.DamagePlayerHitTyped(player, 0, 0, 200, 3.0, stats,
                DamagePacket.Empty.Add(DamageType.Chaos, new(200, 200)));
            Assert.Equal(0, shavsHit);
            Assert.Equal(300u, player.EnergyShield.Current);

            // 5. Eldritch Battery + Ivory Tower: Chaos DoT still bypasses ES protecting Mana and drains Mana directly.
            stats.Remove(Key("chaos_damage_does_not_bypass_energy_shield"));
            stats[Stat.KeystoneEldritchBattery] = 1;
            player.PlayerMana = 400;
            player.EnergyShield.SetMaximum(500, refill: true);
            int ebDotLife = Game.DamagePlayerChaosOverTime(player, 150, 4.0, stats);
            Assert.Equal(0, ebDotLife);
            Assert.Equal(250u, player.PlayerMana);
            Assert.Equal(500u, player.EnergyShield.Current);

            // 6. CompleteResourceRecoveryLocked preserves Poison Mana-before-Life drain instead of reverting to manaBefore.
            stats.Remove(Stat.KeystoneEldritchBattery);
            player.PlayerLife = 100;
            player.PlayerMana = 500;
            player.IncomingAilments.ApplyPoison(10.0, 200.0, 4.0);
            double manaCarry = 0;
            Assert.True(Game.CompleteResourceRecoveryLocked(player, 10.0, 11.0, stats, 0, 500, 1000, ref manaCarry));
            Assert.Equal(100u, player.PlayerLife);
            Assert.Equal(300u, player.PlayerMana);
        }

        // 7. LifeReservation +30 ES per 100 Reserved Life
        Assert.Equal(300, LifeReservation.EnergyShield(reserved: 1050, perHundred: 30));
    }
}
