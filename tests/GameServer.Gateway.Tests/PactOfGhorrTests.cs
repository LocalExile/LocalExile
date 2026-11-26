using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class PactOfGhorrTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_paid_pact_releases_volley_debuffs_until_its_hunger_ends(bool cancel)
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "pact-of-ghorr-test", 450, 450, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            fixture.Equip(("Pact of Ghorr", 1));
            ushort pact = Skill("PactOfGhorr");
            ushort child = Skill("TriggeredBloodrend");
            var near = Target(901, 106);
            var far = Target(902, 400);
            Assert.False(await Game.CastGemSkill(player, child, (100, 100), (106, 100)));
            Assert.True(Game.TryBeginSkillCooldownUse(player, pact, Game.SupportedSkill(player, pact).Effect,
                Game.CooldownForSkill(player, pact), false, () => player.ActionCount++, out var paid));
            Assert.True(await Game.CastGemSkill(player, pact, (100, 100), (106, 100), cooldownUse: paid));
            Assert.Equal(9984u, player.PlayerMana);
            var born = new HashSet<long>();
            double peakTaken = 1;
            bool hunger = false;
            for (int i = 0; i < 250; i++)
            {
                clock.At += .04;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                foreach (var flight in fixture.Session.Runtime.Projectiles)
                    if (flight.Profile.Id == "Fleshrend") born.Add(flight.Id);
                await Game.TickContagions(player);
                hunger |= player.Area.Buffs.Has(player.Id, 2761, player.Id);
                peakTaken = Math.Max(peakTaken, Game.MonsterDotTaken(near, DamageType.Chaos, clock.At));
                if (i == 25 && cancel)
                {
                    Assert.Empty(born);
                    ushort wire = player.Area.Buffs.Next;
                    player.Area.Buffs.Publish();
                    Assert.True(await Game.CancelRemovableBuff(player, wire));
                }
            }
            Assert.True(hunger);
            Assert.Equal(cancel ? 0 : 9, born.Count);
            Assert.Equal(9984u, player.PlayerMana);
            Assert.Equal(cancel ? 1 : 1.75, peakTaken, 6);
            Assert.Equal(1, Game.MonsterDotTaken(near, DamageType.Chaos, clock.At), 6);
            Assert.Equal(!cancel, near.Life < near.MaxLife);
            Assert.Equal(far.MaxLife, far.Life);
            Assert.False(player.Area.Buffs.Has(player.Id, 2761, player.Id));
            Assert.False(player.Area.Buffs.Has(near.ObjectId, 2762, player.Id));
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        ushort Skill(string effect) => Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;

        SpawnedMonster Target(uint id, int x)
        {
            var monster = new SpawnedMonster("Fleshrend witness", new(x, 100, 0), 0, 0, 10000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            player.Area.Monsters[id] = monster;
            player.Area.Grid.Set(new(id), (x, 100));
            return monster;
        }
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double At = 3;
        public double Now(Player player) => At;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}
