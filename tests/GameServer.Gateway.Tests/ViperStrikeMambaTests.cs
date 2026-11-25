using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ViperStrikeMambaTests
{
    [Fact]
    public async Task Mamba_preserves_a_live_poison_and_can_poison_after_it_expires()
    {
        using var mamba = new Encounter("Viper Strike of the Mamba");
        Assert.True(mamba.Target.Poisons.Add(mamba.Clock.At, 50, 8));
        double expiry = mamba.Target.Poisons.Until;

        await mamba.Hit();
        Assert.Equal(1, mamba.Target.Poisons.Stacks(mamba.Clock.At));
        Assert.Equal(50, mamba.Target.Poisons.TotalDps(mamba.Clock.At));
        Assert.Equal(expiry, mamba.Target.Poisons.Until);

        mamba.Clock.At = expiry;
        await mamba.Hit();
        Assert.Equal(1, mamba.Target.Poisons.Stacks(mamba.Clock.At));
        Assert.True(mamba.Target.Poisons.Until > expiry);
    }

    [Fact]
    public async Task Mamba_limits_both_global_and_local_extra_poisons_to_one_per_hit()
    {
        int mamba = await FirstHitStacks("Viper Strike of the Mamba");
        int dualWielded = await FirstHitStacks("Viper Strike of the Mamba", dualWield: true);
        int ordinary = await FirstHitStacks("Viper Strike");
        Assert.Equal(1, mamba);
        Assert.Equal(1, dualWielded);
        Assert.Equal(5, ordinary);
    }

    static async Task<int> FirstHitStacks(string gem, bool dualWield = false)
    {
        using var scene = new Encounter(gem, extraPoisons: true, dualWield: dualWield);
        await scene.Hit();
        return scene.Target.Poisons.Stacks(scene.Clock.At);
    }

    sealed class Encounter : IDisposable
    {
        internal readonly AttackClock Clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? previous;
        readonly IDisposable sources;
        readonly ushort skill;
        internal readonly SpawnedMonster Target;
        Player Player => fixture.Player;

        internal Encounter(string gem, bool extraPoisons = false, bool dualWield = false)
        {
            fixture = new(Clock);
            Player.Use(new(1, "mamba-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
            Player.Walk.Teleport(100, 100, Clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));
            var inventory = Game.ItemsOf(Player);
            var host = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
            var dagger = ItemCatalog.Create(Program.Items.Find("Glass Shank")!);
            if (extraPoisons)
            {
                var mod = Program.Items.Mods.ById("LocalApplyAdditionalPoisonUnique__1")!;
                dagger = dagger with { Mods = [new ItemMod(ModSection.Explicit, mod.Hash, [2])] };
            }
            inventory.Change(PlayerInventories.Weapon1, host.Id, _ => dagger);
            if (dualWield) inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Glass Shank")!));
            sources = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, Player)) return values;
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.BaseChanceToPoisonOnHitPct] = 100;
                if (extraPoisons) values[Stat.AdditionalPoisonsPlusToApplyVsNonPoisonedEnemies] = 2;
                return values;
            });
            skill = Assert.Single(fixture.Equip((gem, 1)));
            Target = new("poison witness", new(108, 100, 0), 0, 0, 1000)
                { ObjectId = 901, Hostile = true, NoRewards = true };
            Player.Area.Monsters[901] = Target;
            Player.Area.Grid.Set(new(901), (108, 100));
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, Clock);
        }

        internal async Task Hit()
        {
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));
            uint life = Target.Life;
            Assert.True(await Game.CastGemSkill(Player, skill, (100, 100), (108, 100), targetId: 901));
            Assert.True(Target.Life < life);
        }

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            sources.Dispose();
            fixture.Dispose();
        }
    }

    internal sealed class AttackClock : IPlayerUpdateSchedule
    {
        public double At = 3;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
