using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class BloodMagicTests
{
    // The Blood Magic keystone turns a skill's whole Mana cost into a Life cost: Tornado Shot's 9 Mana is paid as
    // 9 Life, Mana stays full, and no Lifetap buff comes with it (that buff belongs to the support gem).
    [Fact]
    public void Blood_magic_keystone_pays_skills_with_life()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-bloodmagic-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "bloodmagic", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "BloodMagicChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var bow = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!);
            var tornado = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemTornadoShot")!);
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            conn.PlayerStats[Program.Stats.Key("keystone_blood_magic")!.Value] = 1;
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, bow with
            {
                Sockets = [new ItemSocket(SocketColour.White, tornado)],
                Links = [1],
            });
            var gem = Game.GemSkillEffects(conn).Single(g => g.Name == "Tornado Shot");
            Assert.True(Game.PayMana(conn, gem.Id, out uint cost, out uint lifeCost));
            Assert.Equal(0u, cost);
            Assert.Equal(9u, lifeCost);
            Assert.Equal(991u, conn.PlayerLife);
            Assert.Equal(1000u, conn.PlayerMana);
            Assert.False(conn.Lifetap.ActiveAt(conn.Clock.Elapsed.TotalSeconds));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }
}
