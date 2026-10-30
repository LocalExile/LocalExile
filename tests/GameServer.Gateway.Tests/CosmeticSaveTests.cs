namespace GameServer.Gateway.Tests;

/// <summary>
/// A character's portal cosmetic is saved with it, so /cosmetic portal survives a relog. The save database is one
/// process-wide database for the test run (TestSaves), so every test uses its own character name.
/// </summary>
public class CosmeticSaveTests
{
    public CosmeticSaveTests() => TestSaves.Ensure();

    static string Character() => $"Cosmetic{Guid.NewGuid():N}"[..20];

    [Fact]
    public void A_saved_portal_cosmetic_comes_back_for_that_character_only()
    {
        string character = Character();
        CharacterSaves.SetPortalCosmetic(character, 6);
        Assert.Equal(6, CharacterSaves.PortalCosmetic(character));
        // No field at all for another character: the base effect.
        Assert.Equal(0, CharacterSaves.PortalCosmetic(Character()));
    }

}
