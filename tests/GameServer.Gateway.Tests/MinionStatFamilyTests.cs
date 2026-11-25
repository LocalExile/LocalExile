namespace GameServer.Gateway.Tests;

// The minion stat family: a minion_* stat whose stripped name the monster code does not
// read has to be mapped onto the stat it does read, and the two the strip cannot reach
// at all (the plural poison chance, the dot multiplier with the prefix in the middle)
// are added explicitly. The rest of the family the strip already forwards, so the test
// pins those too: nothing may count twice.
public sealed class MinionStatFamilyTests
{
    [Fact]
    public void The_minion_stats_whose_stripped_name_nothing_reads_reach_the_summons_own_stats()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var golem = new GolemState.Golem(0xB0A1, null!, Game.GolemData.Find("SummonFlameGolem")!, 1);
        var plain = Game.GolemModifiers(player, golem, Game.CharacterPlayerStats(player));
        var stats = new Dictionary<uint, int>(Game.CharacterPlayerStats(player))
        {
            [Stat.MinionSpellSuppressionChancePct] = 23,
            [Stat.BaseMinionDotMultiplierPlus] = 30,
            [Stat.MinionElementalResistancePct] = 15,
            [Stat.MinionChaosResistancePct] = 29,
            [Stat.MinionsChanceToPoisonOnHitPct] = 206,
            [Stat.MinionChanceToDealDoubleDamagePct] = 8,
            [Stat.MinionCastSpeedPlusPct] = 42,
            [Stat.MinionCriticalStrikeChancePlusPct] = 80,
        };
        var with = Game.GolemModifiers(player, golem, stats);
        int Delta(uint stat) => with.Effects[stat] - plain.Effects[stat];
        // The two whose stripped name nothing reads now land on the stat the monster code reads.
        Assert.Equal(23, Delta(Stat.BaseSpellSuppressionChancePct));
        Assert.Equal(30, Delta(Stat.DotMultiplierPlus));
        // Nothing lands on the stripped spell suppression name itself.
        Assert.Equal(plain.Effects[Stat.SpellSuppressionChancePct], with.Effects[Stat.SpellSuppressionChancePct]);
        // The family members the strip already forwards are untouched, so nothing counts twice.
        Assert.Equal(15, Delta(Stat.BaseResistAllElementsPct));
        Assert.Equal(29, Delta(Stat.BaseChaosDamageResistancePct));
        Assert.Equal(206, Delta(Stat.BaseChanceToPoisonOnHitPct));
        Assert.Equal(8, Delta(Stat.ChanceToDealDoubleDamagePct));
        Assert.Equal(42, Delta(Stat.CastSpeedPlusPct));
        Assert.Equal(80, Delta(Stat.CriticalStrikeChancePlusPct));
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double Time = 1;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            Time += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    // The active-minion counts come out of the frame's stat graph: a minion that appears or dies marks the golem step
    // (nothing else changes a golem buff total for a timed summon), and only a minion without a duration is permanent.
    [Fact]
    public async Task The_active_minion_counts_follow_the_frame_stat_graph_and_only_a_golem_is_permanent()
    {
        var clock = new Clock();
        using var f = new TriggeredSummonTests.Fixture(clock);
        var updates = new PlayerUpdatesState(f.Lifetime, clock);
        var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        long frame = 0;
        void Step() { f.Player.Area.Step(++frame, clock.Time); Game.StepPlayerStats(f.Player); }
        int Count(uint stat) => Game.PlayerStatValues(f.Player).GetValueOrDefault(stat);
        try
        {
            f.Player.Connection.PlayerSent = true;
            f.Player.PlayerUpdatesState = updates;
            Assert.True(f.Player.PlayerUpdatesWorker.TryStart(f.Player, updates.Rules, null, updates, out updates.Generation));
            Step();
            Assert.True(Game.StatsPublishedByFrame(f.Player));
            Assert.Equal(0, Count(Stat.NumberOfActiveMinions));

            f.Equip(("Summon Skeletons", 1), ("Summon Flame Golem", 1));
            uint skeleton = await f.Summon("SummonSkeletons", 0);
            Assert.NotEqual(0u, skeleton);
            Assert.False(double.IsPositiveInfinity(f.Player.Summons.Until[skeleton]));
            Step();
            Assert.Equal(1, Count(Stat.NumberOfActiveMinions));
            Assert.Equal(0, Count(Stat.NumberOfActivePermanentMinions));

            ushort source = Game.GemSkillEffects(f.Player).Single(g => Program.Skills.EffectId(g.Effect) == "SummonFlameGolem").Id;
            Assert.NotEqual(0u, await Game.SummonGolem(f.Player, source, Game.GolemData.Find("SummonFlameGolem")!, (100, 100)));
            Step();
            Assert.Equal(2, Count(Stat.NumberOfActiveMinions));
            Assert.Equal(1, Count(Stat.NumberOfActivePermanentMinions));

            // A timed summon dying changes no golem buff total: only its own mark runs the step again.
            int runs = Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Golems);
            using (Game.ProjectileMutation(f.Player))
                Game.GolemDieLocked(f.Player, f.Player.Summons.Fighters[skeleton], clock.Time);
            Step();
            Assert.True(Game.PlayerStatStepRuns(f.Player, PlayerStatStep.Golems) > runs);
            Assert.Equal(1, Count(Stat.NumberOfActiveMinions));
            Assert.Equal(1, Count(Stat.NumberOfActivePermanentMinions));
        }
        finally
        {
            if (ReferenceEquals(f.Player.PlayerUpdatesState, updates))
            {
                f.Player.PlayerUpdatesWorker.Complete(updates.Rules, null, updates, updates.Generation);
                f.Player.RetirePlayerUpdates();
            }
            Game.AreaUpdateContext.Value = outer;
        }
    }
}
