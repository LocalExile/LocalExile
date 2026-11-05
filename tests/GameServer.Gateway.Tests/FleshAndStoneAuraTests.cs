using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>Flesh and Stone (#471): the aura now activates, it alternates its stance on each use, Sand Stance reduces the
/// damage the player takes and blinds nearby monsters, and Blood Stance raises the Physical Damage nearby monsters take.</summary>
public class FleshAndStoneAuraTests
{
    [Fact]
    public void FleshAndStone_is_supported_and_its_buff_follows_the_stance()
    {
        Assert.True(AuraCatalog.Supported("FleshAndStone"));
        Assert.Equal(AuraCatalog.FleshAndStoneBloodRow, AuraCatalog.BuffRow("FleshAndStone"));
        Assert.Equal(AuraCatalog.FleshAndStoneBloodRow, AuraCatalog.BuffRow("FleshAndStone", "Blood"));
        Assert.Equal(AuraCatalog.FleshAndStoneSandRow, AuraCatalog.BuffRow("FleshAndStone", "Sand"));

        // The skill alternates on each activation.
        Assert.Equal("Sand", AuraCatalog.OtherStance("Blood"));
        Assert.Equal("Blood", AuraCatalog.OtherStance("Sand"));
        Assert.Equal("Sand", AuraCatalog.OtherStance(null));
    }

    [Fact]
    public void Sand_reduces_damage_taken_and_Blood_raises_physical_damage_taken()
    {
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("FleshAndStone");
        Assert.True(effect >= 0);
        var sand = new AuraCatalog.Aura(0x999, effect, "FleshAndStone", "Flesh and Stone", 1,
            AuraCatalog.BuffRow("FleshAndStone", "Sand"), 0, 2500, Level: 20, Stance: "Sand");
        var blood = sand with { Stance = "Blood", BuffRow = AuraCatalog.FleshAndStoneBloodRow };

        int reduction = AuraCatalog.FleshAndStoneDamageReduction(sand, 0, skills, Program.Stats);
        Assert.NotEqual(0, reduction);
        uint? takenKey = Program.Stats.Key("base_damage_taken_+%");
        Assert.NotNull(takenKey);
        var sandStats = AuraCatalog.RawStats([sand], 0, skills, Program.Stats).ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal(reduction, sandStats[takenKey!.Value]);
        Assert.DoesNotContain(AuraCatalog.RawStats([blood], 0, skills, Program.Stats), x => x.Key == takenKey);

        // The Blood Stance value is data-driven, and only the Blood tick applies it (EnemyDebuffsTick's stance check).
        Assert.True(AuraCatalog.FleshAndStonePhysicalTaken(blood, 0, skills, Program.Stats) > 0);

        // The Blood Stance share reaches the Physical damage a monster takes and nothing else.
        var monster = new SpawnedMonster("flesh and stone", new(0, 0, 0), 0, 0, 1000);
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var fire = DamagePacket.Of(DamageType.Fire, 100, 100);
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, physical, spell: false));
        monster.TakenFromPlayer = (0, 0, 50);
        Assert.Equal(150, Game.MonsterTakesMitigated(monster, physical, spell: false));
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, fire, spell: false));
    }
}
