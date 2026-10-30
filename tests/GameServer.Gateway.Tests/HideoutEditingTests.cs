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

}
