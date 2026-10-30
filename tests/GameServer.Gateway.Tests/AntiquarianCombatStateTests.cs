namespace GameServer.Gateway.Tests;

public class AntiquarianCombatStateTests
{
    [Fact]
    public void Real_monster_Melee_projectile_umbrella_does_not_make_the_hit_ranged()
    {
        if (!File.Exists("data/tables/ActiveSkills.json")) return;
        int melee = Program.Skills.EffectRow("Melee");
        Assert.True(Program.Skills.IsProjectile(melee));
        Assert.True(Program.Skills.IsMelee(melee));
        Assert.False(Program.Skills.IsRangedAttack(melee));
        if (!File.Exists("data/tables/MonsterVarieties.json")) return;
        Assert.False(Program.MonsterAttackIsRanged(Program.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite")!));
        Assert.True(Program.MonsterAttackIsRanged(Program.Monsters.Find("Metadata/Monsters/Skeletons/SkeletonBow")!));
    }

}
