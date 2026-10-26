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

    [Fact]
    public void Native_locker_has_no_stash_or_hideout_doodad_payload()
    {
        if (FindData("data/object-types.json") is not { } file) return;
        using var json = JsonDocument.Parse(File.ReadAllBytes(file));
        var type = json.RootElement.GetProperty("types").EnumerateArray()
            .Single(t => t.GetProperty("type").GetString() == HideoutCatalog.HeistLockerType);
        var components = type.GetProperty("components").EnumerateArray().Take(type.GetProperty("read").GetInt32())
            .Select(c => c.GetString()!).ToArray();
        Assert.Equal(new[] { "Positioned", "BaseEvents", "Animated", "MinimapIcon", "InteractionAction" }, components);
        Assert.True(HideoutObject.CanSerialize(components));
        byte[] blob = new HideoutObject(100, 200, 0, components, new HideoutDoodad(0, 0)).Blob();
        Assert.Equal(24, blob.Length);
    }

    [Fact]
    public void Native_locker_window_uses_its_own_open_mode()
    {
        // 3.29.3b: dispatcher +0x5278 -> HUD callback +0xcf8 -> HeistStashOpen.
        Assert.Equal("007B37" + new string('0', 32),
            Convert.ToHexString(InstanceWaypoints.OpenUi(InstanceWaypoints.HeistLockerUi)));
        Assert.Equal((byte)56, InstanceInventories.PlayerDefaults[56].Row);
        Assert.Equal((byte)0x24, InstanceInventories.PlayerDefaults[56].Kind);
    }
}
