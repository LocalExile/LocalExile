using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class HolyHammersMechanicsTests
{
    [Fact]
    public void Spiral_points_match_the_native_hammer_action()
    {
        var points = Game.HolyHammerSpiral((336, 1325), (327, 1325), (-1, 0), radius: 12, count: 9);
        Assert.Equal(new[] { (338, 1304), (359, 1315), (361, 1339), (344, 1356), (320, 1356),
            (302, 1341), (297, 1317), (307, 1295), (327, 1282) }, points);
    }

    [Fact]
    public async Task Hammers_follow_contacts_or_spiral_and_only_consume_removable_power_charges()
    {
        using (var regular = new Trial("Holy Hammers", charges: 3))
        {
            var centre = regular.Target(901, (85, 100));
            var near = regular.Target(902, (59, 99));
            var far = regular.Target(903, (33, 98));
            var side = regular.Target(904, (86, 71));
            var emptyBranch = regular.Target(905, (59, 129));

            Assert.False(await regular.Cast(mana: 0));
            Assert.Equal(3, regular.Charges);
            Assert.True(await regular.Cast());
            regular.Advance(.01);
            Assert.Equal(2, regular.Charges);
            Assert.True(centre.Life < centre.MaxLife);
            Assert.Equal(near.MaxLife, near.Life);
            Assert.Equal(side.MaxLife, side.Life);

            regular.Advance(.29);
            Assert.True(side.Life < side.MaxLife);
            Assert.True(near.Life < near.MaxLife);
            Assert.Equal(far.MaxLife, far.Life);
            Assert.InRange((centre.MaxLife - centre.Life) / (double)(near.MaxLife - near.Life), 1.99, 2.01);
            regular.Advance(.40);
            Assert.True(far.Life < far.MaxLife);
            Assert.Equal(emptyBranch.MaxLife, emptyBranch.Life);
        }

        using (var walled = new Trial("Holy Hammers", charges: 0, sight: (_, y) => y == 105 ? 0 : 2))
        {
            var first = walled.Target(901, (85, 100));
            var shielded = walled.Target(902, (85, 110));
            Assert.True(await walled.Cast());
            walled.Advance(.80);
            Assert.True(first.Life < first.MaxLife);
            Assert.Equal(shielded.MaxLife, shielded.Life);
        }

        using (var spiral = new Trial("Holy Hammers of Spirals", charges: 3, minimum: 1))
        {
            var neutral = spiral.Target(901, (48, 70));
            var charged = spiral.Target(902, (71, 78));
            var tooFar = spiral.Target(903, (91, 57));
            Assert.True(await spiral.Cast());
            spiral.Advance(.01);
            Assert.Equal(1, spiral.Charges);
            Assert.Equal(charged.MaxLife, charged.Life);
            spiral.Advance(1.2);
            Assert.True(charged.Life < charged.MaxLife);
            Assert.Equal(tooFar.MaxLife, tooFar.Life);
            Assert.Equal(neutral.MaxLife, neutral.Life);
        }
    }

    sealed class Trial : IDisposable
    {
        static readonly (int X, int Y) Origin = (100, 100), Aim = (56, 100);
        readonly ViperStrikeMambaTests.AttackClock clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? previous;
        readonly IDisposable stats;
        readonly ChargeSession charges;
        readonly ushort skill;

        Player Player => fixture.Player;
        internal int Charges => charges.Effective(ChargeType.Power);

        internal Trial(string gem, int charges, int minimum = 0, Func<int, int, int>? sight = null)
        {
            fixture = new(clock);
            Player.Use(new(1, "holy-hammers-test", 240, 240, Origin.X, Origin.Y, (_, _) => true, [])
                { Sight = sight ?? ((_, _) => 2) });
            Player.Walk.Teleport(Origin.X, Origin.Y, clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            var items = Game.ItemsOf(Player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
            if (minimum > 0)
            {
                var mod = Program.Items.Mods.ById("JunMasterMinimumPowerCharges1")!;
                var ring = ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
                    { Mods = [new ItemMod(ModSection.Explicit, mod.Hash, [minimum])] };
                items.Add(PlayerInventories.Ring1, 0, 0, ring);
            }
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, Player))
                {
                    values[Stat.GlobalCannotCrit] = values[Stat.GlobalAlwaysHit] = 1;
                    values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 10_000;
                }
                return values;
            });
            skill = Assert.Single(fixture.Equip((gem, 1)));
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            this.charges = Game.ChargesOf(Player);
            var character = Game.CharacterPlayerStats(Player);
            this.charges.Observe(character.GetValueOrDefault);
            Assert.Equal(minimum, this.charges.Limits(ChargeType.Power).Minimum);
            this.charges.State.Set(ChargeType.Power, charges, Game.AreaTime(Player), this.charges.Limits(ChargeType.Power));
            Advance(0);
        }

        internal SpawnedMonster Target(uint id, (int X, int Y) at)
        {
            var monster = new SpawnedMonster("Holy Hammers witness", new(at.X, at.Y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            Player.Area.Monsters[id] = monster;
            Player.Area.Grid.Set(new(id), at);
            return monster;
        }

        internal Task<bool> Cast(uint mana = 100)
        {
            Player.PlayerMana = mana;
            return Game.CastGemSkill(Player, skill, Origin, Aim, sequence: 0x8001);
        }

        internal void Advance(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            stats.Dispose();
            fixture.Dispose();
        }
    }
}
