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
        // The equipment and skill seams reconcile the sources; a kill never does (CollectVaalSoulOnKill).
        Game.RefreshVaalSouls(conn);
        Assert.NotNull(Game.VaalSoulPresentation(conn));    // the capacity seed and the current count
        Assert.Null(Game.VaalSoulPresentation(conn));       // nothing changed: no packet and no source walk
        var monster = new SpawnedMonster("corpse", new(0, 0, 1), 0, 0, 0)
        {
            Hostile = true, Dead = true, Life = 0,
            Variety = new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie"), Level = 70,
        };
        conn.Area.Monsters[0x800abcd] = monster;
        Assert.True(Game.CollectVaalSoulOnKill(conn, 0x800abcd, monster));
        Assert.Equal(1u, Assert.Single(conn.VaalSouls.Read()).Current);
        Assert.NotNull(Game.VaalSoulPresentation(conn));    // the one soul that moved
        Assert.Null(Game.VaalSoulPresentation(conn));
    }
}
