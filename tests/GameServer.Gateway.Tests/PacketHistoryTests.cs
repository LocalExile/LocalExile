namespace GameServer.Gateway.Tests;

public class PacketHistoryTests
{
    [Fact]
    public void Keeps_the_last_messages_of_both_directions_oldest_first()
    {
        var history = new PacketHistory();
        history.Received(0x0024, [0x00, 0x24, 1, 2]);
        history.Sent([0x01, 0xab, 9]);
        for (int i = 0; i < PacketHistory.Capacity; i++) history.Sent([0x00, 0x17, (byte)i]);
        history.Sent(new byte[PacketHistory.KeptBytes + 10]);

        Assert.Equal(PacketHistory.Capacity + 3, history.Count);
        var lines = history.Lines();
        Assert.Equal(PacketHistory.Capacity, lines.Count);
        Assert.Contains("=> 0x0017     3 B 001701", lines[0]);   // the first two and the oldest 0x17 are gone
        Assert.EndsWith("…", lines[^1]);                           // only the first KeptBytes are kept
        Assert.Single(history.Lines(1));

        long before = history.Count;
        history.Received(0x001b, [0x00, 0x1b, 1, 0, 0, 0, 0]);   // the clock messages are left out
        history.Sent([0x02, 0xb5]);
        Assert.Equal(before, history.Count);

        var early = new PacketHistory();
        early.Received(0x0024, [0x00, 0x24, 1, 2]);
        early.Sent([0x01, 0xab, 9]);
        Assert.Collection(early.Lines(),
            l => Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3} <= 0x0024     4 B 00240102$", l),
            l => Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3} => 0x01ab     3 B 01AB09$", l));
    }

    [Fact]
    public void A_dump_has_its_header_and_the_folder_keeps_the_newest_files()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"packet-dumps-{Guid.NewGuid():N}");
        try
        {
            Assert.Null(new PacketHistory().Dump(folder, "127.0.0.1:5000", "nothing"));
            var history = new PacketHistory();
            history.Sent([0x00, 0x1d, 1]);
            string path = history.Dump(folder, "127.0.0.1:5000", "127.0.0.1:5000 Someone in 1_1_1 seed 1: error")!;
            Assert.EndsWith("-5000.txt", path);
            var text = File.ReadAllLines(path);
            Assert.Equal("127.0.0.1:5000 Someone in 1_1_1 seed 1: error", text[0]);
            Assert.Contains("=> 0x001d", text[^1]);

            for (int i = 0; i < PacketHistory.KeptDumps + 5; i++)
                File.WriteAllText(Path.Combine(folder, $"packets-20000101-000000-{i:000}-1.txt"), "old");
            history.Dump(folder, "127.0.0.1:5001", "new");
            var kept = Directory.GetFiles(folder, "packets-*.txt");
            Assert.Equal(PacketHistory.KeptDumps, kept.Length);
            Assert.Contains(kept, f => f.EndsWith("-5001.txt"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
