using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class BoneshatterTests
{
    [Fact]
    public async Task Boneshatter_gains_trauma_and_takes_a_physical_self_hit_after_contact()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Boneshatter", 1)));
            var target = Target(player, 921, 108, 100);
            uint life = player.PlayerLife;
            await Swing(fixture, clock, skill, 921);
            Assert.True(target.Life < target.MaxLife);
            Assert.Equal(life - 6, player.PlayerLife);
            Assert.True(player.Area.Buffs.HasRow(player.Id, 1873));
            await Swing(fixture, clock, skill, 921);
            Assert.Equal(life - 18, player.PlayerLife);
            Advance(fixture, clock, 8);
            Assert.True(player.Area.Buffs.HasRow(player.Id, 1873));
            Advance(fixture, clock, 9);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));

            using var duration = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, player)) values[Stat.SkillEffectDurationPlusPct] = 200;
                return values;
            });
            skill = Assert.Single(fixture.Equip(("Boneshatter of Complex Trauma", 1)));
            for (int stack = 1; stack <= 10; stack++)
            {
                life = player.PlayerLife;
                await Swing(fixture, clock, skill, 921);
                Assert.Equal(life - 18 * (uint)stack, player.PlayerLife);
                Assert.Equal(stack < 10, player.Area.Buffs.HasRow(player.Id, 1873));
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Boneshatter_of_carnage_releases_a_pulse_only_after_a_stun()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.AlwaysStunEnemiesThatAreOnFullLife] = 1;
            values[Stat.GlobalCannotCrit] = 1;
            return values;
        });
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Boneshatter of Carnage", 1)));
            var primary = Target(player, 931, 108, 100, suspendStun: true);
            var pulse = Target(player, 932, 108, 117);
            var outside = Target(player, 933, 108, 132);
            uint life = player.PlayerLife;
            await Swing(fixture, clock, skill, 931);
            Assert.True(primary.Life < primary.MaxLife);
            Assert.Equal(pulse.MaxLife, pulse.Life);
            primary = Target(player, 931, 108, 100);
            await Swing(fixture, clock, skill, 931);
            Assert.True(primary.FlinchUntil > 0);
            Assert.True(pulse.Life < pulse.MaxLife);
            Assert.Equal(outside.MaxLife, outside.Life);
            Assert.Equal(life, player.PlayerLife);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Trauma_support_grants_stacking_buff_scales_hit_and_stun_mitigates_self_hit_and_caps_one_per_use()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = Prepare(fixture, clock);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            // Fixture equips a Gnarled Branch (Staff), which satisfies Trauma Support's Axe/Mace/Sceptre/Staff restriction.
            ushort heavy = Assert.Single(fixture.Equip(("Heavy Strike", 1), ("Trauma Support", 1)));
            var supported = Game.SupportedSkill(player, heavy);
            Assert.Single(supported.Supports);
            var target = Target(player, 941, 108, 100);
            var baseHit = Game.SupportedHitFor(player, heavy, suppress: true);
            var baseStun = Game.OwnerStun(player, target, 100, GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100),
                melee: true, spell: false, critical: false, Game.CharacterPlayerStats(player), supported.Stats);

            // Swing 1: 0 stacks before hit -> gains stack 1, takes 13 physical self-damage (level 1 SupportTrauma).
            uint life = player.PlayerLife;
            await Swing(fixture, clock, heavy, 941);
            Assert.True(target.Life < target.MaxLife);
            Assert.Equal(life - 13, player.PlayerLife);
            var buff1 = player.Area.Buffs.Get(player.Id, 1873, player.Id);
            Assert.NotNull(buff1);
            Assert.Equal((ushort)1, buff1.Stacks);
            Assert.Equal(1, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.CurrentTrauma));

            // Added physical attack damage and +5% stun duration per Trauma scale live at 1 stack.
            var oneStackHit = Game.SupportedHitFor(player, heavy, suppress: true);
            Assert.True(oneStackHit[GameServer.Core.World.DamageType.Physical].Min > baseHit[GameServer.Core.World.DamageType.Physical].Min);
            Assert.True(oneStackHit[GameServer.Core.World.DamageType.Physical].Max > baseHit[GameServer.Core.World.DamageType.Physical].Max);
            var oneStackStun = Game.OwnerStun(player, target, 100, GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100),
                melee: true, spell: false, critical: false, Game.CharacterPlayerStats(player), supported.Stats);
            Assert.Equal(baseStun.DurationPct + 5, oneStackStun.DurationPct);

            // Swing 2: gains stack 2, takes 13 * 2 = 26 physical self-damage; further increases damage and stun duration.
            await Swing(fixture, clock, heavy, 941);
            Assert.Equal(life - 39, player.PlayerLife);
            Assert.Equal((ushort)2, player.Area.Buffs.Get(player.Id, 1873, player.Id)?.Stacks);
            Assert.Equal(2, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.CurrentTrauma));
            var twoStackHit = Game.SupportedHitFor(player, heavy, suppress: true);
            Assert.True(twoStackHit[GameServer.Core.World.DamageType.Physical].Min > oneStackHit[GameServer.Core.World.DamageType.Physical].Min);
            var twoStackStun = Game.OwnerStun(player, target, 100, GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Physical, 100, 100),
                melee: true, spell: false, critical: false, Game.CharacterPlayerStats(player), supported.Stats);
            Assert.Equal(baseStun.DurationPct + 10, twoStackStun.DurationPct);

            // Independent 5s stack lifetimes at level 1 SupportTrauma.
            // Swing 1 contacted ~t=1.55 (expires ~6.55); Swing 2 contacted ~t=3.10 (expires ~8.10).
            Advance(fixture, clock, 7.0);
            Assert.Equal((ushort)1, player.Area.Buffs.Get(player.Id, 1873, player.Id)?.Stacks);
            Assert.Equal(1, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.CurrentTrauma));
            Advance(fixture, clock, 8.5);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));
            Assert.Equal(0, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.CurrentTrauma));

            // Armour mitigates Trauma self-damage, and Vengeance ("when Hit by an Enemy") does not grant Rage on self-hit.
            player.Character.Passives.Add(53840); // Vengeance
            using (GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, player))
                    values[Stat.BasePhysicalDamageReductionRating] = values.GetValueOrDefault(Stat.BasePhysicalDamageReductionRating) + 5000;
                return values;
            }))
            {
                player.Pools = Game.PoolsFor(player, player.Pools.Level) with { Accuracy = 10_000, Mana = 10_000 };
                player.PlayerLife = player.Pools.Life;
                player.PlayerMana = player.Pools.Mana;
                life = player.PlayerLife;
                await Swing(fixture, clock, heavy, 941);
                uint taken = life - player.PlayerLife;
                Assert.True(taken > 0 && taken < 13, $"Expected armour to mitigate 13 physical self-damage, took {taken}.");
                var rageInputs = RageStats.Inputs(player, key => Game.CharacterPlayerStats(player).GetValueOrDefault(key));
                Assert.Equal(0, player.Rage.Read(clock.At, rageInputs).Current);
            }
            player.Pools = Game.PoolsFor(player, player.Pools.Level) with { Life = 10_000, Mana = 10_000, Accuracy = 10_000 };
            player.PlayerLife = 10_000;
            player.PlayerMana = 10_000;
            Advance(fixture, clock, clock.At + 6);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));

            // Multistrike repeats and Double Strike paired hits grant at most 1 Trauma per attack use.
            ushort multiHeavy = Assert.Single(fixture.Equip(("Heavy Strike", 1), ("Trauma Support", 1), ("Multistrike Support", 1)));
            await Swing(fixture, clock, multiHeavy, 941);
            // Swing awaits all 3 Multistrike repeats (whose additive fake-clock waits span >3s) plus +1s; advance briefly so we stay inside the 5s stack lifetime.
            Advance(fixture, clock, clock.At + 0.2);
            Assert.Equal((ushort)1, player.Area.Buffs.Get(player.Id, 1873, player.Id)?.Stacks);
            Assert.Equal(10_000u - 13u, player.PlayerLife);
            Advance(fixture, clock, clock.At + 6);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));

            ushort doubleStrike = Assert.Single(fixture.Equip(("Double Strike", 1), ("Trauma Support", 1)));
            await Swing(fixture, clock, doubleStrike, 941);
            Advance(fixture, clock, clock.At + 1);
            Assert.Equal((ushort)1, player.Area.Buffs.Get(player.Id, 1873, player.Id)?.Stacks);
            Advance(fixture, clock, clock.At + 6);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));

            // Hybrid Strikes and Boneshatter of Carnage also gain Trauma when linked with Trauma Support.
            foreach (string skillName in new[] { "Boneshatter of Carnage", "Molten Strike", "Smite", "Static Strike", "Lightning Strike" })
            {
                ushort skill = Assert.Single(fixture.Equip((skillName, 1), ("Trauma Support", 1)));
                await Swing(fixture, clock, skill, 941);
                Assert.Equal((ushort)1, player.Area.Buffs.Get(player.Id, 1873, player.Id)?.Stacks);
                Advance(fixture, clock, clock.At + 6);
                Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));
            }

            // Weapon restriction: equipping a Sword prevents Trauma Support from linking or granting Trauma.
            var swordBase = Program.Items.Find("Rusted Sword")!;
            var sword = ItemCatalog.Create(swordBase) with
            {
                Sockets = [new(GameServer.Core.Protocol.Instance.SocketColour.White, MakeGem("Heavy Strike", 1)),
                    new(GameServer.Core.Protocol.Instance.SocketColour.White, MakeGem("Trauma Support", 1))],
                Links = [2],
            };
            uint weaponId = Game.ItemsOf(player).Items(PlayerInventories.Weapon1).Single().Id;
            Game.ItemsOf(player).Change(PlayerInventories.Weapon1, weaponId, _ => sword);
            ushort swordHeavy = Assert.Single(Game.GemSkillEffects(player)).Id;
            Assert.Empty(Game.SupportedSkill(player, swordHeavy).Supports);
            await Swing(fixture, clock, swordHeavy, 941);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1873));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static GameServer.Core.Protocol.Instance.Item MakeGem(string name, int level)
    {
        var type = Program.Items.FindGem(name)!.Value;
        return ItemCatalog.Create(type.Type) with
        {
            GemVariant = type.Variant.Index,
            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(level),
        };
    }

    static Player Prepare(TriggeredSummonTests.Fixture fixture, Clock clock)
    {
        var player = fixture.Player;
        player.Use(new(1, "boneshatter-test", 350, 350, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerSent = player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        player.Pools = player.Pools with { Accuracy = 10_000 };
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        return player;
    }

    static SpawnedMonster Target(Player player, uint id, int x, int y, bool suspendStun = false)
    {
        var enemy = new SpawnedMonster("boneshatter target", new(x, y, 0), 0, 0, 100_000)
        {
            ObjectId = id, NoRewards = true, CannotBeStunned = suspendStun, HitAnimations = [new(0, .5)],
        };
        player.Area.Monsters[id] = enemy;
        player.Area.Grid.Set(new(id), (x, y));
        return enemy;
    }

    static async Task Swing(TriggeredSummonTests.Fixture fixture, Clock clock, ushort skill, uint id)
    {
        uint mana = fixture.Player.PlayerMana;
        Assert.True(await Game.CastGemSkill(fixture.Player, skill, (100, 100), (108, 100), id));
        Advance(fixture, clock, clock.At + 1);
        Assert.True(fixture.Player.PlayerMana < mana);
    }

    static void Advance(TriggeredSummonTests.Fixture fixture, Clock clock, double now)
    {
        clock.At = now;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(now));
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;

        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}
