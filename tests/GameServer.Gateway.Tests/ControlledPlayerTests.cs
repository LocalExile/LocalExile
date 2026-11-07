using GameServer.Core.Accounts;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

internal static class ControlledPlayerBuildChecks
{
    internal static void VerifyCampaignBuilds()
    {
        string path = Path.Combine(Path.GetTempPath(), $"controlled-player-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var names = new List<string>();
        try
        {
            var accounts = new SqliteAccountStore(Saves.Db);
            foreach (var characterClass in Enum.GetValues<CharacterClass>())
                foreach (int level in new[] { 1, 25, 63, 75 })
                {
                    string name = $"BotBuild{(int)characterClass}L{level}";
                    names.Add(name);
                    var record = new Character(Guid.NewGuid(), Guid.NewGuid(), name, characterClass, level,
                        "Standard", "1_1_town", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    accounts.AddCharacter(record);
                    var build = Game.BuildControlledPlayer(characterClass, level);
                    Assert.Equal(level - 1, build.Passives!.Count);
                    CharacterSaves.Write(name, build);
                    int row = Program.Skills.RowOfClass((int)characterClass);
                    var conn = new ClientState(new(1, "controlled-build-test", 1000, 1000, 100, 100, null, []), 1, 1)
                    { Character = name, CharacterId = record.Id, League = "Standard", ClassRow = row,
                        Pools = CharacterStats.For(Program.Skills.ClassFor(row), level) };
                    Assert.Equal(level, Game.PrepareCharacterForArea("test", conn, level));
                    Game.CharacterStatSources(conn);
                    Assert.Empty(CharacterEquipmentState.Inactive[name]);
                    var gems = Game.GemSkillEffects(conn).ToArray();
                    var active = Assert.Single(gems);
                    var supported = Game.SupportedSkill(conn, active.Id);
                    Assert.True(supported.RequirementsMet, $"{name}: {active.Name} requirements");
                    Assert.True(Game.SupportedHitFor(conn, active.Id, suppress: false).MinTotal > 0, name);
                    if (level >= 25) Assert.True(supported.Supports.Count == 2,
                        $"{name}: {active.Name}, supports: {string.Join(", ", supported.Supports.Select(s => s.Effect.Id))}");
                    var native = Game.PlayerForCreation(conn);
                    Assert.Contains(native.Skills!, s => s.Id == active.Id);
                }
        }
        finally
        {
            foreach (string name in names) { PlayerInventories.Forget(name); CharacterEquipmentState.Replaced(name); }
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
