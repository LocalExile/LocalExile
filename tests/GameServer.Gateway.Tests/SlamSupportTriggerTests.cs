using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class SlamSupportTriggerTests
{
    [Fact]
    public async Task Fissure_is_triggered_at_impact_even_when_the_slam_hits_nothing()
    {
        using var trial = new Trial("Fissure Support", "TriggeredSupportFissure");
        Assert.False(await trial.Cast(manualCompanion: true));
        Assert.False(await trial.Cast(mana: 0));
        Assert.Null(trial.Cooldown);
        Assert.True(await trial.Cast());
        Assert.Equal(1, trial.Cooldown!.SpentUses);
        Assert.Equal(500, trial.Cooldown.TotalBaseMilliseconds);

        var enemy = trial.Target(901, (109, 100));
        Assert.Equal(enemy.MaxLife, enemy.Life);
        trial.Advance(.19);
        Assert.True(enemy.Life < enemy.MaxLife);
    }

    [Fact]
    public async Task Earthshatter_triggers_fissure_when_its_impact_has_no_target()
    {
        using var trial = new Trial("Fissure Support", "TriggeredSupportFissure", "Earthshatter", "Earthshatter");
        Assert.Null(trial.Cooldown);
        Assert.True(await trial.Cast());
        Assert.Equal(1, trial.Cooldown!.SpentUses);
    }

    [Fact]
    public async Task Crystalfall_spends_three_stored_uses_on_hits_and_lands_after_its_fall()
    {
        using var trial = new Trial("Crystalfall Support", "TriggeredSupportCrystalfall");
        Assert.False(await trial.Cast(manualCompanion: true));
        Assert.True(await trial.Cast());
        Assert.Null(trial.Cooldown);
        trial.Advance(1);
        var witnesses = Enumerable.Range(0, 4).Select(i => trial.Target((uint)(901 + i), (88 - i, 100))).ToArray();

        Assert.True(await trial.Cast());
        Assert.Equal(3, trial.Cooldown!.SpentUses);
        Assert.Equal(1000, trial.Cooldown.TotalBaseMilliseconds);
        var slamLife = witnesses.Select(enemy => enemy.Life).ToArray();
        Assert.All(witnesses, enemy => Assert.True(enemy.Life < enemy.MaxLife));
        trial.Advance(.7);
        Assert.Equal(slamLife, witnesses.Select(enemy => enemy.Life));
        trial.Advance(.4);
        Assert.All(witnesses.Zip(slamLife), pair => Assert.True(pair.First.Life < pair.Second));

        trial.Advance(4);
        Assert.True(await trial.Cast());
        var beforeTransfer = witnesses.Select(enemy => enemy.Life).ToArray();
        trial.Retire();
        trial.Advance(1.2);
        Assert.Equal(beforeTransfer, witnesses.Select(enemy => enemy.Life));
    }

    sealed class Trial : IDisposable
    {
        static readonly (int X, int Y) Origin = (100, 100), Aim = (70, 100);
        readonly ViperStrikeMambaTests.AttackClock clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? previous;
        readonly IDisposable stats;
        readonly ushort slam, companion;
        readonly int effect;
        ushort sequence = 0x8000;

        Player Player => fixture.Player;
        internal SingleUseCooldownState? Cooldown => Player.SingleUseCooldowns.GetValueOrDefault(effect);

        internal Trial(string support, string childEffect, string slamName = "Ground Slam", string slamEffect = "GroundSlam")
        {
            fixture = new(clock);
            Player.Use(new(1, "slam-support-test", 240, 240, Origin.X, Origin.Y, (_, _) => true, []));
            Player.Walk.Teleport(Origin.X, Origin.Y, clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            var items = Game.ItemsOf(Player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Maul")!));
            fixture.Equip((slamName, 1), (support, 1));
            var source = Assert.Single(Game.GemSkillEffects(Player), skill => Game.SkillsFor(Player).EffectId(skill.Effect) == childEffect);
            companion = source.Id;
            effect = source.Effect;
            slam = Assert.Single(Game.GemSkillEffects(Player), skill => Game.SkillsFor(Player).EffectId(skill.Effect) == slamEffect).Id;
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, Player))
                {
                    values[Stat.GlobalCannotCrit] = values[Stat.GlobalAlwaysHit] = 1;
                    values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 1000;
                }
                return values;
            });
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            Advance(0);
        }

        internal SpawnedMonster Target(uint id, (int X, int Y) at)
        {
            var monster = new SpawnedMonster("Slam support witness", new(at.X, at.Y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            Player.Area.Monsters[id] = monster;
            Player.Area.Grid.Set(new(id), at);
            return monster;
        }

        internal Task<bool> Cast(bool manualCompanion = false, uint mana = 100)
        {
            Player.PlayerMana = mana;
            return Game.CastGemSkill(Player, manualCompanion ? companion : slam, Origin, Aim, sequence: ++sequence);
        }

        internal void Advance(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }

        internal void Retire() => Player.LeavingArea = true;

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            stats.Dispose();
            fixture.Dispose();
        }
    }
}
