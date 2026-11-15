using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

// Bino's Kitchen Knife (Modules/Core/Game/Combat/PoisonSpreadOnKill.cs): its definition has the spread mod, and a kill of
// a poisoned enemy with it worn spreads the strongest stack within 3 metres and starts the wielder's regeneration.
public sealed class BinosKitchenKnifeTests
{
    [Fact]
    public void Killing_a_poisoned_enemy_spreads_its_strongest_poison_and_regenerates_life()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-binos-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            var conn = new ClientState(AreaLayouts.For(AreaLayouts.LioneyesWatch, 1), 1, 1)
            {
                Character = "Binos" + Guid.NewGuid().ToString("N"), ClassRow = 1, PlayerSent = true, Instance = new(),
            };
            conn.Clock.Stop();
            var items = Game.ItemsFor(conn);
            var knife = new UniqueCatalog(items, "data").Create("Bino's Kitchen Knife", new Random(1));
            Assert.NotNull(knife);
            Assert.Contains(knife.Mods, m => m.Hash == items.Mods.ById("PoisonSpreadAndHealOnPoisonedKillUniqueDagger8")!.Hash);
            conn.PlayerStats[Program.Stats.Key("additional_dexterity")!.Value] = 300;
            conn.PlayerStats[Program.Stats.Key("additional_intelligence")!.Value] = 300;
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, knife);
            conn.Pools = Game.PoolsFor(conn, 85);
            conn.PlayerLife = conn.Pools.Life;
            _ = Game.CharacterStatSources(conn);

            double t = Game.ProjectileWorldTime(conn);
            var (x, y) = conn.Walk.Position(t);
            SpawnedMonster At(int dx) => new("enemy", new PlayerWalk(x + dx, y, 0), 100, 8, 100000) { Life = 100000 };
            var dead = At(0);
            var near = At(25);
            var far = At(60);
            dead.Poisons.Add(t, 50, 4);
            dead.Poisons.Add(t, 200, 3);
            dead.Dead = true; dead.Life = 0;
            dead.Poisons.Clear();
            lock (conn.Monsters) { conn.Monsters[1] = dead; conn.Monsters[2] = near; conn.Monsters[3] = far; }

            Game.SpreadPoisonOnKill(new KillContext(conn, dead, "enemy", (x, y)) { CommittedOutput = [] });

            Assert.Equal(1, near.Poisons.Stacks(t));
            Assert.Equal(200, near.Poisons.TotalDps(t));
            Assert.Equal(t + 2, near.Poisons.Until, 3);
            Assert.False(far.Poisons.Poisoned);
            // A kill that is not the player's (a minion's) spreads nothing.
            Game.SpreadPoisonOnKill(new KillContext(conn, dead, "enemy", (x, y)) { PlayerKill = false, CommittedOutput = [] });
            Assert.Equal(1, near.Poisons.Stacks(t));

            uint regen = Program.Stats.Key("base_life_regeneration_rate_per_minute")!.Value;
            int before = Game.CharacterStatSources(conn).Stats.GetValueOrDefault(regen);
            Assert.NotNull(Game.SpreadPoisonRegenerationPresentation(conn));
            Assert.Equal(before + SpreadPoisonRegeneration.PerMinute, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(regen));
            // Death ends it.
            conn.PlayerLife = 0;
            Assert.NotNull(Game.SpreadPoisonRegenerationPresentation(conn));
            Assert.False(Game.SpreadPoisonRegenerationOf(conn).Applied);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
