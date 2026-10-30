namespace GameServer.Gateway.Tests;

public class AuraSaveTests
{
    public AuraSaveTests() => TestSaves.Ensure();

    [Fact]
    public void Aura_toggles_persist_by_effect_without_losing_other_character_data()
    {
        string character = "Auras" + Guid.NewGuid().ToString("N");
        CharacterSaves.Write(character, new CharacterSaves.Save([], SkillBar: new() { [0] = "Grace" }, Experience: 12345));
        CharacterSaves.SetAuraActive(character, "Grace", true);
        CharacterSaves.SetAuraActive(character, "Determination", true);
        CharacterSaves.SetAuraActive(character, "Grace", true);
        var saved = CharacterSaves.Load(character)!;
        Assert.Equal(new[] { "Determination", "Grace" }, saved.ActiveAuras);
        Assert.Equal(12345u, saved.Experience);
        Assert.Equal("Grace", saved.SkillBar![0]);
        CharacterSaves.SetAuraActive(character, "Grace", false);
        Assert.Equal(new[] { "Determination" }, CharacterSaves.Load(character)!.ActiveAuras);
        CharacterSaves.SetAuraActive(character, "Determination", false);
        Assert.Empty(CharacterSaves.Load(character)!.ActiveAuras!);
        Assert.Null(CharacterSaves.Load(character + "other"));
    }

}
