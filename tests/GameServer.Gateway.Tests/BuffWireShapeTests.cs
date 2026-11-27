namespace GameServer.Gateway.Tests;

public sealed class BuffWireShapeTests
{
    [Fact]
    public void A_non_recovery_row_does_not_send_values_or_u82()
    {
        // convergence (1699): a caller used to send 30 as a value; the client read it as a 30-entry list
        // header and over-read the messages that followed.
        var wire = Convert.ToHexString(new Buff(1699, 0x20000003, Values: [30], U68: Buff.Seconds(4), U82: 7)
            .Add(0x20000003, 0x66d));
        Assert.Equal("01C120000003000000000000066D06A3200000030000024080000000000000", wire);
    }

    [Fact]
    public void A_recovery_row_keeps_values_and_u82()
    {
        // chilled (row 1) has both tails: the +0xff u16 and its one GrantedStats value.
        var wire = Convert.ToHexString(new Buff(1, 0x1000, Values: [30], U82: 30).Add(0x1000, 3));
        Assert.Equal("01C1000010000000000000000003000100001000000000001E0000001E00000000", wire);
    }

    [Fact]
    public void Unchecked_behaviour_rows_emit_required_list_defaults_and_preserve_explicit_entries()
    {
        // TempestShield (row 112, TempestShieldBuff): 0x1417a9410 reads two u32 list words unchecked.
        ushort tempestRow = AuraCatalog.BuffRow("TempestShield");
        var defaultTempest = new Buff(tempestRow, 0x0400_0001, U68: Buff.Seconds(3.5)).Add(0x0400_0001, 5);
        Assert.Equal(
            new Buff(tempestRow, 0x0400_0001, U68: Buff.Seconds(3.5), List: [0u, 0u]).Add(0x0400_0001, 5),
            defaultTempest);
        Assert.Equal([0u, 0u], BuffRows.Spec(tempestRow, 0x0400_0001, null, 3.5).List);

        // Explicit caller entries (e.g. Celestial Tempest Shield art variation + hide flag) are preserved,
        // and short lists are padded from the row's defaults rather than dropped.
        Assert.Equal([2u, 1u], BuffRows.WireList(BuffRows.Of(tempestRow), [2u, 1u]));
        Assert.Equal([2u, 0u], BuffRows.WireList(BuffRows.Of(tempestRow), [2u]));

        // Other unchecked-list behaviours (ChronomancerBuff 1915, QuickGuardBuff 1302) also get safe defaults.
        Assert.Equal([42u, 100u], BuffRows.Spec(AuraCatalog.BuffRow("TemporalRift"), 0x1000, null).List);
        Assert.Equal([0u], BuffRows.Spec(BuffRows.Find("quick_guard"), 0x1000, null, 4).List);
    }
}
