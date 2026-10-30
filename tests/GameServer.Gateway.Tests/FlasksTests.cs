using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class FlasksTests
{
    // Small Life Flask (Flasks.dat, ComponentCharges): 70 life over 3 s, 7 of 21 charges per use.
    static readonly Flasks.Flask SmallLife = new(70, 0, 3.0, 7, 21);
    static Item LifeFlask(int charges) => new(0x1111, ["Base", "Charges", "Flask"], 0x2222) { Charges = charges };

    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void The_small_flasks_come_from_Flasks_dat_and_ComponentCharges()
    {
        var catalog = new ItemCatalog(Data());

        Assert.Equal(SmallLife, catalog.FlaskOf(catalog.Find("Metadata/Items/Flasks/FlaskLife1")!));
        Assert.Equal(new Flasks.Flask(0, 50, 3.0, 6, 24), catalog.FlaskOf(catalog.Find("Metadata/Items/Flasks/FlaskMana1")!));
        Assert.Null(catalog.FlaskOf(catalog.Row(384)!));   // Driftwood Wand
    }

    static Flasks.Effect Life(byte slot, double started, ushort buff = 1) =>
        new(slot, buff, 5, SmallLife.LifePerUse / SmallLife.Seconds, 0, started, started + SmallLife.Seconds);

}
