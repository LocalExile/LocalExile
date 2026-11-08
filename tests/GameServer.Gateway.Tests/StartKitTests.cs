using System.Text.Json;
using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
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
                // The initial recipient gets one create/wake pair. Shared-ground sync must know that recipient,
                // while a later connection still sees the item through the existing shared-ground path.
                foreach (bool support in new[] { false, true })
                {
                    var existingObserver = new object();
                    conn.Instance.Ground.Observe(existingObserver);
                    var packets = QuestobjectsRules.CommitTutorialGem(conn, support, 100, 100);
                    Assert.Equal(2, packets.Count);
                    Assert.Equal(0x2b9, BinaryPrimitives.ReadUInt16BigEndian(packets[0].Packet));
                    Assert.Equal(0x1a9, BinaryPrimitives.ReadUInt16BigEndian(packets[1].Packet));
                    uint id = BinaryPrimitives.ReadUInt32BigEndian(packets[0].Packet.AsSpan(2));
                    Assert.Equal(id, BinaryPrimitives.ReadUInt32BigEndian(packets[1].Packet.AsSpan(2)));
                    var origin = conn.Instance.Ground.Origin(id);
                    Assert.Same(conn, origin.By);
                    Assert.True(origin.Shared);
                    var ground = Assert.IsType<WorldItemObject>(conn.Instance.Ground.Get(id));
                    Assert.Equal(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath),
                        ground.Blob(justDropped: true)), packets[0].Packet);
                    Assert.True(conn.Instance.Ground.IsNewFor(existingObserver, id));
                    var laterObserver = new object();
                    conn.Instance.Ground.Observe(laterObserver);
                    Assert.False(conn.Instance.Ground.IsNewFor(laterObserver, id));
                    Assert.Empty(QuestobjectsRules.CommitTutorialGem(conn, support, 100, 100));
                }
            }
        }
        finally
        {
            foreach (string character in names) PlayerInventories.Forget(character);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }
}
