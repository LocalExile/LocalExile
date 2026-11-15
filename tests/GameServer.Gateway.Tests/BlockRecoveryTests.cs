using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class BlockRecoveryTests
{
    sealed class NonCriticalRolls : Random
    {
        public override int Next(int maxValue) => maxValue - 1;
        public override double NextDouble() => .5;
    }

    sealed class Rules : IDisposable
    {
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public Rules()
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        }
        public void Dispose()
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    static ClientState Player(uint life = 1000, uint mana = 100, int lifeGain = 0, int manaGain = 0)
    {
        var conn = new ClientState(new(1, "block-recovery", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            PlayerLife = life, PlayerMana = mana, Instance = new(), EvasionEntropy = 100,
            IncomingHitRandom = new NonCriticalRolls(),
            Pools = new(100, 1000, 100, 0, 0, BlockChance: 100, SpellBlockChance: 100,
                LifeGainedOnBlock: lifeGain, ManaGainedOnBlock: manaGain),
        };
        conn.Clock.Stop();
        Stat(conn, "keystone_glancing_blows", 1);
        return conn;
    }

    static void Stat(ClientState conn, string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;

    static void Hit(ClientState conn, bool synthetic, bool spell, int damage = 200, DamageType type = DamageType.Physical,
        bool blocked = true)
    {
        lock (conn.Monsters)
        {
            if (synthetic)
            {
                conn.Clock.Start(); // Synthetic tools correctly refuse a paused game.
                try
                {
                    Assert.True(Game.TrySyntheticEnemyHit(conn, type, damage, block: true, spell, suppress: false, out var hit));
                    Assert.Equal(blocked, hit.Blocked);
                }
                finally { conn.Clock.Stop(); }
            }
            else
            {
                var monster = new SpawnedMonster("block recovery", new(100, 100, 0), 0, 0, 1000) { Level = 10 };
                var attack = new MonsterAttack(0, 1, 10, DamagePacket.Of(type, damage, damage)) { Spell = spell };
                var hit = Game.CommitMonsterHitLocked("test", conn, 1, monster, attack,
                    conn.Clock.Elapsed.TotalSeconds, (100, 100), (100, 100), checkRange: false);
                Assert.NotNull(hit);
                Assert.Equal(blocked, hit.Blocked);
                Assert.False(hit.Critical);
            }
        }
    }

    [Fact]
    public void Block_recovery_precedes_damage_and_supplies_the_pools_that_absorb_it()
    {
        using var rules = new Rules();
        foreach (bool synthetic in new[] { false, true })
        foreach (bool spell in new[] { false, true })
        {
            var full = Player(lifeGain: 200);
            Hit(full, synthetic, spell);
            Assert.Equal(870u, full.PlayerLife); // Recovery at full Life is wasted before the 130 damage.

            var low = Player(life: 50, lifeGain: 100);
            Stat(low, "recover_%_of_maximum_life_on_block", 10);
            Stat(low, "life_recovery_rate_+%", -100); // Instant recovery has no rate to modify.
            Hit(low, synthetic, spell);
            Assert.Equal(120u, low.PlayerLife); // The recovery can save a hit that was lethal at 50 Life.

            var lethal = Player(lifeGain: 200);
            Hit(lethal, synthetic, spell, damage: 1700);
            Assert.Equal(0u, lethal.PlayerLife); // 1105 damage still kills; recovery cannot exceed the cap.

            var savage = Player(life: 800, lifeGain: 200);
            Hit(savage, synthetic, spell, damage: 400);
            Assert.Equal(740u, savage.PlayerLife);
            Assert.True(savage.RecentEvents.Happened(StatConditions.TakenSavageHit, savage.Clock.Elapsed.TotalSeconds));
            // The hit loses 260 Life after recovery, even though the whole transaction only loses 60.

            foreach (uint mana in new uint[] { 0, 100 })
            {
                var mindOverMatter = Player(mana: mana, manaGain: 50);
                Stat(mindOverMatter, "mana_%_gained_on_block", 50);
                Stat(mindOverMatter, "keystone_mana_shield", 1);
                Hit(mindOverMatter, synthetic, spell);
                Assert.Equal(922u, mindOverMatter.PlayerLife);
                Assert.Equal(48u, mindOverMatter.PlayerMana); // 40% of 130 comes from the recovered Mana.
            }

            foreach (bool fullShield in new[] { false, true })
            {
                var shield = Player();
                shield.EnergyShield.SetMaximum(200, refill: fullShield);
                Stat(shield, "energy_shield_%_gained_on_block", 100);
                Hit(shield, synthetic, spell);
                Assert.Equal(1000u, shield.PlayerLife);
                Assert.Equal(70u, shield.EnergyShield.Current);
                Assert.False(shield.RecentEvents.Happened(StatConditions.TakenSavageHit, shield.Clock.Elapsed.TotalSeconds));
            }

            var battery = Player(mana: 0);
            battery.EnergyShield.SetMaximum(200);
            Stat(battery, "energy_shield_%_gained_on_block", 100);
            Stat(battery, "keystone_eldritch_battery", 1);
            Stat(battery, "keystone_mana_shield", 1);
            Hit(battery, synthetic, spell);
            Assert.Equal(922u, battery.PlayerLife);
            Assert.Equal(0u, battery.PlayerMana);
            Assert.Equal(148u, battery.EnergyShield.Current);

            var chaos = Player();
            chaos.EnergyShield.SetMaximum(200);
            Stat(chaos, "energy_shield_%_gained_on_block", 100);
            Hit(chaos, synthetic, spell, type: DamageType.Chaos);
            Assert.Equal(870u, chaos.PlayerLife);
            Assert.Equal(200u, chaos.EnergyShield.Current); // Chaos still bypasses the recovered ES.

            var ordinaryBlock = Player(life: 500, mana: 0, lifeGain: 200, manaGain: 100);
            Stat(ordinaryBlock, "keystone_glancing_blows", 0);
            Hit(ordinaryBlock, synthetic, spell);
            Assert.Equal(700u, ordinaryBlock.PlayerLife); // Zero damage does not suppress on-block recovery.
            Assert.Equal(100u, ordinaryBlock.PlayerMana);

            var unblocked = Player(life: 500, mana: 0, lifeGain: 200, manaGain: 100);
            unblocked.Pools = unblocked.Pools with { BlockChance = 0, SpellBlockChance = 0 };
            Hit(unblocked, synthetic, spell, blocked: false);
            Assert.Equal(300u, unblocked.PlayerLife);
            Assert.Equal(0u, unblocked.PlayerMana);
        }
    }

    [Fact]
    public void Block_recovery_obeys_recovery_limits_and_settles_full_pools_before_damage()
    {
        using var rules = new Rules();
        foreach (bool synthetic in new[] { false, true })
        {
            var petrified = Player(life: 450, lifeGain: 200);
            petrified.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
                manaPercent: 0, lowLifeThresholdPct: 50, buff: 7, now: 0);
            Hit(petrified, synthetic, spell: true);
            Assert.Equal(422u, petrified.PlayerLife); // Recovery caps at 500, then 60% of 130 is lost at once.

            var aboveHalf = Player(life: 700, lifeGain: 200);
            aboveHalf.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
                manaPercent: 0, lowLifeThresholdPct: 50, buff: 7, now: 0);
            Hit(aboveHalf, synthetic, spell: true);
            Assert.Equal(570u, aboveHalf.PlayerLife); // Non-flask recovery cannot add Life above the boundary.

            var reserved = Player(life: 200, lifeGain: 200);
            reserved.PetrifiedBlood.Activate(1, 2, loseOverTimePct: 81, preventPct: 40, lifeCostPct: 40,
                manaPercent: 7500, lowLifeThresholdPct: 50, buff: 7, now: 0, reservesLife: true);
            Assert.Equal(250u, reserved.PlayerUnreservedLife);
            Hit(reserved, synthetic, spell: true);
            Assert.Equal(172u, reserved.PlayerLife); // Unreserved Life caps recovery before PB splits the hit.

            var prohibited = Player(life: 450, lifeGain: 200);
            prohibited.EnergyShield.SetMaximum(200);
            Stat(prohibited, "energy_shield_%_gained_on_block", 100);
            Stat(prohibited, "cannot_recover_life", 1);
            Stat(prohibited, "cannot_recover_energy_shield", 1);
            Hit(prohibited, synthetic, spell: true);
            Assert.Equal(320u, prohibited.PlayerLife);
            Assert.Equal(0u, prohibited.EnergyShield.Current);

            var leech = Player(life: 900, mana: 0, lifeGain: 200, manaGain: 100);
            Game.ConfigureLeech(leech, _ => 0, new Dictionary<uint, int>(), onLowLife: false);
            Game.LeechOf(leech).Hit(0, (100, 10, 0), lifeFull: false, manaFull: false, shieldFull: true);
            Assert.True(Game.LeechOf(leech).Active(0).Life);
            Assert.True(Game.LeechOf(leech).Active(0).Mana);
            Stat(leech, "keystone_mana_shield", 1);
            Hit(leech, synthetic, spell: true);
            Assert.Equal(922u, leech.PlayerLife);
            Assert.Equal(48u, leech.PlayerMana);
            Assert.False(Game.LeechOf(leech).Active(0).Life);
            Assert.False(Game.LeechOf(leech).Active(0).Mana);

            var overleech = Player(life: 900, lifeGain: 200);
            Stat(overleech, "life_leech_does_not_stop_at_full_life", 1);
            Game.ConfigureLeech(overleech, name => name == "life_leech_does_not_stop_at_full_life" ? 1 : 0,
                new Dictionary<uint, int>(), onLowLife: false);
            Game.LeechOf(overleech).Hit(0, (100, 0, 0), lifeFull: false, manaFull: true, shieldFull: true);
            Hit(overleech, synthetic, spell: true);
            Assert.Equal(870u, overleech.PlayerLife);
            Assert.True(Game.LeechOf(overleech).Active(0).Life);
        }

        var missed = Player(life: 500, mana: 0, lifeGain: 200, manaGain: 100);
        lock (missed.Monsters)
        {
            var monster = new SpawnedMonster("out of range", new(0, 0, 0), 0, 0, 1000) { Level = 10 };
            var attack = new MonsterAttack(0, 1, 10, DamagePacket.Of(DamageType.Physical, 200, 200));
            Assert.Null(Game.CommitMonsterHitLocked("test", missed, 1, monster, attack,
                missed.Clock.Elapsed.TotalSeconds, (1000, 1000), (0, 0), checkRange: true));
            Assert.Equal(500u, missed.PlayerLife);
            Assert.Equal(0u, missed.PlayerMana);
        }
    }
}
