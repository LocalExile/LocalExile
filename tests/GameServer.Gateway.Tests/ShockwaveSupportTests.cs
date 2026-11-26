using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class ShockwaveSupportTests
{
    [Fact]
    public async Task Ground_Slam_spends_stored_Shockwaves_on_separate_melee_contacts()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Walk.Teleport(100, 100, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Equip(("Ground Slam", 1), ("Shockwave Support", 20));
        var slam = Game.GemSkillEffects(player).Single(g => g.Name == "Ground Slam");
        var shockwave = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "SupportBluntWeaponShockwave");
        Assert.NotNull(Game.GemSkillInstanceDisabled(player, shockwave.Effect, shockwave.Id));
        Assert.Equal(4, Game.CooldownForSkill(player, shockwave.Id)?.Uses);
        using var accuracy = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (ReferenceEquals(owner, player)) stats[Stat.GlobalAlwaysHit] = 1;
            return stats;
        });
        for (uint i = 0; i < 5; i++)
            player.Area.Monsters.Add(901 + i, new("Shockwave witness", new(109 + (int)i, 100, 0), 0, 0, 1_000_000));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Assert.True(await Game.CastGemSkill(player, slam.Id, (100, 100), (80, 100)));
            clock.At += 1;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.Null(Game.SingleUseState(player, shockwave.Effect, shockwave.Id));

            player.NextAttackAt = 0;
            Assert.True(await Game.CastGemSkill(player, slam.Id, (100, 100), (120, 100)));
            clock.At += 1;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.Equal(4, Game.SingleUseState(player, shockwave.Effect, shockwave.Id)?.SpentUses);
            Assert.All(player.Area.Monsters.Values, monster => Assert.True(monster.Life < monster.MaxLife));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }
}
