using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ChargedDashTests
{
    [Fact]
    public void Travel_keeps_separate_path_budget_and_landing_clearance_and_scales_both_damage_phases()
    {
        AreaLayout layout = new(1, "dash-test", 300, 300, 100, 100, (_, _) => true, []);
        var straight = new Game.ChargedDashPath((100, 100), 0);
        straight.Advance((200, 100), 100, layout, new HashSet<(int, int)>());
        Assert.True(straight.Stopped);
        Assert.Equal((160, 100), straight.End);
        Assert.Equal(new[] { (100, 100), (110, 100), (120, 100), (130, 100), (140, 100), (150, 100), (160, 100) },
            straight.WavePoints());

        var early = new Game.ChargedDashPath((100, 100), 0);
        early.Advance((200, 100), 24, layout, new HashSet<(int, int)>());
        Assert.Equal((124, 100), early.End);
        Assert.Equal(new[] { (100, 100), (110, 100), (120, 100) }, early.WavePoints());

        var curved = new Game.ChargedDashPath((100, 100), 0);
        var barriers = new HashSet<(int, int)>();
        curved.Advance((200, 100), 40, layout, barriers);
        curved.Advance((100, 0), 40, layout, barriers);
        curved.Advance((0, 100), 40, layout, barriers);
        Assert.Equal(90, curved.Distance, 6);
        Assert.Equal((130, 60), curved.End);
        Assert.Equal(31, Game.ChargedDashRadius(curved.Progress, 100, 5));

        var obstructed = new Game.ChargedDashPath((100, 100), 0);
        layout = layout with { Sight = (x, _) => x < 134 ? 2 : 0, Clearance = (x, _, _) => x < 127 };
        obstructed.Advance((180, 100), 50, layout, barriers);
        Assert.Equal((133, 100), obstructed.End);
        Assert.Equal((126, 100), obstructed.Landing(layout, barriers, []));
        Assert.Equal(20, Game.ChargedDashRadius(obstructed.Progress, 100, 0));

        var ordinary = new Dictionary<uint, int>
        {
            [Stat.ChargedDashDamagePlusPctFinal] = -100,
            [Stat.ChargedDashDamagePlusPctFinalPerStack] = 75,
            [Stat.ChargedDashChannellingDamageAtFullStacksPlusPctFinal] = 150,
        };
        Assert.Equal((1, 2.5), Game.ChargedDashDamage(ordinary, release: false, stopped: true, stages: 4));
        Assert.Equal((1, 3), Game.ChargedDashDamage(ordinary, release: true, stopped: true, stages: 4));
        ordinary[Stat.ChargedDashDamagePlusPctFinalPerStack] = 145;
        Assert.Equal((1, 5.8), Game.ChargedDashDamage(ordinary, release: true, stopped: false, stages: 4));
    }

    [Fact]
    public async Task Paid_release_hits_the_path_once_and_moves_only_the_normal_variant()
    {
        foreach (string name in new[] { "Charged Dash", "Charged Dash of Projection" })
        {
            var clock = new HeldClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.Use(new(1, "dash-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
            player.Walk.Teleport(100, 100, clock.At);
            uint weapon = Game.ItemsOf(player).Items(PlayerInventories.Weapon1).Single().Id;
            Game.ItemsOf(player).Change(PlayerInventories.Weapon1, weapon, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
            ushort skill = Assert.Single(fixture.Equip((name, 1)));
            using var mods = GameEvents.PlayerStatSources.Add("Game", (p, source) => !ReferenceEquals(p, player) ? source
                : new Dictionary<uint, int>(source) { [Stat.GlobalAlwaysHit] = 1, [Stat.GlobalCannotCrit] = 1 });
            player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            var saved = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                var target = Target(801, 138, 100);
                var outside = Target(802, 138, 136);
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (180, 100), sequence: 0x8001));
                Assert.Equal((100, 100), player.Walk.Position(clock.At));
                uint mana = player.PlayerMana;
                clock.At += 1.1;
                double releaseAt = clock.At;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task first = ReleaseAfterSignal();
                Task second = ReleaseAfterSignal();
                release.SetResult();
                await Task.WhenAll(first, second);
                clock.Advance(1);
                Assert.Equal(0, player.ChannelSkill);
                Assert.Equal(mana, player.PlayerMana);
                Assert.Equal(20_000u, outside.Life);
                Assert.InRange(20_000u - target.Life, 1u, 30u);
                Assert.Equal(name == "Charged Dash", player.Walk.Position(clock.At).X > 100);

                player.Walk.Teleport(100, 100, clock.At);
                uint beforeStun = target.Life;
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (180, 100), sequence: 0x8002));
                Assert.True(player.HitRecovery.Begin(clock.At, .2f));
                clock.Advance(1.0);
                Assert.Equal(0, player.ChannelSkill);
                Assert.Equal((100, 100), player.Walk.Position(clock.At));
                Assert.Equal(beforeStun, target.Life);

                async Task ReleaseAfterSignal()
                {
                    await release.Task;
                    Game.ChannelReleased(player, releaseAt);
                }
            }
            finally { Game.AreaUpdateContext.Value = saved; }

            SpawnedMonster Target(uint id, int x, int y)
            {
                var monster = new SpawnedMonster("Charged Dash witness", new(x, y, 0), 0, 0, 20_000) { NoRewards = true };
                player.Area.Monsters[id] = monster;
                return monster;
            }
        }
    }

    [Fact]
    public async Task Aborting_a_paid_channel_preserves_its_replacement_and_retires_the_matching_dash_without_a_release()
    {
        var clock = new HeldClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var start = (X: 100, Y: 100);
        var aim = (X: 180, Y: 100);
        player.Use(new(1, "dash-test", 400, 400, start.X, start.Y, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(start.X, start.Y, clock.At);
        var items = Game.ItemsOf(player);
        uint weapon = items.Items(PlayerInventories.Weapon1).Single().Id;
        items.Change(PlayerInventories.Weapon1, weapon, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        ushort skill = Assert.Single(fixture.Equip(("Charged Dash", 1)));
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        var saved = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            int effect = Game.SupportedSkill(player, skill).Effect;
            int stale = Game.BeginPaidChannel(player, skill, effect, player.ActionCount, 0x8001);
            Assert.True(await Game.CastGemSkill(player, skill, start, aim, sequence: 0x8002));
            var replacement = player.PaidChannel;
            int active = player.ChannelGeneration;
            Assert.NotEqual(stale, active);
            Assert.NotNull(replacement);

            clock.At += 1.1;
            Game.AbortStartedChannel(player, stale);
            Assert.Same(replacement, player.PaidChannel);
            Assert.Equal(skill, player.ChannelSkill);
            Assert.Equal(active, player.ChannelGeneration);

            Game.AbortStartedChannel(player, active);
            Assert.Null(player.PaidChannel);
            Assert.Equal(0, player.ChannelSkill);
            Assert.Equal(active + 1, player.ChannelGeneration);
            clock.Advance(1);
            Assert.Equal(start, player.Walk.Position(clock.At));
        }
        finally { Game.AreaUpdateContext.Value = saved; }
    }

    sealed class HeldClock : IPlayerUpdateSchedule
    {
        readonly List<(double When, TaskCompletionSource Tcs)> waits = [];
        internal double At = 3;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource();
            waits.Add((At + milliseconds / 1000.0, completion));
            return completion.Task;
        }

        internal void Advance(double seconds)
        {
            double end = At + seconds;
            while (waits.Count > 0)
            {
                var next = waits.MinBy(wait => wait.When);
                if (next.When > end) break;
                waits.Remove(next);
                At = Math.Max(At, next.When);
                next.Tcs.SetResult();
            }
            At = end;
        }
    }
}
