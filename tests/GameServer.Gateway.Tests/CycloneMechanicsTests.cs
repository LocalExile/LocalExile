using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class CycloneMechanicsTests
{
    [Fact]
    public void Cyclone_and_Tumult_convert_only_additional_melee_range_to_increased_area()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        fixture.Equip(("Cyclone", 1), ("Cyclone of Tumult", 1));
        var skills = Game.GemSkillEffects(player).ToArray();
        Assert.Equal(2, skills.Length);
        foreach (var skill in skills)
            Assert.Equal(16, Game.SkillRadius(player, Game.SupportedSkill(player, skill.Id).Stats, false, skill.Effect));

        using var stats = GameEvents.PlayerStatSources.Add("Game", (p, source) => !ReferenceEquals(p, player) ? source
            : new Dictionary<uint, int>(source) { [Stat.MeleeWeaponRangePlus] = 10 });
        player.MarkStatsDirty(StatInput.Items);
        foreach (var skill in skills)
        {
            var supported = Game.SupportedSkill(player, skill.Id);
            var area = Game.SkillAreaStats(player, supported.Stats, skill.Effect);
            Assert.Equal(150, area[Stat.SkillAreaOfEffectPlusPct]);
            Assert.Equal(25, Game.SkillRadius(player, supported.Stats, false, skill.Effect));
        }
    }

    [Fact]
    public void Tumult_uses_the_area_clock_and_keeps_only_undecayed_stages_when_the_gem_is_reused()
    {
        var clock = new AreaClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        ushort skill = Assert.Single(fixture.Equip(("Cyclone of Tumult", 1)));
        var gem = Game.SupportedSkill(player, skill);
        var area = player.Area;
        var updates = Game.AreaUpdateContext.Value;
        var owner = GameArea.Own(area);
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            const ushort sequence = 0x8001;
            player.Connection.LockstepClient = true;
            player.ApplyMovementSpeedAt(clock.At);
            double ordinaryWalk = player.Walk.Speed;
            double initialAttack = Game.SupportedChannelSeconds(player, skill)!.Value;
            StartChannel();
            int tickMs = Program.FrameMs;
            StepTo(399);
            Check(0, 0);
            StepTo(450);
            Check(1, -10);
            StepTo(1300);
            Check(3, -30);
            StepTo(2200);
            Check(5, -50);
            Assert.InRange(Game.SupportedChannelSeconds(player, skill)!.Value, initialAttack / 2.25 - .005, initialAttack / 2.25 + .005);
            Assert.Equal(21, Game.CycloneHitRadius(player, skill, gem.Effect, gem.Stats));

            Game.ChannelReleased(player, clock.At);
            Assert.Equal(ordinaryWalk, player.Walk.Speed, 5);
            StepTo(2700);
            Assert.Equal(4, ReadStages());
            StartChannel();
            Check(4, -40);
            StepTo(3200);
            Check(5, -50);
            Game.ChannelReleased(player, clock.At);
            StepTo(5300);
            Assert.Equal(0, ReadStages());
            Assert.False(player.Area.Buffs.Has(player.Id, 1305, player.Id));
            Assert.Equal(initialAttack, Game.SupportedChannelSeconds(player, skill)!.Value, 5);

            void StartChannel()
            {
                player.LockstepSkill.Press((100, 100), null, skill, 0x0408, repeats: false);
                Assert.True(player.LockstepSkill.Due(free: true));
                int action = ++player.ActionCount;
                int generation = Game.BeginPaidChannel(player, skill, gem.Effect, action, sequence);
                Game.BeginCycloneStages(player, skill, gem, action, generation, clock.At);
                player.ApplyMovementSpeedAt(clock.At);
            }

            void StepTo(int milliseconds)
            {
                for (long frame = area.Frame + 1; frame * tickMs <= milliseconds; frame++)
                {
                    clock.At = frame * tickMs / 1000.0;
                    area.Step(frame, clock.At);
                    area.Stats.Publish();
                }
            }

            int ReadStages() => area.Stats.WrittenValue(player.Id, Stat.CycloneCurrentNumberOfStages);

            void Check(int stages, int movement)
            {
                Assert.Equal(stages, ReadStages());
                Assert.Equal(stages > 0, player.Area.Buffs.Has(player.Id, 1305, player.Id));
                Assert.Equal(movement, player.ChannelMovement.Percent);
                Assert.Equal(ordinaryWalk * (100 + movement) / 100, player.Walk.Speed, 5);
            }
        }
        finally
        {
            Game.AreaUpdateContext.Value = updates;
            GameArea.Own(owner);
        }
    }

    sealed class AreaClock : IPlayerUpdateSchedule
    {
        internal double At;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping) =>
            throw new InvalidOperationException("The stage test advances area frames directly.");
    }
}
