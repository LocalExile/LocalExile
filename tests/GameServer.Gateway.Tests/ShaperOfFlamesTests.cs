using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ShaperOfFlamesTests
{
    static Func<string, int> With(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    static SpawnedMonster At(int x) => new($"at {x}", new(x, 100, 1), 0, 0, 100)
        { Mods = new([], [], new(new Dictionary<string, int>())) };

    // Shaper of Flames: "All Damage can Ignite" puts a physical hit's damage into the ignite's base (90% per second) and
    // lets it ignite at all; "Hits always Ignite" needs no chance roll.
    [Fact]
    public void All_damage_can_ignite_and_hits_always_ignite()
    {
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var shaper = With(("all_damage_can_ignite", 1), ("always_ignite", 1));
        var rows = AilmentPanelStats.Compute(new("spell", Spell: true), physical, new DamageConversion(), shaper);
        Assert.Contains(("minimum_ignite_damage_to_deal_per_minute_from_non_critical_spell_damage", 5400), rows);
        var plain = AilmentPanelStats.Compute(new("spell", Spell: true), physical, new DamageConversion(), With());
        Assert.Contains(("minimum_ignite_damage_to_deal_per_minute_from_non_critical_spell_damage", 0), plain);

        var settings = MonsterIgnites.Settings.From(rows, "spell", shaper);
        Assert.Equal(100, settings.Chance);
        Assert.True(settings.CanIgnite(physical));
        Assert.False(MonsterIgnites.Settings.From(plain, "spell", With()).CanIgnite(physical));

        var conn = new ClientState(new(1, "shaper", 300, 300, 100, 100, null, []), 1, 1);
        var m = At(100);
        conn.Monsters[1] = m;
        Assert.NotNull(Game.IgniteOnHitLocked(conn, 1, m, settings, critical: false, now: .25));
        Assert.Equal(90, m.Ignites.PerSecond(.25), 3);
    }

    // "Enemies Ignited by you have 40% of Physical Damage converted to Fire Damage": only while the monster burns.
    [Fact]
    public void Ignited_enemies_convert_physical_damage_to_fire()
    {
        var conn = new ClientState(new(1, "shaper", 300, 300, 100, 100, null, []), 1, 1) { PlayerLife = 300 };
        var m = At(100);
        conn.Monsters[1] = m;
        var shaper = With(("enemies_ignited_by_you_have_physical_damage_%_converted_to_fire", 40));
        Assert.Null(Game.EnemyAuraConversionLocked(conn, m, (100, 100), .25, shaper));

        Assert.True(m.Ignites.Add(0, 10, 4));
        var conversion = Game.EnemyAuraConversionLocked(conn, m, (100, 100), .25, shaper);
        Assert.NotNull(conversion);
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100).Convert(conversion);
        Assert.Equal((60.0, 40.0), (hit[DamageType.Physical].Min, hit[DamageType.Fire].Min));
        Assert.Null(Game.EnemyAuraConversionLocked(conn, m, (100, 100), .25, With()));
    }
}
