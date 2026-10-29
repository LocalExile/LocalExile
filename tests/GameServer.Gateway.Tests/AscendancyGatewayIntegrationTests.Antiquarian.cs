using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    internal static readonly ushort[][] AntiquarianBuilds =
    [
        [23940, 24105, 48353, 18893, 34223, 34762, 62851, 8636],
        [62851, 8636, 40664, 27339, 61085, 43431, 34708, 36978],
        [34708, 36978, 5913, 55407, 31396, 45368, 23940, 24105],
    ];

    static int AntiquarianStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Antiquarian_legal_build_paid_full_refund_reallocation_and_reload(int index)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Antiquarian",
            refundPoints: 100, level: 60);
        var nodes = AntiquarianBuilds[index];
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(8, game.State.MainAscendancy.Allocated.Count);
        Assert.Equal(index == 2 ? 72 : 272, game.State.Pools.Strength);
        Assert.Equal(index == 0 ? 14 : 7, game.State.Pools.Dexterity);
        Assert.Equal(index == 2 ? 14 : 0, game.State.Pools.Intelligence);
        Assert.Equal(index == 0 ? 140 : 40, AntiquarianStat(game.State, "global_defences_+%"));
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Null((await game.Change(nodes.Reverse().ToArray(), false, refundPoints: true)).Error);
        Assert.Empty(game.State.MainAscendancy.Allocated);
        Assert.Equal((ushort)60, game.State.RefundPoints);
        Assert.Equal(14, game.State.Pools.Intelligence);
        Assert.False(Program.PlayerAlwaysHits(game.State));
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(game.State.Pools.Strength, game.Reload().Pools.Strength);
        Assert.Equal((ushort)60, game.Reload().RefundPoints);
    }

    [Fact]
    public async Task Body_defences_convert_fractionally_and_real_hit_breaks_ward_and_grants_data_adrenaline()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Antiquarian",
            nodes: AntiquarianBuilds[0], refundPoints: 100, level: 60);
        var type = Program.Items.Find("Plate Vest")!;
        var item = ItemCatalog.Create(type);
        game.Items.Add(PlayerInventories.BodyArmour, 0, 0, item);
        game.State.Pools = Program.PoolsFor(game.State, 60);
        var local = ItemModStats.Local(item, Program.Items.Mods);
        var body = ItemDefences.Of(Program.Items.Defences(type)!.Value, local, item.Quality);
        Assert.Equal((uint)((body.Armour + body.Evasion) * .5m * 2.4m), game.State.Pools.Ward);
        Assert.Equal((int)(body.Armour * .5m * 2.4m), game.State.Pools.Armour);
        Assert.Equal(game.State.Pools.Ward, game.State.Ward.Current);
        double now = game.State.Clock.Elapsed.TotalSeconds;
        int ward = (int)game.State.Ward.Current;
        int taken = Program.DamagePlayerHit(game.State, ward + 10, 0, now, Program.CharacterPlayerStats(game.State));
        Assert.Equal(10, taken);
        Assert.True(game.State.Ward.Broken);
        Assert.True(game.State.Antiquarian.Adrenaline(now));
        Assert.Equal(100, AntiquarianStat(game.State, "damage_+%"));
        Assert.Equal(38, game.State.Pools.AttackSpeedPct);
        Assert.Equal(25, game.State.Pools.MovementVelocityPct);
        Assert.Equal(10, AntiquarianStat(game.State, "base_additional_physical_damage_reduction_%"));
        uint damageKey = Program.Stats.Key("damage_+%")!.Value;
        Assert.Equal(0, game.State.StatSourcesCache!.Sources.GetValueOrDefault(damageKey));
        Assert.Equal(100, AntiquarianStat(game.State, "damage_+%"));
        Assert.Equal(0, game.State.StatSourcesCache.Sources.GetValueOrDefault(damageKey));
        double deadline = game.State.Antiquarian.AdrenalineUntil;
        game.State.Ward.Restore();
        Program.DamagePlayerHit(game.State, 1, 0, now + .1, Program.CharacterPlayerStats(game.State));
        Assert.Equal(deadline, game.State.Antiquarian.AdrenalineUntil);
        Assert.Null((await game.Change([34762], false, refundPoints: true)).Error);
        Assert.False(game.State.Antiquarian.Adrenaline(now));
        Assert.Equal(0, AntiquarianStat(game.State, "damage_+%"));
        Assert.Equal(10, game.State.Pools.AttackSpeedPct);
    }

    [Fact]
    public async Task Turtle_accepted_stun_starts_recovery_and_source_removal_cancels_the_future_integral()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Antiquarian",
            nodes: AntiquarianBuilds[1], refundPoints: 100, level: 60);
        double now = game.State.Clock.Elapsed.TotalSeconds;
        var intent = Program.CommitPlayerHitRecoveryLocked(game.State, (int)(game.State.PlayerMaxLife * .4) + 1, now);
        Assert.NotNull(intent);
        Assert.Equal(.066f, intent.Seconds);
        Assert.Equal(game.State.PlayerMaxLife * .08, game.State.Antiquarian.RecoveryPerSecond(now), 8);
        Assert.True(AntiquarianStat(game.State, "life_recovery_per_minute") > 0);
        Assert.Null((await game.Change([43431], false, refundPoints: true)).Error);
        Assert.Equal(0, game.State.Antiquarian.RecoveryPerSecond(game.State.Clock.Elapsed.TotalSeconds));
        Assert.True(game.State.Antiquarian.TakeRecovery(now + 10) < game.State.PlayerMaxLife * .08);
    }

    [Fact]
    public async Task Shield_override_uses_only_the_usable_active_shield_and_flat_damage_is_attack_specific()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Antiquarian",
            nodes: AntiquarianBuilds[2], refundPoints: 100, level: 60);
        var shield = ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!);
        game.Items.Add(PlayerInventories.Offhand1, 0, 0, shield);
        game.State.Pools = Program.PoolsFor(game.State, 60);
        Assert.Equal(60, game.State.Pools.BlockChanceBase);
        Assert.Equal(60, game.State.Pools.BlockChance);
        int Get(string name) => AntiquarianStat(game.State, name);
        Assert.Equal(50, AntiquarianEffects.RangedPhysical(150, true, Get));
        Assert.Equal(0, AntiquarianEffects.RangedPhysical(50, true, Get));
        Assert.Equal(150, AntiquarianEffects.RangedPhysical(150, false, Get));
        Assert.Null((await game.Change([45368], false, refundPoints: true)).Error);
        Assert.Equal(Program.Items.ShieldBlock(Program.Items.Find("Splintered Tower Shield")!), game.State.Pools.BlockChanceBase);
    }

    [Fact]
    public void Mixed_hit_ward_keeps_chaos_out_of_energy_shield()
    {
        var conn = new ClientState(new(1, "ward-mixture", 128, 128, 32, 32, null, []), 0, 1)
            { PlayerLife = 100 };
        conn.Ward.SetMaximum(5); conn.EnergyShield.SetMaximum(10, true);
        Assert.Equal(2, Program.DamagePlayerHit(conn, 3, 4, 0, new Dictionary<uint, int>()));
        Assert.Equal(10u, conn.EnergyShield.Current);
        Assert.True(conn.Ward.Broken);
    }

    [Fact]
    public async Task Goliath_zero_intelligence_disables_real_equipment_and_gem_requirements()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Antiquarian", level: 60);
        await game.Command("/set_stat base_intelligence 200");
        var robeType = Program.Items.Find("Simple Robe")!;
        var robe = game.Items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(robeType));
        var gemType = Program.Items.Find("Fireball")!;
        var fireball = ItemCatalog.Create(gemType) with { GemExperience = Program.Items.Gem(gemType)!.ExperienceFor(8) };
        Assert.Null(ProgramValue<string?>("GemRequirementsUnmet", game.State, fireball));
        Assert.Null((await game.Change([62851, 8636], true)).Error);
        Assert.Equal(0, game.State.Pools.Intelligence);
        Assert.True(ProgramValue<bool>("IsInactive", game.State, robe.Id));
        Assert.Contains("intelligence", ProgramValue<string?>("GemRequirementsUnmet", game.State, fireball));
        Assert.Equal(0u, game.State.Pools.EnergyShield);
    }

    [Fact]
    public async Task Actual_Enduring_Cry_consumes_Whaletooth_duration_and_retains_its_recovery_rate()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Antiquarian",
            nodes: AntiquarianBuilds[1], level: 60);
        game.State.Monsters[2] = new SpawnedMonster("power", new(32, 32, 0), 0, 2, 100)
            { Rarity = MonsterRarity.Unique, Hostile = true };
        game.State.PlayerStats[Program.Stats.Key("warcry_speed_+%")!.Value] = 10000;
        double before = game.State.Clock.Elapsed.TotalSeconds;
        Assert.True(ProgramValue<bool>("UseChargeSkill", game.State, Program.Skills.EffectRow("EnduringCry"), 1, 0, (32, 32)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (game.State.EnduringCryRecovery.Until == 0) await Task.Delay(10, timeout.Token);
        double after = game.State.Clock.Elapsed.TotalSeconds;
        Assert.InRange(game.State.EnduringCryRecovery.Until, before + 3.175, after + 3.175);
        Assert.Equal(game.State.PlayerMaxLife * .08, game.State.EnduringCryRecovery.Rate(after), 8);
        Assert.Equal(game.State.PlayerMaxLife * .08 * 3.175, game.State.EnduringCryRecovery.Take(after + 10), 6);
        Assert.Equal(0, game.State.EnduringCryRecovery.Take(after + 11));
    }

    [Fact]
    public void Ending_temporary_recovery_corrects_native_predicted_Life_even_without_a_later_recovery_tick()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Antiquarian", level: 60);
        var conn = game.State;
        conn.PlayerLife = 123;
        conn.Clock.Stop();
        double now = conn.Clock.Elapsed.TotalSeconds;
        conn.EnduringCryRecovery.Begin(now - 2, 1, 10);
        conn.SentEnduringCryUntil = now - 1;
        var packet = Program.AntiquarianPresentation(conn)!;
        Assert.NotNull(packet);
        var correction = InstanceProtocol.LifeUpdate(Program.PlayerId, 123);
        Assert.True(packet.AsSpan().IndexOf(correction) >= 0);
        Assert.Null(Program.AntiquarianPresentation(conn));
    }
}
