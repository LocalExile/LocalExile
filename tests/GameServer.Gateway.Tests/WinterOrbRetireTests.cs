namespace GameServer.Gateway.Tests;

public sealed class WinterOrbRetireTests
{
    [Fact]
    public void LethalHitRetiresTheOrbAndQueuesItsStageIcon()
    {
        // Dying with stages left the stage icon on the client: the lethal hit retires combat from RageWhenHitLocked before
        // PlayerDiedAtEpoch, so RetireOwnedCombat dropped the orb runtime and the orb loop's cleanup found it already
        // detached, removing nothing — the icon kept the stages the player died with.
        var conn = new ClientState(new(1, "winter-orb-retire", 400, 400, 100, 100, null, []), 1, 1);
        var runtime = new Game.WinterOrbRuntime { ObjectId = 7, Stages = 6, Channelling = true, PresentedBuff = 0x42 };
        conn.WinterOrbState = runtime;
        conn.PlayerLife = 0;

        Game.RageWhenHitLocked(conn);

        Assert.Null(conn.WinterOrbState);
        Assert.False(runtime.Channelling);
        Assert.Contains(conn.RetiredCombatBuffs, buff => buff.Buff == 0x42);
    }
}
