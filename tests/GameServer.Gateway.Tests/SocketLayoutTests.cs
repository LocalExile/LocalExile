using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class SocketLayoutTests
{
    [Theory]
    [InlineData("RRGBBB", "6")]
    public void Words_are_link_groups(string text, string groups)
    {
        var parsed = SocketLayout.Parse(text.Split(' '), out string? error);
        Assert.Null(error);
        Assert.Equal(groups, string.Join(',', parsed!.Value.Links));
        Assert.Equal(parsed.Value.Links.Sum(n => n), parsed.Value.Colours.Count);
    }

    static Item Armour() => new(0x1234, ["Base", "Mods", "Sockets"], 0x10);

    static ItemCatalog.BaseType Base(string itemClass, byte width = 2, byte height = 3) =>
        new(0, "test", "test", 0x1234, width, height, 0x10, ["Base", "Mods", "Sockets"], 0, Class: itemClass);
}
