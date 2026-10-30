namespace GameServer.Gateway.Tests;

// The attack timer the default attack and the attack gem skills share. While the button is held the client re-aims an
// attack every frame; the timer makes at most one of those a swing (live 2026-10-25: Holy Strike on the right mouse
// button cast 12 times a second, 6 mana each, while the cursor moved).
public class AttackTimingTests
{
    [Fact]
    public void The_first_attack_is_ready()
    {
        Assert.True(AttackTiming.Ready(now: 0, next: 0, seconds: 0.8));
    }

}
