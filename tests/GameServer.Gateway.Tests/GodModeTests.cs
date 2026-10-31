namespace GameServer.Gateway.Tests;

/// <summary>/god outlives the connection it was turned on in: every area is a new connection (Program.Player.cs).</summary>
public class GodModeTests
{
    [Fact]
    public void God_mode_is_restored_on_a_new_connection_of_the_character_until_turned_off()
    {
        string name = $"GodTest{Guid.NewGuid():N}";
        ClientState Connect() => new(new AreaLayout(0x1234, "1_1_3", 200, 200, 70, 70, null, []), 1, 1) { Character = name };
        var before = Connect();
        Program.RestoreGod(before);
        Assert.False(before.God);
        Program.GodCharacters[name] = true;
        var after = Connect();
        Program.RestoreGod(after);
        Assert.True(after.God);
        Program.GodCharacters.TryRemove(name, out _);
        var off = Connect();
        Program.RestoreGod(off);
        Assert.False(off.God);
    }
}
