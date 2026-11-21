using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MinionFireTests
{
    [Fact]
    public async Task Infernal_legion_burns_the_minion_and_only_one_overlapping_field_hits_each_enemy()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Summon Skeletons", 20), ("Infernal Legion Support", 1));
        var item = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
        Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1, item.Id, row => row with { Links = [2] });
        var first = await Skeleton(f);
        var second = await Skeleton(f);
        first.Monster.Walk.Teleport(100, 100, 0);
        second.Monster.Walk.Teleport(100, 100, 0);
        var target = f.Conn.Monsters[900] = Enemy(104);
        var far = f.Conn.Monsters[901] = Enemy(160);
        Game.InfernalLegionTickLocked(f.Conn, 1);
        Game.InfernalLegionTickLocked(f.Conn, 2);
        Assert.InRange(target.MaxLife - target.Life, 16u, 17u);
        Assert.Equal(far.MaxLife, far.Life);

        uint before = first.Monster.Life;
        double multiplier = Game.MonsterDotTaken(first.Monster, DamageType.Fire, 2);
        Game.GolemDotsLocked(f.Conn, first, 1, 2);
        Assert.Equal((uint)Math.Floor(first.Monster.MaxLife * .4 * multiplier), before - first.Monster.Life);
    }

    [Fact]
    public async Task Minion_instability_explodes_a_living_minion_once_and_excludes_distant_enemies()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Summon Skeletons", 20));
        uint key = Program.Stats.Key("minions_explode_on_low_life_maximum_life_%_to_deal")!.Value;
        f.Conn.PlayerStats[key] = 33;
        var fighter = await Skeleton(f);
        fighter.Monster.Walk.Teleport(100, 100, 0);
        var near = f.Conn.Monsters[900] = Enemy(105);
        var far = f.Conn.Monsters[901] = Enemy(160);
        int expected = (int)Math.Round(fighter.Monster.MaxLife * .33);
        Assert.False(Game.MinionInstabilityLocked(f.Conn, fighter, 1));
        fighter.Monster.Life = fighter.Monster.MaxLife / 2;
        Assert.True(Game.MinionInstabilityLocked(f.Conn, fighter, 1));
        Assert.True(fighter.Monster.Dead);
        Assert.Equal(expected, (int)(near.MaxLife - near.Life));
        Assert.Equal(far.MaxLife, far.Life);
        uint remaining = near.Life;
        Assert.False(Game.MinionInstabilityLocked(f.Conn, fighter, 2));
        Assert.Equal(remaining, near.Life);
    }

    private static SpawnedMonster Enemy(int x) => new("target", new(x, 100, 0), 100, 3, 100000)
        { Hostile = true };

    private static async Task<GolemState.Golem> Skeleton(MinionUtilityTests.Fixture f)
    {
        ushort source = Game.GemSkillEffects(f.Conn).Single().Id;
        uint id = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, source,
            Game.SummonData.Find("SummonSkeletons")!, (100, 100));
        return f.Conn.Summons.Fighters[id];
    }
}
