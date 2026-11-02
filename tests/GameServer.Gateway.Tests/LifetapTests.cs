using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class LifetapTests
{
    // Lifetap's 300% cost multiplier turns Tornado Shot's 9 mana into 27 Life; the first spend reaches the level-1
    // 23-Life threshold, so the same use also grants the buff. While it runs, supported hits get 10% more damage and
    // flasks get 20% increased Life recovery; ending the buff clears both without touching Mana.
    [Fact]
    public void Supported_skill_pays_life_and_grants_the_lifetap_buff()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-lifetap-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "lifetap", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "LifetapChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var bow = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!);
            var tornado = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemTornadoShot")!);
            var lifetap = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemLifetap")!);
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, bow with
            {
                Sockets = [new ItemSocket(SocketColour.White, tornado), new ItemSocket(SocketColour.White, lifetap)],
                Links = [2],
            });
            var gem = Game.GemSkillEffects(conn).Single(g => g.Name == "Tornado Shot");
            var supported = Game.SupportedSkill(conn, gem.Id);
            Assert.True(Lifetap.Supported(supported.Stats));
            Assert.Equal(27, Lifetap.Stat(supported.Stats, "mana_cost"));
            Assert.Equal(23, Lifetap.Stat(supported.Stats, "support_lifetap_spent_life_threshold"));
            Assert.Equal(10, Lifetap.Stat(supported.Stats, "support_lifetap_damage_+%_final_while_buffed"));
            Assert.Equal(4, Lifetap.DurationSeconds(supported.Stats), 6);

            Assert.True(Game.PayMana(conn, gem.Id, out uint cost, out uint lifeCost));
            Assert.Equal(0u, cost);
            Assert.Equal(27u, lifeCost);
            Assert.Equal(973u, conn.PlayerLife);
            Assert.Equal(1000u, conn.PlayerMana);
            Assert.True(conn.Lifetap.ActiveAt(conn.Clock.Elapsed.TotalSeconds));

            Assert.Equal(1.1, Game.ConditionalSupportMore(supported.Stats, attack: true, chilled: false, lowLife: false,
                fullLife: false, lifetap: true), 6);
            Assert.Equal(1, Game.ConditionalSupportMore(supported.Stats, attack: true, chilled: false, lowLife: false,
                fullLife: false, lifetap: false));
            Assert.Equal(20, Lifetap.FlaskRecovery(conn));

            conn.Lifetap.Clear();
            Assert.False(conn.Lifetap.ActiveAt(conn.Clock.Elapsed.TotalSeconds));
            Assert.Equal(0, Lifetap.FlaskRecovery(conn));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
