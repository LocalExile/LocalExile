namespace GameServer.Gateway.Tests;

/// <summary>Reap's Blood Charges through the area sweep (issue #75, Charges tab): a surviving enemy gives one, a kill
/// takes one, the charges held scale the next hit and the cost, and the Charges tab rows publish the count.</summary>
public partial class ConditionalCriticalGatewayTests
{
    static uint BloodKey(string name) => Program.Stats.Key(name)!.Value;

    // Reap's 6% critical chance: draws far above it never crit, so the hits are the flat chaos damage alone.
    static Draws NoCriticals() => new(Enumerable.Repeat(9999, 40).ToArray());

    static uint PayReap(Contact f)
    {
        var args = new object?[] { f.Conn, f.Skill, 0u };
        Assert.True((bool)Method("PayMana").Invoke(null, args)!);
        return (uint)args[2]!;
    }

    [Fact]
    public async Task A_surviving_enemy_gives_a_blood_charge_that_scales_the_next_hit_and_the_cost()
    {
        using var f = new Contact(NoCriticals(), gemName: "Bloodreap");
        f.Set("physical_damage_+%", -100000);   // the strength bonus too: the hit is the flat chaos only
        var before = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(0, before[BloodKey(BloodCharges.Current)]);
        Assert.Equal(BloodCharges.BaseMaximum, before[BloodKey(BloodCharges.Maximum)]);
        Assert.Equal(1, before[BloodKey(BloodCharges.Show)]);
        uint baseCost = PayReap(f);

        await f.Sweep();
        uint life1 = f.Target.Life;
        int first = (int)(f.Target.MaxLife - life1);
        Assert.True(first > 0);
        Assert.Equal(1, f.Conn.BloodCharges);
        Assert.Equal(1, Program.CharacterPlayerStats(f.Conn)[BloodKey(BloodCharges.Current)]);

        await f.Sweep();
        int second = (int)(life1 - f.Target.Life);
        // 15% more damage per Blood Charge (Reap's blood_scythe_damage_+%_final_per_charge at quality 0).
        Assert.Equal((int)Math.Floor(first * 1.15), second);
        Assert.Equal(2, f.Conn.BloodCharges);
        // 20% more cost per charge held.
        Assert.Equal((uint)Math.Round(baseCost * 1.4), PayReap(f));
        // The HUD buff (BuffDefinitions 1822) went out with the count.
        Assert.Equal(2, f.Conn.SentBloodCharges);
        Assert.NotEqual(0, f.Conn.BloodChargeBuff);
    }

    [Fact]
    public async Task A_kill_takes_a_blood_charge_and_gives_none()
    {
        using var f = new Contact(NoCriticals(), gemName: "Bloodreap", targetLife: 10);
        lock (f.Conn.Monsters) f.Conn.BloodCharges = 3;
        await f.Sweep();
        Assert.True(f.Target.Dead || f.Target.Life == 0);
        Assert.Equal(2, f.Conn.BloodCharges);
        Assert.Equal(2, Program.CharacterPlayerStats(f.Conn)[BloodKey(BloodCharges.Current)]);
    }

    [Fact]
    public void Without_a_blood_charge_skill_the_rows_are_hidden()
    {
        using var f = new Contact(NoCriticals());
        var stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(0, stats[BloodKey(BloodCharges.Show)]);
        Assert.Equal(0, stats[BloodKey(BloodCharges.Current)]);
        Assert.Equal(BloodCharges.BaseMaximum, stats[BloodKey(BloodCharges.Maximum)]);
    }
}
