using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class JuggernautNodesTests
{
    // Block chance caps at 90%, so a certain block needs a certain roll.
    sealed class LowRolls : Random
    {
        public override double NextDouble() => 0;
    }

    // Expected numbers: wiki Stun (200 x damage / threshold, 20% or less ignored, reduction above 75 diminishes, 350 ms)
    // and Metadata/Characters/Character.ot's stun multipliers and melee duration.
    [Fact]
    public void Stuns_follow_the_wiki_and_grant_unyieldings_charge()
    {
        Assert.Equal(0, HitReaction.Chance(new(100), 1000));
        Assert.Equal(40, HitReaction.Chance(new(200), 1000), 6);
        Assert.Equal(100, HitReaction.Chance(new(500), 1000));
        Assert.Equal(40, HitReaction.Chance(new(150, ThresholdReductionPct: 25), 1000), 6);
        Assert.Equal(40, HitReaction.Chance(new(25, ThresholdReductionPct: 100), 1000), 6);   // 100 -> 87.5

        Assert.Equal(125, HitReaction.StunningDamage(100, physicalShare: 1, melee: true), 6);
        Assert.Equal(100, HitReaction.StunningDamage(100, physicalShare: 0, melee: true), 6);
        Assert.Equal(75, HitReaction.StunningDamage(100, physicalShare: 1, melee: false), 6);
        Assert.Equal(56.25, HitReaction.StunningDamage(100, physicalShare: 0, melee: false), 6);

        var conn = new ClientState(new(1, "stun", 1000, 1000, 100, 100, null, []), 1, 1);
        var charges = Game.ChargesOf(conn);
        Func<string, int> stats = name => name == "chance_to_gain_endurance_charge_when_you_stun_enemy_%" ? 100 : 0;
        charges.Observe(stats);

        var boss = new SpawnedMonster("boss", new(0, 0, 0), 0, 0, 1000) { Rarity = MonsterRarity.Unique, HitAnimations = [new(0, 2)] };
        Assert.True(HitReaction.TryFlinch(boss, new(500, DurationPct: 100, Melee: true, Owner: conn), 10, new Random(1)));
        // The recovery is the wiki's .35 x 2 x 1.5 = 1.05 s, but never shorter than the monster's own TakeHit clip
        // (this boss's runs 2 s), so the client does not cut the clip it is playing in half.
        Assert.Equal(12, boss.FlinchUntil, 6);
        Assert.False(HitReaction.TryFlinch(boss, new(500), 15.96, new Random(1)));   // 4 s after the stun ended
        Assert.True(HitReaction.TryFlinch(boss, new(500), 16.06, new Random(1)));
        Assert.Equal(18.06, boss.FlinchUntil, 6);

        charges.Step(16, stats, () => default, alive: true, moving: false, inTown: false, new Random(1));
        Assert.Equal(1, charges.Effective(ChargeType.Endurance));
        Assert.False(HitReaction.TryFlinch(new("scratch", new(0, 0, 0), 0, 0, 1000) { HitAnimations = [new(0, 2)] }, new(100), 0, new Random(1)));
    }

    [Fact]
    public void Undeniable_reads_strength_and_untiring_pays_prevented_damage_for_ten_seconds()
    {
        // 2 per level + 500 + 2 x 100 Strength; floor(702 / 150) x 1% (the stat's id says 200, its text and the client 150).
        var pools = CharacterStats.For(new(0, 100, 0, 0), 1, new(Accuracy: 500, AccuracyFromStrengthPct: 200, AttackSpeedPer150Accuracy: 1));
        Assert.Equal(702, pools.Accuracy);
        Assert.Equal(4, pools.AttackSpeedPct);

        var prevented = new PreventedDamageRecovery();
        prevented.Add(100, 1000);
        Assert.Equal(15, prevented.Take(100, 101, .015), 6);
        Assert.Equal(135, prevented.Take(101, 112, .015), 6);
        Assert.Equal(0, prevented.Take(112, 113, .015));

        string path = Path.Combine(Path.GetTempPath(), $"localexile-juggernaut-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "juggernaut", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "JuggernautChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Armour: 1000, BlockChance: 100),
            IncomingHitRandom = new LowRolls(),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            // The whole raw hit is prevented when blocked, the blocked hit still counts as hit, and only armour's share otherwise.
            Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 100, block: true, spell: false, suppress: false, out var blocked));
            Assert.True(blocked.Blocked);
            Assert.Equal(100, Game.PreventedDamageOf(conn).Take(0, 1000, .1), 6);
            Func<string, int> stats = name => name == "chance_to_gain_endurance_charge_when_hit_%" ? 100 : 0;
            var charges = Game.ChargesOf(conn);
            charges.Step(5, stats, () => default, alive: true, moving: false, inTown: false, new Random(1));
            Assert.Equal(1, charges.Effective(ChargeType.Endurance));
            Assert.Equal(5, charges.LastHitAt);
            charges.Die();
            Assert.Equal(double.NegativeInfinity, charges.LastHitAt);   // "recently" ends at death
            Game.PreventedDamageOf(conn).Clear();

            // A monster's real hit with a blocked share: 1000 armour leaves 33 of 100 and Glancing Blows deals 65% of that, so 79 is prevented.
            conn.PlayerStats[Program.Stats.Key("keystone_glancing_blows")!.Value] = 1;
            conn.EvasionEntropy = 100;   // this one cannot be evaded
            var glanced = Game.CommitMonsterHitLocked("test", conn, 1, new("hitter", new(0, 0, 0), 0, 0, 1000) { Level = 10 },
                new(0, 1, 10, DamagePacket.Of(DamageType.Physical, 100, 100)), conn.Clock.Elapsed.TotalSeconds, (0, 0), (0, 0), checkRange: false);
            Assert.True(glanced!.Blocked);
            Assert.Equal(21, glanced.Damage);
            Assert.Equal(79, Game.PreventedDamageOf(conn).Take(0, 1000, .1), 6);
            conn.PlayerStats.Remove(Program.Stats.Key("keystone_glancing_blows")!.Value);
            Game.PreventedDamageOf(conn).Clear();

            Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 100, block: false, spell: false, suppress: false, out var hit));
            Assert.InRange(hit.Damage, 1, 99);
            Assert.Equal(100 - hit.Damage, Game.PreventedDamageOf(conn).Take(0, 1000, .1), 6);

            conn.PlayerStats[Program.Stats.Key("accuracy_rating")!.Value] = 1500;
            conn.PlayerStats[Program.Stats.Key("attack_speed_+%_per_200_accuracy_rating")!.Value] = 1;
            var without = Game.PoolsFor(conn, 1);
            conn.PlayerStats[Program.Stats.Key("gain_accuracy_rating_equal_to_2_times_strength")!.Value] = 1;
            var with = Game.PoolsFor(conn, 1);
            Assert.Equal(without.Accuracy + 2 * with.Strength, with.Accuracy);
            Assert.InRange(with.Accuracy, 1500, 1649);
            Assert.Equal(21, with.AttackSpeedPct);   // 10% increased from accuracy with the unarmed character's 10% more

            // /set_stat's block and flat armour (the Untiring checks) replace their raw sums in the pools.
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            Game.ItemsOf(conn).Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
            var shielded = Game.PoolsFor(conn, 1);
            Assert.Equal(24, shielded.BlockChance);
            conn.PlayerStats[Program.Stats.Key("shield_block_%")!.Value] = 75;
            conn.PlayerStats[Program.Stats.Key("base_physical_damage_reduction_rating")!.Value] = 5000;
            var set = Game.PoolsFor(conn, 1);
            Assert.Equal(75, set.BlockChance);   // 24 + 75, capped
            Assert.Equal(shielded.Armour + 5000, set.Armour);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
