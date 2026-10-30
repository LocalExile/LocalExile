using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class BossFightTests
{
    const string Hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";

    [Fact]
    public void Hillock_draws_his_sword_at_half_life()
    {
        var stage = BossFights.For(Hillock);
        Assert.NotNull(stage);
        Assert.Equal((50, 2612, (byte)2), (stage.AtLifePct, stage.SkillEffect, stage.TransitionTo));
        Assert.Null(BossFights.For("Metadata/Monsters/Zombies/ZombieBite"));
    }

}
