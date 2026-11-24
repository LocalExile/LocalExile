using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MonsterActionsTests
{
    [Fact]
    public async Task Spawned_monsters_expose_actual_actor_skills_and_ActorAction_guards_before_bumping_generation()
    {
        using var f = new MinionUtilityTests.Fixture();

        // A scripted Atlas boss (kingSkills path): SkillIds must carry every skill written onto the actor, not only the
        // single default swing fallback.
        var shaperVariety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(AtlasBosses.Shaper));
        uint shaperId = await Game.SpawnMonster(f.Player, shaperVariety, 84, MonsterRarity.Unique, 105, 100, unrisen: false);
        var shaper = f.Player.Area.Monsters[shaperId];
        var shaperOriginSkills = f.Player.Instance.World.MonsterOrigins[shaperId].Object.Skills;
        Assert.NotNull(shaperOriginSkills);
        Assert.Equal(shaperOriginSkills!.Count, shaper.SkillIds.Count);
        foreach (int effect in AtlasBosses.ShaperEffects)
            Assert.Contains((effect, AtlasBosses.Skill(shaperVariety, effect)), shaper.SkillIds);

        var shaperNames = MonsterActions.Names(f.Player, shaperId);
        Assert.NotNull(shaperNames);
        Assert.Contains("AtlasBossCelestialBeam", shaperNames!);
        Assert.Contains("ShaperMelee", shaperNames!);
        Assert.Equal(AtlasBosses.CelestialBeamEffect, AtlasBosses.EffectByName(shaper, "AtlasBossCelestialBeam"));
        Assert.Equal(AtlasBosses.CelestialBeamEffect, AtlasBosses.EffectByName(shaper, "atlasbosscelestialbeam"));
        Assert.Equal(AtlasBosses.CelestialBeamEffect, AtlasBosses.EffectByName(shaper, AtlasBosses.CelestialBeamEffect.ToString()));
        Assert.Equal(-1, AtlasBosses.EffectByName(shaper, "NoSuchSkill"));

        // A regular monster: SkillIds matches its actor skill list and resolves through MonsterActions.Names / EffectByName.
        var skeletonVariety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Skeletons/SkeletonBowSplitArrow"));
        uint skeletonId = await Game.SpawnMonster(f.Player, skeletonVariety, 20, MonsterRarity.Normal, 110, 100, unrisen: false);
        var skeleton = f.Player.Area.Monsters[skeletonId];
        var skeletonOriginSkills = f.Player.Instance.World.MonsterOrigins[skeletonId].Object.Skills;
        Assert.NotNull(skeletonOriginSkills);
        Assert.Equal(skeletonOriginSkills!.Count, skeleton.SkillIds.Count);
        Assert.NotEmpty(skeleton.SkillIds);
        var skeletonNames = MonsterActions.Names(f.Player, skeletonId);
        Assert.NotNull(skeletonNames);
        Assert.NotEmpty(skeletonNames!);
        Assert.Equal(skeleton.SkillIds[0].Effect, AtlasBosses.EffectByName(skeleton, skeletonNames![0]));

        // ActorAction must refuse without incrementing ActionGeneration during entry grace period or when dead / 0 life.
        int genBefore = shaper.ActionGeneration;
        double now = Game.AreaTime(f.Player);
        var pos = f.Player.Walk.Position(now);
        f.Player.EntryProtection.Start(now, pos, GracePeriod.BuffRow, town: false, hideout: false);
        Assert.True(Game.InGracePeriod(f.Player));
        Assert.False(await MonsterActions.Use(f.Player, shaperId, "AtlasBossCelestialBeam"));
        Assert.Equal(genBefore, shaper.ActionGeneration);

        f.Player.EntryProtection.End(Game.AreaTime(f.Player), f.Player.Walk.Position(Game.AreaTime(f.Player)), action: true);
        Assert.False(Game.InGracePeriod(f.Player));

        shaper.Dead = true;
        Assert.False(await MonsterActions.Use(f.Player, shaperId, "AtlasBossCelestialBeam"));
        Assert.Equal(genBefore, shaper.ActionGeneration);

        shaper.Dead = false;
        shaper.Life = 0;
        Assert.False(await MonsterActions.Use(f.Player, shaperId, "AtlasBossCelestialBeam"));
        Assert.Equal(genBefore, shaper.ActionGeneration);

        // Once alive and out of grace period, playing a kingSkills boss action succeeds and advances ActionGeneration once.
        shaper.Life = shaper.MaxLife;
        Assert.True(await MonsterActions.Use(f.Player, shaperId, "AtlasBossCelestialBeam"));
        Assert.Equal(genBefore + 1, shaper.ActionGeneration);
    }
}
