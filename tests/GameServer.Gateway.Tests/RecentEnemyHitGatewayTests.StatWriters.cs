using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    static void PrepareFullStatWriter(Fixture f, string writer)
    {
        if (writer == "mageblood")
        {
            var items = PlayerInventories.For(f.Conn.Character!, Program.Items);
            items.Add(PlayerInventories.Belt, 0, 0, Program.Uniques.Create("Mageblood", new Random(1))!);
            items.Add(PlayerInventories.Flask, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Granite Flask")!) with { Rarity = ItemRarity.Magic });
        }
        f.Set("no_inherent_rage_loss", 1);
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        f.Conn.PlayerLife = f.Conn.Pools.Life;
        _ = f.Chance; // Warm catalogs before arranging the four-second window.
        if (writer == "rage")
            Assert.True(f.Conn.Rage.Gain(3, f.Conn.Clock.Elapsed.TotalSeconds, new(NoInherentLoss: true)));
        if (writer == "mageblood") Assert.True(f.Conn.Pools.Armour >= 1500);
    }

    static Task SendFullStatWriter(Fixture f, string writer)
    {
        if (writer == "rage") return Program.SendRage("recent-writer-test", f.Stream, f.Ciphers, f.Conn);
        if (writer == "aura") f.Conn.AuraStatsDirty = true;
        string method = writer == "aura" ? "SendOwnAuraChanges" : "RefreshMagebloodFlasks";
        object?[] args = writer == "aura"
            ? ["recent-writer-test", f.Stream, f.Ciphers, f.Conn, false]
            : ["recent-writer-test", f.Stream, f.Ciphers, f.Conn];
        return (Task)Method(method).Invoke(null, args)!;
    }

    static void AssertFullRecentPacket(Fixture f, byte[] bytes, int count, int chance)
    {
        var stats = (List<(uint, int)>)Method("PlayerStatsWithCleared").Invoke(null, [f.Conn])!;
        Assert.Contains((4534u, count), stats);
        Assert.Contains((12268u, chance), stats);
        // Match the actual full player Stats packet, not the Actor's separate embedded stat rows.
        int at = bytes.AsSpan().IndexOf(InstanceEntities.StatsUpdate(Program.PlayerId, [])[..12]);
        Assert.True(at >= 0, "Expected a full player Stats packet from the real writer.");
        Assert.True(bytes.AsSpan(at).StartsWith(InstanceEntities.StatsUpdate(Program.PlayerId, stats)),
            "The full player snapshot encoded stale recent-hit values.");
    }

    [Fact]
    public async Task Unchanged_Rage_build_cannot_acknowledge_recent_values_without_sending_player_stats()
    {
        using var f = new Fixture();
        PrepareFullStatWriter(f, "rage");
        await SendFullStatWriter(f, "rage"); await f.Read();
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        await f.Present(); await f.Read();
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        await SendFullStatWriter(f, "rage");
        Assert.Empty(await f.Read());
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        await f.Present();
        Assert.Equal(InstanceEntities.StatsUpdate(Program.PlayerId, [(4534u, 2), (12268u, 20)]), await f.Read());
        Assert.Equal((2, 20), f.Conn.PresentedRecentEnemyHits);
    }
}
