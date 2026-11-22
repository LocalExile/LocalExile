namespace GameServer.Gateway.Tests;

public sealed class MapAttackBlockTests
{
    [Fact]
    public void Map_block_is_attack_only_and_can_be_reduced_by_supported_hits()
    {
        var input = new MapMods(new Dictionary<string, int> { ["map_monsters_base_block_%"] = 50 }, 0);
        var monster = new SpawnedMonster("map blocker", new(10, 10, 0), 0, 0, 1000)
        {
            Mods = new MonsterModSet([], [], new MonsterModEffects(Game.MapMonsterEffects(input)))
        };
        var skill = new Dictionary<uint, int>
        {
            [Program.Stats.Key("support_reduce_enemy_block_and_spell_block_%")!.Value] = 50
        };
        Assert.Equal(63, Enumerable.Range(1, 128).Count(seed => Game.MonsterBlocks(monster, new Random(seed), spell: false)));
        Assert.False(Game.MonsterBlocks(monster, new Random(1), spell: true));
        Assert.False(Game.MonsterBlocks(monster, new Random(1), spell: false, skill: skill));
    }
}
