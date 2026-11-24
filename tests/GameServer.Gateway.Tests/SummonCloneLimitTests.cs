using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The Blink / Mirror Arrow clone cap read base_number_of_mirror_arrow_clones_allowed, which is not a
// 3.29.3c Stats row, so SummonLimit always fell back to 100. The real cap stat is
// base_number_of_clones_allowed (Stats 8217); the transfigured clones already had their own limit stats.
public sealed class SummonCloneLimitTests
{
    [Fact]
    public void Blink_and_Mirror_Arrow_clones_read_the_real_clone_cap_stat()
    {
        var catalog = new SummonCatalog();
        Assert.Equal(Stat.BaseNumberOfClonesAllowed, catalog.Find("BlinkArrow")!.LimitStat);
        Assert.Equal(Stat.BaseNumberOfClonesAllowed, catalog.Find("MirrorArrow")!.LimitStat);
        Assert.Equal(Stat.BaseNumberOfBlinkMirrorArrowRainOfArrowsClones, catalog.Find("BlinkArrowAltX")!.LimitStat);
        Assert.Equal(Stat.BaseNumberOfBlinkMirrorArrowElementalHitClones, catalog.Find("BlinkArrowAltY")!.LimitStat);
    }

    [Fact]
    public void Minion_cannot_crit_and_predictive_monster_swing_sequence_rules_hold()
    {
        var attack = new MonsterAttack(0x4009, 1.0, 10, DamagePacket.Of(DamageType.Physical, 10, 10));
        var ability = new GolemState.Ability(406, 0x4009, "Melee", attack, new Dictionary<uint, int>(), 0, 0, 1, 0.5, Crit: 500);
        var cannotCritOnly = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<uint, int>
        {
            [Stat.Of("cannot_crit")] = 1,
        }));
        var cannotCritWithAlways = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<uint, int>
        {
            [Stat.Of("cannot_crit")] = 1,
            [Stat.AlwaysCrit] = 1,
        }));
        var alwaysCrit = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<uint, int>
        {
            [Stat.AlwaysCrit] = 1,
        }));
        var normal = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<uint, int>()));

        Assert.False(Game.GolemCriticalStrike(ability, cannotCritOnly, roll: 0));
        Assert.False(Game.GolemCriticalStrike(ability, cannotCritWithAlways, roll: 0));
        Assert.True(Game.GolemCriticalStrike(ability, alwaysCrit, roll: 9999));
        Assert.True(Game.GolemCriticalStrike(ability, normal, roll: 0));

        var player = new Player(new(1, "swing-seq", 300, 300, 100, 100, null, []), 1, 1)
        {
            Connection = { LockstepClient = false },
        };
        var monster = new SpawnedMonster("Zombie", new PlayerWalk(100, 100, 8), 0, 1, 100);

        ushort first = Game.MonsterSwingSequence(player, monster, walkUp: false, sequence: Game.MonsterActionSequence);
        Assert.NotEqual(Game.MonsterActionSequence, first);
        Assert.Equal(monster.Sequence, first);

        ushort second = Game.MonsterSwingSequence(player, monster, walkUp: false, sequence: Game.MonsterActionSequence);
        Assert.NotEqual(Game.MonsterActionSequence, second);
        Assert.NotEqual(first, second);
        Assert.Equal(monster.Sequence, second);

        Assert.Equal((ushort)0x1234, Game.MonsterSwingSequence(player, monster, walkUp: false, sequence: 0x1234));
        Assert.Equal(second, monster.Sequence);

        player.Connection.LockstepClient = true;
        Assert.Equal(Game.MonsterActionSequence, Game.MonsterSwingSequence(player, monster, walkUp: false, sequence: Game.MonsterActionSequence));
        Assert.Equal(Game.MonsterActionSequence, Game.MonsterSwingSequence(player, monster, walkUp: true, sequence: Game.MonsterActionSequence));
        Assert.Equal((ushort)0x2345, Game.MonsterSwingSequence(player, monster, walkUp: true, sequence: 0x2345));
        Assert.Equal(second, monster.Sequence);
    }
}
