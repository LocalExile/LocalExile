using System.Text.Json;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public class StartKitTests
{
    // Characters.dat decides the kit; a kit built before admission knew the class would be the Witch's, for every class.
    [Fact]
    public void Admission_gives_each_class_its_own_kit_and_the_preflight_builds_none()
    {
        using var characters = JsonDocument.Parse(File.ReadAllBytes("data/tables/Characters.json"));
        using var gems = JsonDocument.Parse(File.ReadAllBytes("data/tables/SkillGems.json"));
        string path = Path.Combine(Path.GetTempPath(), $"localexile-start-kit-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        string? Gem(JsonElement row, string column) =>
            Program.Items.Row(gems.RootElement[row.GetProperty(column).GetInt32()].GetProperty("BaseItemTypesKey").GetInt32())?.Name;
        string? Name(uint hash) => Program.Items.ByHash(hash)?.Name;

        var names = new List<string>();
        try
        {
            foreach (var (row, classRow) in characters.RootElement.EnumerateArray().Select((row, i) => (row, i)))
            {
                string character = $"StartKit{classRow}{Guid.NewGuid():N}";
                names.Add(character);
                var conn = new ClientState(new(1, "start-kit", 1000, 1000, 100, 100, null, []), 1, 1)
                {
                    Character = character, ClassRow = classRow, Instance = new(),
                    LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
                };

                Game.ValidateSavedProgress(conn, null);
                Assert.Null(CharacterSaves.Load(character));

                Game.PrepareCharacterForArea("start-kit", conn, 1);
                Assert.Equal(Program.Items.Row(row.GetProperty("StartWeapons")[0].GetInt32())?.Name, Name(Game.ItemsOf(conn).StartWeapon!.TypeHash));
                var tutorial = CharacterSaves.Load(character)!.Tutorial!;
                Assert.Equal(Gem(row, "StartSkillGem"), Name(tutorial.SkillGem.Type));
                Assert.Equal(Gem(row, "StartSupportGem"), Name(tutorial.SupportGem.Type));
            }
        }
        finally
        {
            foreach (string character in names) PlayerInventories.Forget(character);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
