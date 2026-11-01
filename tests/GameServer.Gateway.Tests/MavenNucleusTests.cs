namespace GameServer.Gateway.Tests;

/// <summary>Maven's Writ combat effect, Nucleus emerge state, and the per-hit life cap.</summary>
public sealed class MavenNucleusTests
{
    static SkillCatalog Skills => new("data");

    [Fact]
    public void CombatEffectSkipsTeleportAndPicksMavenProjectile()
    {
        var skills = Skills;
        var maven = Game.Monsters.Find(Game.MavenNucleus.Maven);
        Assert.NotNull(maven);
        int effect = Game.MavenNucleus.CombatEffect(skills, maven!);
        Assert.Equal("MavenProjectile", skills.EffectId(effect));
        Assert.True(Game.MavenNucleus.AttackIsRanged(skills, effect));
        Assert.Equal("Metadata/Projectiles/MavenProjectile", Game.MavenNucleus.Shot(skills, effect)!.Metadata);
    }

    [Fact]
    public void NucleusCombatEffectIsTheProjectileBarrage()
    {
        var skills = Skills;
        var brain = Game.Monsters.Find(Game.MavenNucleus.Nucleus);
        Assert.NotNull(brain);
        int effect = Game.MavenNucleus.CombatEffect(skills, brain!);
        Assert.Equal("MavenBrainSmallProjectileBarrage", skills.EffectId(effect));
        Assert.Equal("Metadata/Projectiles/MavenBrainSmallProjectileBarrage", Game.MavenNucleus.Shot(skills, effect)?.Metadata);
        var states = Game.MavenNucleus.StartStates(brain!);
        if (brain!.StateMachine is { } machine)
        {
            Assert.NotNull(states);
            int emerged = machine.IndexOf("emerged");
            Assert.True(emerged >= 0);
            Assert.Equal(1, states!.SharedStates[emerged]);
        }
    }

    [Fact]
    public void HitCapStopsTheBoostFromSkippingAPhase()
    {
        Assert.Equal(80_000, Game.MavenNucleus.CapHit(Game.MavenNucleus.Maven, 1_000_000, int.MaxValue));
        Assert.Equal(50, Game.MavenNucleus.CapHit(Game.MavenNucleus.Nucleus, 1_000_000, 50));
        Assert.Equal(int.MaxValue, Game.MavenNucleus.CapHit("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal", 1_000_000, int.MaxValue));
    }
}
