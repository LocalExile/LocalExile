using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class PrideAuraTests
{
    [Fact]
    public void Pride_is_a_supported_aura_with_its_own_buff()
    {
        Assert.True(AuraCatalog.Supported("Pride"));
        Assert.Equal((ushort)1331, AuraCatalog.BuffRow("Pride"));
    }

    [Fact]
    public void Pride_ramps_physical_damage_taken_and_scales_with_aura_effect()
    {
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("Pride");
        Assert.True(effect >= 0);
        var aura = new AuraCatalog.Aura(0x999, effect, "Pride", "Pride", 1, AuraCatalog.BuffRow("Pride"),
            0, 0, Level: 20, Quality: 0);

        Assert.Equal((19, 39), AuraCatalog.PridePhysicalDamageTaken(aura, 0, skills, Program.Stats));
        Assert.Equal((29, 59), AuraCatalog.PridePhysicalDamageTaken(aura, 50, skills, Program.Stats));
        Assert.Equal(50, AuraRadius.Of(aura, 0, skills, Program.Stats));
        Assert.Equal(59, AuraRadius.Of(aura with { Quality = 20 }, 0, skills, Program.Stats));
        Assert.Equal(70, AuraRadius.Of(aura, 100, skills, Program.Stats));

        Assert.Equal(19, AuraCatalog.PridePhysicalMore(19, 39, 0));
        Assert.Equal(29, AuraCatalog.PridePhysicalMore(19, 39, 2));
        Assert.Equal(39, AuraCatalog.PridePhysicalMore(19, 39, 4));
        Assert.Equal(39, AuraCatalog.PridePhysicalMore(19, 39, 10));

        var monster = new SpawnedMonster("pride", new(0, 0, 0), 0, 0, 1000);
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var fire = DamagePacket.Of(DamageType.Fire, 100, 100);
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, physical, spell: false));
        monster.PridePhysicalMore = 19;
        Assert.Equal(119, Game.MonsterTakesMitigated(monster, physical, spell: false));
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, fire, spell: false));
        monster.PridePhysicalMore = 39;
        Assert.Equal(139, Game.MonsterTakesMitigated(monster, physical, spell: false));
        var mixed = physical.Add(fire);
        monster.PridePhysicalMore = 50;
        Assert.Equal(250, Game.MonsterTakesMitigated(monster, mixed, spell: false));
    }
}
