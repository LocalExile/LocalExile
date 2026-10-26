using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class HideoutEditingTests
{
    [Fact]
    public void Stable_keys_survive_new_object_ids_and_keep_templates_and_characters_separate()
    {
        TestSaves.Ensure();
        string name = "Layout-" + Guid.NewGuid().ToString("N");
        CharacterSaves.Write(name, new([], Experience: 123, ActiveAuras: ["Grace"]));
        var editor = new HideoutEditing();
        editor.Register(77, "npc:Helena", 100, 200, 4, 4);
        Assert.True(editor.TryMove(new(77, 110, 220, 0, 1.5f), (_, _) => true,
            (key, p) => CharacterSaves.SetHideoutPlacement(name, "HideoutForest", key, p), out var moved));
        Assert.Equal(new HideoutPlacement(110, 220, 1.5f), moved!.Position);
        CharacterSaves.SetHideoutPlacement(name, "HideoutArenaMaven", "npc:Helena", new(300, 400, 2));
        var save = CharacterSaves.Load(name)!;
        Assert.Equal(123u, save.Experience);
        Assert.Equal(new[] { "Grace" }, save.ActiveAuras);
        Assert.Equal(new HideoutPlacement(110, 220, 1.5f), save.HideoutPlacements!["HideoutForest"]["npc:Helena"]);
        Assert.Equal(300, save.HideoutPlacements["HideoutArenaMaven"]["npc:Helena"].X);
        Assert.Null(CharacterSaves.Load(name + "other"));
        var next = new HideoutEditing();
        var restored = save.HideoutPlacements["HideoutForest"]["npc:Helena"];
        next.Register(999, "npc:Helena", restored.X, restored.Y, restored.Rotation, 4);
        Assert.True(next.TryMove(new(999, 115, 225, 0, 0), null, (_, _) => { }, out var again));
        Assert.Equal("npc:Helena", again!.Key);
    }

    [Fact]
    public void Invalid_objects_terrain_and_nonfinite_rotation_never_write()
    {
        var editor = new HideoutEditing();
        editor.Register(7, "stash", 10, 20, 0, 2);
        void Unexpected(string key, HideoutPlacement p) => Assert.Fail("Invalid move reached persistence");
        Assert.False(editor.TryMove(new(8, 10, 20, 0, 0), null, Unexpected, out _));
        Assert.False(editor.TryMove(new(7, 10, 20, 0, float.NaN), null, Unexpected, out _));
        Assert.False(editor.TryMove(new(7, 10, 20, 0, float.PositiveInfinity), null, Unexpected, out _));
        Assert.False(editor.TryMove(new(7, 10, 20, 0, 0), (_, _) => false, Unexpected, out _));
    }

    [Fact]
    public void Failed_save_preserves_previous_durable_placement()
    {
        TestSaves.Ensure();
        string name = "LayoutFailure-" + Guid.NewGuid().ToString("N");
        CharacterSaves.Write(name, new([]));
        CharacterSaves.SetHideoutPlacement(name, "Home", "stash", new(10, 20, 0));
        var editor = new HideoutEditing();
        editor.Register(7, "stash", 10, 20, 0, 2);
        Saves.FailWrite = key => key == name;
        try
        {
            Assert.ThrowsAny<IOException>(() => editor.TryMove(new(7, 30, 40, 0, 0), null,
                (key, p) => CharacterSaves.SetHideoutPlacement(name, "Home", key, p), out _));
        }
        finally { Saves.FailWrite = null; }
        Assert.Equal(new HideoutPlacement(10, 20, 0), CharacterSaves.Load(name)!.HideoutPlacements!["Home"]["stash"]);
    }

    [Fact]
    public void Every_functional_piece_and_npc_restores_without_changing_appearance()
    {
        HideoutCatalog.Doodad D(string name) => new(name, 123, name, "model.ao", 1, 2, 3, 4);
        var scene = new HideoutScene(D("Stash"), (3, 4), D("Bench"), D("Harvest"), D("Device"),
            [new("Helena", 5, 6, 7)]);
        string[] keys = ["stash", "guild-stash", "bench", "harvest", "map-device", "npc:Helena"];
        var saved = keys.Select((k, i) => (k, p: new HideoutPlacement(100 + i, 200 + i, 1.5f)))
            .ToDictionary(x => x.k, x => x.p);
        var changed = HideoutEditing.Apply(scene, saved);
        Assert.Equal(100, changed.Stash.X);
        Assert.Equal((101, 201), changed.Guild);
        Assert.Equal(102, changed.Bench.X);
        Assert.Equal(103, changed.Harvest.X);
        Assert.Equal(104, changed.Device.X);
        Assert.Equal(105, changed.Npcs[0].X);
        Assert.Equal(1.5f, changed.Npcs[0].Rotation);
        Assert.Equal(scene.Device.Hash, changed.Device.Hash);
        Assert.Equal(scene.Device.Variation, changed.Device.Variation);
        Assert.Equal(scene.Device.Model, changed.Device.Model);
        Assert.Equal(scene, HideoutEditing.Apply(scene, null));
    }
}
