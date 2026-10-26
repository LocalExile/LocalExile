using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class SocketLayoutTests
{
    [Theory]
    [InlineData("RRGBBB", "6")]
    [InlineData("R-R-G-B", "4")]
    [InlineData("RRG B", "3,1")]
    [InlineData("r g b", "1,1,1")]
    [InlineData("RR GB W", "2,2,1")]
    public void Words_are_link_groups(string text, string groups)
    {
        var parsed = SocketLayout.Parse(text.Split(' '), out string? error);
        Assert.Null(error);
        Assert.Equal(groups, string.Join(',', parsed!.Value.Links));
        Assert.Equal(parsed.Value.Links.Sum(n => n), parsed.Value.Colours.Count);
    }

    [Theory]
    [InlineData("RRGBBBR")]
    [InlineData("RXG")]
    [InlineData("-")]
    public void Bad_layouts_are_refused(string text)
    {
        Assert.Null(SocketLayout.Parse(text.Split(' '), out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Apply_sets_colours_and_links_and_keeps_gems()
    {
        var gem = new Item(0x99, ["Base"], 0x20);
        var item = Armour() with { Sockets = [new(SocketColour.Red, gem), new(SocketColour.Red)], Links = [2] };
        var (colours, links) = SocketLayout.Parse(["BBBBB", "G"], out _)!.Value;
        var after = SocketLayout.Apply(item, Base("Body Armour"), colours, links, out string? error)!;
        Assert.Null(error);
        Assert.Equal([5, 1], after.Links!.Select(n => (int)n));
        Assert.Same(gem, after.Sockets[0].Gem);
        Assert.Equal(SocketColour.Blue, after.Sockets[0].Colour);
        Assert.Equal("BBBBB G", SocketLayout.Describe(after));
    }

    [Fact]
    public void Apply_refuses_over_capacity_dropped_gems_and_socketless_classes()
    {
        var gem = new Item(0x99, ["Base"], 0x20);
        var (four, fourLinks) = SocketLayout.Parse(["RRRR"], out _)!.Value;
        var (one, oneLinks) = SocketLayout.Parse(["R"], out _)!.Value;
        Assert.Null(SocketLayout.Apply(Armour(), Base("Wand", 1, 3), four, fourLinks, out var error));
        Assert.NotNull(error);
        Assert.Null(SocketLayout.Apply(Armour(), Base("Ring", 1, 1), one, oneLinks, out error));
        Assert.NotNull(error);
        var socketed = Armour() with { Sockets = [new(SocketColour.Red), new(SocketColour.Red, gem)], Links = [2] };
        Assert.Null(SocketLayout.Apply(socketed, Base("Body Armour"), one, oneLinks, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Describe_shows_unlinked_sockets_and_none()
    {
        Assert.Equal("none", SocketLayout.Describe(Armour()));
        Assert.Equal("R G", SocketLayout.Describe(Armour() with { Sockets = [new(SocketColour.Red), new(SocketColour.Green)] }));
    }

    static Item Armour() => new(0x1234, ["Base", "Mods", "Sockets"], 0x10);

    static ItemCatalog.BaseType Base(string itemClass, byte width = 2, byte height = 3) =>
        new(0, "test", "test", 0x1234, width, height, 0x10, ["Base", "Mods", "Sockets"], 0, Class: itemClass);
}
