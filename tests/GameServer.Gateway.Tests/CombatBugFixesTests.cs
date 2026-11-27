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

    [Fact]
    public void Vengeance_grants_rage_when_hit_and_scales_armour_per_rage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"combat-vengeance-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            using var wire = new PartyFollowerTests.Wire();
            var player = CreateTestPlayer("VengeanceTest");
            player.Clock.Start();
            player.Connection.Session = new("vengeance-test", wire.Stream, wire.Ciphers);
            // Vengeance notable (rage_notable2108, hash 53840): Gain 3 Rage when Hit by an Enemy, 1% increased Armour per Rage.
            const ushort vengeanceHash = 53840;
            player.Character.Passives.Add(vengeanceHash);
            // Provide base armour via PlayerStatSources so percentage scaling is directly observable.
            using var statHook = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
            {
                if (!ReferenceEquals(p, player)) return s;
                s[Stat.BasePhysicalDamageReductionRating] = s.GetValueOrDefault(Stat.BasePhysicalDamageReductionRating) + 1000;
                return s;
            });
            try
            {
                var basePools = Game.PoolsFor(player, 1);
                Assert.Equal(1000, basePools.Armour);
                Assert.Equal(0, basePools.ArmourPct);

                // Synthetic enemy hit must trigger Vengeance's GainXRageWhenHit (+3 Rage).
                Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 10, block: false, spell: false,
                    suppress: false, out _));
                var inputs = RageStats.Inputs(player, key => Game.CharacterPlayerStats(player).GetValueOrDefault(key));
                Assert.Equal(3, inputs.GainWhenHit);
                Assert.Equal(3, player.Rage.Read(Game.AreaTime(player), inputs).Current);

                // 3 Rage with 1% increased Armour per Rage scales 1000 base Armour to 1030.
                var ragedPools = Game.PoolsFor(player, 1);
                Assert.Equal(3, ragedPools.ArmourPct);
                Assert.Equal(1030, ragedPools.Armour);

                // Higher Rage scales Armour proportionally and refreshes Pools in RageSnapshot.
                player.Rage.Gain(17, Game.AreaTime(player), inputs);
                Assert.Equal(20, player.Rage.Read(Game.AreaTime(player), inputs).Current);
                var twentyRagePools = Game.PoolsFor(player, 1);
                Assert.Equal(20, twentyRagePools.ArmourPct);
                Assert.Equal(1200, twentyRagePools.Armour);
            }
            finally
            {
                PlayerInventories.Forget(player.Character.Name!);
            }
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public async Task Axe_mastery_destroys_corpses_on_hit_kills_and_respects_dot_and_cannot_be_destroyed()
    {
        string path = Path.Combine(Path.GetTempPath(), $"combat-axemastery-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            using var wire = new PartyFollowerTests.Wire();
            var player = CreateTestPlayer("AxeMasteryTest");
            player.Clock.Start();
            player.Connection.Session = new("axe-mastery-test", wire.Stream, wire.Ciphers);

            // Allocate real Axe Mastery node with Axe1 ("Enemies Killed by your Hits are destroyed").
            ushort axeNode = Enumerable.Range(1, ushort.MaxValue - 1).Select(i => (ushort)i)
                .First(i => Program.Passives.Find(i) is { IsMastery: true }
                    && Program.Passives.EffectsFor(i).Any(e => e.Id == "Axe1"));
            player.Character.Passives.Add(axeNode);
            player.Character.MasteryEffects = new Dictionary<ushort, ushort>
            {
                [axeNode] = Program.Passives.EffectsFor(axeNode).Single(e => e.Id == "Axe1").Hash,
            };

            var stats = Game.CharacterPlayerStats(player);
            Assert.NotEqual(0, stats.GetValueOrDefault(Stat.EnemiesExplodeOnKill));

            var variety = Game.Monsters.All.First(v => v.Supported && v.Id.Contains("Zombie", StringComparison.OrdinalIgnoreCase));
            SpawnedMonster Spawn(uint id, Dictionary<uint, int>? extraMods = null)
            {
                var m = new SpawnedMonster(variety.Name, new(0, 0, 0), 0, 0, 100)
                {
                    ObjectId = id,
                    Variety = variety,
                    Hostile = true,
                    Level = 1,
                    NoRewards = true,
                };
                if (extraMods is not null)
                    m.Mods = MonsterModSet.None with { Effects = new(extraMods) };
                player.Area.Monsters[id] = m;
                return m;
            }

            try
            {
                // 1. Hit kill destroys the corpse and removes it from Area.Monsters.
                var hitVictim = Spawn(501);
                var hit = Game.ApplyMonsterHitLocked(player, hitVictim, damage: 200, rampageSource: null,
                    playerStats: stats, allyAt: null, cull: default, cullPlayer: stats);
                Assert.NotNull(hit);
                Assert.Equal(0u, hit.Value.Life);
                await Game.MonsterKilled(player, 501, hitVictim, byMinion: false, byHit: true);
                Assert.True(hitVictim.CorpseConsumed);
                Assert.False(player.Area.Monsters.ContainsKey(501));

                // 2. Non-hit (DoT) kill must NOT destroy the corpse.
                var dotVictim = Spawn(502);
                dotVictim.Life = 0;
                dotVictim.Dead = true;
                await Game.MonsterKilled(player, 502, dotVictim, byMinion: false, byHit: false);
                Assert.False(dotVictim.CorpseConsumed);
                Assert.True(player.Area.Monsters.ContainsKey(502));

                // 3. Monster with corpse_cannot_be_destroyed must NOT be destroyed on hit kill.
                var protectedVictim = Spawn(503, new Dictionary<uint, int> { [Stat.CorpseCannotBeDestroyed] = 1 });
                var protectedHit = Game.ApplyMonsterHitLocked(player, protectedVictim, damage: 200, rampageSource: null,
                    playerStats: stats, allyAt: null, cull: default, cullPlayer: stats);
                Assert.NotNull(protectedHit);
                Assert.Equal(0u, protectedHit.Value.Life);
                await Game.MonsterKilled(player, 503, protectedVictim, byMinion: false, byHit: true);
                Assert.False(protectedVictim.CorpseConsumed);
                Assert.True(player.Area.Monsters.ContainsKey(503));
            }
            finally
            {
                PlayerInventories.Forget(player.Character.Name!);
            }
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
