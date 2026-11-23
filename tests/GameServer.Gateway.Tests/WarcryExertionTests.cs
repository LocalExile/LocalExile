using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class WarcryExertionTests
{
    [Fact]
    public void War_bringer_doubles_the_native_count_and_each_eligible_attack_spends_one()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var native = IntimidatingCryStats();
        Game.ApplyIntimidatingCryLocked(player, native, new Dictionary<uint, int>(), 1);
        Assert.Equal(2, Game.IntimidatingCryExerts(player));

        Game.ApplyIntimidatingCryLocked(player, native, new Dictionary<uint, int> { [Stat.WarcriesExertTwiceAsManyAttacks] = 1 }, 1);
        Assert.Equal(4, Game.IntimidatingCryExerts(player));
        Assert.False(Game.ConsumeIntimidatingCryExertLocked(player, eligible: false));
        for (int remaining = 3; remaining >= 0; remaining--)
        {
            Assert.True(Game.ConsumeIntimidatingCryExertLocked(player, eligible: true));
            Assert.Equal(remaining, Game.IntimidatingCryExerts(player));
        }
        Assert.False(Game.ConsumeIntimidatingCryExertLocked(player, eligible: true));
    }

    [Theory]
    [InlineData("Molten Strike", false)]
    [InlineData("Shield Charge", false)]
    [InlineData("Shield Charge", true)]
    public async Task Paid_attacks_apply_both_multipliers_only_when_eligible(string attack, bool blockTravelExertion)
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var previous = Game.AreaUpdateContext.Value;
        var clock = new AttackClock();
        player.ProjectileRuntimeState = f.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                if (blockTravelExertion) values[Stat.TravelSkillsCannotBeExerted] = 1;
            }
            return values;
        });
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, _, rolled) => ReferenceEquals(owner, player)
            ? DamagePacket.Of(DamageType.Physical, 1000, 1000) : rolled);
        try
        {
            if (attack == "Shield Charge")
            {
                var items = Game.ItemsOf(player);
                items.Change(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id,
                    _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
                items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
            }
            ushort skill = Assert.Single(f.Equip((attack, 1), ("Overexertion Support", 1)));
            var native = Game.SupportedSkill(player, skill).Stats;
            Assert.Equal(7, native[Stat.SupportOverexertionDamagePlusPctFinalPerWarcryExertingAction]);
            Assert.Equal(10, native[Stat.SupportOverexertionDamagePlusPctFinalIfExerted]);
            uint normal = await Hit(901);
            Assert.Equal(1000u, normal);

            Game.ApplyIntimidatingCryLocked(player, IntimidatingCryStats(), new Dictionary<uint, int>(), clock.At);
            Assert.Equal(2, Game.IntimidatingCryExerts(player));
            Assert.Equal(blockTravelExertion ? normal : 1177u, await Hit(902));
            Assert.Equal(blockTravelExertion ? 2 : 1, Game.IntimidatingCryExerts(player));

            async Task<uint> Hit(uint id)
            {
                var target = new SpawnedMonster("exerted attack target", new(104, 100, 0), 0, 0, 1_000_000);
                player.Area.Monsters.Add(id, target);
                player.NextAttackAt = 0;
                // At zero travel distance Shield Charge lands one hit and has no distance bonus.
                var aim = attack == "Shield Charge" ? (100, 100) : (104, 100);
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), aim, id));
                while (target.Life == target.MaxLife && clock.At < 10)
                {
                    clock.At += .05;
                    f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                }
                Assert.True(target.Life < target.MaxLife);
                player.Area.Monsters.Remove(id);
                return target.MaxLife - target.Life;
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static Dictionary<uint, int> IntimidatingCryStats()
    {
        int effect = Program.Items.FindGem("Intimidating Cry")!.Value.Variant.Effect;
        return Program.Skills.EffectStats(effect, 1, 0).ToDictionary(stat => stat.Item1, stat => stat.Item2);
    }

    sealed class AttackClock : IPlayerUpdateSchedule
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
