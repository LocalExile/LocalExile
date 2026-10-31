using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

// Progress that is not items: saved as changes to the saved state, so several connections or tasks of one character
// never write an older copy over a newer one; and loads that never drop what they don't know.
public class ProgressSaveTests : IDisposable
{
    const uint WandType = 0xd9b1a12f;
    readonly string _folder = TestSaves.Catalog(new TestSaves.Type(WandType, "Wand", 1, 3));
    readonly string _character = $"Prog{Guid.NewGuid():N}"[..20];

    public ProgressSaveTests() => TestSaves.Ensure();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    // A level table (index = level − 1) like ExperienceLevels.dat's start: these tests run without the game data.
    static readonly uint[] Levels = [0, 525, 1760, 3781, 7184, 12186, 19324, 29377, 43181, 61693, 85990];
    static uint Plus(uint xp, uint gain) => Progression.AddExperience(xp, gain, Levels);

    [Fact]
    public void Delve_completion_preserves_other_progress_and_never_reduces_depth()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], Experience: 123, Sulphite: 500));
        CharacterSaves.CompleteDelveDepth(_character, 3);
        CharacterSaves.CompleteDelveDepth(_character, 2);
        var save = CharacterSaves.Load(_character)!;
        Assert.Equal(3, save.CompletedDelveDepth);
        Assert.Equal(123u, save.Experience);
        Assert.Equal((ushort)500, save.Sulphite);
    }

    [Fact]
    public void Experience_adds_to_the_saved_total_not_to_a_connections_copy()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], Experience: 1000));

        // An old connection (its copy: 1000) and the new one (copy: also 1000) both gain: both gains count.
        Assert.Equal((1000u, 1100u), CharacterSaves.AddExperience(_character, 1000, xp => Plus(xp, 100)));
        Assert.Equal((1100u, 1150u), CharacterSaves.AddExperience(_character, 1000, xp => Plus(xp, 50)));
        Assert.Equal(1150u, CharacterSaves.Load(_character)!.Experience);
    }

    [Fact]
    public void Experience_gained_by_many_tasks_at_once_all_counts()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], Experience: 0));

        Parallel.For(0, 100, _ => CharacterSaves.AddExperience(_character, 0, xp => Plus(xp, 3)));

        Assert.Equal(300u, CharacterSaves.Load(_character)!.Experience);
    }

    [Fact]
    public void The_level_comes_from_the_saved_experience_not_the_accounts_copy()
    {
        // The account still says 5: the level-up's update to Auth failed.
        Assert.Equal(10, CharacterSaves.LevelOf(new CharacterSaves.Save([], Experience: Levels[9] + 1), accountLevel: 5, Levels));
        Assert.Equal(1, CharacterSaves.LevelOf(new CharacterSaves.Save([], Experience: 0), accountLevel: 5, Levels));
        // A save without experience (from before experience was saved) takes the account's.
        Assert.Equal(5, CharacterSaves.LevelOf(new CharacterSaves.Save([]), accountLevel: 5, Levels));
        Assert.Equal(1, CharacterSaves.LevelOf(null, accountLevel: 0, Levels));
    }

    [Fact]
    public void Quest_flags_and_waypoints_another_connection_saved_stay()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([], QuestFlags: ["a1q1-KilledHillock"], Waypoints: ["1_1_town"]));

        // This connection loaded before them and sets its own.
        CharacterSaves.ChangeQuestFlags(_character, ["a1q4-PlacedGlyphs"], on: true);
        CharacterSaves.AddWaypoints(_character, ["1_1_2"]);
        CharacterSaves.AddVisited(_character, ["1_1_2"]);
        var save = CharacterSaves.Load(_character)!;
        Assert.Equal(["a1q1-KilledHillock", "a1q4-PlacedGlyphs"], save.QuestFlags);
        Assert.Equal(["1_1_town", "1_1_2"], save.Waypoints);
        Assert.Equal(["1_1_2"], save.Visited);

        CharacterSaves.ChangeQuestFlags(_character, ["a1q1-KilledHillock"], on: false);
        Assert.Equal(["a1q4-PlacedGlyphs"], CharacterSaves.Load(_character)!.QuestFlags);
    }

    [Fact]
    public void Save_fields_this_build_does_not_know_are_kept_by_the_next_write()
    {
        Saves.Db.Put(SaveDatabase.Table.Characters, _character, """{"Inventories":{},"Experience":5,"GemLocks":[1,2],"Ascendancy":{"Class":"Elementalist"}}""");

        CharacterSaves.SetExperience(_character, 9);

        string json = Saves.Get(SaveDatabase.Table.Characters, _character)!;
        Assert.Contains("\"GemLocks\"", json);
        Assert.Contains("Elementalist", json);
        Assert.Equal(9u, CharacterSaves.Load(_character)!.Experience);
    }

    [Fact]
    public void Stash_fields_this_build_does_not_know_are_kept_by_the_next_write()
    {
        string account = $"Acc{Guid.NewGuid():N}"[..16];
        Saves.Db.Put(SaveDatabase.Table.Stashes, account, """{"Tabs":[],"Gold":3,"Folders":["Maps"]}""");

        AccountStash.For(account, new ItemCatalog(_folder)).AddGold(1);

        Assert.Contains("\"Folders\"", Saves.Get(SaveDatabase.Table.Stashes, account));
    }

    [Fact]
    public void A_save_holding_an_item_the_game_data_does_not_know_stops_the_load_and_is_not_written()
    {
        var items = PlayerInventories.For(_character, new ItemCatalog(_folder));
        items.Add(PlayerInventories.MainInventory, 0, 0, new Item(WandType, ["Base"], 0x40a1));
        PlayerInventories.Forget(_character);
        string saved = Saves.Get(SaveDatabase.Table.Characters, _character)!;

        // The game data missing (not extracted): the login stops, the save stays as it is.
        Assert.Throws<UnknownSavedItemException>(() => PlayerInventories.For(_character, new ItemCatalog(Path.Combine(_folder, "none"))));
        Assert.Equal(saved, Saves.Get(SaveDatabase.Table.Characters, _character));

        // With the data, loading writes nothing (it changes nothing), and the item is there.
        var again = PlayerInventories.For(_character, new ItemCatalog(_folder));
        Assert.Equal(saved, Saves.Get(SaveDatabase.Table.Characters, _character));
        Assert.Single(again.Items(PlayerInventories.MainInventory));
    }

    [Fact]
    public void A_socketed_gem_the_game_data_does_not_know_stops_the_load()
    {
        var gem = new CharacterSaves.SavedItem(0xdeadbeef);
        var wand = new CharacterSaves.SavedItem(WandType, Sockets: [new(SocketColour.Blue, gem)]);

        Assert.Throws<UnknownSavedItemException>(() => CharacterSaves.FromSaved(wand, new ItemCatalog(_folder)));
    }
}
