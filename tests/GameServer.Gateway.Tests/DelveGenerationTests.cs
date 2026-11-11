namespace GameServer.Gateway.Tests;

public sealed class DelveGenerationTests
{
    [Fact]
    public void Mine_monkey_mortar_writes_empty_state_machine_before_tasks()
    {
        var blob = new GameServer.Core.Protocol.Instance.ProjectileObject("Metadata/Projectiles/DelveMonkeyMortar", 172, 1460, Height: 8)
            { StateMachine = new([], 0) }.Blob();
        Assert.Equal(30, blob.Length);
        Assert.Equal(new byte[] { 0, 0, 0 }, blob[^3..]);
    }

}
