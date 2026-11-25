using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class VaalGroundSlamWaveTests
{
    [Fact]
    public async Task Vaal_ground_slam_hits_radially_as_its_front_reaches_each_target()
    {
        var timer = new SlamClock();
        using var f = new TriggeredSummonTests.Fixture(timer);
        var player = f.Player;
        player.ProjectileRuntimeState = f.Session;
        player.Walk.Teleport(100, 100, timer.At);
        var old = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, timer);
        var hits = new List<(SpawnedMonster Target, double Seconds)>();
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, monster, packet) =>
        {
            if (ReferenceEquals(owner, player)) hits.Add((monster, timer.At));
            return packet;
        });
        try
        {
            SpawnedMonster Target(uint id, int x)
            {
                var m = new SpawnedMonster("radial wave target", new(x, 100, 0), 0, 0, 1_000_000);
                player.Area.Monsters.Add(id, m);
                player.Area.Grid.Set(new(id), (x, 100));
                return m;
            }

            var farBehind = Target(900, 72);
            var near = Target(901, 110);
            var outside = Target(902, 142);
            f.Equip(("Vaal Ground Slam", 1));
            var skill = Assert.Single(Game.GemSkillEffects(player), g => Program.Skills.EffectId(g.Effect) == "VaalGroundSlam");
            Game.RefreshVaalSouls(player);
            int cost = Assert.NotNull(Program.Skills.VaalBudget(skill.Effect, skill.Level)).BaseCost;
            for (int i = 0; i < cost; i++) Assert.True(player.VaalSouls.Gain());
            Assert.True(Game.TryBeginSkillCooldownUse(player, skill.Id, skill.Effect, Game.CooldownForSkill(player, skill.Id),
                instant: false, () => { }, out var use));
            Assert.True(await Game.CastGemSkill(player, skill.Id, (100, 100), (108, 100), cooldownUse: use));

            Assert.True(near.Life < near.MaxLife);
            Assert.True(farBehind.Life < farBehind.MaxLife);
            Assert.Equal(outside.MaxLife, outside.Life);
            double elapsed = hits.Single(hit => hit.Target == farBehind).Seconds - hits.Single(hit => hit.Target == near).Seconds;
            Assert.InRange(elapsed, .155, .192);
        }
        finally
        {
            Game.AreaUpdateContext.Value = old;
        }
    }

    sealed class SlamClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
