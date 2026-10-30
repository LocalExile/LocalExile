using GameServer.Core.Projectiles;
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
    // Devout of Solaris through ring A's other side, without Garukhan (so a shock still counts).
    static readonly ushort[] PolytheistSolaris = [59443, 61900, 21979, 32742];
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
            // Abberath: the footstep-triggered Abberath's Fury and the hoofprints the client draws from the player stats.
            bool abberath = allocated && index == 0;
            Assert.Equal(abberath ? 1 : 0, Program.FootstepSkills(state, state.Clock.Elapsed.TotalSeconds).Count);
            Assert.Equal(abberath ? 1 : 0, PolytheistStat(state, "goat_footprints_from_item"));
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
        // One monster attack of 100 physical damage through MonsterSwing (or the attacker's own), optionally while 50%
        // shocked or walking, with the evasion entropy preset (99: every attack hits); the life it took.
        public async Task<uint> PolytheistMonsterHit(bool shocked, bool walking = false, int entropy = 99, SpawnedMonster? attacker = null)
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
                var monster = attacker ?? new SpawnedMonster("Polytheist incoming contact", new(position.X + 2, position.Y, 30), 0, 2, 1000)
                {
                    Hostile = true, Attack = new(SkillCatalog.FirstSkillId, .002, 20, DamagePacket.Of(DamageType.Physical, 100, 100)),
                };
                monster.Walk.Teleport(position.X + 2, position.Y, now);
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

        public Task<(string? Error, byte[] Bytes)> PolytheistAttackerSlows() => Exchange(async (stream, cipher) =>
        {
            await ProgramValue<Task>("SendAttackerSlows", "polytheist-integration", stream, cipher, State);
            return null;
        });

        // One area spell sweep (AreaSkillHit, at once) cast from self at aim, with a monster standing at each; which it hit.
        public async Task<(bool Self, bool Aim)> PolytheistAreaSpell(ushort skillId, (int X, int Y) self, (int X, int Y) aim)
        {
            var gem = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
                "GemSkillEffects", State).Single(g => g.Id == skillId);
            SpawnedMonster At((int X, int Y) p) => new("Polytheist area target", new(p.X, p.Y, 30), 0, 2, 100000) { Hostile = true };
            var (near, far) = (At(self), At(aim));
            (State.Monsters[46], State.Monsters[47]) = (near, far);
            Assert.Null((await Exchange(async (stream, cipher) =>
            {
                await (Task)GatewayMethod("AreaSkillHit").Invoke(null, ["polytheist-integration", stream, cipher, State, skillId, gem,
                    self, aim, true, (ushort)0, null, null, null])!;
                return null;
            })).Error);
            State.Monsters.Remove(46);
            State.Monsters.Remove(47);
            return (near.Life < near.MaxLife, far.Life < far.MaxLife);
        }

        public Task<(string? Error, byte[] Bytes)> PolytheistDeath() => Exchange(async (stream, cipher) =>
        {
            State.PlayerLife = 0;
            await ProgramValue<Task>("PlayerDied", "polytheist-integration", stream, cipher, State, "integration death");
            return null;
        });

        // One player tick of the passive auras (TickPassiveAuras).
        public Task<(string? Error, byte[] Bytes)> PolytheistAuraTick() => Exchange(async (stream, cipher) =>
        {
            await Program.TickPassiveAuras("polytheist-integration", stream, cipher, State);
            return null;
        });

        // One footstep's skill (TriggerFootstepSkill) with a monster dx cells east of the player and one a cell further.
        public async Task<(SpawnedMonster Near, SpawnedMonster Far, byte[] Bytes)> PolytheistFootstep(PassiveGrantedSkills.Granted skill, int dx)
        {
            double now = State.Clock.Elapsed.TotalSeconds;
            var at = State.Walk.Position(now);
            SpawnedMonster At(int x)
            {
                var m = new SpawnedMonster("Polytheist footstep target", new(at.X + x, at.Y, 30), 0, 2, 100000) { Hostile = true };
                m.Walk.Teleport(at.X + x, at.Y, now);
                return m;
            }
            var (near, far) = (At(dx), At(dx + 1));
            (State.Monsters[48], State.Monsters[49]) = (near, far);
            var (error, bytes) = await Exchange(async (stream, cipher) =>
            {
                await Program.TriggerFootstepSkill("polytheist-integration", stream, cipher, State, skill);
                return null;
            });
            Assert.Null(error);
            State.Monsters.Remove(48);
            State.Monsters.Remove(49);
            return (near, far, bytes);
        }
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

    static (ushort Id, int Effect) PolytheistSkill(ClientState state, string name) => ProgramValue<IEnumerable<(ushort Id, int Effect,
        string Name, int Level, int Quality, uint Gem)>>("GemSkillEffects", state).Where(g => g.Name == name).Select(g => (g.Id, g.Effect)).Single();

    static void EquipPolytheistGems(GatewayCharacter game, params string[] names) =>
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = [.. names.Select(n => new ItemSocket(SocketColour.White, FamilyGem(n)))], Links = [.. names.Select(_ => (byte)1)],
        });

    [Fact]
    public async Task Polytheist_Gruthkul_disables_spells_and_slows_enemies_that_hit_you()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Polytheist", refundPoints: 40, level: 60);
        var state = game.State;
        EquipPolytheistGems(game, "Fireball", "Ground Slam", "Clarity");
        var (fireball, slam, clarity) = (PolytheistSkill(state, "Fireball"), PolytheistSkill(state, "Ground Slam"), PolytheistSkill(state, "Clarity"));
        CharacterSaves.SetAuraActive(game.Character, "Clarity", true);
        void Reconcile()
        {
            lock (state.Monsters) typeof(Program).GetMethod("ReconcileOwnAuras", System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [state, false]);
        }
        string? Disabled((ushort Id, int Effect) skill) => ProgramValue<string?>("GemDisabled", state, skill.Effect);
        int Flag(ushort id) => FamilySkills(state).Single(s => s.Id == id).Stats!.FirstOrDefault(v => v.Key == OwnAuraRestriction.SkillDisabled).Value;
        var monster = new SpawnedMonster("Polytheist hasty attacker", new(10, 10, 30), 0, 2, 1000)
        {
            Hostile = true, Attack = new(SkillCatalog.FirstSkillId, .002, 20, DamagePacket.Of(DamageType.Physical, 1, 1)),
            Mods = new([], [], new(new Dictionary<string, int> { ["attack_speed_+%"] = 20 })),
        };
        double Swing(double at) => Program.MonsterSwingSeconds(state, monster, at);
        var plain = state.Pools;
        Assert.Equal(0, ProgramValue<double>("LifeRegenPerSecond", state));
        Reconcile();
        Assert.Single(state.Auras);
        Assert.All(new[] { fireball, slam, clarity }, s => Assert.Null(Disabled(s)));
        Assert.True(ProgramValue<DamagePacket>("SupportedHit", state, fireball.Id).MaxTotal > 0);
        await game.PolytheistMonsterHit(false, attacker: monster);
        Assert.Equal(.002, Swing(monster.HitPlayerAt), 12);

        Assert.Null((await game.Change(PolytheistBuilds[2], true)).Error);
        // Your Spells are disabled: spells and spell auras, never attacks. A running spell aura is suspended (its intent
        // kept) and not restored while the node is allocated. The skill record carries skill_disabled_by_stat.
        Assert.Equal(("spells are disabled", "spells are disabled", (string?)null), (Disabled(fireball), Disabled(clarity), Disabled(slam)));
        Assert.Empty(state.Auras);
        Reconcile();
        Assert.Empty(state.Auras);
        Assert.Equal((1, 1, 0), (Flag(fireball.Id), Flag(clarity.Id), Flag(slam.Id)));
        // Spell Skills deal no Damage; +400 to maximum Life; Regenerate 8% of Life per second.
        Assert.Equal(0, ProgramValue<DamagePacket>("SupportedHit", state, fireball.Id).MaxTotal);
        Assert.True(Program.SupportedProjectileHit(state, slam.Id, area: true).MaxTotal > 0);
        Assert.Equal(plain.Life + 400, state.Pools.Life);
        Assert.Equal(state.Pools.Life * .08, ProgramValue<double>("LifeRegenPerSecond", state), 6);
        // Enemies that have Hit you with an Attack Recently have 10% reduced Attack Speed: not after an evaded attack; after
        // a hit its 20% increased becomes 10% for 4 s, sent on its Stats component; a death ends it.
        monster.HitPlayerAt = double.NegativeInfinity;
        Assert.Equal(0u, await game.PolytheistMonsterHit(false, entropy: 0, attacker: monster));
        Assert.Equal(double.NegativeInfinity, monster.HitPlayerAt);
        await game.PolytheistMonsterHit(false, attacker: monster);
        double slowed = .002 * 1.2 / 1.1, hitAt = monster.HitPlayerAt;
        Assert.Equal(slowed, Swing(hitAt), 12);
        Assert.Equal(slowed, Swing(hitAt + 4), 12);
        Assert.Equal(.002, Swing(hitAt + 4.01), 12);
        var slowedStats = InstanceEntities.StatsUpdate(43, (monster.Attack! with { Seconds = Swing(state.Clock.Elapsed.TotalSeconds) }).Stats());
        Assert.True(game.LastBytes.AsSpan().IndexOf(slowedStats) >= 0);
        state.Monsters[45] = monster;
        Assert.Empty((await game.PolytheistAttackerSlows()).Bytes);
        Assert.Null((await game.PolytheistDeath()).Error);
        Assert.Equal(.002, Swing(hitAt), 12);
        Assert.True((await game.PolytheistAttackerSlows()).Bytes.AsSpan().IndexOf(InstanceEntities.StatsUpdate(45, monster.Attack!.Stats())) >= 0);
        state.Monsters.Remove(45);

        state.PlayerLife = state.PlayerMaxLife;
        CharacterSaves.SetAuraActive(game.Character, "Clarity", true);   // the death cleared the intent
        Assert.Null((await game.Change([.. PolytheistBuilds[2].Reverse()], false, refundPoints: true)).Error);
        Assert.All(new[] { fireball, slam, clarity }, s => Assert.Null(Disabled(s)));
        Reconcile();
        Assert.Single(state.Auras);
    }

    [Fact]
    public async Task Polytheist_Lunaris_nova_at_the_target_more_spell_projectiles_and_spell_area()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Polytheist", level: 60);
        var state = game.State;
        EquipPolytheistGems(game, "Ice Nova", "Fireball", "Ground Slam");
        var (nova, fireball, slam) = (PolytheistSkill(state, "Ice Nova"), PolytheistSkill(state, "Fireball"), PolytheistSkill(state, "Ground Slam"));
        (int X, int Y) self = (20, 20), aim = (100, 20);
        int Panel(ushort id, string name) => FamilySkills(state).Single(s => s.Id == id).Stats!
            .FirstOrDefault(v => v.Key == Program.Stats.Key(name)).Value;
        int Radius((ushort Id, int Effect) skill, bool spell) => ProgramValue<int>("SkillRadius", state,
            Program.Skills.EffectStats(skill.Effect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2), spell, skill.Effect);
        var (plainNova, plainSlam) = (Radius(nova, true), Radius(slam, false));
        // A Nova goes off around the caster wherever it is aimed; other area spells land where they are aimed.
        Assert.Equal((true, false), await game.PolytheistAreaSpell(nova.Id, self, aim));
        Assert.Equal(aim, Program.SpellCentre(state, fireball.Effect, self, aim));
        Assert.Equal((1, 1), (Panel(nova.Id, "skill_is_nova_skill"), Panel(fireball.Id, "total_number_of_projectiles_to_fire")));

        Assert.Null((await game.Change(PolytheistBuilds[1], true)).Error);
        // Nova Spells Cast at the targeted location; Spells fire 2 additional Projectiles; 30% increased spell area only.
        Assert.Equal((false, true), await game.PolytheistAreaSpell(nova.Id, self, aim));
        Assert.Equal(3, Panel(fireball.Id, "total_number_of_projectiles_to_fire"));
        int baseNova = Program.Skills.EffectStats(nova.Effect, 1).Single(kv => kv.Item1 == Program.Stats.Key("active_skill_base_area_of_effect_radius")).Item2;
        Assert.Equal((int)Math.Floor(baseNova * Math.Floor(100 * Math.Sqrt(1.3)) / 100), Radius(nova, true));
        Assert.Equal(baseNova, plainNova);
        Assert.Equal(plainSlam, Radius(slam, false));
    }

    [Fact]
    public async Task Polytheist_Solaris_blinds_nearby_enemies_for_their_hits_and_the_players_crits()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Polytheist", refundPoints: 40, level: 60);
        var state = game.State;
        EquipPolytheistGems(game, "Fireball");
        var fireball = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", state).Single(g => g.Name == "Fireball");
        // A 5% unarmed attack base and no increased critical strike chance.
        state.PlayerStats[Program.Stats.Key(AttackBaseCriticalStrike.UnarmedBaseStat)!.Value] = 500;
        state.Pools = Program.PoolsFor(state, 60);
        double now = state.Clock.Elapsed.TotalSeconds;
        var at = state.Walk.Position(now);
        state.Walk.Teleport(at.X, at.Y, now);
        SpawnedMonster Enemy(int dx)
        {
            var m = new SpawnedMonster("Polytheist blind target", new(at.X + dx, at.Y, 30), 0, 2, 1000)
            {
                Hostile = true, Attack = new(SkillCatalog.FirstSkillId, .002, 40, DamagePacket.Of(DamageType.Physical, 100, 100)),
            };
            m.Walk.Teleport(at.X + dx, at.Y, now);
            return m;
        }
        var (near, far, attacker) = (Enemy(35), Enemy(36), Enemy(2));
        (state.Monsters[50], state.Monsters[51]) = (near, far);
        Task<uint> Hit(bool shocked, int entropy = 99) => game.PolytheistMonsterHit(shocked, entropy: entropy, attacker: attacker);

        // Plain: nothing is blinded and every chance is the ordinary one.
        Assert.Empty((await game.PolytheistAuraTick()).Bytes);
        Assert.Equal(500, state.Pools.AttackCriticalStrikeChance);
        Assert.Null(Program.AttackCriticalChanceVsBlinded(state, Program.CharacterPlayerStats(state)));
        Assert.Null(Program.SupportedCriticalChanceVsBlinded(state, fireball.Id, release: false));
        var (plain, shocked) = (await Hit(false), await Hit(true));
        Assert.Equal(Math.Round(plain * 1.5), shocked);

        Assert.Null((await game.Change(PolytheistSolaris, true)).Error);
        state.Monsters[43] = attacker;
        ushort buff = state.NextBuff;
        // Nearby Enemies are Blinded: the blinded buff (179) on each living hostile monster within 35 cells, once.
        var tick = (await game.PolytheistAuraTick()).Bytes;
        Assert.Equal((true, false, true), (Program.Blinded(state, 50), Program.Blinded(state, 51), Program.Blinded(state, 43)));
        ushort? Added(uint id) => Enumerable.Range(buff, 2).Select(b => (ushort?)b).SingleOrDefault(b => tick.AsSpan()
            .IndexOf(InstanceEntities.AddBuff(id, b!.Value, FlaskGrounds.BlindedBuffRow, Program.PlayerId)) >= 0);
        Assert.Equal(buff + 2, state.NextBuff);
        Assert.NotNull(Added(43));
        ushort nearBuff = Assert.NotNull(Added(50));
        Assert.Empty((await game.PolytheistAuraTick()).Bytes);
        // Blind: 20% less accuracy and evasion. The blinded attacker's hit is evaded at an entropy an ordinary one reaches.
        Assert.Equal((800, 1000), (Program.UnderBlind(state, 50, 1000), Program.UnderBlind(state, 51, 1000)));
        int evasion = ProgramValue<int>("PlayerEvasion", state, state.Clock.Elapsed.TotalSeconds);
        int accuracy = Program.Monsters.BaseAccuracy(attacker.Level)!.Value;
        int ordinaryChance = DamageModel.AccuracyHitChance(evasion, accuracy);
        Assert.True(DamageModel.AccuracyHitChance(evasion, FlaskGrounds.Blinded(accuracy)) < ordinaryChance);
        Assert.Equal(0u, await Hit(false, entropy: 100 - ordinaryChance));
        // 10% reduced Damage taken from Blinded Enemies, additive with the shock's 50% increase (x1.4, not x1.35).
        Assert.Equal((Math.Round(plain * .9), Math.Round(plain * 1.4)), (await Hit(false), await Hit(true)));
        // 100% increased Critical Strike Chance against Blinded Enemies joins the increases, only against a blinded
        // target: the default attack goes from 5% to 10%, a spell's chance doubles.
        int? attackVs = Program.AttackCriticalChanceVsBlinded(state, Program.CharacterPlayerStats(state));
        Assert.Equal((1000, 500), (Program.CriticalChanceOn(state, 50, 500, attackVs), Program.CriticalChanceOn(state, 51, 500, attackVs)));
        int spell = ProgramValue<int>("SupportedCriticalChance", state, fireball.Id);
        Assert.True(spell > 0);
        Assert.Equal(2 * spell, Program.SupportedCriticalChanceVsBlinded(state, fireball.Id, release: false));
        // Enemies Blinded by you have 100% reduced Critical Strike Chance: monsters only crit through always_crit, which
        // the client's chance (FUN_14275c050) makes 10000 before any increase is read, so a reduction cannot touch it.
        Assert.Equal(10_000, new MonsterModEffects(new Dictionary<string, int> { ["always_crit"] = 1 }).CriticalChance);

        // A refund takes the aura's blind off; the smoke cloud's own blind on another monster stays.
        state.FlaskGrounds.SetBlinded(51, true, () => 999);
        Assert.Null((await game.Change([.. PolytheistSolaris.Reverse()], false, refundPoints: true)).Error);
        Assert.True((await game.PolytheistAuraTick()).Bytes.AsSpan().IndexOf(InstanceEntities.RemoveBuff(50, nearBuff)) >= 0);
        Assert.Equal((false, true), (Program.Blinded(state, 50), Program.Blinded(state, 51)));
        state.FlaskGrounds.SetBlinded(51, false, () => 0);
        // A death ends it too (the buff is CancelOnDeath), and the attacker's hits take full damage again.
        Assert.Null((await game.Change(PolytheistSolaris, true)).Error);
        state.Monsters[43] = attacker;
        await game.PolytheistAuraTick();
        Assert.True(Program.Blinded(state, 43));
        Assert.Null((await game.PolytheistDeath()).Error);
        await game.PolytheistAuraTick();
        Assert.False(Program.Blinded(state, 43) || Program.Blinded(state, 50));
        state.PlayerLife = state.PlayerMaxLife;
        Assert.Equal(plain, await Hit(false));

        // A released projectile keeps the chance against a blinded target for its contact (the fixture closes the state).
        await using var runtime = new ProjectileRuntimeFixture(state);
        var use = ProgramValue<ProjectileUse>("CaptureGemProjectile", state, runtime.Session, fireball.Id, fireball, (at.X + 50, at.Y));
        Assert.Equal(2 * spell, Assert.IsType<ProjectileOffense>(use.Payload).VsBlindedCriticalChance);
    }

    [Fact]
    public async Task Polytheist_Abberath_footsteps_trigger_Abberaths_Fury_and_ignites_deal_nothing()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Polytheist", refundPoints: 40, level: 60);
        var state = game.State;
        state.PlayerStats[Program.Stats.Key("fire_damage_+%")!.Value] = 100;
        double now = state.Clock.Elapsed.TotalSeconds;
        var at = state.Walk.Position(now);
        state.Walk.Teleport(at.X, at.Y, now);
        // The footsteps of `seconds` of walking at `speed` times the base walking speed (FootstepsDue), or of standing.
        int Steps(double seconds, double speed = 1, bool walk = true)
        {
            double t = state.Clock.Elapsed.TotalSeconds, run = 0;
            state.Walk.SetSpeed(Program.WalkSpeed * speed, t);
            if (walk) state.Walk.Order(at.X + 90, at.Y, t);
            Assert.True(!walk || state.Walk.ArrivesAt > t + seconds);
            int steps = Program.FootstepsDue(state, ref run, t, t + seconds);
            state.Walk.Teleport(at.X, at.Y, t);
            state.Walk.SetSpeed(Program.WalkSpeed, t);
            return steps;
        }
        (int Damage, bool Ignited) Ignite()
        {
            var stats = Program.CharacterPlayerStats(state);
            int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            var probe = new PlayerElementalAilments();
            var fire = DamagePacket.Of(DamageType.Fire, 1000, 1000);
            probe.Apply(fire, fire, true, 1000, 0, Get, new Random(1));
            bool ignited = probe.Snapshot(.5).Ignite;
            return (probe.Advance(0, 1, Get, 0, 75, false), ignited);
        }
        // The run animation's two footsteps per 800 ms loop (the first 233 ms in), faster with the walk's speed.
        Assert.Equal((0, 1, 2, 1), (PassiveGrantedSkills.Footsteps(0, .2), PassiveGrantedSkills.Footsteps(.233, .633),
            PassiveGrantedSkills.Footsteps(0, .8), PassiveGrantedSkills.Footsteps(.633, 1.05)));
        Assert.Equal((2, 0, 2, 4), (Steps(.8), Steps(.8, walk: false), Steps(.4, speed: 2), Steps(.8, speed: 2)));
        // A plain character has no footstep skill, and an ignite burns it.
        Assert.Empty(Program.FootstepSkills(state, now));
        var plainIgnite = Ignite();
        Assert.True(plainIgnite.Damage > 0 && plainIgnite.Ignited);

        Assert.Null((await game.Change([59443, 61900], true)).Error);
        // Triggers Level 20 Abberath's Fury: 300-450 fire, here with 100% increased fire damage.
        var skill = Assert.Single(Program.FootstepSkills(state, now));
        Assert.Equal(("RepeatingShockwave", 20), (skill.Id, skill.Level));
        var hit = Program.GrantedSpellHit(state, skill.Effect, skill.Level);
        Assert.Equal((600.0, 900.0, 900.0), (hit[DamageType.Fire].Min, hit[DamageType.Fire].Max, hit.MaxTotal));
        // A footstep explodes where the player stands and hits the monster 7 cells away, not the one at 8.
        var (near, far, bytes) = await game.PolytheistFootstep(skill, PassiveGrantedSkills.FootstepExplosionRadius);
        uint taken = near.MaxLife - near.Life;
        Assert.InRange(taken, 600u, 900u * (uint)state.Pools.SpellCriticalStrikeMultiplier / 100);
        Assert.Equal(far.MaxLife, far.Life);
        string art = Program.Art.Animated(PassiveGrantedSkills.ExplosionArtVariation, 0)!;
        Assert.Equal("Metadata/Effects/Microtransactions/Aura/Uniques/Disjunction/Abberaths_Hooves_fireimpact.ao", art);
        Assert.True(bytes.AsSpan().IndexOf(new EffectObject(at.X, at.Y, art).Blob()) >= 0);
        // No footstep skill while frozen.
        var cold = DamagePacket.Of(DamageType.Cold, state.PlayerMaxLife, state.PlayerMaxLife);
        lock (state.Monsters)
            Program.ApplyPlayerAilmentsLocked(state, cold, cold, true, false, now, Program.CharacterPlayerStats(state));
        Assert.Empty(Program.FootstepSkills(state, now));
        state.ElementalAilments.Clear();
        // Burning Hoofprints: the client picks the goat footprints from the player stats; Unaffected by Ignite keeps the
        // ignite (is_ignited) and takes its damage.
        var final = ProgramValue<IEnumerable<(uint Key, int Value)>>("FinalPlayerStats", state).ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal((1, 20), (final[Program.Stats.Key("goat_footprints_from_item")!.Value],
            final[Program.Stats.Key("display_abberaths_hooves_skill_level")!.Value]));
        Assert.Equal((0, true), Ignite());

        // Devout of Gruthkul disables the spell (and would zero its damage); a refund brings it back.
        Assert.Null((await game.Change([30769, 5266], true)).Error);
        Assert.Empty(Program.FootstepSkills(state, now));
        Assert.Equal(0, Program.GrantedSpellHit(state, skill.Effect, skill.Level).MaxTotal);
        Assert.Null((await game.Change([5266, 30769], false, refundPoints: true)).Error);
        Assert.Single(Program.FootstepSkills(state, now));
        Assert.Null((await game.Change([61900, 59443], false, refundPoints: true)).Error);
        Assert.Empty(Program.FootstepSkills(state, now));
        Assert.Equal(plainIgnite, Ignite());
    }
}
