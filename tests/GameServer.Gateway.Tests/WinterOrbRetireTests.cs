namespace GameServer.Gateway.Tests;

public sealed class WinterOrbRetireTests
{
    [Fact]
    public void DeathRetiresTheOrbAndQueuesItsStageIcon()
    {
        // Dying with stages left the stage icon on the client: RetireOwnedCombat drops the orb runtime, and the orb loop's
        // cleanup then finds it already detached and removes nothing, so the icon kept the stages the player died with.
        var conn = new ClientState(new(1, "winter-orb-retire", 400, 400, 100, 100, null, []), 1, 1);
        var runtime = new Game.WinterOrbRuntime { ObjectId = 7, Stages = 6, Channelling = true, PresentedBuff = 0x42 };
        conn.WinterOrbState = runtime;

        Game.WinterOrbRetire(conn);

        Assert.Null(conn.WinterOrbState);
        Assert.False(runtime.Channelling);
        Assert.Contains(conn.RetiredCombatBuffs, buff => buff.Buff == 0x42);
    }
}
