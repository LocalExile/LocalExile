using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

internal static class ControlledPlayerBuildChecks
{
    internal static async Task VerifyReservationAttackSelection(PartyFollowerTests.Runtime runtime, PartyFollowerTests.Wire wire)
    {
        var instance = new InstanceState();
        var leader = runtime.Connection(instance, wire);
        var bot = runtime.Connection(instance, wire, "HeraldFollower" + Guid.NewGuid().ToString("N"));
        bot.Pools = new(25, 1000, 1000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000);
        bot.Passives = [.. Game.BuildControlledPlayer(CharacterClass.Witch, 25).Passives!];
        bot.LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]));
        // Inspect the AI's submitted request without starting background player updates or a cast timer.
        bot.PlayerSent = false;
        var selected = new List<ushort>();
        using var requests = GameEvents.Packet(InstanceTransfer.UseSkillOnObjectId).Add("Game", (conn, bytes) =>
        {
            if (!ReferenceEquals(conn, bot)) return Task.FromResult(false);
            selected.Add(InstanceTransfer.ReadUseSkillOnObject(bytes).SkillId);
            return Task.FromResult(true);
        });
        try
        {
            var items = Game.ItemsOf(bot);
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!);
            ItemSocket Socket(string name) => new(SocketColour.White, ItemCatalog.Create(Program.Items.FindGem(name)!.Value.Type));
            items.Add(PlayerInventories.Weapon1, 0, 0, wand with
            { Sockets = [Socket("Herald of Ice"), Socket("Herald of Thunder"), Socket("Fireball")] });
            var gems = Game.GemSkillEffects(bot).ToArray();
            Assert.Equal(new[] { "Herald of Ice", "Herald of Thunder", "Fireball" }, gems.Select(g => g.Name));
            Assert.All(gems, g => Assert.True(SkillHitPlan.ForSkill(Program.Skills, g.Effect, g.Level, g.Quality).CanHit));
            instance.World.Monsters[0x08000000] = new("follower target", new(110, 100, 0), 0, 0, 1000) { Hostile = true };
            await Game.StepControlledPlayer(bot, leader);
            Assert.Equal(gems[2].Id, Assert.Single(selected));

            // With only reactive reservation hits equipped, automatic combat leaves their toggles alone.
            selected.Clear();
            bot.ControlledNextAction = 0;
            var equipped = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, equipped.Id, item => item with { Sockets = [.. item.Sockets.Take(2)] });
            await Game.StepControlledPlayer(bot, leader);
            Assert.Empty(selected);
        }
        finally
        {
            PlayerInventories.Forget(bot.Character!);
            CharacterEquipmentState.Replaced(bot.Character!);
        }
    }

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
                    var conn = new Player(new(1, "controlled-build-test", 1000, 1000, 100, 100, null, []), 1, 1)
                    { Character = name, CharacterId = record.Id, League = "Standard", ClassRow = row,
                        Pools = CharacterStats.For(Program.Skills.ClassFor(row), level) };
                    Assert.Equal(level, Game.PrepareCharacterForArea(conn, level));
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
