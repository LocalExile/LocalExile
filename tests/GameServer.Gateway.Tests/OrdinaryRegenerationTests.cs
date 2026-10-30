using System.Reflection;

namespace GameServer.Gateway.Tests;

public sealed class OrdinaryRegenerationTests : IDisposable
{
    readonly string name = "Regen" + Guid.NewGuid().ToString("N");
    static uint Key(string stat) => Program.Stats.Key(stat)!.Value;
    static Func<string, int> Inputs(params (string Stat, int Value)[] values)
    {
        var stats = values.ToDictionary(s => s.Stat, s => s.Value);
        return n => stats.GetValueOrDefault(n);
    }

    ClientState Character()
    {
        TestSaves.Ensure();
        var conn = new ClientState(new(0, "OrdinaryRegen", 128, 128, 32, 32, null, []), 0, 1)
            { Character = name, ClassRow = SkillCatalog.WitchRow, PlayerLife = 100 };
        conn.Clock.Stop();
        conn.Pools = Program.PoolsFor(conn, 20);
        return conn;
    }

    static void Set(ClientState conn, string stat, int value) => conn.PlayerStats[Key(stat)] = value;
    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!).ToDictionary(s => s.Item1, s => s.Item2);

    [Fact]
    public void Life_regeneration_and_recovery_increases_multiply_the_flat_plus_percentage_source()
    {
        var get = Inputs(("base_life_regeneration_rate_per_minute", 120),
            ("life_regeneration_rate_per_minute_%", 60), ("life_regeneration_rate_+%", 50),
            ("life_recovery_rate_+%", 20));
        // 2 flat/sec + 1% of 1000/sec = 12; 50% regeneration and 20% recovery give 21.6/sec.
        Assert.Equal(1296, PanelStats.LifePerMinute(get, 1000), 9);
        var panel = PanelStats.Recovery(get, 1000, 500).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(1296, panel["total_life_recovery_per_minute_from_regeneration"]);
        Assert.Equal(1296, panel["life_recovery_per_minute"]);
    }

    [Fact]
    public void Mana_regeneration_scales_native_base_percentage_and_flat_sources_before_recovery()
    {
        var get = Inputs(("base_mana_regeneration_rate_per_minute", 1200),
            ("mana_regeneration_rate_per_minute_%", 20), ("mana_regeneration_rate_+%", 40),
            ("mana_recovery_rate_+%", 20));
        // (105+20)% of 500 + 1200 = 1825/min; *1.4 *1.2 = 3066/min.
        Assert.Equal(3066, PanelStats.ManaPerMinute(get, 500), 9);
        var panel = PanelStats.Recovery(get, 1000, 500).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(3066, panel["total_mana_recovery_per_minute_from_regeneration"]);
        Assert.Equal(3066, panel["mana_recovery_per_minute"]);
    }

    [Theory]
    [InlineData("no_life_regeneration", -1)]
    [InlineData("no_life_regeneration", 1)]
    [InlineData("cannot_recover_life", -1)]
    [InlineData("cannot_recover_life", 1)]
    public void Life_prohibitions_stop_only_life_regeneration(string stat, int value)
    {
        var get = Inputs(("base_life_regeneration_rate_per_minute", 600), (stat, value));
        Assert.Equal(0, PanelStats.LifePerMinute(get, 1000));
        Assert.Equal(525, PanelStats.ManaPerMinute(get, 500));
    }

    [Theory]
    [InlineData("no_mana_regeneration", -1)]
    [InlineData("no_mana_regeneration", 1)]
    [InlineData("cannot_leech_or_regenerate_mana", -1)]
    [InlineData("cannot_leech_or_regenerate_mana", 1)]
    public void Mana_prohibitions_stop_only_mana_regeneration(string stat, int value)
    {
        var get = Inputs(("base_life_regeneration_rate_per_minute", 600), (stat, value));
        Assert.Equal(600, PanelStats.LifePerMinute(get, 1000));
        Assert.Equal(0, PanelStats.ManaPerMinute(get, 500));
    }

    [Theory]
    [InlineData("life_regeneration_rate_+%", true)]
    [InlineData("life_recovery_rate_+%", true)]
    [InlineData("mana_regeneration_rate_+%", false)]
    [InlineData("mana_recovery_rate_+%", false)]
    public void Reductions_beyond_one_hundred_percent_cannot_turn_regeneration_into_damage(string stat, bool life)
    {
        var get = Inputs(("base_life_regeneration_rate_per_minute", 600), (stat, -150));
        Assert.Equal(0, life ? PanelStats.LifePerMinute(get, 1000) : PanelStats.ManaPerMinute(get, 500));
    }

    [Fact]
    public void Native_integer_publication_saturates_extreme_rates_instead_of_wrapping_negative()
    {
        var get = Inputs(("base_life_regeneration_rate_per_minute", int.MaxValue),
            ("base_mana_regeneration_rate_per_minute", int.MaxValue),
            ("life_regeneration_rate_+%", int.MaxValue), ("mana_regeneration_rate_+%", int.MaxValue),
            ("life_recovery_rate_+%", int.MaxValue), ("mana_recovery_rate_+%", int.MaxValue));
        foreach (var (_, value) in PanelStats.Recovery(get, uint.MaxValue, uint.MaxValue))
            Assert.Equal(int.MaxValue, value);
    }

    [Fact]
    public void Actor_publication_and_resource_writer_share_modifiers_and_clear_removed_sources()
    {
        var conn = Character();
        Set(conn, "base_life_regeneration_rate_per_minute", 600);
        Set(conn, "life_regeneration_rate_+%", 50);
        Set(conn, "life_recovery_rate_+%", 20);
        Set(conn, "mana_recovery_rate_+%", 20);
        var stats = Published(conn);
        Assert.Equal(1080, stats[Key("total_life_recovery_per_minute_from_regeneration")]);
        Assert.Equal(1080, stats[Key("life_recovery_per_minute")]);
        Assert.Equal(18d, (double)typeof(Program).GetMethod("LifeRegenPerSecond", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [conn])!);
        Assert.Equal(1.2 * 1.05 * conn.Pools.Mana, stats[Key("total_mana_recovery_per_minute_from_regeneration")], 0);

        Set(conn, "no_life_regeneration", 1);
        Set(conn, "no_mana_regeneration", 1);
        stats = Published(conn);
        Assert.Equal(0, stats[Key("total_life_recovery_per_minute_from_regeneration")]);
        Assert.Equal(0, stats[Key("life_recovery_per_minute")]);
        Assert.Equal(0, stats[Key("total_mana_recovery_per_minute_from_regeneration")]);
        Assert.Equal(0, stats[Key("mana_recovery_per_minute")]);

        conn.PlayerStats.Clear();
        stats = Published(conn);
        Assert.Equal(0, stats.GetValueOrDefault(Key("total_life_recovery_per_minute_from_regeneration")));
        Assert.Equal((int)Math.Round(1.05 * conn.Pools.Mana), stats[Key("total_mana_recovery_per_minute_from_regeneration")]);
    }

    [Theory]
    [InlineData(false, 1.8)]
    [InlineData(true, 0)]
    public void Fortification_regeneration_matches_the_panel_multiplier_and_prohibition(bool prohibited, double multiplier)
    {
        var conn = Character();
        Set(conn, FortificationState.LifeRegenPerStack, 12);
        Set(conn, "life_regeneration_rate_+%", 50);
        Set(conn, "life_recovery_rate_+%", 20);
        Set(conn, "no_life_regeneration", prohibited ? 1 : 0);
        double now = conn.Clock.Elapsed.TotalSeconds;
        Program.FortificationOf(conn).State.GrantExplicitStacks(now, 20);

        var stats = Program.CharacterPlayerStats(conn);

        Assert.Equal((int)Math.Round(conn.Pools.Life * 2.4 * multiplier),
            stats.GetValueOrDefault(Key("total_life_recovery_per_minute_from_regeneration")));
        Assert.Equal(conn.Pools.Life * .04 * multiplier, Program.FortificationOf(conn).TakeRecovery(now + 1), 9);
    }

    [Fact]
    public void Fortification_settles_old_multiplier_then_integrates_new_rate_only_until_expiry()
    {
        var session = new FortificationSession();
        session.Observe(0, n => n == FortificationState.LifeRegenPerStack ? 12 : 0);
        session.ConfigureRecovery(0, 1000, false, 1.8);
        session.State.GrantExplicitStacks(0, 20);
        session.ConfigureRecovery(1, 1000, false, 0);
        Assert.Equal(72, session.TakeRecovery(2), 9);
        session.ConfigureRecovery(2, 1000, false, 1.8);
        session.Read(8);
        Assert.Equal(288, session.TakeRecovery(8), 9); // 4 remaining seconds at 72 Life/sec; expires at 6.
        Assert.Equal(0, session.TakeRecovery(9));
    }

    [Theory]
    [InlineData(-600, 12, 54)]
    [InlineData(600, -2, 6)]
    public void Signed_developer_sources_keep_the_previous_linear_source_contract_with_Fortification(
        int flat, int perStack, double perSecond)
    {
        var baseline = Inputs(("base_life_regeneration_rate_per_minute", flat),
            ("life_regeneration_rate_+%", 50), ("life_recovery_rate_+%", 20));
        var combined = Inputs(("base_life_regeneration_rate_per_minute", flat),
            ("life_regeneration_rate_per_minute_%", 20 * perStack),
            ("life_regeneration_rate_+%", 50), ("life_recovery_rate_+%", 20));
        var session = new FortificationSession();
        session.Observe(0, n => n == FortificationState.LifeRegenPerStack ? perStack : 0);
        session.ConfigureRecovery(0, 1000, false, PanelStats.LifeRegenerationMultiplier(baseline));
        session.State.GrantExplicitStacks(0, 20);

        Assert.Equal(perSecond, PanelStats.LifePerMinute(combined, 1000) / 60, 9);
        Assert.Equal(perSecond, PanelStats.LifePerMinute(baseline, 1000) / 60 + session.TakeRecovery(1), 9);
    }

    public void Dispose() => PlayerInventories.Forget(name);
}
