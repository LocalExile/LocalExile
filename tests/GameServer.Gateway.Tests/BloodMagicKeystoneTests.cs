using GameServer.Core.Protocol.Instance;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class BloodMagicKeystoneTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    [Fact]
    public void Blood_Magic_removes_all_mana_and_converts_skill_reservations_to_life()
    {
        var items = new ItemCatalog("data");
        var graceBase = items.Find("Metadata/Items/Gems/SkillGemGrace")!;
        var graceGem = ItemCatalog.Create(graceBase);
        var helmetBase = items.Find("Iron Hat")!;
        var helmet = ItemCatalog.Create(helmetBase) with
        {
            Sockets = [new(SocketColour.Green, graceGem)],
            Links = [1],
        };

        string path = Path.Combine(Path.GetTempPath(), $"blood-magic-{Guid.NewGuid():N}.db");
        var oldLogic = Program.GameLogicOrNull;
        var oldLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "bm", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "Hero" + Guid.NewGuid().ToString("N")[..8],
            ClassRow = 0,
            Experience = CharacterStats.LevelStarts[80],
            PlayerLife = 3000,
            PlayerMana = 2000,
            PlayerSent = true,
            Instance = new(),
            Pools = new(80, 3000, 2000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
            conn.PlayerStats[Key(name)] = 1000;
        try
        {
            Game.ItemsOf(conn).Add(PlayerInventories.Helmet, 0, 0, helmet);
            var skill = Game.GemSkillEffects(conn).Single(g => g.Name == "Grace");

            // Before Blood Magic: positive Mana pool, Grace reserves 50% Mana
            conn.Pools = Game.PoolsFor(conn, 80);
            Assert.True(conn.Pools.Mana > 0);
            var beforeRes = Assert.IsType<SkillCatalog.ReservationCost>(Game.SupportedReservationCost(conn, skill.Id));
            Assert.Equal(5000, beforeRes.ManaPercent);
            Assert.Equal(0, beforeRes.LifePercent);
            Assert.False(Game.ReservationContext(conn, skill.Id).Life);

            // Allocate Blood Magic (57279: keystone_blood_magic = 1)
            conn.Passives.Add(57279);
            conn.Pools = Game.PoolsFor(conn, 80);
            Assert.Equal(0u, conn.Pools.Mana);

            var afterRes = Assert.IsType<SkillCatalog.ReservationCost>(Game.SupportedReservationCost(conn, skill.Id));
            Assert.Equal(0, afterRes.ManaPercent);
            Assert.Equal(5000, afterRes.LifePercent);
            Assert.True(Game.ReservationContext(conn, skill.Id).Life);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = oldLifetime;
            Program.SetGameLogic(oldLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
