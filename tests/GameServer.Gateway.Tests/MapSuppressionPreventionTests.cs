using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MapSuppressionPreventionTests
{
    [Fact]
    public void Map_reduces_the_amount_prevented_by_a_successful_spell_suppression()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        uint chance = Program.Stats.Key("base_spell_suppression_chance_%")!.Value;
        conn.PlayerStats[chance] = 100;
        Game.SyntheticEnemyHit Hit(ClientState player, bool spell)
        {
            player.Pools = Game.BuildPoolsFor(player, 90);
            Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 100, block: false, spell, suppress: spell, out var result));
            return result;
        }

        int outside = Hit(conn, spell: true).Damage;
        var area = new MapDevice.MapArea(conn.Layout.Hash, "MapWorldsStrand", "Test map", 16);
        var mods = new MapMods(new Dictionary<string, int> { ["map_players_spell_damage_%_suppressed"] = -20 });
        conn.Instance!.OpenedWith(new MapDevice.Run(conn.Character!, area, mods));
        var inside = Hit(conn, spell: true);
        Assert.True(inside.Suppressed);
        Assert.Equal(100, inside.Suppression.Chance);
        Assert.Equal(outside + 20, inside.Damage);
        Assert.Equal(100, Hit(conn, spell: false).Damage);
        var returned = new ClientState(conn.Layout, 2, 1)
        {
            Instance = new(), Character = conn.Character, Pools = conn.Pools, LeagueModule = conn.LeagueModule,
            PlayerLife = conn.PlayerLife, PlayerMana = conn.PlayerMana
        };
        returned.Clock.Start();
        returned.PlayerStats[chance] = 100;
        Assert.Equal(outside, Hit(returned, spell: true).Damage);
    }
}
