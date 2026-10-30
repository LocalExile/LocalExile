using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceMicrotransactionsTests
{
    [Fact]
    public void One_record_is_the_bytes_the_client_reads()
    {
        // 23-microtransactions.md §1: u8 +0x08, u32 +0x04 (the MtxTypes HASH32 the panel resolves), u32 +0x00 (the
        // record id), u64 raw +0x10, two wstrs, u8 +0x09, the two variant flags (0), then the three empty lists and the
        // flags byte. Live 2026-10-22 the client read exactly these bytes in this order (trace-mtx-read.js).
        byte[] m = InstanceMicrotransactions.Purchased(
        [
            new InstanceMicrotransactions.Record(MtxTypeHash: 0x12345678, Id: 0x00ABCDEF, Name: "Hi",
                Qword: 0x1122334455667788),
        ]);
        Assert.Equal("01F3" + "01" + "00" + "12345678" + "00ABCDEF" + "8877665544332211" + "00024869" + "0000" +
            "00" + "00" + "00" + "000000" + "01", Convert.ToHexString(m));
    }

}
