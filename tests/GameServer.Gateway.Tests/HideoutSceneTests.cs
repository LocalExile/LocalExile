using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class HideoutSceneTests
{
    [Fact]
    public void Choice_is_shared_with_auth_and_preserves_character_progress()
    {
        TestSaves.Ensure();
        string name = "Home" + Guid.NewGuid().ToString("N");
        CharacterSaves.Write(name, new CharacterSaves.Save([], Experience: 12345, ActiveAuras: ["Grace"]));
        CharacterSaves.SetSelectedHideout(name, "HideoutShapersRealm");
        Assert.Equal("HideoutShapersRealm", CharacterHome.SelectedHideout(Saves.Db, name));
        Assert.Equal(12345u, CharacterSaves.Load(name)!.Experience);
        Assert.Equal(new[] { "Grace" }, CharacterSaves.Load(name)!.ActiveAuras);
        CharacterSaves.SetSelectedHideout(name, "HideoutForest");
        Assert.Equal("HideoutForest", CharacterHome.SelectedHideout(Saves.Db, name));
        Assert.Null(CharacterHome.SelectedHideout(Saves.Db, name + "other"));
    }

}
