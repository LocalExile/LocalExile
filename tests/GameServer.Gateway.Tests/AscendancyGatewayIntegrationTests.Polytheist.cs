using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // Start 409 opens three seven-node rings; each build is one whole ring plus the next ring's first small node.
    internal static readonly ushort[][] PolytheistBuilds =
    [
        [27664, 22056, 26231, 32742, 21979, 61900, 59443, 46465],
        [46465, 64171, 51229, 43703, 29846, 1830, 6605, 30769],
        [30769, 5266, 322, 24342, 33835, 23429, 25478, 27664],
    ];
    static readonly ushort[] PolytheistSmallNodes = [27664, 46465, 6605, 33835, 30769, 29846, 21979, 59443, 322, 51229, 26231, 25478];

    static int PolytheistStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    [Fact]
    public void Polytheist_checklist_is_the_actual_graph_and_three_builds_cover_every_node()
    {
        if (!DataAvailable()) return;
        var definition = Program.Ascendancies.Find(5, AscendancyFamily.Phrecia, "Polytheist")!;
        Assert.Equal(new AscendancyIdentity(AscendancyFamily.Phrecia, "Guardian", "3.29.3b"), definition.Identity);
        Assert.Equal((ushort)409, definition.StartNode);
        Assert.Equal(new ushort[] { 409, 27664, 32742, 43703, 24342, 46465, 6605, 33835, 30769, 29846, 64171, 22056,
            1830, 21979, 23429, 59443, 61900, 322, 5266, 51229, 26231, 25478 }.Order(), definition.Nodes.Keys.Order());
        Assert.Equal(definition.Nodes.Keys.Where(n => n != 409).Order(), PolytheistBuilds.SelectMany(b => b).Distinct().Order());
        Assert.All(definition.Nodes.Values, n => Assert.Equal(0, n.PointsGranted));
        // The default gate stays closed; the opt-in's refusal names this class's page.
        if (Environment.GetEnvironmentVariable("Game_VERIFIED_POLYTHEIST") != "1")
            Assert.Throws<AscendancyRuleException>(() => AscendancyClientCompatibility.RequirePlayable(definition.Identity));
        Assert.Contains("polytheist.md", Assert.Throws<AscendancyRuleException>(
            () => VerifiedGamblerGraph.Verify([], [], "Polytheist")).Message);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Polytheist_legal_build_paid_refund_and_reload(int index)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Polytheist", refundPoints: 100, level: 60);
        var nodes = PolytheistBuilds[index];
        var plain = game.State.Pools;
        int small = nodes.Count(PolytheistSmallNodes.Contains);
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(8, game.State.MainAscendancy.Allocated.Count);
        AssertNodeConsumers(game.State, true);
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        AssertNodeConsumers(game.Reload(), true);

        Assert.Null((await game.Change(nodes.Reverse().ToArray(), false, refundPoints: true)).Error);
        Assert.Empty(game.State.MainAscendancy.Allocated);
        Assert.Equal((ushort)60, game.State.RefundPoints);
        AssertNodeConsumers(game.State, false);
        if (index == 2)
        {
            // A plain character is frozen and chilled by a critical cold hit; Brine King then drops both on the next tick.
            var state = game.State;
            var cold = DamagePacket.Of(DamageType.Cold, state.PlayerMaxLife, state.PlayerMaxLife);
            double now = state.Clock.Elapsed.TotalSeconds;
            lock (state.Monsters)
                Program.ApplyPlayerAilmentsLocked(state, cold, cold, true, false, now, Program.CharacterPlayerStats(state));
            Assert.Equal((false, 30, true, 0), state.ElementalAilments.Snapshot(now));
            Assert.Null((await game.Change(nodes, true)).Error);
            lock (state.Monsters)
                Program.AdvancePlayerAilmentsLocked(state, now, now + .25, Program.CharacterPlayerStats(state));
            Assert.Equal((false, 0, false, 0), state.ElementalAilments.Snapshot(now + .25));
        }
        else Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal((ushort)60, game.Reload().RefundPoints);

        void AssertNodeConsumers(ClientState state, bool allocated)
        {
            // Small nodes: 10% increased Defences reach every defence's increase; light radius goes to the client.
            int defences = allocated ? 10 * small : 0;
            Assert.Equal(defences, PolytheistStat(state, "global_defences_+%"));
            Assert.Equal(plain.ArmourPct + defences, state.Pools.ArmourPct);
            Assert.Equal(plain.EvasionPct + defences, state.Pools.EvasionPct);
            Assert.Equal(plain.EnergyShieldPct + defences, state.Pools.EnergyShieldPct);
            Assert.True(allocated ? state.Pools.Evasion > plain.Evasion : state.Pools.Evasion == plain.Evasion);
            Assert.Equal(allocated ? 5 * small : 0, PolytheistStat(state, "light_radius_+%"));
            // Brine King: maximum cold resistance, and the same critical cold hit neither chills nor freezes.
            bool brine = allocated && index == 2;
            Assert.Equal(plain.ColdMaxResistance + (brine ? 10 : 0), state.Pools.ColdMaxResistance);
            var probe = new PlayerElementalAilments();
            var stats = Program.CharacterPlayerStats(state);
            var hit = DamagePacket.Of(DamageType.Cold, 1000, 1000);
            probe.Apply(hit, hit, true, 1000, 0, n => stats.GetValueOrDefault(Program.Stats.Key(n)!.Value), new Random(1));
            Assert.Equal(brine ? (false, 0, false, 0) : (false, 30, true, 0), probe.Snapshot(0));
            // Ralakesh: three of each charge without gaining any, in the stats and in the charge session.
            int minimum = allocated && index == 1 ? 3 : 0;
            var session = ProgramValue<ChargeSession>("ChargesOf", state);
            foreach (var type in Charges.Types)
            {
                Assert.Equal(minimum, PolytheistStat(state, $"current_{Charges.Name(type)}_charges"));
                Assert.Equal(minimum, session.Limits(type).Minimum);
                Assert.Equal(minimum, session.Effective(type));
            }
        }
    }

    sealed record PolytheistRecovery(int Evasion, int EvasionStat, double Life, int LifeStat, double Mana, int ManaStat);

    // The same character standing, then walking: what monster attacks roll against, what the resource writer applies
    // and the finals the panel and the client's pool stepping read.
    static (PolytheistRecovery Still, PolytheistRecovery Walking) PolytheistWhileMoving(ClientState state)
    {
        PolytheistRecovery Read()
        {
            var stats = Program.CharacterPlayerStats(state);
            int Final(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            return new(ProgramValue<int>("PlayerEvasion", state, state.Clock.Elapsed.TotalSeconds), Final("evasion_rating"),
                ProgramValue<double>("LifeRegenPerSecond", state), Final("life_recovery_per_minute"),
                ProgramValue<double>("ManaRegenPerSecond", state), Final("mana_recovery_per_minute"));
        }
        double now = state.Clock.Elapsed.TotalSeconds;
        var at = state.Walk.Position(now);
        state.Walk.Teleport(at.X, at.Y, now);
        var still = Read();
        state.Walk.Order(at.X > 64 ? at.X - 60 : at.X + 60, at.Y, state.Clock.Elapsed.TotalSeconds);
        var walking = Read();
        Assert.True(state.Clock.Elapsed.TotalSeconds < state.Walk.ArrivesAt);
        state.Walk.Teleport(at.X, at.Y, state.Clock.Elapsed.TotalSeconds);
        return (still, walking);
    }

    sealed partial class GatewayCharacter
    {
        // One monster attack of 100 physical damage through MonsterSwing, optionally while 50% shocked or walking, with
        // the evasion entropy preset (99: every attack hits); the life it took.
        public async Task<uint> PolytheistMonsterHit(bool shocked, bool walking = false, int entropy = 99)
        {
            uint before = State.PlayerLife;
            await Exchange(async (stream, cipher) =>
            {
                double now = State.Clock.Elapsed.TotalSeconds;
                var position = State.Walk.Position(now);
                State.Walk.Teleport(position.X, position.Y, now);
                if (shocked)
                {
                    var lightning = DamagePacket.Of(DamageType.Lightning, State.PlayerMaxLife, State.PlayerMaxLife);
                    lock (State.Monsters)
                        Program.ApplyPlayerAilmentsLocked(State, lightning, lightning, true, false, now, Program.CharacterPlayerStats(State));
                    Assert.Equal(50, State.ElementalAilments.Snapshot(now).Shock);
                }
                if (walking) State.Walk.Order(position.X > 64 ? position.X - 60 : position.X + 60, position.Y, now);
                State.EvasionEntropy = entropy;
                State.LastEvasionAt = State.Clock.Elapsed.TotalSeconds;
                var monster = new SpawnedMonster("Polytheist incoming contact", new(position.X + 2, position.Y, 30), 0, 2, 1000)
                {
                    Hostile = true, Attack = new(SkillCatalog.FirstSkillId, .002, 20, DamagePacket.Of(DamageType.Physical, 100, 100)),
                };
                State.Monsters[43] = monster;
                await (Task)GatewayMethod("MonsterSwing").Invoke(null, ["polytheist-integration", stream, cipher,
                    State, 43u, monster, (position.X + 2, position.Y), position, State.IncomingHitEpoch])!;
                State.Monsters.Remove(43);
                State.ElementalAilments.Clear();
                State.Walk.Teleport(position.X, position.Y, State.Clock.Elapsed.TotalSeconds);
                return null;
            });
            uint taken = before - State.PlayerLife;
            State.PlayerLife = State.PlayerMaxLife;
            return taken;
        }

        // The finals the player tick sends again when a walk starts or ends, sent while walking.
        public Task<(string? Error, byte[] Bytes)> PolytheistWalkingStats() => Exchange(async (stream, cipher) =>
        {
            double now = State.Clock.Elapsed.TotalSeconds;
            var position = State.Walk.Position(now);
            State.Walk.Order(position.X > 64 ? position.X - 60 : position.X + 60, position.Y, now);
            await ProgramValue<Task>("SendWhileMovingStats", "polytheist-integration", stream, cipher, State);
            State.Walk.Teleport(position.X, position.Y, State.Clock.Elapsed.TotalSeconds);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> PolytheistDeath() => Exchange(async (stream, cipher) =>
        {
            State.PlayerLife = 0;
            await ProgramValue<Task>("PlayerDied", "polytheist-integration", stream, cipher, State, "integration death");
            return null;
        });
    }

    [Fact]
    public async Task Polytheist_Garukhan_evasion_and_regeneration_while_moving_and_unaffected_by_shock()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Polytheist", level: 60);
        var state = game.State;
        var plain = PolytheistWhileMoving(state);
        Assert.Equal(plain.Still, plain.Walking);
        Assert.Empty((await game.PolytheistWalkingStats()).Bytes);
        Assert.Equal(150u, await game.PolytheistMonsterHit(shocked: true));

        Assert.Null((await game.Change(PolytheistBuilds[0], true)).Error);
        var (still, walking) = PolytheistWhileMoving(state);
        // 100% increased Evasion Rating while moving joins the other increases; standing keeps the ordinary rating.
        int pct = state.Pools.EvasionPct;
        Assert.Equal(state.Pools.Evasion, still.Evasion);
        Assert.Equal((int)(15m * (100 + pct) / 100), still.Evasion);
        Assert.Equal((int)(15m * (200 + pct) / 100), walking.Evasion);
        Assert.Equal((still.Evasion, walking.Evasion), (still.EvasionStat, walking.EvasionStat));
        // Regenerate 4% of Life per second and 100% increased Mana Regeneration Rate while moving, and nothing standing.
        Assert.Equal(0, PolytheistStat(state, "mana_regeneration_rate_+%"));
        Assert.Equal((plain.Still.Life, plain.Still.Mana), (still.Life, still.Mana));
        Assert.Equal(still.Life + state.Pools.Life * .04, walking.Life, 6);
        Assert.Equal(2 * still.Mana, walking.Mana, 6);
        Assert.Equal((int)Math.Round(walking.Life * 60), walking.LifeStat);
        Assert.Equal((int)Math.Round(walking.Mana * 60), walking.ManaStat);
        Assert.Equal((int)Math.Round(still.Mana * 60), still.ManaStat);
        // The client steps its pools from those finals and derives none of them, so a walk start or end sends them again.
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var sent = InstanceEntities.StatsUpdate(Program.PlayerId, [(Key("evasion_rating"), walking.Evasion),
            (Key("life_recovery_per_minute"), walking.LifeStat), (Key("total_life_recovery_per_minute_from_regeneration"), walking.LifeStat),
            (Key("mana_recovery_per_minute"), walking.ManaStat), (Key("total_mana_recovery_per_minute_from_regeneration"), walking.ManaStat)]);
        Assert.True((await game.PolytheistWalkingStats()).Bytes.AsSpan().IndexOf(sent) >= 0);
        // MonsterSwing rolls against the walking rating: an entropy that the standing chance just reaches is evaded.
        int accuracy = Program.Monsters.BaseAccuracy(1)!.Value;
        int standing = DamageModel.AccuracyHitChance(still.Evasion, accuracy);
        Assert.True(DamageModel.AccuracyHitChance(walking.Evasion, accuracy) < standing);
        Assert.Equal(0u, await game.PolytheistMonsterHit(shocked: false, walking: true, entropy: 100 - standing));
        Assert.Equal(100u, await game.PolytheistMonsterHit(shocked: false, walking: false, entropy: 100 - standing));
        // Unaffected by Shock: the shock is still on the player (asserted in the helper), with no extra damage taken.
        Assert.Equal(100u, await game.PolytheistMonsterHit(shocked: true));
    }

    [Fact]
    public async Task Polytheist_Ryslatha_more_maximum_attack_damage_and_no_regeneration_after_a_hit()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Polytheist", level: 60);
        var state = game.State;
        var gem = FamilyGem("Ground Slam");
        EquipFamilyGem(game, gem);
        var weapon = ProgramValue<SkillCatalog.Unarmed>("WeaponFor", state);
        DamagePacket[] Hits()
        {
            var stats = Program.CharacterPlayerStats(state);
            ushort slam = FamilySkills(state).Single(s => s.EffectsPerLevelRow
                == Program.Skills.FindByRow(0, Program.Items.GemEffects(gem).First(), 1)!.EffectsPerLevelRow).Id;
            return [Program.AttackHit(weapon, stats, melee: true), AttackSkillHit.Resolve(weapon, stats, [], Program.Stats, 10000, melee: true),
                OrdinaryBowShot.Capture(weapon, stats, [], Program.Stats, 0, 0, 0).Hit, Program.SupportedProjectileHit(state, slam, area: true)];
        }
        (int Min, int Max) NativeBounds()
        {
            var final = ProgramValue<IEnumerable<(uint Key, int Value)>>("FinalPlayerStats", state).ToDictionary(s => s.Key, s => s.Value);
            return (final[Program.Stats.Key("main_hand_minimum_physical_damage")!.Value],
                final[Program.Stats.Key("main_hand_maximum_physical_damage")!.Value]);
        }
        var plain = Hits();
        var plainBounds = NativeBounds();
        var monster = new SpawnedMonster("Polytheist regenerating target", new(10, 10, 0), 0, 2, 1000)
        {
            Mods = new([], [], new(new Dictionary<string, int> { ["life_regeneration_rate_per_minute_%"] = 600 })),
        };
        state.Monsters[44] = monster;
        uint Regenerated(double after, double seconds = .5)
        {
            monster.Life = 500;
            monster.Regen = 0;
            Program.RegenerateConnectionMonsters(state, monster.PlayerHitAt + after, seconds);
            return monster.Life - 500;
        }
        void Hit() => typeof(Program).GetMethod("ApplyMonsterHitLocked", System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [state, monster, 0]);
        Hit();
        Assert.Equal(50u, Regenerated(.5));

        Assert.Null((await game.Change(PolytheistBuilds[2], true)).Error);
        // 30% more Maximum Attack Damage: every attack hit's maxima, minima unchanged, in the default attack (and panel),
        // the attack gems, the bow and the native physical bounds the client shows.
        var ryslatha = Hits();
        Assert.All(plain, p => Assert.True(p.MaxTotal > 0));
        for (int i = 0; i < plain.Length; i++)
            foreach (var (type, range) in plain[i].Components)
            {
                Assert.Equal(range.Min, ryslatha[i][type].Min, 9);
                Assert.Equal(range.Max * 1.3, ryslatha[i][type].Max, 9);
            }
        Assert.Equal((plainBounds.Min, (int)Math.Floor(weapon.HitDamage(Program.CharacterPlayerStats(state))[DamageType.Physical].Max * 1.3)),
            NativeBounds());
        // Enemies you've Hit Recently have 100% reduced Life Regeneration rate: none for 4 s, half of the step that ends
        // a quarter second after, all of it later; a death ends "recently".
        Hit();
        Assert.Equal(0u, Regenerated(.5));
        Assert.Equal(0u, Regenerated(4));
        Assert.Equal(25u, Regenerated(4.25));
        Assert.Equal(50u, Regenerated(4.5));
        Hit();
        double hitAt = monster.PlayerHitAt;
        Assert.Null((await game.PolytheistDeath()).Error);
        Assert.Equal(double.NegativeInfinity, monster.PlayerHitAt);
        monster.Life = 500;
        monster.Regen = 0;
        Program.RegenerateConnectionMonsters(state, hitAt + .5, .5);
        Assert.Equal(550u, monster.Life);
    }
}
