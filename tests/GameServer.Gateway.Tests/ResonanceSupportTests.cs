using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ResonanceSupportTests
{
    [Fact]
    public async Task A_supported_hit_fills_the_other_elements_and_they_decay_after_attacks_stop()
    {
        using var f = new Fixture("Trinity Support", DamageType.Fire);
        f.EnableFrameStats();
        Assert.True(Game.StatsPublishedByFrame(f.Player));

        Assert.True(await f.Cast());
        f.Advance(1);
        f.Step();
        Assert.Equal((0, 0, 0), f.Elemental());

        var target = f.AddTarget();
        Assert.True(await f.Cast(Fixture.TargetId));
        Assert.True(target.Life < target.MaxLife);
        f.Player.RecentEvents.Clear();
        f.Step();
        f.Step(); // Settle any mana-condition or RecentEvents mark from Cast before checking Resonance ticks.
        Assert.False(f.Player.Area.Buffs.Has(f.Player.Id, 1677, f.Player.Id));
        Assert.True(f.Player.Area.Buffs.Has(f.Player.Id, 1678, f.Player.Id));
        Assert.True(f.Player.Area.Buffs.Has(f.Player.Id, 1679, f.Player.Id));

        // Ticking resonance up through StatsChangeAt runs only leaf PlayerStatStep.Resonance, never Sources.
        int sourcesBefore = Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Sources);
        int resonanceBefore = Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Resonance);
        f.Advance(0.4);
        f.Step();
        Assert.Equal((0, 10, 10), f.Elemental());
        f.Advance(0.6);
        f.Step();
        Assert.Equal((0, 25, 25), f.Elemental());
        f.Advance(1);
        f.Step();
        Assert.Equal((0, 50, 50), f.Elemental());
        Assert.Equal(sourcesBefore, Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Sources));
        Assert.True(Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Resonance) >= resonanceBefore + 3);

        // Sustained hits past 12s keep the resonance buffs active on AreaBuffs.
        for (int i = 0; i < 7; i++)
        {
            Assert.True(await f.Cast(Fixture.TargetId));
            f.Advance(2);
            f.Step();
        }
        f.Player.RecentEvents.Clear();
        f.Step();
        Assert.Equal((0, 50, 50), f.Elemental());
        Assert.True(f.Player.Area.Buffs.Has(f.Player.Id, 1678, f.Player.Id));
        Assert.True(f.Player.Area.Buffs.Has(f.Player.Id, 1679, f.Player.Id));

        // Stopping attacks decays counters and clears the buffs at 0 without re-running PlayerStatStep.Sources.
        sourcesBefore = Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Sources);
        resonanceBefore = Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Resonance);
        f.Advance(1);
        f.Step();
        Assert.Equal((0, 45, 45), f.Elemental());
        Assert.True(f.Player.Area.Buffs.Has(f.Player.Id, 1678, f.Player.Id));
        Assert.Equal(sourcesBefore, Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Sources));
        f.Advance(1);
        f.Step();
        Assert.Equal((0, 40, 40), f.Elemental());
        Assert.Equal(sourcesBefore, Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Sources));
        f.Advance(8);
        f.Step();
        Assert.Equal((0, 0, 0), f.Elemental());
        Assert.False(f.Player.Area.Buffs.Has(f.Player.Id, 1678, f.Player.Id));
        Assert.False(f.Player.Area.Buffs.Has(f.Player.Id, 1679, f.Player.Id));
        Game.ClearResonance(f.Player);
        f.Step();
        Assert.Equal(sourcesBefore, Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Sources));
        Assert.True(Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Resonance) >= resonanceBefore + 3);
    }

    [Fact]
    public async Task Unholy_resonance_is_separate_and_its_resistance_bonus_requires_all_three()
    {
        using (var f = new Fixture("Unholy Trinity Support", DamageType.Chaos))
        {
            var target = f.AddTarget();
            var support = new Dictionary<uint, int>(Game.SupportedSkill(f.Player, f.Skill).Stats)
            {
                // A guaranteed roll checks the threshold independently of the gem's ordinary chance.
                [Stat.HitsIgnoreEnemyMonsterPhysicalDamageReductionPctChanceWhileAllUnholyResonanceIs25] = 100,
                [Stat.HitsIgnoreEnemyMonsterLightningAndChaosResistancePctChanceWhileAllUnholyResonanceIs25] = 100,
            };
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100)
                .Add(DamageType.Chaos, new(100, 100)).Add(DamageType.Lightning, new(100, 100));
            Assert.True(await f.Cast(Fixture.TargetId));
            f.Advance(1);
            Assert.Equal(25, f.Stat(Stat.PhysicalUnholyResonance));
            Assert.Equal(25, f.Stat(Stat.LightningUnholyResonance));
            Assert.Equal(0, f.Stat(Stat.ChaosUnholyResonance));
            Assert.Equal((0, 0, 0), f.Elemental());
            Assert.False(Game.WithResonanceHitLocked(f.Player, support, hit, default, default, f.Now).Mods.IgnoreLightning);

            f.Dominant = DamageType.Lightning;
            Assert.True(await f.Cast(Fixture.TargetId));
            f.Advance(1);
            Assert.True(target.Life < target.MaxLife);
            Assert.Equal(25, f.Stat(Stat.VirtualLowestUnholyResonance));
            var active = Game.WithResonanceHitLocked(f.Player, support, hit, default, default, f.Now);
            Assert.Equal(112, active.Packet[DamageType.Physical].Max, 6);
            Assert.True(active.Mods.IgnoreLightning && active.Mods.IgnoreChaos && active.Mods.IgnorePhysicalReduction);
        }

        // Mirage Archer benefits from the player's existing Resonance without generating Resonance.
        using var mirage = new Fixture("Trinity Support", DamageType.Fire, bow: true);
        using var mirageStats = GameEvents.PlayerStatSources.Add("Game", (owner, sources) =>
        {
            if (!ReferenceEquals(owner, mirage.Player)) return sources;
            var copy = new Dictionary<uint, int>(sources)
            {
                [Stat.GlobalAlwaysHit] = 1,
                [Stat.GlobalCannotCrit] = 1,
            };
            return copy;
        });
        var bowTarget = mirage.AddTarget((110, 100));
        mirage.AdvanceOutput(0.25); // an output callback overtakes the test schedule, without waiting on wall time
        Assert.True(mirage.FireBow((110, 100)));
        Assert.True(mirage.AdvanceUntilPlayerHit(1.0));
        Game.ClearResonance(mirage.Player);
        Assert.Equal((0, 0, 0), mirage.Elemental());

        bowTarget.Life = bowTarget.MaxLife;
        uint baseline = mirage.AdvanceUntilHit(bowTarget, 3.0);
        Assert.True(baseline > 0);
        mirage.Advance(0.1);
        Assert.Equal((0, 0, 0), mirage.Elemental());

        // Build all three resonances to >= 25 via player hits, then observe Mirage Archer's boosted hit.
        mirage.Dominant = DamageType.Fire;
        Assert.True(mirage.FireBow((110, 100)));
        Assert.True(mirage.AdvanceUntilPlayerHit(1.0));
        mirage.Advance(1.1);
        mirage.Dominant = DamageType.Cold;
        Assert.True(mirage.FireBow((110, 100)));
        Assert.True(mirage.AdvanceUntilPlayerHit(1.0));
        mirage.Advance(1.1);
        int lowest = mirage.Stat(Stat.VirtualLowestResonance);
        Assert.True(lowest >= 25, $"Expected all three Resonances to reach 25, but the lowest was {lowest}.");

        bowTarget.Life = bowTarget.MaxLife;
        uint boosted = mirage.AdvanceUntilHit(bowTarget, 3.0);
        Assert.True(boosted > baseline, $"Expected Mirage Archer hit ({boosted}) to exceed baseline ({baseline}) with active Resonance.");
    }

    sealed class Fixture : IDisposable, IPlayerUpdateSchedule
    {
        public const uint TargetId = 271;
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? previous = Game.AreaUpdateContext.Value;
        readonly PlayerUpdatesState updates;
        readonly IDisposable damage;
        long frame;
        public readonly ushort Skill;
        public DamageType Dominant;
        public Player Player => fixture.Player;
        public double Now = 1;
        double IPlayerUpdateSchedule.Now(Player player) => Now;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            MoveClock(milliseconds / 1000d, advanceProjectiles: false);
        }

        public Fixture(string support, DamageType dominant, bool bow = false)
        {
            fixture = new(this);
            updates = new(fixture.Lifetime, this);
            Player.Walk.Teleport(100, 100, Now);
            if (bow)
            {
                var items = Game.ItemsOf(Player);
                foreach (var old in items.Items(PlayerInventories.Weapon1).ToArray())
                    items.Remove(PlayerInventories.Weapon1, old.Id);
                var gems = new[] { "Ice Shot", "Mirage Archer Support", support }
                    .Select(name => Program.Items.FindGem(name)!.Value)
                    .Select(g => new ItemSocket(SocketColour.White, ItemCatalog.Create(g.Type) with { GemVariant = g.Variant.Index }))
                    .ToArray();
                items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with
                {
                    Sockets = gems,
                    Links = [(byte)gems.Length],
                });
                Skill = Assert.Single(Game.GemSkillEffects(Player)).Id;
            }
            else
            {
                Skill = Assert.Single(fixture.Equip(("Absolution of Inspiring", 1), (support, 1)));
            }
            Dominant = dominant;
            Game.AreaUpdateContext.Value = updates;
            Player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Now));
            damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, _, rolled) => ReferenceEquals(owner, Player)
                ? DamagePacket.Of(Dominant, 120, 120) : rolled);
        }

        public void EnableFrameStats()
        {
            Player.Connection.PlayerSent = true;
            Player.PlayerUpdatesState = updates;
            Assert.True(Player.PlayerUpdatesWorker.TryStart(Player, updates.Rules, null, updates, out updates.Generation));
            Step();
        }

        public void Step()
        {
            Player.Area.Step(++frame, Now);
            Game.StepPlayerStats(Player);
            Player.Area.Buffs.Publish();
        }

        public Task<bool> Cast(uint id = 0) => Game.CastGemSkill(Player, Skill, (100, 100), (104, 100), id);
        public bool FireBow((int X, int Y) aim)
        {
            var gem = Game.GemSkillEffects(Player).Single(g => g.Id == Skill);
            var use = Game.CaptureGemProjectile(Player, fixture.Session, Skill, gem, aim, new(1));
            fixture.Session.Schedule(fixture.Session.Runtime.Now, () => true, () => use);
            return true;
        }
        public bool AdvanceUntilPlayerHit(double maxSeconds)
        {
            double before = Player.LastEnemyHitAt;
            return AdvanceUntil(() => Player.LastEnemyHitAt > before, maxSeconds);
        }

        public uint AdvanceUntilHit(SpawnedMonster target, double maxSeconds)
        {
            uint previousLife = target.Life, damage = 0;
            void ObserveHit()
            {
                uint life = target.Life;
                if (damage == 0 && life < previousLife) damage = previousLife - life;
                previousLife = life;
            }

            // One advance can commit multiple arrows. Compare the first individual hit in each phase.
            target.LifeChanged += ObserveHit;
            try
            {
                AdvanceUntil(() => damage > 0, maxSeconds);
                return damage;
            }
            finally { target.LifeChanged -= ObserveHit; }
        }

        bool AdvanceUntil(Func<bool> observed, double maxSeconds)
        {
            const double step = 0.05;
            for (int i = 0; i < (int)(maxSeconds / step); i++)
            {
                Advance(step);
                if (observed()) return true;
            }
            return false;
        }
        public SpawnedMonster AddTarget((int X, int Y)? at = null)
        {
            var pos = at ?? (104, 100);
            var target = new SpawnedMonster("resonance target", new(pos.X, pos.Y, 0), 0, 0, 1_000_000);
            Player.Area.Monsters.Add(TargetId, target);
            return target;
        }
        public int Stat(uint key) => Game.PlayerStatValues(Player).GetValueOrDefault(key);
        public (int, int, int) Elemental() => (Stat(global::Stat.FireResonance), Stat(global::Stat.ColdResonance),
            Stat(global::Stat.LightningResonance));
        public void Advance(double seconds) => MoveClock(seconds, advanceProjectiles: true);
        public void AdvanceOutput(double seconds)
        {
            using (Game.ProjectileMutation(Player, advance: false))
                fixture.Session.AdvanceTo(Math.Max(fixture.Session.Runtime.Now, ProjectileRuntime.Time(Now))
                    + ProjectileRuntime.Time(seconds));
        }
        void MoveClock(double seconds, bool advanceProjectiles)
        {
            using (Game.ProjectileMutation(Player, advance: false))
            {
                // Output callbacks can advance the session too; both drivers share its gate and monotonic time.
                Now = Math.Max(Now, fixture.Session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond) + seconds;
                if (advanceProjectiles) fixture.Session.AdvanceTo(ProjectileRuntime.Time(Now));
            }
        }
        public void Dispose()
        {
            if (ReferenceEquals(Player.PlayerUpdatesState, updates))
            {
                Player.PlayerUpdatesWorker.Complete(updates.Rules, null, updates, updates.Generation);
                Player.RetirePlayerUpdates();
            }
            Game.AreaUpdateContext.Value = previous;
            damage.Dispose();
            fixture.Dispose();
        }
    }
}
