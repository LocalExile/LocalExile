using System.Reflection;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class LeechLimitPanelTests : IDisposable
{
    readonly string name = "LeechPanel" + Guid.NewGuid().ToString("N");
    static uint Key(string stat) => Program.Stats.Key(stat)!.Value;
    PlayerInventories Inventory => PlayerInventories.For(name, Program.Items);
    ClientState Character()
    {
        TestSaves.Ensure();
        return new(new(ushort.MaxValue, "LeechPanel", 128, 128, 32, 32, null, []), 0, 1)
        {
            Character = name, ClassRow = SkillCatalog.WitchRow, PlayerLife = 1000,
            Pools = new(100, 1000, 500, 0, 0, EnergyShield: 200),
        };
    }

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!).ToDictionary(s => s.Item1, s => s.Item2);
    static void Set(ClientState conn, string stat, int value) => conn.PlayerStats[Key(stat)] = value;
    static ItemMod Mod(string stat, int value)
    {
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    [Theory]
    [InlineData("MaxLifeLeechAmount", "maximum_life_leech_amount_per_leech", "virtual_maximum_life_leech_amount_per_leech_permyriad_max_life", "cannot_leech_life", 0)]
    [InlineData("MaxLifeLeechRecoveryRate", "maximum_total_recovery_per_minute_from_life_leech", "virtual_maximum_life_leech_rate_permyriad_per_minute", "cannot_leech_life", 6)]
    [InlineData("MaxManaLeechAmount", "maximum_mana_leech_amount_per_leech", "virtual_maximum_mana_leech_amount_per_leech_permyriad_max_mana", "cannot_leech_mana", 0)]
    [InlineData("MaxManaLeechRecoveryRate", "maximum_total_recovery_per_minute_from_mana_leech", "virtual_maximum_mana_leech_rate_permyriad_per_minute", "cannot_leech_mana", 6)]
    [InlineData("MaxEnergyShieldLeechAmount", "maximum_energy_shield_leech_amount_per_leech", "virtual_maximum_energy_shield_leech_amount_per_leech_permyriad_max_energy_shield", "cannot_leech_energy_shield", 0)]
    [InlineData("MaxEnergyShieldLeechRecoveryRate", "maximum_total_recovery_per_minute_from_energy_shield_leech", "virtual_maximum_energy_shield_leech_rate_permyriad_per_minute", "cannot_leech_energy_shield", 6)]
    public void All_six_native_rows_receive_their_absolute_percent_and_prohibition_inputs(
        string id, string absolute, string percent, string prohibited, int conversion)
    {
        using var table = JsonDocument.Parse(File.ReadAllBytes("data/tables/CharacterPanelStats.json"));
        var row = table.RootElement.EnumerateArray().Single(r => r.GetProperty("Id").GetString() == id);
        Assert.Equal(new[] { (int)Key(absolute) - 1, (int)Key(percent) - 1 },
            row.GetProperty("StatsKeys1").EnumerateArray().Select(k => k.GetInt32()));
        Assert.Contains((int)Key(prohibited) - 1, row.GetProperty("StatsKeys3").EnumerateArray().Select(k => k.GetInt32()));
        Assert.Equal(new[] { conversion, 0 }, row.GetProperty("__col8").EnumerateArray().Select(k => k.GetInt32()));
        var published = Published(Character());
        Assert.Contains(Key(absolute), published.Keys);
        Assert.Contains(Key(percent), published.Keys);
        Assert.Equal(0, published[Key(prohibited)]);
    }

    [Fact]
    public void Actor_defaults_are_limits_even_without_a_leech_source_and_do_not_recover_resources()
    {
        var conn = Character();
        conn.PlayerMana = 50;
        var stats = Published(conn);
        Assert.Equal(100, stats[Key("maximum_life_leech_amount_per_leech")]);
        Assert.Equal(50, stats[Key("maximum_mana_leech_amount_per_leech")]);
        Assert.Equal(20, stats[Key("maximum_energy_shield_leech_amount_per_leech")]);
        Assert.Equal(12000, stats[Key("maximum_total_recovery_per_minute_from_life_leech")]);
        Assert.Equal(6000, stats[Key("maximum_total_recovery_per_minute_from_mana_leech")]);
        Assert.Equal(1200, stats[Key("maximum_total_recovery_per_minute_from_energy_shield_leech")]);
        Assert.Equal(1000u, conn.PlayerLife);
        Assert.Equal(50u, conn.PlayerMana);
        Assert.Equal(0u, conn.EnergyShield.Current);
        Assert.Equal(0, stats.GetValueOrDefault(Key("life_recovery_per_minute_from_leech")));
    }

    [Theory]
    [InlineData(22356, "life", 16800)]
    [InlineData(1382, "mana", 8400)]
    [InlineData(15852, "energy_shield", 1680)]
    public void Actual_passive_sources_change_only_their_resource_limit(ushort node, string pool, int expected)
    {
        var conn = Character();
        conn.Passives.Add(node); // Source calculation control; this does not claim native tree-allocation coverage.
        var stats = Published(conn);
        Assert.Equal(expected, stats[Key($"maximum_total_recovery_per_minute_from_{pool}_leech")]);
        Assert.Equal(100, stats[Key("maximum_life_leech_amount_per_leech")]);
        Assert.Equal(50, stats[Key("maximum_mana_leech_amount_per_leech")]);
        Assert.Equal(20, stats[Key("maximum_energy_shield_leech_amount_per_leech")]);
        conn.Passives.Clear();
        stats = Published(conn);
        Assert.Equal(12000, stats[Key("maximum_total_recovery_per_minute_from_life_leech")]);
        Assert.Equal(6000, stats[Key("maximum_total_recovery_per_minute_from_mana_leech")]);
        Assert.Equal(1200, stats[Key("maximum_total_recovery_per_minute_from_energy_shield_leech")]);
    }

    [Fact]
    public void Equipped_native_mods_and_removal_flow_through_the_full_source_pipeline()
    {
        var conn = Character();
        // Synthetic source fixture using actual native Mods rows; no claim about these mods' crafting domains.
        var item = ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        {
            Mods = [Mod("maximum_life_leech_rate_+%", 40), Mod("maximum_mana_leech_rate_+%", 40),
                Mod("maximum_energy_shield_leech_amount_per_leech_is_doubled", 1)],
        };
        var placed = Inventory.Add(6, 0, 0, item)!;
        var stats = Published(conn);
        Assert.Equal(16800, stats[Key("maximum_total_recovery_per_minute_from_life_leech")]);
        Assert.Equal(8400, stats[Key("maximum_total_recovery_per_minute_from_mana_leech")]);
        Assert.Equal(40, stats[Key("maximum_energy_shield_leech_amount_per_leech")]);
        Inventory.Remove(6, placed.Id);
        stats = Published(conn);
        Assert.Equal(12000, stats[Key("maximum_total_recovery_per_minute_from_life_leech")]);
        Assert.Equal(50, stats[Key("maximum_mana_leech_amount_per_leech")]);
        Assert.Equal(20, stats[Key("maximum_energy_shield_leech_amount_per_leech")]);
    }

    [Fact]
    public void Refreshed_full_maxima_drive_caps_while_reservation_does_not_reduce_them()
    {
        var conn = Character();
        conn.Auras[0x103] = new(0x103, Program.Skills.EffectRow("Grace"), "Grace", "Grace", 1, 143, 0, 5000);
        var stats = Published(conn);
        Assert.Equal(6000, stats[Key("maximum_total_recovery_per_minute_from_mana_leech")]);
        Assert.Equal(50, stats[Key("maximum_mana_leech_amount_per_leech")]);
        conn.Pools = conn.Pools with { Life = 2000, Mana = 250, EnergyShield = 0 };
        stats = Published(conn);
        Assert.Equal(200, stats[Key("maximum_life_leech_amount_per_leech")]);
        Assert.Equal(25, stats[Key("maximum_mana_leech_amount_per_leech")]);
        Assert.Equal(0, stats[Key("maximum_energy_shield_leech_amount_per_leech")]);
        Assert.Equal(0, stats[Key("maximum_total_recovery_per_minute_from_energy_shield_leech")]);
    }

    [Theory]
    [InlineData("base_cannot_leech_life", "life", 0)]
    [InlineData("base_cannot_leech_mana", "mana", 0)]
    [InlineData("cannot_leech_or_regenerate_mana", "mana", 0)]
    [InlineData("cannot_leech_energy_shield", "energy_shield", 0)]
    public void Signed_prohibitions_clear_published_caps_then_zero_restores_them(string input, string pool, int expected)
    {
        var conn = Character();
        Set(conn, input, -1);
        var stats = Published(conn);
        Assert.Equal(expected, stats[Key($"maximum_{pool}_leech_amount_per_leech")]);
        Assert.NotEqual(0, stats[Key($"cannot_leech_{pool}")]);
        Set(conn, input, 0);
        stats = Published(conn);
        Assert.True(stats[Key($"maximum_{pool}_leech_amount_per_leech")] > 0);
        Assert.Equal(0, stats[Key($"cannot_leech_{pool}")]);
    }

    [Fact]
    public void Low_life_condition_hides_life_and_mana_limits_but_preserves_ES_and_restores_at_boundary()
    {
        var conn = Character();
        Set(conn, "cannot_leech_when_on_low_life", 1);
        conn.PlayerLife = 500;
        var stats = Published(conn);
        Assert.Equal(0, stats[Key("maximum_life_leech_amount_per_leech")]);
        Assert.Equal(0, stats[Key("maximum_mana_leech_amount_per_leech")]);
        Assert.Equal(20, stats[Key("maximum_energy_shield_leech_amount_per_leech")]);
        Assert.Equal(0, stats[Key("cannot_leech")]);
        conn.PlayerLife = 501;
        stats = Published(conn);
        Assert.Equal(100, stats[Key("maximum_life_leech_amount_per_leech")]);
        Assert.Equal(50, stats[Key("maximum_mana_leech_amount_per_leech")]);
    }

    [Fact]
    public void Explicit_zero_raw_and_virtual_replacements_preserve_companion_consistency()
    {
        var conn = Character();
        Set(conn, "maximum_life_leech_rate_%_per_minute", 0);
        Set(conn, "virtual_maximum_mana_leech_amount_per_leech_permyriad_max_mana", 0);
        var stats = Published(conn);
        Assert.Equal(0, stats[Key("maximum_total_recovery_per_minute_from_life_leech")]);
        Assert.Equal(0, stats[Key("virtual_maximum_life_leech_rate_permyriad_per_minute")]);
        Assert.Equal(0, stats[Key("maximum_mana_leech_amount_per_leech")]);
        Assert.Equal(0, stats[Key("virtual_maximum_mana_leech_amount_per_leech_permyriad_max_mana")]);
        conn.PlayerStats.Clear();
        stats = Published(conn);
        Assert.Equal(12000, stats[Key("maximum_total_recovery_per_minute_from_life_leech")]);
        Assert.Equal(50, stats[Key("maximum_mana_leech_amount_per_leech")]);
    }

    public void Dispose() => PlayerInventories.Forget(name);
}
