using GameServer.Core.Protocol.Instance;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// C→S 0x32, "Click to level up ALL": one click levels every ready socketed gem by one level and leaves the rest alone.
public sealed class GemLevelUpAllTests
{
    [Fact]
    public void Level_up_all_takes_only_ready_gems_and_only_once()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-level-all-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "level-all", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "LevelAll" + Guid.NewGuid().ToString("N"), PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        conn.Clock.Stop();
        try
        {
            var items = Game.ItemsOf(conn);
            var fireball = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var type = Program.Items.Gem(Program.Items.ByHash(fireball.TypeHash)!)!;
            int nextThreshold = type.ExperienceFor(2);
            var ready = fireball with { GemExperience = nextThreshold - 1 };
            var notReady = fireball with { GemExperience = type.ExperienceFor(1) };
            var locked = ready with { GemFlags = 4 };
            var maxed = fireball with { GemExperience = type.ExperienceFor(type.MaxLevel) };
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
            {
                Sockets = [new ItemSocket(SocketColour.White, ready), new ItemSocket(SocketColour.White, notReady),
                    new ItemSocket(SocketColour.White, locked), new ItemSocket(SocketColour.White, maxed)],
                Links = [4],
            };
            items.Add(PlayerInventories.Weapon1, 0, 0, wand);

            var changed = Game.LevelUpReadyGems(conn);
            var row = Assert.Single(changed);
            var gemChange = Assert.Single(row.Gems);
            Assert.Equal(ready, gemChange.Before);
            Assert.Equal(nextThreshold, gemChange.After.GemExperience);

            var host = Assert.Single(Game.ItemsOf(conn).Items(PlayerInventories.Weapon1));
            Assert.Equal(nextThreshold, host.Item.Sockets[0].Gem!.GemExperience);
            Assert.Equal(type.ExperienceFor(1), host.Item.Sockets[1].Gem!.GemExperience);
            Assert.Equal(nextThreshold - 1, host.Item.Sockets[2].Gem!.GemExperience);
            Assert.Equal(type.ExperienceFor(type.MaxLevel), host.Item.Sockets[3].Gem!.GemExperience);

            // A second click finds nothing ready (the levelled gem is at its new level's threshold), so no duplicate level.
            Assert.Empty(Game.LevelUpReadyGems(conn));

            // Experience alone never levels a gem: it stops one point below the next level's threshold and waits for a click.
            var (_, autoLevelled) = Game.CommitGemExperience(conn, 1_000_000);
            Assert.False(autoLevelled);
            var afterXp = Assert.Single(Game.ItemsOf(conn).Items(PlayerInventories.Weapon1)).Item;
            Assert.Equal(type.ExperienceFor(3) - 1, afterXp.Sockets[0].Gem!.GemExperience);
            Assert.Equal(2, type.Level(afterXp.Sockets[0].Gem!.GemExperience));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
