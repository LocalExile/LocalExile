using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class UnendingHungerTests
{
    sealed class Roll(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => value;
    }

    [Fact]
    public void Hunger_and_Violent_Dead_use_the_displayed_40_intelligence_threshold()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var catalog = Game.ItemsFor(player);
        var unique = Game.UniquesFor(player);
        uint intelligence = Stat.BaseIntelligence;
        foreach (var (name, count) in new[] { ("Unending Hunger", 1), ("Violent Dead", 2) })
        {
            var item = Assert.IsType<Item>(unique.Create(name, new Random(7)));
            var (radius, effects) = ThresholdJewels.Read(item, catalog.Mods);
            Assert.Equal(1200, radius);
            Assert.Equal(count, effects.Count);
            foreach (var effect in effects)
            {
                Assert.Equal(40, effect.Required);
                Assert.False(ThresholdJewels.Met(effect, new Dictionary<uint, int> { [intelligence] = 39 }));
                Assert.True(ThresholdJewels.Met(effect, new Dictionary<uint, int> { [intelligence] = 40 }));
            }
        }
    }

    [Fact]
    public void A_raised_spectre_consumes_nearby_souls_in_its_own_unrefreshable_window()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var tree = Game.PassivesFor(player);
        player.Character.Passives = [tree.JewelNodeForSlot(1)!.Value];
        var items = Game.ItemsOf(player);
        var jewel = Assert.IsType<Item>(Game.UniquesFor(player).Create("Unending Hunger", new Random(7)));
        items.Add(PlayerInventories.PassiveJewels, 1, 0, jewel);
        var playerStats = Game.CharacterPlayerStats(player);
        Assert.Equal(50, playerStats[Game.SpectreSoulEaterChance]);

        var variety = Game.Monsters.Find("Metadata/Monsters/ZombieDismember/ZombieDrowned1")!;
        GolemState.Golem Actor(uint id, int x, string kind)
        {
            var m = new SpawnedMonster(kind, new(x, 100, 0), 100, 3, 1000)
            {
                Hostile = false,
                Variety = variety,
            };
            var definition = new GolemCatalog.Definition(Program.Skills.EffectRow("RaiseSpectre"), "RaiseSpectre",
                kind, variety.Id, 0, [], false, false, false);
            var fighter = new GolemState.Golem(id, m, definition, 1) { Player = new(playerStats) };
            player.Area.Monsters[id] = m;
            player.Summons.Fighters[id] = fighter;
            return fighter;
        }
        var spectre = Actor(301, 100, "Spectre");
        var other = Actor(302, 150, "Spectre");
        var zombie = Actor(303, 100, "Zombie");
        Assert.False(Game.StartSpectreSoulEaterLocked(player, zombie, 1, new Roll(0)));
        Assert.False(Game.StartSpectreSoulEaterLocked(player, spectre, 1, new Roll(50)));
        Assert.True(Game.StartSpectreSoulEaterLocked(player, spectre, 1, new Roll(49)));
        Assert.True(Game.StartSpectreSoulEaterLocked(player, other, 1, new Roll(49)));
        Assert.Equal(0, Game.SpectreSoulEaterCount(spectre, 1));
        Assert.False(Game.StartSpectreSoulEaterLocked(player, spectre, 2, new Roll(0)));

        void Feed(uint id, (int X, int Y) at)
        {
            var victim = new SpawnedMonster("victim", new(at.X, at.Y, 0), 100, 3, 1000)
            { Hostile = true, Dead = true, Life = 0, Variety = variety };
            player.Area.Monsters[id] = victim;
            Game.CommitAcceptedKillLocked(player, id, victim, 2, playerKill: true);
        }
        Feed(990, (20, 100));
        Assert.Equal(0, Game.SpectreSoulEaterCount(spectre, 2));
        for (uint i = 0; i < 48; i++) Feed(1000 + i, (101, 100));
        Assert.Equal(45, Game.SpectreSoulEaterCount(spectre, 2));
        Assert.Equal(0, Game.SpectreSoulEaterCount(other, 2));
        Assert.Equal(0, player.SoulEater.Stacks(2));
        Assert.Equal(225, Game.GolemModifiers(player, spectre, playerStats, 2).Effects[Stat.AttackAndCastSpeedPlusPct]);
        Assert.Equal(44, Game.SpectreSoulEaterCount(spectre, 6.5));
        Assert.Equal(0, Game.SpectreSoulEaterCount(spectre, 21));
        Assert.True(Game.StartSpectreSoulEaterLocked(player, spectre, 21, new Roll(49)));
        Game.GolemDieLocked(player, spectre, 22);
        Assert.Null(spectre.SoulEater);
    }
}
