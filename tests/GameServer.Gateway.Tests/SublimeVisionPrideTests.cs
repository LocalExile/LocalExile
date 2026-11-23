using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class SublimeVisionPrideTests
{
    sealed class Roll(int result) : Random
    {
        public override int Next(int minValue, int maxValue) => result;
    }

    [Fact]
    public async Task Sublime_Pride_rolls_for_own_active_aura_and_runs_the_physical_kill_explosion()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        f.Equip(("Pride", 1));
        var catalog = Game.ItemsFor(player);
        var unique = Game.UniquesFor(player);
        var jewel = Enumerable.Range(0, 128).Select(seed => unique.Create("Sublime Vision", new Random(seed)))
            .OfType<Item>().First(i => i.Mods.Any(mod => catalog.Mods.ByHash(mod.Hash)?.Id == "SublimeVisionPride"));
        player.Character.Passives = [Game.PassivesFor(player).JewelNodeForSlot(1)!.Value];
        var items = Game.ItemsOf(player);
        var socketed = items.Add(PlayerInventories.PassiveJewels, 1, 0, jewel);
        uint key = Stat.ExplodeEnemiesFor10PctLifeAsPhysicalOnKillChancePctWhileUsingPride;

        Assert.Equal(0, Game.PrideExplosionLifePct(player, 25, new Roll(0)));
        CharacterSaves.SetAuraActive(player.Character.Name!, "Pride", true);
        Game.ReconcileOwnAuras(player);
        Assert.Contains(player.Auras.Values, a => a.EffectId == "Pride");
        int chance = Game.CharacterPlayerStats(player).GetValueOrDefault(key);
        Assert.Equal(25, chance);
        Assert.Equal(10, Game.PrideExplosionLifePct(player, chance, new Roll(24)));
        Assert.Equal(0, Game.PrideExplosionLifePct(player, chance, new Roll(25)));

        var pride = Assert.Single(jewel.Mods, mod => catalog.Mods.ByHash(mod.Hash)?.Id == "SublimeVisionPride");
        items.Change(PlayerInventories.PassiveJewels, socketed.Id, item => item with
        { Mods = item.Mods.Select(mod => mod == pride ? new ItemMod(mod.Section, mod.Hash, [100]) : mod).ToArray() });
        var corpse = new SpawnedMonster("corpse", new(100, 100, 0), 100, 3, 2000) { Hostile = true, Life = 0 };
        var nearby = new SpawnedMonster("nearby", new(105, 100, 0), 100, 3, 10000) { Hostile = true };
        player.Area.Monsters[900] = corpse;
        player.Area.Monsters[901] = nearby;
        uint life = nearby.Life;
        Assert.True(await Game.ExplodeOnKill(player, 900, corpse, (100, 100)));
        Assert.True(corpse.CorpseConsumed);
        Assert.Equal(life - 200, nearby.Life);

        f.Equip();
        Game.ReconcileOwnAuras(player, validateSkills: true);
        Assert.Equal(0, Game.PrideExplosionLifePct(player, chance, new Roll(0)));
    }
}
