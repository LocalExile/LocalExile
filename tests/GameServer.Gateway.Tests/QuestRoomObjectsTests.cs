using System.Text.Json;
using GameServer.Data.Terrain;

namespace GameServer.Gateway.Tests;

/// <summary>A room with a quest server object (an NPC, a quest object, a quest chest) takes its actual placement for its
/// objects instead of the doodad fit, which can match a repeated room more than once (one NPC per match).</summary>
public sealed class QuestRoomObjectsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "quest-room-objects-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Theory]
    [InlineData("Metadata/NPC/Act1/Fairgraves", true)]
    [InlineData("Metadata/QuestObjects/MerveilExitBlocker", true)]
    [InlineData("Metadata/Chests/QuestChests/Fairgraves/AllFlameSlaveGirl", true)]
    [InlineData("Metadata/NPC/Info/MerveilDiary", false)]
    [InlineData("Metadata/MiscellaneousObjects/Doodad", false)]
    [InlineData("Metadata/Monsters/Zombies/Tolman/TolmanBossSpawner", false)]
    public void Quest_objects_are_npcs_quest_objects_and_quest_chests(string type, bool quest) =>
        Assert.Equal(quest, RoomFile.IsQuestObject(type));

    [Fact]
    public void A_quest_room_takes_its_placement_for_its_objects()
    {
        // Fairgraves' ship room: its doodads fit twice in the capture (at (100, 100) and (300, 300)), rooms.json places it
        // once at (300, 300): one Fairgraves there. The pack room without a quest object keeps both fits.
        string data = Path.Combine(folder, "data");
        Directory.CreateDirectory(Path.Combine(data, "walkability"));
        (int X, int Y)[] doodads = [(1, 2), (5, 9), (12, 3), (8, 14)];
        const string ship = "Metadata/Terrain/Test/ship.arm", pool = "Metadata/Terrain/Test/pack.arm";
        File.WriteAllText(Path.Combine(data, "room-points.json"), JsonSerializer.Serialize(new
        {
            note = "test",
            areas = new Dictionary<string, string[]> { ["9_9_9"] = [ship, pool] },
            rooms = new Dictionary<string, object>
            {
                [ship] = new
                {
                    tag = "intactship", points = Array.Empty<object>(),
                    doodads = doodads.Select(d => (object)new { model = "rock.ao", x = d.X, y = d.Y })
                        .Append(new { model = "Fairgraves.ao", x = 30, y = 40, type = "Metadata/NPC/Act1/Fairgraves", angle = 0, scale = 1 }),
                },
                [pool] = new
                {
                    tag = "pool", points = new[] { new { name = "%area%-nestpack", x = 4, y = 4, angle = 0 } },
                    doodads = doodads.Select(d => new { model = "rock.ao", x = d.X, y = d.Y }),
                },
            },
        }));
        File.WriteAllText(Path.Combine(data, "walkability", "9_9_9_seed1.doodads.json"), JsonSerializer.Serialize(new
        {
            area = "9_9_9", seed = 1, models = new[] { "rock.ao" },
            doodads = new[] { 100, 300 }.SelectMany(o => doodads.Select(d => new[] { 0, o + d.X, o + d.Y, 0 })).ToArray(),
        }));
        File.WriteAllText(Path.Combine(data, "walkability", "9_9_9_seed1.rooms.json"), JsonSerializer.Serialize(new
        {
            area = "9_9_9", seed = 1, rooms = new[] { new { path = ship, x = 300, y = 300, width = 50, height = 50, code = 0 } },
        }));
        var (points, objects, captured) = AreaPopulation.FitRooms("9_9_9", 1, [], data);
        Assert.Equal([("Metadata/NPC/Act1/Fairgraves", 330, 340)], objects.Select(o => (o.Type, o.X, o.Y)));
        Assert.Contains(ship, captured);
        Assert.Equal(2, points.Count(p => p.Name == "%area%-nestpack"));
    }
}
