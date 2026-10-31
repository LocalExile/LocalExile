using System.Reflection;

namespace GameServer.Gateway.Tests;

/// <summary>Brutal, Affliction and Absorption Charges through the Gateway's stat composition (issue #75, Charges tab):
/// the "gain … instead of" flags of the three conversion uniques turn the held standard charges into the special type's,
/// with the special rows, effects and buff and without the standard ones.</summary>
public class ConvertedChargesGatewayTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    static ClientState Character(string name)
    {
        TestSaves.Ensure();
        return new ClientState(new(ushort.MaxValue, name, 100, 100, 10, 10, null, []), 1, 1)
            { Character = name + Guid.NewGuid().ToString("N"), PlayerLife = 100 };
    }

    static ChargeSession SessionOf(ClientState conn) =>
        (ChargeSession)typeof(Program).GetMethod("ChargesOf", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!;

    static void Hold(ClientState conn, ChargeType type, int count)
    {
        lock (conn.Monsters)
            SessionOf(conn).State.Set(type, count, conn.Clock.Elapsed.TotalSeconds, new(3, 0, Charges.BaseDurationMs));
    }

    [Fact]
    public void Held_endurance_charges_become_brutal_charges_with_the_flag_and_raise_the_stun_threshold()
    {
        var conn = Character("BrutalCharges");
        Hold(conn, ChargeType.Endurance, 2);
        var plain = Program.CharacterPlayerStats(conn);
        Assert.Equal(2, plain[Key("current_endurance_charges")]);
        Assert.Equal(0, plain[Key("current_brutal_charges")]);
        Assert.Equal(8, plain[Key("additional_physical_damage_reduction_%")]);
        Assert.Equal(0, plain.GetValueOrDefault(Key("stun_threshold_+%")));

        conn.PlayerStats[Key(Charges.ConversionFlag(ChargeType.Endurance))] = 1;
        conn.PlayerStats[Key(Charges.MinimumConversionFlag(ChargeType.Endurance))] = 1;
        conn.PlayerStats[Key("base_minimum_endurance_charges")] = 1;
        var converted = Program.CharacterPlayerStats(conn);
        Assert.Equal(0, converted[Key("current_endurance_charges")]);
        Assert.Equal(0, converted[Key("actual_current_endurance_charges")]);
        Assert.Equal(2, converted[Key("current_brutal_charges")]);
        Assert.Equal(1, converted[Key("minimum_brutal_charges")]);
        // Character.ot's coefficients (PanelStatDefaults): 3% triple damage and 10% stun threshold per Brutal Charge.
        Assert.Equal(6, converted[Key("base_chance_to_deal_triple_damage_%")]);
        Assert.Equal(20, converted[Key("stun_threshold_+%")]);
        Assert.Equal(0, converted.GetValueOrDefault(Key("additional_physical_damage_reduction_%")));
        Assert.Equal(0, converted.GetValueOrDefault(Key("additional_elemental_damage_reduction_%")));
        Assert.True(SessionOf(conn).Converted(ChargeType.Endurance));
        // The threshold is a real stun input now, not an unsupported gate: a huge hit still stuns.
        Assert.NotNull(Program.CommitPlayerHitRecoveryLocked(conn, 10000, 0));
    }

    [Fact]
    public void Affliction_and_absorption_charges_take_the_other_two_types()
    {
        var conn = Character("AfflictionCharges");
        Hold(conn, ChargeType.Frenzy, 3);
        Hold(conn, ChargeType.Power, 1);
        conn.PlayerStats[Key(Charges.ConversionFlag(ChargeType.Frenzy))] = 1;
        conn.PlayerStats[Key(Charges.ConversionFlag(ChargeType.Power))] = 1;
        var stats = Program.CharacterPlayerStats(conn);
        Assert.Equal(3, stats[Key("current_affliction_charges")]);
        Assert.Equal(1, stats[Key("current_absorption_charges")]);
        Assert.Equal(0, stats[Key("current_frenzy_charges")]);
        Assert.Equal(0, stats[Key("current_power_charges")]);
        // Not Frenzy's 4% attack and cast speed, not Power's 50% critical chance per charge.
        Assert.Equal(0, stats.GetValueOrDefault(Key("attack_speed_+%")));
        Assert.Equal(0, stats.GetValueOrDefault(Key("critical_strike_chance_+%")));
        // The Affliction more multiplier reaches damage with ailments only.
        int Get(string name) => stats.GetValueOrDefault(Key(name));
        Assert.Equal(1.0, ChargeEffects.DamageMore(Get), 9);
        Assert.Equal(1.24, ChargeEffects.DamageMore(Get, ailment: true), 9);
    }
}
