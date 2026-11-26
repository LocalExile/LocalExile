using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class ShockwaveSupportTests
{
    [Theory]
    [InlineData("Limbsplit", 1)]
    [InlineData("The Cauteriser", 5)]
    public async Task Gore_Shockwave_requires_150_Strength_an_accepted_melee_hit_and_a_ready_cooldown(
        string itemName, int level)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        var inventory = Game.ItemsOf(player);
        var old = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        var axe = new UniqueCatalog(Program.Items).Create(itemName, new Random(429));
        Assert.NotNull(axe);
        inventory.Change(PlayerInventories.Weapon1, old.Id, _ => axe);
        fixture.Equip(("Heavy Strike", 1));
        var strike = Game.GemSkillEffects(player).Single(g => g.Name == "Heavy Strike");
        var gore = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "GoreShockwave");
        Assert.Equal(level, gore.Level);
        Assert.NotNull(Game.GemSkillInstanceDisabled(player, gore.Effect, gore.Id));
        Assert.Equal(1000, Game.CooldownForSkill(player, gore.Id)?.BaseMilliseconds);
        using var accuracy = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (ReferenceEquals(owner, player))
            {
                stats[Stat.GlobalAlwaysHit] = 1;
                stats[Stat.AttackSpeedPlusPct] = 1000;
                stats[Stat.CannotKnockback] = 1;
            }
            return stats;
        });

        var primary = new SpawnedMonster("first melee contact", new(110, 100, 0), 0, 0, 1_000_000);
        var nearby = new SpawnedMonster("gore wave witness", new(129, 100, 0), 0, 0, 1_000_000);
        player.Area.Monsters.Add(901, primary);
        player.Area.Monsters.Add(902, nearby);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            void Strength(int value)
            {
                ushort strength = Game.ItemsFor(player).Mods.ById("StrengthUniqueTwoHandAxe3")!.Hash;
                int bonus = value - Game.SkillsFor(player).ClassFor(player.Character.ClassRow).Strength;
                inventory.Change(PlayerInventories.Weapon1, old.Id, item => item with
                {
                    Mods = item.Mods.Select(m => m.Hash == strength ? m with { Values = [bonus] } : m).ToArray(),
                });
                Game.RecomputePoolsAndSpeed(player);
                Game.MarkPlayerStats(player, PlayerStatStep.ClassAttributes);
                Assert.Equal(value, player.Pools.Strength);
            }

            async Task Strike(bool trigger, bool invulnerable = false)
            {
                primary.EncounterStats = invulnerable ? new Dictionary<uint, int> { [Stat.BaseCannotBeDamaged] = 1 } : null;
                uint mainLife = primary.Life, waveLife = nearby.Life;
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, strike.Id, (100, 100), (110, 100)));
                clock.At += .15;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.Equal(!invulnerable, primary.Life < mainLife);
                Assert.Equal(trigger, nearby.Life < waveLife);
            }

            Strength(149);
            await Strike(false);
            Assert.Null(Game.SingleUseState(player, gore.Effect, gore.Id));
            Strength(150);
            await Strike(false, invulnerable: true);
            Assert.Null(Game.SingleUseState(player, gore.Effect, gore.Id));
            await Strike(true);
            Assert.Equal(1, Game.SingleUseState(player, gore.Effect, gore.Id)?.SpentUses);
            await Strike(false);
            clock.At += 1;
            await Strike(true);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Ground_Slam_spends_stored_Shockwaves_on_separate_melee_contacts()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Walk.Teleport(100, 100, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Equip(("Ground Slam", 1), ("Shockwave Support", 20));
        var slam = Game.GemSkillEffects(player).Single(g => g.Name == "Ground Slam");
        var shockwave = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "SupportBluntWeaponShockwave");
        Assert.NotNull(Game.GemSkillInstanceDisabled(player, shockwave.Effect, shockwave.Id));
        Assert.Equal(4, Game.CooldownForSkill(player, shockwave.Id)?.Uses);
        using var accuracy = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (ReferenceEquals(owner, player)) stats[Stat.GlobalAlwaysHit] = 1;
            return stats;
        });
        for (uint i = 0; i < 5; i++)
            player.Area.Monsters.Add(901 + i, new("Shockwave witness", new(109 + (int)i, 100, 0), 0, 0, 1_000_000));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Assert.True(await Game.CastGemSkill(player, slam.Id, (100, 100), (80, 100)));
            clock.At += 1;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.Null(Game.SingleUseState(player, shockwave.Effect, shockwave.Id));

            player.NextAttackAt = 0;
            Assert.True(await Game.CastGemSkill(player, slam.Id, (100, 100), (120, 100)));
            clock.At += 1;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.Equal(4, Game.SingleUseState(player, shockwave.Effect, shockwave.Id)?.SpentUses);
            Assert.All(player.Area.Monsters.Values, monster => Assert.True(monster.Life < monster.MaxLife));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }
}
