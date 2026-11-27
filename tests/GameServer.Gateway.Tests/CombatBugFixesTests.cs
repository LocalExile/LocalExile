using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class CombatBugFixesTests
{
    static Player CreateTestPlayer(string name)
    {
        var player = new Player(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = name },
            PlayerLife = 1000,
            PlayerMana = 100,
            Instance = new(),
            Pools = new(100, 1000, 100, 0, 0),
        };
        player.Clock.Stop();
        return player;
    }

    [Fact]
    public async Task Player_curses_deduplicate_across_sources_and_Temporal_Chains_clamps_slow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"combat-bugfixes-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            using var wire = new PartyFollowerTests.Wire();
            var player = CreateTestPlayer("CurseDedup");
            player.Connection.Session = new("curse-dedup-test", wire.Stream, wire.Ciphers);

            const ushort temporalChainsRow = 69;
            const ushort vulnerabilityRow = 79;
            object heldTc = new(), heldVuln = new();

            // Two timed Temporal Chains from different casters + one held source must not stack.
            await Game.AddPlayerDebuff(player, temporalChainsRow, caster: 101,
                new Dictionary<uint, int> { [Stat.TemporalChainsActionSpeedPlusPctFinal] = -20 }, 10);
            await Game.AddPlayerDebuff(player, temporalChainsRow, caster: 102,
                new Dictionary<uint, int> { [Stat.TemporalChainsActionSpeedPlusPctFinal] = -35 }, 10);
            await Game.HoldPlayerDebuff(player, heldTc, temporalChainsRow, caster: 103,
                new Dictionary<uint, int> { [Stat.TemporalChainsActionSpeedPlusPctFinal] = -15 });

            var sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(-35, sources.GetValueOrDefault(Stat.TemporalChainsActionSpeedPlusPctFinal));
            Assert.Equal(0.65, Game.PlayerActionFactorFrom(player, sources.GetValueOrDefault), precision: 4);

            // Extreme Temporal Chains slow clamps at -75% (0.25 action factor), never slowing action speed to 0.
            await Game.AddPlayerDebuff(player, temporalChainsRow, caster: 104,
                new Dictionary<uint, int> { [Stat.TemporalChainsActionSpeedPlusPctFinal] = -95 }, 10);
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(-95, sources.GetValueOrDefault(Stat.TemporalChainsActionSpeedPlusPctFinal));
            Assert.Equal(0.25, Game.PlayerActionFactorFrom(player, sources.GetValueOrDefault), precision: 4);

            // Unaffected by Temporal Chains suppresses the action speed slow completely.
            sources[Stat.UnaffectedByTemporalChains] = 1;
            Assert.Equal(1.0, Game.PlayerActionFactorFrom(player, sources.GetValueOrDefault), precision: 4);

            // Non-TC curses also deduplicate across held and timed sources (strongest wins).
            await Game.HoldPlayerDebuff(player, heldVuln, vulnerabilityRow, caster: 201,
                new Dictionary<uint, int> { [Stat.PhysicalDamageTakenPlusPct] = 22 });
            await Game.AddPlayerDebuff(player, vulnerabilityRow, caster: 202,
                new Dictionary<uint, int> { [Stat.PhysicalDamageTakenPlusPct] = 35 }, 10);
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(35, sources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));

            // Releasing the held Vulnerability leaves the timed +35% intact; holding a stronger one replaces it.
            await Game.ReleasePlayerDebuff(player, heldVuln);
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(35, sources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));
            await Game.HoldPlayerDebuff(player, heldVuln, vulnerabilityRow, caster: 203,
                new Dictionary<uint, int> { [Stat.PhysicalDamageTakenPlusPct] = 45 });
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(45, sources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));
            await Game.ReleasePlayerDebuff(player, heldVuln);
            sources = Game.PlayerDebuffStats(player, []);
            Assert.Equal(35, sources.GetValueOrDefault(Stat.PhysicalDamageTakenPlusPct));

            // Map Temporal Chains must not stack on top of an already-stronger debuff Temporal Chains.
            var area = new MapDevice.MapArea(player.Layout.Hash, "MapWorldsStrand", "Strand", 1);
            var map = new MapMods(new Dictionary<uint, int> { [Stat.MapPlayerHasLevelXTemporalChains] = 15 });
            player.Instance.OpenedWith(new MapDevice.Run(player.Character.Name!, area, map));
            var pools = Game.PoolsFor(player, 1);
            Assert.Equal(0, pools.MapTemporalChainsActionSpeedPct);
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
