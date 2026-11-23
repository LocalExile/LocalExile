namespace GameServer.Gateway.Tests;

public sealed class FreshMeatTests
{
    [Fact]
    public async Task RagingSpiritLosesWakenedFuryWhenItsNativeDurationEnds()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        fixture.Equip(("Summon Raging Spirit", 1), ("Fresh Meat Support", 1));
        uint id = await fixture.Summon("SummonRagingSpirit", 0);
        var spirit = player.Summons.Fighters[id];
        var attack = spirit.Abilities.First(ability => !ability.Attack.Spell && ability.Attack.Damage.MaxTotal > 0);
        double lifetime = player.Summons.Until[id] - spirit.SummonedAt;
        double ends = spirit.SummonedAt + lifetime * .1;
        var stats = Game.CharacterPlayerStats(player);
        var active = Game.GolemModifiers(player, spirit, stats, ends - .01);
        var expired = Game.GolemModifiers(player, spirit, stats, ends);

        Assert.Equal(ends, spirit.FreshMeatUntil, 6);
        Assert.True(Game.GolemCriticalChance(attack, active) > Game.GolemCriticalChance(attack, expired));
        Assert.Equal(50, active.Effects[Stat.BaseCriticalStrikeMultiplierPlus]
            - expired.Effects[Stat.BaseCriticalStrikeMultiplierPlus]);

        ushort golemSource = fixture.Equip(("Summon Flame Golem", 1), ("Fresh Meat Support", 1)).First();
        uint golemId = await Game.SummonGolem(player, golemSource, Game.GolemData.Find("SummonFlameGolem")!, (100, 100));
        var golem = player.Golems.All[golemId];
        var golemStats = Game.CharacterPlayerStats(player);
        var activeSpell = Game.GolemAbilities(player, golem.Monster.Variety!, golem.Monster.Level, golem, golemStats,
            golem.FreshMeatUntil - .01).First(ability => ability.Attack.Spell);
        var expiredSpell = Game.GolemAbilities(player, golem.Monster.Variety!, golem.Monster.Level, golem, golemStats,
            golem.FreshMeatUntil).First(ability => ability.Attack.Spell && ability.Effect == activeSpell.Effect);
        Assert.Equal(golem.SummonedAt + 10, golem.FreshMeatUntil, 6);
        Assert.Equal(expiredSpell.Attack.Seconds / 1.25, activeSpell.Attack.Seconds, 6);

        ushort zombieSource = fixture.Equip(("Raise Zombie", 1), ("Fresh Meat Support", 1)).First();
        uint restoredId = await Game.SummonFighter(player, zombieSource, Game.SummonData.Find("RaiseZombie")!,
            (100, 100), restoredVariety: "Metadata/Monsters/Zombies/Zombie", life: .5);
        Assert.NotEqual(0u, restoredId);
        Assert.Equal(0, player.Summons.Fighters[restoredId].FreshMeatUntil);
    }
}
