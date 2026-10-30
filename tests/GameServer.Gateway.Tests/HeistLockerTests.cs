using GameServer.Core.Protocol.Instance;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class HeistLockerTests
{
    static string? FindData(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }

    [Fact]
    public void Every_hideout_gets_a_locker_without_displacing_existing_furniture()
    {
        if (FindData("data/hideouts.json") is not { } file) return;
        var hideouts = HideoutCatalog.Load(file, Path.Combine(Path.GetDirectoryName(file)!, "tables"));
        var fallback = hideouts.First(h => h.AreaId == HideoutCatalog.DefaultArea);
        Assert.NotEmpty(hideouts);
        foreach (var hideout in hideouts)
        {
            var scene = HideoutScene.Build(hideout, fallback, null);
            var locker = Assert.IsType<HideoutCatalog.Doodad>(scene.Heist);
            Assert.Equal(HideoutCatalog.HeistLockerType, locker.Type);
            Assert.True(locker.X > 0 && locker.Y > 0);
            Assert.NotEqual((scene.Stash.X, scene.Stash.Y), (locker.X, locker.Y));
            Assert.NotEqual((scene.Harvest.X, scene.Harvest.Y), (locker.X, locker.Y));
            var restored = HideoutEditing.Apply(scene,
                new Dictionary<string, HideoutPlacement> { ["heist-locker"] = new(101, 202, 1.5f) });
            Assert.Equal((101, 202, 1.5f), (restored.Heist!.X, restored.Heist.Y, restored.Heist.Rotation));
            Assert.Equal(scene.Stash, restored.Stash);
            Assert.Equal(scene.Npcs, restored.Npcs);
        }
    }

}
