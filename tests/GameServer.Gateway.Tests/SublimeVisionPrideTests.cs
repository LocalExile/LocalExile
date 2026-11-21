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
        var conn = f.Conn;
        f.Equip(("Pride", 1));
        var catalog = Game.ItemsFor(conn);
        var unique = Game.UniquesFor(conn);
        var jewel = Enumerable.Range(0, 128).Select(seed => unique.Create("Sublime Vision", new Random(seed)))
            .OfType<Item>().First(i => i.Mods.Any(mod => catalog.Mods.ByHash(mod.Hash)?.Id == "SublimeVisionPride"));
        conn.Passives = [Game.PassivesFor(conn).JewelNodeForSlot(1)!.Value];
        var items = Game.ItemsOf(conn);
        items.Add(PlayerInventories.PassiveJewels, 1, 0, jewel);
        uint key = Program.Stats.Key(Game.PrideExplosionChance)!.Value;

        Assert.Equal(0, Game.PrideExplosionLifePct(conn, 25, new Roll(0)));
        CharacterSaves.SetAuraActive(conn.Character!, "Pride", true);
        Game.ReconcileOwnAuras(conn);
        Assert.Contains(conn.Auras.Values, a => a.EffectId == "Pride");
        int chance = Game.CharacterPlayerStats(conn).GetValueOrDefault(key);
        Assert.Equal(25, chance);
        Assert.Equal(10, Game.PrideExplosionLifePct(conn, chance, new Roll(24)));
        Assert.Equal(0, Game.PrideExplosionLifePct(conn, chance, new Roll(25)));

        conn.PlayerStats[key] = 100;
        var corpse = new SpawnedMonster("corpse", new(100, 100, 0), 100, 3, 2000) { Hostile = true, Life = 0 };
        var nearby = new SpawnedMonster("nearby", new(105, 100, 0), 100, 3, 10000) { Hostile = true };
        conn.Monsters[900] = corpse;
        conn.Monsters[901] = nearby;
        uint life = nearby.Life;
        Assert.True(await Game.ExplodeOnKill("test", f.Stream, f.Ciphers, conn, 900, corpse, (100, 100)));
        Assert.True(corpse.CorpseConsumed);
        Assert.Equal(life - 200, nearby.Life);

        f.Equip();
        Game.ReconcileOwnAuras(conn, validateSkills: true);
        Assert.Equal(0, Game.PrideExplosionLifePct(conn, chance, new Roll(0)));
    }
}
