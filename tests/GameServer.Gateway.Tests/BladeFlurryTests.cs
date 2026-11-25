using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class BladeFlurryTests
{
    [Fact]
    public void Blade_flurry_acquires_in_the_aimed_direction_and_scales_the_distinct_stage_bonuses()
    {
        Assert.Equal((78, 100), Game.BladeFlurrySearchCentre((100, 100), (99, 100), 22, 0x8001, 4));
        Assert.Equal((100, 122), Game.BladeFlurrySearchCentre((100, 100), (100, 200), 22, 0x8001, 4));

        Dictionary<uint, int> normal = new()
        {
            [Stat.DisplayMaxChargedAttackStats] = 6,
            [Stat.ChargedAttackDamagePerStackPlusPctFinal] = 25,
            [Stat.BladeFlurryDamagePlusPctFinalWhileAtMaxStages] = 20,
        };
        Assert.Equal(6, Game.BladeFlurryMaxStages(normal, new Dictionary<uint, int>()));
        Assert.Equal(7, Game.BladeFlurryMaxStages(normal, new Dictionary<uint, int> { [Stat.Plus1MaxChargedAttackStages] = 1 }));
        Assert.Equal(1.25, Game.BladeFlurryStageBonus(normal, 1, 6).Damage);
        Assert.Equal(2.25, Game.BladeFlurryStageBonus(normal, 5, 6).Damage);
        Assert.Equal(3, Game.BladeFlurryStageBonus(normal, 6, 6).Damage, 6);

        Dictionary<uint, int> incision = new()
        {
            [Stat.BladeFlurryCriticalStrikeChancePerStagePlusPctFinal] = 40,
            [Stat.BladeFlurryCriticalStrikeMultiplierPlusWhileAtMaxStages] = 40,
        };
        Assert.Equal(2.2, Game.BladeFlurryStageBonus(incision, 3, 6).CriticalMore, 6);
        Assert.Equal(0, Game.BladeFlurryStageBonus(incision, 5, 6).CriticalMultiplier);
        Assert.Equal(new Game.BladeFlurryBonus(1, 3.4, 40), Game.BladeFlurryStageBonus(incision, 6, 6));
    }

    [Fact]
    public async Task Paid_channel_can_change_target_and_releases_its_stages_once_without_paying_again()
    {
        var clock = new AreaClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "flurry-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        uint weaponId = Game.ItemsOf(player).Items(PlayerInventories.Weapon1).Single().Id;
        Game.ItemsOf(player).Change(PlayerInventories.Weapon1, weaponId, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        ushort id = Assert.Single(fixture.Equip(("Blade Flurry", 1)));
        var gem = Assert.Single(Game.GemSkillEffects(player));
        var supported = Game.SupportedSkill(player, id);
        using var mods = GameEvents.PlayerStatSources.Add("Game", (p, source) => !ReferenceEquals(p, player) ? source
            : new Dictionary<uint, int>(source)
            {
                [Stat.GlobalAlwaysHit] = 1, [Stat.GlobalCannotCrit] = 1,
                [Stat.AttackMinimumAddedPhysicalDamage] = 100, [Stat.AttackMaximumAddedPhysicalDamage] = 100,
            });
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        var updates = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            var left = Target(234, 74, 100);
            var upper = Target(235, 100, 74);
            const ushort sequence = 0x8001;
            int action = ++player.ActionCount;
            int generation = Game.BeginPaidChannel(player, id, gem.Effect, action, sequence);
            Game.BeginBladeFlurry(player, supported, id, action, generation, sequence, (75, 100));
            uint fullStageHit = 0;
            for (int i = 1; i <= 7; i++)
            {
                clock.At += .4;
                if (i == 3) Game.ChannelAimedAt(player, 100, 75, clock.At);
                uint life = upper.Life;
                await Game.AreaSkillHit(player, id, gem, (100, 100), (75, 100), immediate: true,
                    sequence: sequence, actionToken: action, channelGeneration: generation);
                Assert.True(player.Area.Buffs.Has(player.Id, 640, player.Id));
                player.Area.Stats.Publish();
                Assert.Equal(Math.Min(6, i), player.Area.Stats.WrittenValue(player.Id, Stat.CurrentChargedAttackStacks));
                if (i == 7) fullStageHit = life - upper.Life;
                if (i == 2) { Assert.True(left.Life < 20_000); Assert.Equal(20_000u, upper.Life); }
            }
            Assert.True(upper.Life < 20_000);
            uint before = upper.Life, mana = player.PlayerMana;
            await Game.ReleaseBladeFlurry(player, clock.At);
            Assert.False(player.Area.Buffs.Has(player.Id, 640, player.Id));
            Assert.InRange((double)(before - upper.Life), fullStageHit * 5.6, fullStageHit * 6.4);
            Assert.Equal(mana, player.PlayerMana);
            before = upper.Life;
            await Game.ReleaseBladeFlurry(player, clock.At);
            Assert.Equal(before, upper.Life);

            SpawnedMonster Target(uint objectId, int x, int y)
            {
                var monster = new SpawnedMonster("Blade Flurry witness", new(x, y, 0), 0, 0, 20_000)
                {
                    NoRewards = true,
                };
                player.Area.Monsters[objectId] = monster;
                return monster;
            }
        }
        finally { Game.AreaUpdateContext.Value = updates; }
    }

    sealed class AreaClock : IPlayerUpdateSchedule
    {
        internal double At = 3;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000.0;
            return Task.CompletedTask;
        }
    }
}
