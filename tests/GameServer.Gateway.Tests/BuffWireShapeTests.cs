using GameServer.Core.Protocol.Instance;

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

    [Fact]
    public void Recovery_and_u82_rows_pad_missing_wire_fields_to_definition_shape()
    {
        // taunted (row 408) is IsRecovery with 2 GrantedStats [taunt_target_id, is_taunted].
        // Omitting Values used to under-write 8 bytes on 0x01c1 (Gemling Captain Taunt), desyncing 0x141fe6090.
        ushort tauntedRow = FlaskGrounds.TauntedBuffRow;
        uint captainId = 0x0400_0042;
        uint playerId = 0x2000_0001;

        var omitted = new Buff(tauntedRow, captainId, U68: Buff.Seconds(3.5)).Add(playerId, 7);
        var zeroed = new Buff(tauntedRow, captainId, Values: [0u, 0u], U68: Buff.Seconds(3.5)).Add(playerId, 7);
        Assert.Equal(zeroed, omitted);

        var tauntedInfo = BuffRows.Of(tauntedRow);
        Assert.Equal([captainId, 0u], BuffRows.WireValues(tauntedInfo, [captainId]));
        Assert.Equal([captainId, 1u], BuffRows.WireValues(tauntedInfo, [captainId, 1u, 99u]));

        uint[] exactValues = [captainId, 1u];
        Assert.Same(exactValues, BuffRows.WireValues(tauntedInfo, exactValues));
        var explicitTaunt = new Buff(tauntedRow, captainId, Values: exactValues, U68: Buff.Seconds(3.5)).Add(playerId, 7);
        Assert.Equal(
            InstanceEntities.AddBuff(playerId, 7, tauntedRow, captainId, values: exactValues, u68: Buff.Seconds(3.5)),
            explicitTaunt);

        // frozen (row 2) has __col44 (HasU82) without IsRecovery: omitting U82 must still write u16 0.
        Assert.Equal(
            new Buff(2, captainId, U82: 0).Add(playerId, 8),
            new Buff(2, captainId).Add(playerId, 8));

        // chilled (row 1) has both HasU82 and IsRecovery (1 GrantedStat): omitting both must still write u16 0 + u32 0.
        Assert.Equal(
            new Buff(1, captainId, Values: [0u], U82: 0).Add(playerId, 9),
            new Buff(1, captainId).Add(playerId, 9));

        // trauma (row 1873) is non-recovery, has no u82, and has no list backend: emits U68 + Stacks and empty list.
        var traumaInfo = BuffRows.Of(1873);
        Assert.NotNull(traumaInfo);
        Assert.Equal("trauma", traumaInfo.Id);
        Assert.False(traumaInfo.IsRecovery);
        Assert.False(traumaInfo.HasU82);
        Assert.Null(BuffRows.DefaultList(traumaInfo));
        Assert.Equal(
            InstanceEntities.AddBuff(playerId, 10, 1873, playerId, u68: Buff.Seconds(5), stacks: 3),
            new Buff(1873, playerId, U68: Buff.Seconds(5), Stacks: 3).Add(playerId, 10));
    }
}
