using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class VolcanicFissureSnakingTests
{
    [Fact]
    public async Task Distinct_fissure_legs_resolve_at_contact_and_only_skip_the_last_eruptions_targets()
    {
        using var trial = new Trial((x, y) => y != 89 || x < 54 || x > 66);
        var body = trial.Target(901, (88, 100));
        var first = trial.Target(902, (60, 100));
        var second = trial.Target(903, (36, 100));
        var blocked = trial.Target(904, (60, 78));
        var friendly = trial.Target(905, (52, 100), hostile: false);
        var blast = trial.Target(906, (60, 115));

        Assert.False(await trial.Cast(mana: 0));
        trial.Advance(.3);
        Assert.Equal(first.MaxLife, first.Life);
        Assert.True(await trial.Cast());
        Assert.Equal(body.MaxLife, body.Life);
        trial.Advance(.1);
        Assert.True(body.Life < body.MaxLife);
        Assert.Equal(first.MaxLife, first.Life);
        trial.Advance(.14);
        Assert.True(first.Life < first.MaxLife);
        Assert.Equal(blast.MaxLife, blast.Life);
        uint directLife = first.Life;
        trial.Advance(.08);
        Assert.True(blast.Life < blast.MaxLife);
        Assert.Equal(directLife, first.Life);
        Assert.Equal(second.MaxLife, second.Life);
        trial.Advance(.51);
        Assert.True(second.Life < second.MaxLife);
        uint firstLife = first.Life;
        trial.Advance(.52);
        Assert.True(first.Life < firstLife);
        Assert.Equal(blocked.MaxLife, blocked.Life);
        Assert.Equal(friendly.MaxLife, friendly.Life);
    }

    [Fact]
    public async Task Quality_adds_a_leg_and_a_retired_cast_cannot_hit_again()
    {
        using var trial = new Trial((_, _) => true, quality: 20);
        var first = trial.Target(901, (60, 100));
        var second = trial.Target(902, (36, 100));

        Assert.True(await trial.Cast());
        trial.Advance(1.34);
        Assert.True(first.Life < first.MaxLife);
        uint beforeQualityLeg = second.Life;
        trial.Advance(.55);
        Assert.True(second.Life < beforeQualityLeg);

        Assert.True(await trial.Cast(sequence: 0x8002));
        trial.Advance(.35);
        uint beforeRetire = second.Life;
        trial.Retire();
        trial.Advance(2);
        Assert.Equal(beforeRetire, second.Life);
    }

    sealed class Trial : IDisposable
    {
        static readonly (int X, int Y) Origin = (100, 100), Aim = (60, 100);
        readonly ViperStrikeMambaTests.AttackClock clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? previous;
        readonly IDisposable stats;
        readonly ushort skill;

        Player Player => fixture.Player;

        internal Trial(Func<int, int, bool> walkable, byte quality = 0)
        {
            fixture = new(clock);
            Player.Use(new(1, "snaking-fissure-test", 240, 240, Origin.X, Origin.Y, walkable, []));
            Player.Walk.Teleport(Origin.X, Origin.Y, clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            var items = Game.ItemsOf(Player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
            skill = Assert.Single(fixture.Equip(("Volcanic Fissure of Snaking", 1)));
            if (quality != 0)
                items.Change(PlayerInventories.Weapon1, host.Id, item => item with
                {
                    Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { Quality = quality } }]
                });
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, Player))
                {
                    values[Stat.GlobalCannotCrit] = values[Stat.GlobalAlwaysHit] = 1;
                    values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 10_000;
                }
                return values;
            });
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            Advance(0);
        }

        internal SpawnedMonster Target(uint id, (int X, int Y) at, bool hostile = true)
        {
            var monster = new SpawnedMonster("Snaking Fissure target", new(at.X, at.Y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = hostile, NoRewards = true };
            Player.Area.Monsters[id] = monster;
            Player.Area.Grid.Set(new(id), at);
            return monster;
        }

        internal Task<bool> Cast(uint mana = 100, ushort sequence = 0x8001)
        {
            Player.PlayerMana = mana;
            return Game.CastGemSkill(Player, skill, Origin, Aim, sequence: sequence);
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
