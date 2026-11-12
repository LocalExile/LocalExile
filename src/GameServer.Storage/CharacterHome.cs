namespace GameServer.Storage;

/// <summary>Shared, uncached read of the Gateway's character home for Auth's next login assignment.</summary>
public static class CharacterHome
{
    public static string? SelectedHideout(SaveDatabase database, string character) =>
        database.CharacterText(character, "selected_hideout", "SelectedHideout");
}
