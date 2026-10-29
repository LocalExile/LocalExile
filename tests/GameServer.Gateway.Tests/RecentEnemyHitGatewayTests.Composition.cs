using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    [Theory]
    [InlineData(false, 29, 133)]
    [InlineData(true, 29, 66)]
    [InlineData(true, 30, 100)]
    public async Task Current_monster_packet_shares_one_decision_between_physical_and_enabled_chaos(
        bool chaosArmour, int roll, int damage)
    {
        using var f = new Fixture();
        f.Set(ArmourAttackDamage.Per450Armour, 2);
        _ = f.Chance;
        f.Conn.Pools = f.Conn.Pools with { ArmourAppliesToChaosDamage = chaosArmour };
        for (int i = 0; i < 3; i++) f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        var random = new TraceRandom(armourRoll: roll, blockRoll: .99);
        f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        var monster = f.Monster(DamagePacket.Of(DamageType.Physical, 100, 100)
            .Add(DamageType.Chaos, new(100, 100)));
        await f.Swing(monster).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000u - (uint)damage, f.Conn.PlayerLife);
        Assert.Equal(new[] { "range:100:101", "range:100:101", "double", "next:100" }, random.Draws);
        Assert.Equal(4, f.Count);
        Assert.Equal(500, f.Conn.Pools.Armour);
        Assert.Equal(500, Program.CharacterPlayerStats(f.Conn)[Key("physical_damage_reduction_rating")]);
        Assert.Equal(2, Program.CharacterPlayerStats(f.Conn)[Key("attack_damage_+%")]);
    }

    [Fact]
    public async Task Actual_Endurance_charges_resists_Fortification_and_ES_compose_after_one_armour_decision()
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        var charges = (ChargeSession)Method("ChargesOf").Invoke(null, [f.Conn])!;
        charges.State.Set(ChargeType.Endurance, 2, f.Conn.Clock.Elapsed.TotalSeconds, new(3, 0, 10000));
        Program.FortificationOf(f.Conn).State.GrantExplicitStacks(f.Conn.Clock.Elapsed.TotalSeconds, 20);
        _ = f.Chance;
        f.Conn.Pools = f.Conn.Pools with { Life = 10000, Armour = 500, Evasion = 0,
            FireResistance = 50, ChaosResistance = 50, ArmourAppliesToChaosDamage = true, EnergyShield = 100 };
        f.Conn.EnergyShield.SetMaximum(100, refill: true);
        Assert.Equal(8, Program.CharacterPlayerStats(f.Conn)[Key("base_additional_physical_damage_reduction_%")]);
        Assert.Equal(8, Program.CharacterPlayerStats(f.Conn)[Key("additional_elemental_damage_reduction_%")]);
        var random = new TraceRandom(armourRoll: 0, blockRoll: .99);
        f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        await f.Swing(f.Monster(DamagePacket.Of(DamageType.Physical, 100, 100)
            .Add(DamageType.Fire, new(100, 100)).Add(DamageType.Chaos, new(100, 100))))
            .WaitAsync(TimeSpan.FromSeconds(5));
        // Physical 25, fire 46, chaos 10. Fortification floors total 81 to 64 and chaos to 8.
        // Chaos bypasses ES; the remaining 56 removes ES and no Life.
        Assert.Equal(9992u, f.Conn.PlayerLife);
        Assert.Equal(44u, f.Conn.EnergyShield.Current);
        Assert.Equal(2, charges.Effective(ChargeType.Endurance));
        Assert.True(charges.Pending);
        Assert.Equal(1, f.Count);
        Assert.Single(random.Draws, d => d == "next:100");
        Assert.Equal(500, f.Conn.Pools.Armour);
    }

    [Theory]
    [InlineData("/hit chaos 100", 26, 1)]
    [InlineData("/spellhit chaos 100", 16, 2)]
    [InlineData("/blockhit spell chaos 100", 26, 1)]
    public async Task Parsed_chaos_contacts_keep_armour_Fortification_suppression_and_ES_bypass_order(
        string command, int damage, int armourAndSuppressionDraws)
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        Program.FortificationOf(f.Conn).State.GrantExplicitStacks(f.Conn.Clock.Elapsed.TotalSeconds, 20);
        _ = f.Chance;
        f.Conn.Pools = f.Conn.Pools with { Life = 10000, Armour = 500, ArmourAppliesToChaosDamage = true,
            SpellSuppressionChance = 100, EnergyShield = 100 };
        f.Conn.EnergyShield.SetMaximum(100, refill: true);
        var random = new TraceRandom(armourRoll: 0, blockRoll: .99);
        f.Conn.IncomingHitRandom = random;
        await f.Command(command);
        Assert.Equal(10000u - (uint)damage, f.Conn.PlayerLife);
        Assert.Equal(100u, f.Conn.EnergyShield.Current);
        Assert.Equal(1, f.Count);
        Assert.Equal(armourAndSuppressionDraws, random.Draws.Count(d => d == "next:100"));
        Assert.Equal(500, f.Conn.Pools.Armour);
        string reply = System.Text.Encoding.ASCII.GetString(await f.Read());
        Assert.Contains(command.StartsWith("/blockhit")
            ? "26; not blocked (0% chance); life 9974/10000." : "67% armour", reply);
    }

    [Theory]
    [InlineData("zero", 100, 0, 2)]
    [InlineData("remove", 100, 0, 2)]
    [InlineData("expiry", 100, 0, 1)]
    [InlineData("gain", 66, 1, 2)]
    [InlineData("armour-zero", 200, 0, 2)]
    [InlineData("chaos-off", 133, 1, 2)]
    public async Task Queued_current_monster_swing_samples_sources_rating_flag_and_recent_window_at_contact(
        string change, int damage, int armourDraws, int count)
    {
        using var f = new Fixture();
        _ = f.Chance;
        f.Conn.Pools = f.Conn.Pools with { ArmourAppliesToChaosDamage = true };
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds - (change == "expiry" ? 3 : 0));
        if (change == "gain") f.Set(RecentEnemyHits.PerHit, 0);
        var random = new TraceRandom(armourRoll: 0, blockRoll: .99);
        f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        var monster = f.Monster(DamagePacket.Of(DamageType.Physical, 100, 100)
            .Add(DamageType.Chaos, new(100, 100)));
        await f.Gate.WaitAsync();
        var swing = f.Swing(monster);
        try
        {
            Assert.False(swing.IsCompleted);
            lock (f.Conn.Monsters)
            {
                if (change == "zero") f.Set(RecentEnemyHits.PerHit, 0);
                if (change == "remove") f.Conn.PlayerStats.Remove(Key(RecentEnemyHits.PerHit));
                if (change == "gain") f.Set(RecentEnemyHits.PerHit, 10);
                if (change == "armour-zero") f.Conn.Pools = f.Conn.Pools with { Armour = 0 };
                if (change == "chaos-off") f.Conn.Pools = f.Conn.Pools with { ArmourAppliesToChaosDamage = false };
            }
            if (change == "expiry") await Task.Delay(1100);
        }
        finally { f.Gate.Release(); }
        await swing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000u - (uint)damage, f.Conn.PlayerLife);
        Assert.Equal(count, f.Count);
        Assert.Equal(armourDraws, random.Draws.Count(d => d == "next:100"));
        Assert.Equal(new[] { "range:100:101", "range:100:101", "double" }, random.Draws.Take(3));
        Assert.Equal(change == "armour-zero" ? 0 : 500, f.Conn.Pools.Armour);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_contact_refreshes_restricted_aura_defences_before_reading_resistance_or_block(bool monster)
    {
        using var f = new Fixture();
        var ordinary = Program.PoolsFor(f.Conn, 100);
        f.Conn.Auras[500] = new(500, Program.Skills.EffectRow("PurityOfFire"), "PurityOfFire",
            "Purity of Fire", 1000, AuraCatalog.BuffRow("PurityOfFire"), 0, 0, Level: 20);
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        Assert.True(f.Conn.Pools.FireResistance > ordinary.FireResistance);
        f.Conn.PlayerLife = ordinary.Life;
        f.Conn.PlayerMana = 0;
        var random = new TraceRandom(armourRoll: 0, blockRoll: .99);
        f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        if (monster)
        {
            await f.Gate.WaitAsync();
            var swing = f.Swing(f.Monster(DamagePacket.Of(DamageType.Fire, 100, 100)));
            f.Set("base_your_auras_are_disabled", 1);
            Assert.False(swing.IsCompleted);
            f.Gate.Release();
            await swing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            f.Set("base_your_auras_are_disabled", 1);
            await f.Command("/blockhit attack fire 100");
        }
        Assert.Empty(f.Conn.Auras);
        Assert.Equal(ordinary.FireResistance, f.Conn.Pools.FireResistance);
        Assert.Equal(ordinary.Life - 100, f.Conn.PlayerLife);
        Assert.Equal(0u, f.Conn.PlayerMana);
        Assert.Equal(1, f.Count);
        Assert.DoesNotContain("next:100", random.Draws);
    }

    [Fact]
    public async Task Queued_pool_replacement_uses_current_resists_block_and_armour_together()
    {
        using var f = new Fixture(0);
        f.Set(RecentEnemyHits.GenericChance, 100);
        f.Set("base_additional_physical_damage_reduction_%", 20);
        f.Set("additional_elemental_damage_reduction_%", 20);
        var random = new TraceRandom(armourRoll: 0, blockRoll: .99);
        f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        f.Conn.Pools = f.Conn.Pools with { BlockChance = 100 };
        await f.Gate.WaitAsync();
        var swing = f.Swing(f.Monster(DamagePacket.Of(DamageType.Physical, 100, 100)
            .Add(DamageType.Fire, new(100, 100)).Add(DamageType.Chaos, new(100, 100))));
        try
        {
            Assert.False(swing.IsCompleted);
            Program.FortificationOf(f.Conn).State.GrantExplicitStacks(f.Conn.Clock.Elapsed.TotalSeconds, 20);
            _ = f.Chance;
            f.Conn.Pools = f.Conn.Pools with { Life = 10000, Armour = 500, ArmourAppliesToChaosDamage = true,
                FireResistance = 50, ChaosResistance = 50, BlockChance = 0, EnergyShield = 100 };
            f.Conn.EnergyShield.SetMaximum(100, refill: true);
        }
        finally { f.Gate.Release(); }
        await swing.WaitAsync(TimeSpan.FromSeconds(5));
        // Physical 13, fire 40, chaos 10; Fortification gives 50 total, including 8 chaos bypass.
        Assert.Equal(9992u, f.Conn.PlayerLife);
        Assert.Equal(58u, f.Conn.EnergyShield.Current);
        Assert.Equal(1, f.Count);
        Assert.Single(random.Draws, d => d == "next:100");
        Assert.Equal(500, f.Conn.Pools.Armour);
    }

    [Fact]
    public async Task Current_raw_pool_rebuild_keeps_Strength_mana_and_chaos_armour_with_authoritative_recent_stats()
    {
        using var f = new Fixture();
        var ordinary = Program.PoolsFor(f.Conn, 100);
        f.Conn.MainAscendancy = new(Nodes: [38080, 29437]);
        f.Conn.PlayerStats[Program.Stats.Key(PowerOfPurpose.RawFlag)!.Value] = 0; // Isolate the Strength-mana control.
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        f.Conn.PlayerLife = f.Conn.Pools.Life;
        f.Conn.PlayerMana = ordinary.Mana;
        uint combined = ordinary.Mana + (uint)(ordinary.Strength / 4 * 8);
        f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        await f.Command("/set_stat X_mana_per_4_strength 0");
        Assert.Equal(ordinary.Mana, f.Conn.Pools.Mana);
        Assert.True(f.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        await f.Command("/set_stat X_mana_per_4_strength off");
        Assert.Equal(combined, f.Conn.Pools.Mana);
        Assert.Equal(ordinary.Mana, f.Conn.PlayerMana);
        await f.Command("/set_stat base_armour_applies_to_chaos_damage 0");
        Assert.False(f.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Equal(combined, f.Conn.Pools.Mana);
        await f.Command("/set_stat base_armour_applies_to_chaos_damage off");
        Assert.True(f.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Equal(combined, f.Conn.Pools.Mana);
        Assert.Equal(ordinary.Mana, f.Conn.PlayerMana);
        Assert.Equal((1, 10), f.Conn.PresentedRecentEnemyHits);
        Assert.Null(f.Conn.MainAscendancy.Selection);
    }

    [Theory]
    [InlineData(true, false, true, 33, 1)]
    [InlineData(false, true, true, 33, 1)]
    [InlineData(true, true, true, 0, 0)]
    [InlineData(true, false, false, 100, 0)]
    public async Task Whole_packet_relevance_ignores_immune_components_and_keeps_chaos_flag_boolean(
        bool physicalImmune, bool chaosImmune, bool enabled, int damage, int draws)
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        f.Set("physical_immunity", physicalImmune ? 1 : 0);
        f.Set("chaos_immunity", chaosImmune ? 1 : 0);
        f.Conn.Pools = f.Conn.Pools with { ArmourAppliesToChaosDamage = enabled };
        var random = new TraceRandom(armourRoll: 0, blockRoll: .99);
        f.Conn.IncomingHitRandom = random;
        f.Conn.EvasionEntropy = 99; f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        await f.Swing(f.Monster(DamagePacket.Of(DamageType.Physical, 100, 100)
            .Add(DamageType.Chaos, new(100, 100)))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000u - (uint)damage, f.Conn.PlayerLife);
        Assert.Equal(draws, random.Draws.Count(d => d == "next:100"));
        Assert.Equal(1, f.Count);
        Assert.Equal(500, f.Conn.Pools.Armour);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zero_or_removed_source_preserves_current_chaos_armour_damage_and_seeded_RNG(bool remove)
    {
        using var f = new Fixture();
        if (remove) f.Conn.PlayerStats.Remove(Key(RecentEnemyHits.PerHit));
        else f.Set(RecentEnemyHits.PerHit, 0);
        f.Conn.Pools = f.Conn.Pools with { ArmourAppliesToChaosDamage = true };
        var random = new TraceRandom(456);
        f.Conn.IncomingHitRandom = random;
        var expected = new Random(456);
        var packet = DamagePacket.Of(DamageType.Physical, 50, 150).Add(DamageType.Chaos, new(20, 40));
        expected.Next(100); // Initial evasion entropy precedes component and block draws.
        var rolled = packet.Roll(expected);
        expected.NextDouble();
        int damage = DamageModel.MitigatePhysical((int)rolled[DamageType.Physical].Min, 500)
            + DamageModel.MitigatePhysical((int)rolled[DamageType.Chaos].Min, 500);
        await f.Swing(f.Monster(packet)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000u - (uint)damage, f.Conn.PlayerLife);
        Assert.Equal(new[] { "next:100", "range:50:151", "range:20:41", "double" }, random.Draws);
        Assert.Equal(expected.Next(), random.Next());
        Assert.Equal(1, f.Count);
    }

    [Theory]
    [InlineData("zero-life")]
    [InlineData("removed")]
    [InlineData("replaced")]
    public async Task Paused_accepted_echo_cannot_commit_after_its_monster_is_retired(string change)
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        var random = new TraceRandom(); f.Conn.IncomingHitRandom = random;
        var monster = f.Monster();
        await f.Gate.WaitAsync();
        var swing = f.Swing(monster);
        lock (f.Conn.Monsters) f.Conn.Clock.Stop();
        f.Gate.Release();
        await Task.Delay(120);
        Assert.False(swing.IsCompleted);
        lock (f.Conn.Monsters)
        {
            if (change == "zero-life") monster.Life = 0;
            if (change == "removed") f.Conn.Monsters.Remove(42);
            if (change == "replaced") f.Monster();
            f.Conn.Clock.Start();
        }
        await swing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10000u, f.Conn.PlayerLife);
        Assert.Equal(0, f.Count);
        Assert.Empty(random.Draws);
    }
}
