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
        Game.SyntheticEnemyHit Hit(bool spell)
        {
            conn.Pools = Game.BuildPoolsFor(conn, 90);
            Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 100, block: false, spell, suppress: spell, out var result));
            return result;
        }

        int outside = Hit(spell: true).Damage;
        var area = new MapDevice.MapArea(conn.Layout.Hash, "MapWorldsStrand", "Test map", 16);
        var mods = new MapMods(new Dictionary<string, int> { ["map_players_spell_damage_%_suppressed"] = -20 });
        conn.Instance!.OpenedWith(new MapDevice.Run(conn.Character!, area, mods));
        var inside = Hit(spell: true);
        Assert.True(inside.Suppressed);
        Assert.Equal(100, inside.Suppression.Chance);
        Assert.Equal(outside + 20, inside.Damage);
        Assert.Equal(100, Hit(spell: false).Damage);
        conn.Instance = new();
        Assert.Equal(outside, Hit(spell: true).Damage);
    }
}
