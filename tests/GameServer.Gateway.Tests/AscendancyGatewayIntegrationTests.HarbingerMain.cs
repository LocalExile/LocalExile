using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Harbinger_current_executor_excludes_companions_and_preserves_cursed_spawn_speed_roll()
    {
        using var game = HarbingerGame([1811, 60867]);
        await using var wire = new ProjectileRuntimeFixture(game.State);
        var variety = Program.Monsters.Find("Metadata/Monsters/Goatman/GoatmanLeapSlam")!;
        uint id = await Program.SpawnMonster("harbinger-main", wire.Stream, wire.Ciphers, game.State,
            variety, 5, MonsterRarity.Normal, 34, 32, log: false);
        var monster = game.State.Monsters[id];
        Assert.NotEqual(0, monster.SpeedRoll);
        double ordinarySpeed = monster.Walk.Speed;
        double now = game.State.Clock.Elapsed.TotalSeconds;
        game.State.TemporalChains[id] = new(40, 1, new EffectClock(20, now), -50, -50, -25, false);
        Program.SetMonsterCurseSpeed(game.State, id, now);
        Assert.Equal(ordinarySpeed * .5, monster.Walk.Speed, 9);
        var plan = Program.PlanMonsterAi(game.State, new(), now);
        Assert.Equal(id, Assert.Single(plan.Swings).Id);
        Assert.Equal(now + monster.Attack!.Seconds / .5, monster.BusyUntil, 9);
        game.State.TemporalChains.Remove(id);
        Program.SetMonsterCurseSpeed(game.State, id, now);
        Assert.Equal(ordinarySpeed, monster.Walk.Speed, 9);

        // Even an accidentally hostile companion must never enter the ordinary executor.
        monster = new("owned actor", new(34, 32, 10), 100, 1, 1)
        { Hostile = true, Companion = Program.Harbingers.ForNode(60867)!, Attack = monster.Attack };
        game.State.Monsters[id] = monster;
        plan = Program.PlanMonsterAi(game.State, new(), now + 1);
        Assert.Empty(plan.Swings);
        Assert.Empty(plan.Moves);
        Assert.False(Program.MonsterActionCurrent(game.State, id, monster, monster.ActionGeneration));
    }

    [Fact]
    public async Task Harbinger_swing_uses_curse_at_accepted_gate_and_keeps_release_fraction_and_projectile_speed()
    {
        using var game = HarbingerGame();
        await using var wire = new ProjectileRuntimeFixture(game.State);
        // A synthetic attack isolates gate timing from contact damage and catalog availability.
        var projectile = new SkillCatalog.MonsterProjectileInfo("test", 40, true, 1);
        var monster = new SpawnedMonster("gate swing", new(34, 32, 10), 100, 1, 100)
        {
            Hostile = true,
            Attack = new(0x102, .8, 10, DamagePacket.Of(DamageType.Physical, 1, 1))
            { Projectile = projectile, Release = new("attack1", .375, null, false) },
        };
        game.State.Monsters[2] = monster;
        await wire.HoldOutput();
        var swing = Program.MonsterSwing("harbinger-main", wire.Stream, wire.Ciphers, game.State, 2, monster, 0);
        Assert.False(swing.IsCompleted);
        double now = game.State.Clock.Elapsed.TotalSeconds;
        game.State.TemporalChains[2] = new(40, 1, new EffectClock(20, now), -50, -50, -25, false);
        double before = game.State.Clock.Elapsed.TotalSeconds;
        wire.ReleaseOutput();
        var bytes = await wire.ReadWire();
        Assert.NotEmpty(bytes);
        double after = game.State.Clock.Elapsed.TotalSeconds;
        Assert.InRange(monster.BusyUntil, before + 1.6, after + 1.6);
        Assert.Equal(.375, monster.Attack.Release!.Fraction);
        Assert.Equal(40, monster.Attack.Projectile!.Speed);
        monster.ActionGeneration++;
        await swing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Harbinger_native_build_cache_never_contaminates_server_timing_or_the_mode_catalog(bool ruthless)
    {
        using var game = HarbingerGame([1811, 60867], ruthless);
        EquipFamilyGem(game, FamilyGem("Fireball"));
        var skill = ProgramValue<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", game.State).Single(g => g.Name == "Fireball");
        double now = game.State.Clock.Elapsed.TotalSeconds;
        var cold = DamagePacket.Of(DamageType.Cold, 100, 100);
        Program.ApplyPlayerAilmentsLocked(game.State, cold, cold, false, false, now, Program.CharacterPlayerStats(game.State));
        double chill = game.State.ElementalAilments.ActionFactor(now);
        Assert.InRange(chill, .7, .95);
        var raw = Program.CharacterPlayerStats(game.State);
        var scopeType = typeof(Program).GetNestedType("SkillBuildScope", BindingFlags.NonPublic)!;
        var scope = Activator.CreateInstance(scopeType, [game.State, raw])!;
        var local = typeof(Program).GetField("StatsScope", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var value = local.GetType().GetProperty("Value")!;
        var previous = value.GetValue(local);
        value.SetValue(local, scope);
        try
        {
            uint action = Program.Stats.Key(ActionSpeed.Virtual)!.Value;
            var first = ProgramValue<IEnumerable<(uint Key, int Value)>>("FinalPlayerStats", game.State).ToList();
            Assert.Equal((int)Math.Round(100 * (chill - 1)), first.Single(s => s.Key == action).Value);
            first.Clear(); // Public callers own their returned lists; the cached native snapshot survives.
            var second = ProgramValue<IEnumerable<(uint Key, int Value)>>("FinalPlayerStats", game.State).ToDictionary(s => s.Key, s => s.Value);
            Assert.NotEmpty(second);
            var server = ProgramValue<IEnumerable<(uint Key, int Value)>>("ServerSkillPlayerStats", game.State).ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(0, server[action]);
            Assert.Equal(0, raw[action]);
            var catalog = ProgramValue<SkillCatalog>("SkillsFor", game.State);
            Assert.Equal(!ruthless, ReferenceEquals(Program.Skills, catalog));
            int expected = (int)Math.Ceiling(SpellCastTiming.ForSkill(catalog, skill.Effect, 0)!.Value.DurationMilliseconds!.Value / chill);
            Assert.Equal(expected, ProgramValue<int?>("CastMs", game.State, skill.Effect, skill.Id));
            _ = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State);
            Assert.Equal(expected, ProgramValue<int?>("CastMs", game.State, skill.Effect, skill.Id));
        }
        finally { value.SetValue(local, previous); }
    }
}
