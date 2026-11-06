using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class VolleySupportTests
{
    // The native client's parallel volley layout function (0x141bb89d0) reads
    // virtual_additional_projectiles_fire_parallel_x_dist (0x18b2); Volley Support must publish it.
    [Fact]
    public void Volley_support_populates_virtual_parallel_projectile_x_distance()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-volley-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "volley", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "VolleyChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var wand = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1") ?? Program.Items.Find("Driftwood Wand")!);
            var fireball = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var volley = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemParallelProjectiles")!);
            var spark = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSpark")!);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wand with
            {
                Sockets = [new ItemSocket(SocketColour.White, fireball), new ItemSocket(SocketColour.White, volley),
                    new ItemSocket(SocketColour.White, spark)], Links = [2, 1],
            });
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            var skills = Game.PlayerSkills(conn);
            var fb = skills.Single(s => Game.SkillEffectId(conn, s.Id) == "Fireball");
            uint xDistKey = Program.Stats.Key("virtual_additional_projectiles_fire_parallel_x_dist")!.Value;
            Assert.NotNull(fb.Stats);
            Assert.Contains((xDistKey, 80), fb.Stats!);
            // A targeted skill delta must retain the full build's rows, while omitting unrelated gem panels.
            var selected = Game.PlayerSkills(conn, (id, _) => id == fb.Id);
            Assert.Contains(skills, s => Game.SkillEffectId(conn, s.Id) == "Spark");
            Assert.DoesNotContain(selected, s => Game.SkillEffectId(conn, s.Id) == "Spark");
            Assert.Equal(fb.Stats, selected.Single(s => s.Id == fb.Id).Stats);
            Assert.DoesNotContain(Game.PlayerSkills(conn, (_, _) => false), s => s.Id == fb.Id);
            Assert.Same(skills, conn.PlayerSkillsCache); // A partial build cannot poison the complete-list cache.
            uint momentum = Program.Stats.Key("base_current_momentum")!.Value;
            conn.PlayerStats[momentum] = 0;
            using (new Game.PlayerStatScope(conn))
            {
                Assert.Equal(0, Game.CharacterPlayerStats(conn)[momentum]);
                conn.PlayerStats[momentum] = 2; // Post-admission state, while the action retains its old snapshot.
                var updated = Game.PlayerSkills(conn, (id, _) => id == fb.Id).Single(s => s.Id == fb.Id);
                Assert.Contains((momentum, 2), updated.Stats!);
                Assert.Equal(0, Game.CharacterPlayerStats(conn)[momentum]);
            }
            Assert.Same(skills, conn.PlayerSkillsCache);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }
}
