using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

/// <summary>The pair-resistance stats raise both elements they name (Mistwall's fire and cold).</summary>
public class UniqueResistanceTests
{
    [Fact]
    public void Pair_resistance_on_a_unique_raises_both_named_elements()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-pairres-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "pairres", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "PairResChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(), God = true,
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var before = Game.PoolsFor(conn, 80);
            var items = Program.Items;
            var mistwall = Assert.IsType<Item>(new UniqueCatalog(items).Create("Mistwall", new Random(17)));
            int pair = ItemModStats.Sum([mistwall], items.Mods).GetValueOrDefault("fire_and_cold_damage_resistance_%");
            Assert.True(pair > 0);
            Game.ItemsOf(conn).Add(PlayerInventories.Offhand1, 0, 0, mistwall);
            var after = Game.PoolsFor(conn, 80);
            Assert.Equal(before.FireResistance + pair, after.FireResistance);
            Assert.Equal(before.ColdResistance + pair, after.ColdResistance);
            Assert.Equal(before.LightningResistance, after.LightningResistance);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
