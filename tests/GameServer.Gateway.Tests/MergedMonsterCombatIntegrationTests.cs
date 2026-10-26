using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Actual_owned_wolf_hits_honour_target_leech_and_extra_critical_damage_flags(
        bool preventsLeech, bool preventsExtraCritical)
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(classRow: 3, baseClass: 2, choice: "Wildspeaker", level: 30,
            refundPoints: 60);
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        Assert.Null((await game.Change(PathTo(definition, 26963), true)).Error);
        await game.TriggerWolf(true);
        uint wolfId = Assert.Single(game.State.SummonObjects).Value.Object;
        var wolf = game.State.Monsters[wolfId];
        Assert.Same(MonsterModSet.None, wolf.Mods);
        Assert.DoesNotContain(wolf, MonsterEmerge.Unrisen(game.State));
        wolf.Life = wolf.MaxLife / 2;
        wolf.Attack = wolf.Attack! with { Seconds = .002 };
        game.State.PlayerStats[Program.Stats.Key("minion_additional_base_critical_strike_chance")!.Value] = 10000;
        game.State.PlayerStats[Program.Stats.Key("minion_critical_strike_multiplier_+")!.Value] = 300;
        var profile = Program.WolfProfile(game.State, wolf.Variety!, wolf.Level);
        Assert.Equal(10000, profile.Chance);
        Assert.True(profile.Multiplier > 100);
        var flags = new Dictionary<string, int>
        {
            ["cannot_have_life_leeched_from"] = preventsLeech ? 1 : 0,
            ["self_take_no_extra_damage_from_critical_strikes"] = preventsExtraCritical ? 1 : 0,
            ["base_additional_physical_damage_reduction_%"] = 50,
        };
        var target = new SpawnedMonster("merged mod target", new WalkEstimator(32, 32, 0), 0, 0, 1000000)
        { Mods = new([], [], new(flags)) };
        game.State.Monsters[12345] = target;
        uint wolfLife = wolf.Life;
        await game.CreatureAttack(wolfId, wolf, 12345, target);
        double factor = .5 * (preventsExtraCritical ? 1 : profile.Multiplier / 100.0);
        Assert.InRange(1000000u - target.Life,
            (uint)Math.Floor(profile.Attack.Damage.MinTotal * factor),
            (uint)Math.Ceiling(profile.Attack.Damage.MaxTotal * factor));
        double now = game.State.Clock.Elapsed.TotalSeconds;
        uint recovered = wolf.Leech.Recover(0, now + 1, wolf.MaxLife, wolfLife);
        if (preventsLeech) Assert.Equal(wolfLife, recovered);
        else Assert.True(recovered > wolfLife);
        Assert.Equal(0, game.PlayerStat("current_power_charges"));
    }

    sealed class FirstRoll : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }

    [Fact]
    public void Ordinary_bow_contact_composes_mod_mitigation_and_blocks_before_impale_or_charge_commit()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 30);
        var shot = new OrdinaryBowShot(DamagePacket.Of(DamageType.Physical, 100, 100)
            .Add(DamageType.Fire, new(100, 100)), 100, 10000, 200, 100);
        var target = new SpawnedMonster("bow mod target", new WalkEstimator(32, 32, 0), 0, 0, 10000)
        {
            Mods = new([], [], new(new Dictionary<string, int>
            {
                ["monster_base_block_%"] = 75,
                ["base_fire_damage_resistance_%"] = 50,
                ["base_additional_physical_damage_reduction_%"] = 50,
                ["self_take_no_extra_damage_from_critical_strikes"] = 1,
            })),
        };
        game.State.Monsters[12345] = target;
        var blocked = Program.CommitOrdinaryBowHitLocked(game.State, target, shot, new FirstRoll(), game.State.ProjectileEpoch);
        Assert.Null(blocked.Applied);
        Assert.Equal(10000u, target.Life);
        Assert.Equal(0, target.Impale.Stacks);
        target = new SpawnedMonster("unblocked bow mod target", new WalkEstimator(32, 32, 0), 0, 0, 10000)
        {
            Mods = target.Mods with { Effects = new(new Dictionary<string, int>
            {
                ["base_fire_damage_resistance_%"] = 50,
                ["base_additional_physical_damage_reduction_%"] = 50,
                ["self_take_no_extra_damage_from_critical_strikes"] = 1,
            }) },
        };
        game.State.Monsters[12345] = target;
        var landed = Program.CommitOrdinaryBowHitLocked(game.State, target, shot, new FirstRoll(), game.State.ProjectileEpoch);
        Assert.NotNull(landed.Applied);
        Assert.True(landed.Critical);
        Assert.Equal(100, landed.Damage); // 100 physical with 50% reduction + 100 fire with 50% resistance.
        Assert.Equal(9900u, target.Life);
        Assert.Equal(1, target.Impale.Stacks);
    }
}
