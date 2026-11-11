using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class DefensiveJewelTests
{
    [Fact]
    public void Melding_shares_the_final_elemental_maximum_before_overcap_modifiers()
    {
        var jewel = new UniqueCatalog(Program.Items).Create("Melding of the Flesh", new Random(17))!;
        var sources = ItemModStats.Global([jewel], Program.Items.Mods, Program.Stats);
        sources[Program.Stats.Key("base_maximum_fire_damage_resistance_%")!.Value] = 25;
        sources[Program.Stats.Key("base_maximum_chaos_damage_resistance_%")!.Value] = 2;
        sources[Program.Stats.Key("base_cold_damage_resistance_%")!.Value] = 200;
        sources[Program.Stats.Key(ResistanceOvercap.ColdEvasionFlag)!.Value] = 1;
        var bonus = Game.PassiveBonus(sources, default, default);
        var result = ResistanceOvercap.Applied(bonus, sources, new Dictionary<uint, int>(), 0, Program.Stats);
        Assert.Equal(15, result.FireMaxResistance);
        Assert.Equal(15, result.ColdMaxResistance);
        Assert.Equal(15, result.LightningMaxResistance);
        Assert.Equal(2, result.ChaosMaxResistance);
        Assert.Equal(result.ColdResistance - 90, result.ColdOvercapEvasionPct);
    }

    [Fact]
    public void Bloodnotch_recovers_a_share_of_a_surviving_stunning_hit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bloodnotch-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "bloodnotch", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "StunningHit", PlayerLife = 1000, PlayerMana = 100, Instance = new(),
            Pools = new(100, 1000, 100, 0, 0), EvasionEntropy = 100, IncomingHitRandom = new Random(7),
        };
        try
        {
            conn.PlayerStats[Program.Stats.Key("damage_taken_%_recovered_as_life_from_stunning_hits")!.Value] = 50;
            var monster = new SpawnedMonster("hitter", new(0, 0, 0), 0, 0, 1000);
            var attack = new MonsterAttack(0, 1, 10, DamagePacket.Of(DamageType.Physical, 500, 500));
            var hit = Game.CommitMonsterHitLocked("test", conn, 1, monster, attack, 1, (0, 0), (0, 0), checkRange: false);
            Assert.NotNull(hit!.Recovery);
            Assert.Equal(1000u - (uint)hit.Damage + (uint)hit.Damage / 2, hit.Life);
            conn.PlayerLife = 100;
            conn.EvasionEntropy = 100;
            var fatal = Game.CommitMonsterHitLocked("test", conn, 1, monster, attack, 2, (0, 0), (0, 0), checkRange: false);
            Assert.Equal(0u, fatal!.Life);
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
