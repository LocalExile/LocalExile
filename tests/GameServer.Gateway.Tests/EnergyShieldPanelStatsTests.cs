using System.Reflection;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public sealed class EnergyShieldPanelStatsTests : IDisposable
{
    const string RegenerationDisplay = "total_energy_shield_recovery_per_minute_from_regeneration";
    const string RegenerationPercent = "base_energy_shield_regeneration_rate_per_minute_%";
    const string Prediction = "energy_shield_recovery_per_minute";
    const string RechargeDisplay = "energy_shield_recharge_rate_per_minute";
    readonly string _name = "EsPanel" + Guid.NewGuid().ToString("N");

    public EnergyShieldPanelStatsTests() => TestSaves.Ensure();

    ClientState Character(uint maximum = 185) =>
        new(new(0, "EnergyShieldPanel", 128, 128, 32, 32, null, []), 0, 1)
        {
            Character = _name,
            ClassRow = SkillCatalog.WitchRow,
            Pools = new(20, 500, 200, 100, 0, EnergyShield: maximum),
        };

    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    static void Set(ClientState conn, string name, int value) => conn.PlayerStats[Key(name)] = value;
    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!)
        .ToDictionary(s => s.Item1, s => s.Item2);

    [Fact]
    public void Native_ESRegen_row_reads_the_published_regeneration_total()
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes("data/tables/CharacterPanelStats.json"));
        var row = table.RootElement.EnumerateArray().Single(r => r.GetProperty("Id").GetString() == "ESRegen");
        Assert.Equal("Energy Shield Regeneration per Second", row.GetProperty("Text").GetString());
        Assert.Equal(1058u, Key(RegenerationDisplay));
        Assert.Equal(new[] { (int)Key(RegenerationDisplay) - 1 },
            row.GetProperty("StatsKeys1").EnumerateArray().Select(k => k.GetInt32()));
        // Native formatting modifier 2 converts the transmitted per-minute value to the per-second row.
        Assert.Contains(2, row.GetProperty("__col8").EnumerateArray().Select(k => k.GetInt32()));

        var conn = Character();
        Assert.Equal(0, Program.CharacterPlayerStats(conn)[Key(RegenerationDisplay)]);
    }

    [Fact]
    public void Published_total_combines_flat_percentage_regeneration_and_recovery_modifiers()
    {
        var conn = Character(1000);
        Set(conn, "base_energy_shield_regeneration_rate_per_minute", 90);
        Set(conn, RegenerationPercent, 60);
        Set(conn, "energy_shield_regeneration_rate_+%", 50);
        Set(conn, "energy_shield_recovery_rate_+%", 20);

        // (90 flat + 1% of 1000 ES each second) * 1.5 regeneration * 1.2 recovery = 1242 per minute.
        var stats = Program.CharacterPlayerStats(conn);
        Assert.Equal(1242, stats[Key(RegenerationDisplay)]);
        Assert.Equal(1242, stats[Key(Prediction)]);
        Assert.Equal(24000, stats[Key(RechargeDisplay)]);
        Assert.Equal(1242, Published(conn)[Key(RegenerationDisplay)]);
    }

    [Fact]
    public void Removing_the_real_energy_shield_mastery_publishes_zero_in_the_next_full_update()
    {
        var conn = Character();
        conn.Passives.Add(18240); // Energy Shield Mastery, native group 25.
        conn.MasteryEffects[18240] = 35810; // EnergyShield4: regenerate 2% of ES per second.
        Assert.True(Program.Passives.IsEffectFor(18240, Program.Passives.FindEffect(35810)!));
        Assert.Equal(222, Program.CharacterPlayerStats(conn)[Key(RegenerationDisplay)]);
        Assert.Equal(222, Published(conn)[Key(RegenerationDisplay)]);

        conn.Passives.Clear();
        conn.MasteryEffects.Clear();
        Assert.Equal(0, Program.CharacterPlayerStats(conn)[Key(RegenerationDisplay)]);
        Assert.Equal(0, Published(conn)[Key(RegenerationDisplay)]);
        Assert.Contains(Key(RegenerationDisplay), conn.SentStatKeys);
    }

    [Fact]
    public void Percentage_regeneration_uses_each_refreshed_maximum_energy_shield()
    {
        var conn = Character();
        Set(conn, RegenerationPercent, 120);
        Assert.Equal(222, Published(conn)[Key(RegenerationDisplay)]);

        conn.Pools = conn.Pools with { EnergyShield = 370 };
        Assert.Equal(444, Published(conn)[Key(RegenerationDisplay)]);
        conn.Pools = conn.Pools with { EnergyShield = 0 };
        Assert.Equal(0, Published(conn)[Key(RegenerationDisplay)]);
    }

    [Theory]
    [InlineData("no_energy_shield_recharge_or_regeneration", 0)]
    [InlineData("cannot_recharge_energy_shield", 222)]
    public void The_existing_recovery_prohibitions_keep_their_distinct_scopes(string prohibition, int expected)
    {
        var conn = Character();
        Set(conn, RegenerationPercent, 120);
        Assert.Equal(222, Published(conn)[Key(RegenerationDisplay)]);
        Set(conn, prohibition, 1);
        Assert.Equal(expected, Program.CharacterPlayerStats(conn)[Key(RegenerationDisplay)]);
        Assert.Equal(expected, Published(conn)[Key(RegenerationDisplay)]);
        Set(conn, prohibition, 0);
        Assert.Equal(222, Published(conn)[Key(RegenerationDisplay)]);
    }

    [Theory]
    [InlineData("base_energy_shield_regeneration_rate_per_minute", -60)]
    [InlineData("energy_shield_regeneration_rate_+%", -100)]
    [InlineData("energy_shield_recovery_rate_+%", -100)]
    public void Nonpositive_regeneration_inputs_publish_zero(string input, int value)
    {
        var conn = Character();
        Set(conn, "base_energy_shield_regeneration_rate_per_minute", 60);
        Set(conn, input, value);
        Assert.Equal(0, Program.CharacterPlayerStats(conn)[Key(RegenerationDisplay)]);
        Assert.Equal(0, Published(conn)[Key(RegenerationDisplay)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(777)]
    public void An_explicit_display_override_wins_until_removed_without_changing_recovery(int value)
    {
        var conn = Character();
        Set(conn, RegenerationPercent, 120);
        Set(conn, RegenerationDisplay, value);
        var stats = Program.CharacterPlayerStats(conn);
        Assert.Equal(value, stats[Key(RegenerationDisplay)]);
        Assert.Equal(222, stats[Key(Prediction)]);
        Assert.Equal(value, Published(conn)[Key(RegenerationDisplay)]);

        conn.PlayerStats.Remove(Key(RegenerationDisplay));
        Assert.Equal(222, Program.CharacterPlayerStats(conn)[Key(RegenerationDisplay)]);
        Assert.Equal(222, Published(conn)[Key(RegenerationDisplay)]);
    }

    [Fact]
    public void Recharge_changes_native_prediction_but_never_the_regeneration_row()
    {
        var conn = Character(300);
        Set(conn, "base_energy_shield_regeneration_rate_per_minute", 60);
        conn.EnergyShield.SetMaximum(300, refill: true);
        conn.EnergyShield.Damage(200, bypass: false, now: 0);
        int Get(string name) => conn.PlayerStats.GetValueOrDefault(Key(name));

        conn.EnergyShield.Advance(0, 1.9, Get, alive: true);
        Assert.False(conn.EnergyShield.Recharging);
        var before = Published(conn);
        Assert.Equal(60, before[Key(RegenerationDisplay)]);
        Assert.Equal(60, before[Key(Prediction)]);
        Assert.Equal(6000, before[Key(RechargeDisplay)]);

        conn.EnergyShield.Advance(1.9, 2.1, Get, alive: true);
        Assert.True(conn.EnergyShield.Recharging);
        var recharging = Published(conn);
        Assert.Equal(60, recharging[Key(RegenerationDisplay)]);
        Assert.Equal(6060, recharging[Key(Prediction)]);
        Assert.Equal(6000, recharging[Key(RechargeDisplay)]);

        conn.EnergyShield.Damage(1, bypass: false, now: 2.1);
        Assert.False(conn.EnergyShield.Recharging);
        var interrupted = Published(conn);
        Assert.Equal(60, interrupted[Key(RegenerationDisplay)]);
        Assert.Equal(60, interrupted[Key(Prediction)]);
        Assert.Equal(6000, interrupted[Key(RechargeDisplay)]);
    }

    [Theory]
    [InlineData(150, 2)]
    [InlineData(250, 2)]
    public void Fractional_per_minute_totals_use_the_existing_ES_publication_rounding(int percent, int expected)
    {
        var conn = Character(1);
        Set(conn, RegenerationPercent, percent);
        var stats = Program.CharacterPlayerStats(conn);
        Assert.Equal(expected, stats[Key(RegenerationDisplay)]);
        Assert.Equal(expected, stats[Key(Prediction)]);
    }

    public void Dispose() => PlayerInventories.Forget(_name);
}
