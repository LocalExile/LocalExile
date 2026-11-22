using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MapPhysicalAsChaosTests
{
    [Fact]
    public void Map_gains_extra_chaos_without_converting_physical_or_replacing_elemental_damage()
    {
        var effects = Game.MapMonsterEffects(new MapMods(new Dictionary<string, int>
        {
            ["map_monsters_%_physical_damage_to_add_as_chaos"] = 80,
            ["map_monsters_%_physical_damage_to_add_as_fire"] = 20,
            ["map_monsters_%_physical_damage_to_add_as_cold"] = 30,
            ["map_monsters_%_physical_damage_to_add_as_lightning"] = 40
        }, 0));
        var raw = DamagePacket.Of(DamageType.Physical, 100, 100);
        var hit = new MonsterModEffects(effects).Hit(raw);
        Assert.Equal(100, hit[DamageType.Physical].Max);
        Assert.Equal(80, hit[DamageType.Chaos].Max);
        Assert.Equal(20, hit[DamageType.Fire].Max);
        Assert.Equal(30, hit[DamageType.Cold].Max);
        Assert.Equal(40, hit[DamageType.Lightning].Max);
        Assert.Equal(hit.Components, MonsterModEffects.AddedTo(raw, effects).Components);
    }
}
