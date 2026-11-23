using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ResonanceSupportTests
{
    [Fact]
    public async Task A_supported_hit_fills_the_other_elements_and_they_decay_after_attacks_stop()
    {
        using var f = new Fixture("Trinity Support", DamageType.Fire);
        Assert.True(await f.Cast());
        f.Advance(1);
        Assert.Equal((0, 0, 0), f.Elemental());

        var target = f.AddTarget();
        Assert.True(await f.Cast(Fixture.TargetId));
        Assert.True(target.Life < target.MaxLife);
        f.Advance(1);
        Assert.Equal((0, 25, 25), f.Elemental());
        f.Advance(1);
        Assert.Equal((0, 50, 50), f.Elemental());
        f.Advance(1);
        Assert.Equal((0, 45, 45), f.Elemental());
        f.Advance(9);
        Assert.Equal((0, 0, 0), f.Elemental());
    }

    [Fact]
    public async Task Unholy_resonance_is_separate_and_its_resistance_bonus_requires_all_three()
    {
        using var f = new Fixture("Unholy Trinity Support", DamageType.Chaos);
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

    sealed class Fixture : IDisposable, IPlayerUpdateSchedule
    {
        public const uint TargetId = 271;
        readonly TriggeredSummonTests.Fixture fixture = new();
        readonly PlayerUpdatesState? previous = Game.AreaUpdateContext.Value;
        readonly IDisposable damage;
        public readonly ushort Skill;
        public DamageType Dominant;
        public Player Player => fixture.Player;
        public double Now = 1;
        double IPlayerUpdateSchedule.Now(Player player) => Now;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            Now += milliseconds / 1000d;
        }

        public Fixture(string support, DamageType dominant)
        {
            Skill = Assert.Single(fixture.Equip(("Absolution of Inspiring", 1), (support, 1)));
            Dominant = dominant;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, this);
            Player.ProjectileRuntimeState = fixture.Session;
            damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, _, rolled) => ReferenceEquals(owner, Player)
                ? DamagePacket.Of(Dominant, 120, 120) : rolled);
        }

        public Task<bool> Cast(uint id = 0) => Game.CastGemSkill(Player, Skill, (100, 100), (104, 100), id);
        public SpawnedMonster AddTarget()
        {
            var target = new SpawnedMonster("resonance target", new(104, 100, 0), 0, 0, 1_000_000);
            Player.Area.Monsters.Add(TargetId, target);
            return target;
        }
        public int Stat(uint key) => Game.PlayerStatValues(Player).GetValueOrDefault(key);
        public (int, int, int) Elemental() => (Stat(global::Stat.FireResonance), Stat(global::Stat.ColdResonance),
            Stat(global::Stat.LightningResonance));
        public void Advance(double seconds)
        {
            Now += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Now));
        }
        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            damage.Dispose();
            fixture.Dispose();
        }
    }
}
