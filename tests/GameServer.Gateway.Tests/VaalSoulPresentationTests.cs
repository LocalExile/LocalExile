namespace GameServer.Gateway.Tests;

public sealed class VaalSoulPresentationTests
{
    [Fact]
    public void A_kill_gains_one_soul_and_the_client_hears_about_it_once()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Player;
        // 1000 more of each attribute so CurrentGemRequirements meets level-20 Vaal Summon Skeletons' Intelligence requirement.
        conn.God = true;
        f.Equip(("Vaal Summon Skeletons", 20));
        // The equipment and skill seams reconcile the sources before the first kill.
        Game.RefreshVaalSouls(conn);
        Assert.NotNull(Game.VaalSoulPresentation(conn));    // the capacity seed and the current count
        Assert.Null(Game.VaalSoulPresentation(conn));       // nothing changed: no packet and no source walk
        var monster = new SpawnedMonster("corpse", new(0, 0, 1), 0, 0, 0)
        {
            Hostile = true, Dead = true, Life = 0,
            Variety = new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie"), Level = 70,
        };
        conn.Area.Monsters[0x800abcd] = monster;
        int before = conn.VaalSouls.Revision;
        Game.CollectVaalSoulAtDeathLocked(conn, monster);
        Assert.True(monster.SoulKillProcessed);
        Assert.Equal(1u, Assert.Single(conn.VaalSouls.Read()).Current);
        Assert.Equal(before + 1, conn.VaalSouls.Revision);
        Assert.NotNull(Game.VaalSoulPresentation(conn));    // the one soul that moved
        Assert.Null(Game.VaalSoulPresentation(conn));
    }

    [Fact]
    public void Vaal_kills_reuse_sources_until_equipment_changes_and_skip_unchargeable_budgets()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        player.God = true;
        f.Equip(("Vaal Summon Skeletons", 1), ("Vaal Fireball", 1));
        var variety = new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie");
        const uint id = 0x800abcd;
        void Kill()
        {
            var monster = new SpawnedMonster("corpse", new(0, 0, 1), 0, 0, 0)
            {
                ObjectId = id, Hostile = true, Dead = true, Life = 0, Variety = variety, Level = 70,
            };
            player.Area.Monsters[id] = monster;
            Game.CollectVaalSoulAtDeathLocked(player, monster);
            Assert.True(monster.SoulKillProcessed);
        }

        Kill();
        var items = Game.ItemsOf(player);
        long changes = items.Changes;
        Assert.NotEmpty(items.ChangeSocketedGems((_, gem) => gem with { GemExperience = gem.GemExperience + 1 }));
        Assert.Equal(changes, items.Changes);
        int before = player.VaalSouls.Revision;
        Kill();
        Assert.Equal(before + 1, player.VaalSouls.Revision);
        Assert.Collection(player.VaalSouls.Read(), b => Assert.Equal(1u, b.Current), b => Assert.Equal(1u, b.Current));

        player.VaalPreventionUntil = double.PositiveInfinity;
        before = player.VaalSouls.Revision;
        Kill();
        Assert.Equal(before, player.VaalSouls.Revision);
        player.VaalPreventionUntil = 0;
        while (player.VaalSouls.Gain()) { }
        before = player.VaalSouls.Revision;
        Kill();
        Assert.Equal(before, player.VaalSouls.Revision);

        f.Equip(("Vaal Fireball", 1));
        Kill();
        var remaining = Assert.Single(player.VaalSouls.Read());
        Assert.Equal(remaining.Capacity, remaining.Current);
        f.Equip(("Vaal Fireball", 1), ("Vaal Summon Skeletons", 1));
        Kill();
        Assert.Contains(player.VaalSouls.Read(), b => b.ActiveSkillsRow != remaining.ActiveSkillsRow && b.Current == 1);
    }
}
