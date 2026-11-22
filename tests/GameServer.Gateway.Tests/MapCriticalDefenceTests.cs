namespace GameServer.Gateway.Tests;

public sealed class MapCriticalDefenceTests
{
    [Fact]
    public void Map_critical_defence_only_reduces_the_extra_part_and_preserves_immunity()
    {
        var input = new MapMods(new Dictionary<string, int> { ["map_monsters_base_self_critical_strike_multiplier_-%"] = 40 }, 0);
        var effects = Game.MapMonsterEffects(input);
        SpawnedMonster Target() => new("map target", new(10, 10, 0), 0, 0, 1000)
        {
            Mods = new MonsterModSet([], [], new MonsterModEffects(effects))
        };
        var target = Target();
        Assert.Equal(130, Game.CriticalMultiplierOn(target, 150));
        Assert.Equal(100, Game.CriticalMultiplierOn(target, 100));
        Assert.Equal(90, Game.CriticalMultiplierOn(target, 90));
        effects["self_take_no_extra_damage_from_critical_strikes"] = 1;
        Assert.Equal(100, Game.CriticalMultiplierOn(Target(), 200));
    }
}
