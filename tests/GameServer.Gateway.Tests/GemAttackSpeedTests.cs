using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class GemAttackSpeedTests
{
    // Tornado Shot's GEPL AttackSpeedMultiplier is -20; the supported attack pace must apply it, and the character's
    // increased attack speed must still scale that pace. A Crude Bow's base attack time is 714 ms.
    [Fact]
    public void Tornado_shot_supported_pace_includes_inherent_and_player_attack_speed()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-attack-speed-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "attack-speed", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "AttackSpeedChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
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
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, bow with
            {
                Sockets = [new ItemSocket(SocketColour.White, tornado)],
            });
            var gem = Game.GemSkillEffects(conn).Single(g => g.Name == "Tornado Shot");

            // 714 ms / (1 - 20/100) = 892.5 ms; the client's -20% inherent swing speed is part of the supported pace.
            Assert.Equal(0.8925, Game.SupportedAttackSeconds(conn, gem.Id), 4);

            conn.Pools = conn.Pools with { AttackSpeedPct = 20 };
            Assert.Equal(0.74375, Game.SupportedAttackSeconds(conn, gem.Id), 5);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }

    // Projectile speed is the Projectiles.dat row speed plus the player's virtual projectile_speed_+% (which already
    // includes base_projectile_speed_+%) and the skill's raw base input. Tornado Shot's row is 2173 world units/s.
    [Fact]
    public void Projectile_speed_applies_player_and_skill_increases()
    {
        const double baseline = 2173 / (250.0 / 23.0);
        Assert.Equal(baseline, ProjectileSpeed.CellsPerSecond(2173, _ => 0, _ => 0), 4);
        Assert.Equal(baseline * 1.2, ProjectileSpeed.CellsPerSecond(2173,
            name => name == "projectile_speed_+%" ? 20 : 0, _ => 0), 4);
        Assert.Equal(baseline * 1.1, ProjectileSpeed.CellsPerSecond(2173, _ => 0,
            name => name == "base_projectile_speed_+%" ? 10 : 0), 4);
    }
}
