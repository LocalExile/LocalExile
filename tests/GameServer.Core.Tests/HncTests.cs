using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class HncTests
{
    public HncTests() => HncSender.UseMaskForTests([.. Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11))]);

    // Replays the client's 0x1c reader (0x141fa7150): bits 9..31, then 0..8 of the u32, LSB first into 8-bit bytes.
    private static List<byte> ClientBits(IEnumerable<uint> carriers)
    {
        var bytes = new List<byte>();
        int acc = 0, have = 0;
        void Push(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                acc |= (int)((value >> i) & 1) << have;
                if (++have == 8) { bytes.Add((byte)acc); acc = 0; have = 0; }
            }
        }
        foreach (uint v in carriers) { Push(v >> 9, 23); Push(v & 0x1ff, 9); }
        return bytes;
    }

    [Fact]
    public void Idle_carriers_decrypt_to_zero_bytes_on_the_client()
    {
        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        var server = new HncSender(key);
        var carriers = Enumerable.Range(0, 25).Select(_ => server.NextIdleU32()).ToList();

        byte[] seen = [.. ClientBits(carriers)];
        Assert.Equal(100, seen.Length);
        HncSender.Pair(key, isClient: true).Receive.Process(seen);
        Assert.All(seen, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Carrier_bit_order_matches_the_reader()
    {
        foreach (uint v in new uint[] { 0x12345678, 0xffffffff, 0x80000001, 0x000001ff })
            Assert.Equal(HncSender.CarrierBytes(v), ClientBits([v]));
    }

    [Fact]
    public void Derived_key_differs_from_instance_key_and_is_deterministic()
    {
        byte[] key = new byte[64];
        Assert.Equal(HncSender.DeriveKey(key), HncSender.DeriveKey(key));
        Assert.NotEqual(key, HncSender.DeriveKey(key));
    }
}
