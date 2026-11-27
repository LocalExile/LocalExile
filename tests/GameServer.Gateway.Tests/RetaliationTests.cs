using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class RetaliationTests
{
    internal sealed class LowRolls : Random
    {
        public override double NextDouble() => 0;
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double Time = 1;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping) =>
            throw new InvalidOperationException("Skill admission must run in the current area frame.");
    }

    [Theory]
    [InlineData("Crushing Fist", 2469)]
    [InlineData("Glacial Shield Swipe", 2468)]
    [InlineData("Swordstorm", 2472)]
    [InlineData("Eviscerate", 2473)]
    public void Block_opens_one_melee_retaliation_without_expiring_other_skills(string gem, ushort readyRow)
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var ids = fixture.Equip((gem, 1), ("Divine Retribution", 1));
        ushort id = ids[0];
        ushort divine = ids.Last();
        var skill = Game.SupportedSkill(player, id);
        player.IncomingHitRandom = new LowRolls();
        var clock = new Clock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.CharacterPlayerStats(player);
            uint mana = player.PlayerMana;
            Assert.False(Admit());
            Assert.Equal(mana, player.PlayerMana);
            Block();
            var ready = Assert.IsType<Buff>(player.Area.Buffs.Get(player.Id, readyRow, player.Id, tag: id));
            Assert.Equal(id, ready.SourceSkill);
            Assert.False(Admit(triggered: true));
            clock.Time += 2;
            Assert.False(Admit());

            Block();
            player.PlayerMana = 0;
            Assert.False(Admit());
            Assert.True(player.Area.Buffs.Has(player.Id, readyRow, player.Id, tag: id));
            player.PlayerMana = mana;
            Assert.True(Admit());
            Assert.False(player.Area.Buffs.Has(player.Id, readyRow, player.Id, tag: id));
            Assert.True(player.Area.Buffs.Has(player.Id, 2470, player.Id, tag: divine));
            Assert.True(player.PlayerMana < mana);
            Assert.False(Admit());

            bool Admit(bool triggered = false) => Game.TryBeginCooldownUseCore(player, skill.Effect,
                Game.CooldownForSkill(player, id), false, () => { }, out _, actionToken: null,
                reserve: true, requestedAt: null, skillId: id, triggered: triggered);

            void Block()
            {
                player.Pools = player.Pools with { SpellBlockChance = 100 };
                Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 1,
                    block: true, spell: true, suppress: false, out var hit));
                Assert.True(hit.Blocked);
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Swordstorm_hits_eight_times_at_the_forward_centre_after_its_release()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "swordstorm-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        var inventory = Game.ItemsOf(player);
        var main = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        inventory.Change(PlayerInventories.Weapon1, main.Id, _ => ItemCatalog.Create(Program.Items.Find("Rusted Hatchet")!));
        var axe = ItemCatalog.Create(Program.Items.Find("Rusted Hatchet")!);
        // A shield satisfies the ordinary class restriction on the main hand alone.
        var shield = inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
        ushort id = Assert.Single(fixture.Equip(("Swordstorm", 1)));
        var skill = Game.SupportedSkill(player, id);
        var front = Target(901, 126);
        var rear = Target(902, 86);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.CharacterPlayerStats(player);
            player.IncomingHitRandom = new LowRolls();
            player.Pools = player.Pools with { SpellBlockChance = 100 };
            Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 1,
                block: true, spell: true, suppress: false, out var block));
            Assert.True(block.Blocked);
            Assert.NotNull(Game.WeaponRestricted(player, skill.Effect));
            inventory.Change(PlayerInventories.Offhand1, shield.Id, _ => axe);
            Game.RecomputePoolsAndSpeed(player);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, id, (100, 100), (110, 100), sequence: 1));
            Assert.Equal(AttackHand.Both, Game.AttackHandFor(player, id, 1));
            Step(.19);
            Assert.Equal(front.MaxLife, front.Life);
            uint previousLife = front.Life;
            Step(.02);
            Assert.InRange(front.Life, 1u, previousLife - 1);
            for (int pulse = 1; pulse < 8; pulse++)
            {
                previousLife = front.Life;
                Step(.15);
                Assert.InRange(front.Life, 1u, previousLife - 1);
            }
            previousLife = front.Life;
            Step(.4);
            Assert.Equal(previousLife, front.Life);
            Assert.Equal(rear.MaxLife, rear.Life);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        SpawnedMonster Target(uint key, int x)
        {
            var target = new SpawnedMonster("Swordstorm target", new(x, 100, 0), 0, 0, 10000)
                { ObjectId = key, Hostile = true, NoRewards = true };
            player.Area.Monsters[key] = target;
            player.Area.Grid.Set(new(key), (x, 100));
            return target;
        }

        void Step(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }
    }

    [Fact]
    public void Divine_Retribution_spends_one_unexpired_block_opportunity_after_cost_admission()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort id = Assert.Single(fixture.Equip(("Divine Retribution", 1)));
        var skill = Game.SupportedSkill(player, id);
        player.IncomingHitRandom = new LowRolls();
        var clock = new Clock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.CharacterPlayerStats(player);
            bool Admit(bool reserve = true, bool triggered = false) => Game.TryBeginCooldownUseCore(player, skill.Effect,
                reserve ? Game.CooldownForSkill(player, id) : null, false, () => { }, out _, actionToken: null,
                reserve: reserve, requestedAt: null, skillId: id, triggered: triggered);

            void Block()
            {
                player.Pools = player.Pools with { SpellBlockChance = 100 };
                Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 1,
                    block: true, spell: true, suppress: false, out var hit));
                Assert.True(hit.Blocked);
            }

            uint mana = player.PlayerMana;
            Assert.False(Admit());
            Assert.Equal(mana, player.PlayerMana);
            Assert.Null(Game.SingleUseState(player, skill.Effect, id));

            Block();
            Assert.False(Admit(triggered: true));
            Assert.False(Game.TriggerPayloadAvailable(player, skill));
            var readyBuff = Assert.IsType<Buff>(player.Area.Buffs.Get(player.Id, 2470, player.Id, tag: id));
            Assert.Equal(id, readyBuff.SourceSkill);
            Assert.Equal((ushort)0, readyBuff.U82);
            clock.Time += 2;
            Assert.True(player.RecentEvents.Happened(StatConditions.Blocked, clock.Time));
            Assert.False(Admit());
            Assert.Equal(mana, player.PlayerMana);

            Block();
            player.PlayerMana = 0;
            Assert.False(Admit());
            Assert.True(player.Area.Buffs.Has(player.Id, 2470, player.Id, tag: id));
            player.PlayerMana = mana;
            Assert.True(Admit());
            Assert.True(player.PlayerMana < mana);
            Assert.False(player.Area.Buffs.Has(player.Id, 2470, player.Id, tag: id));
            Assert.Equal(1, Game.SingleUseState(player, skill.Effect, id)!.SpentUses);
            // The usual four-second block condition survives the one-shot retaliation window.
            Assert.True(player.RecentEvents.Happened(StatConditions.Blocked, clock.Time));
            Assert.False(Admit(reserve: false));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }
}
