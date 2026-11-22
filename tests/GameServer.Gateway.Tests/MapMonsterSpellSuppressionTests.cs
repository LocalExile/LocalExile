using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MapMonsterSpellSuppressionTests
{
    [Fact]
    public void Map_suppression_reduces_spell_hit_damage_after_resistance()
    {
        var input = new MapMods(new Dictionary<string, int>
        {
            ["map_monsters_spell_suppression_chance_%"] = 100,
            ["map_monsters_additional_elemental_resistance"] = 25
        });
        var monster = new SpawnedMonster("map suppressor", new(10, 10, 0), 0, 0, 1000)
        {
            Mods = new MonsterModSet([], [], new MonsterModEffects(Game.MapMonsterEffects(input)))
        };
        var fire = DamagePacket.Of(DamageType.Fire, 100, 100);
        Assert.Equal(45, Game.MonsterHitDamage(monster, fire, critical: false, attack: false, now: 1, default).Damage);
        Assert.Equal(75, Game.MonsterHitDamage(monster, fire, critical: false, attack: true, now: 1, default).Damage);
        Assert.Equal(45, Game.MonsterTakesTrapOrMineHit(monster, fire.Scale(.75), spell: true, now: 1));
        Assert.Equal(.75, Game.MonsterDotTaken(monster, DamageType.Fire, now: 1), 8);
    }
}
