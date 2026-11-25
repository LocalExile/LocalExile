using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ShaperOfFlamesTests
{
    static Func<uint, int> With(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => Stat.Of(s.Stat) == name).Sum(s => s.Value);

    static SpawnedMonster At(int x, bool hostile = true) => new($"at {x}", new(x, 100, 1), 0, 0, 100)
        { Hostile = hostile, Mods = new([], [], new(new Dictionary<uint, int>())) };

    // Shaper of Flames: "All Damage can Ignite" puts a physical hit's damage into the ignite's base (90% per second) and
    // lets it ignite at all; "Hits always Ignite" needs no chance roll.
    [Fact]
    public void All_damage_can_ignite_and_hits_always_ignite()
    {
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var shaper = With(("all_damage_can_ignite", 1), ("always_ignite", 1));
        var rows = AilmentPanelStats.Compute(new("spell", Spell: true), physical, new DamageConversion(), shaper);
        Assert.Contains((Stat.MinimumIgniteDamageToDealPerMinuteFromNonCriticalSpellDamage, 5400), rows);
        var plain = AilmentPanelStats.Compute(new("spell", Spell: true), physical, new DamageConversion(), With());
        Assert.Contains((Stat.MinimumIgniteDamageToDealPerMinuteFromNonCriticalSpellDamage, 0), plain);

        var settings = MonsterIgnites.Settings.From(rows, "spell", shaper);
        Assert.Equal(100, settings.Chance);
        Assert.True(settings.CanIgnite(physical));
        Assert.False(MonsterIgnites.Settings.From(plain, "spell", With()).CanIgnite(physical));

        var player = new Player(new(1, "shaper", 300, 300, 100, 100, null, []), 1, 1);
        var m = At(100);
        player.Area.Monsters[1] = m;
        Assert.NotNull(Game.IgniteOnHitLocked(player, 1, m, settings, critical: false, now: .25));
        Assert.Equal(90, m.Ignites.PerSecond(.25), 3);
    }

    [Fact]
    public void Recover_life_only_from_an_accepted_player_ignite_on_an_enemy()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        const uint startingLife = 300;
        player.PlayerLife = startingLife;
        int amount = 45;
        uint blocked = uint.MaxValue;
        void Configure() => Game.ConfigureLeech(player,
            name => name == Stat.RecoverXLifeOnEnemyIgnited ? amount : name == blocked ? 1 : 0, onLowLife: false);

        var settings = new MonsterIgnites.Settings(100, 1, 1, 1, 1, 4);
        SpawnedMonster Enemy() => At(100);
        Configure();
        Assert.NotNull(Game.IgniteOnHitLocked(player, 1, Enemy(), settings, critical: false, now: 1));
        Assert.Equal(startingLife + 45, player.PlayerLife);
        Assert.True(Game.LeechOf(player).InstantLifeChanged);

        Assert.NotNull(Game.IgniteOnHitLocked(player, 2, Enemy(), settings, false, 2, playerIgnite: false));
        Assert.NotNull(Game.IgniteOnHitLocked(player, 3, At(100, hostile: false), settings, false, 3));
        Assert.Null(Game.IgniteOnHitLocked(player, 4, Enemy(), settings with { Chance = 0 }, false, 4));
        Assert.Equal(startingLife + 45, player.PlayerLife);

        foreach (uint prohibition in new[] { Stat.CannotRecoverLife, Stat.KeystoneVaalPact })
        {
            blocked = prohibition;
            Configure();
            Assert.NotNull(Game.IgniteOnHitLocked(player, 5, Enemy(), settings, false, 5));
            Assert.Equal(startingLife + 45, player.PlayerLife);
        }

        blocked = uint.MaxValue;
        amount = 0;
        Configure();
        Assert.NotNull(Game.IgniteOnHitLocked(player, 6, Enemy(), settings, false, 6));
        Assert.Equal(startingLife + 45, player.PlayerLife);

        amount = 45;
        Configure();
        uint cap = player.PlayerMaxLife;
        player.PlayerLife = cap - 3;
        Assert.NotNull(Game.IgniteOnHitLocked(player, 7, Enemy(), settings, false, 7));
        Assert.Equal(cap, player.PlayerLife);
    }

    // "Enemies Ignited by you have 40% of Physical Damage converted to Fire Damage": only while the monster burns.
    [Fact]
    public void Ignited_enemies_convert_physical_damage_to_fire()
    {
        var player = new Player(new(1, "shaper", 300, 300, 100, 100, null, []), 1, 1) { PlayerLife = 300 };
        var m = At(100);
        player.Area.Monsters[1] = m;
        var shaper = With(("enemies_ignited_by_you_have_physical_damage_%_converted_to_fire", 40));
        Assert.Null(Game.EnemyAuraConversionLocked(player, m, (100, 100), .25, shaper));

        Assert.True(m.Ignites.Add(0, 10, 4));
        var conversion = Game.EnemyAuraConversionLocked(player, m, (100, 100), .25, shaper);
        Assert.NotNull(conversion);
        var hit = DamagePacket.Of(DamageType.Physical, 100, 100).Convert(conversion);
        Assert.Equal((60.0, 40.0), (hit[DamageType.Physical].Min, hit[DamageType.Fire].Min));
        Assert.Null(Game.EnemyAuraConversionLocked(player, m, (100, 100), .25, With()));
    }
}
