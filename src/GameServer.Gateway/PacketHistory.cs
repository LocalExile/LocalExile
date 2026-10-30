using System.Text;

/// <summary>
/// The last messages of one connection in both directions (C→S as framed, S→C as written), each with its time, id, length
/// and first bytes. A client crash usually follows a message the server sent wrong or left out (the dual-wield crash,
/// 3a718b29: the off-hand stats were never sent), and the server only sees the disconnect, so these are the first lead:
/// a handler error or a connection that breaks prints the last ones to the log, Game_PACKET_DUMPS=&lt;folder&gt; (live.ps1
/// sets logs\packets) writes all of them to a file at every disconnect, and /lastpackets prints them on demand.
/// </summary>
sealed class PacketHistory
{
    public const int Capacity = 256;
    // Bytes kept per message: the id and the fields after it, enough to tell most messages apart without a full copy.
    public const int KeptBytes = 48;
    // Dump files kept in the folder; older ones are deleted at each new dump.
    public const int KeptDumps = 60;

    readonly record struct Entry(DateTime At, bool Sent, ushort Id, int Length, byte[] Head);

    readonly Entry[] entries = new Entry[Capacity];
    readonly Lock gate = new();
    int next;
    long count;

    /// <summary>Where the disconnect dumps go (Game_PACKET_DUMPS), or null: none.</summary>
    public static string? DumpFolder { get; set; } =
        Environment.GetEnvironmentVariable("Game_PACKET_DUMPS") is { Length: > 0 } folder && folder != "0" ? folder : null;

    /// <summary>Messages recorded since the connection opened (more than Capacity once the oldest are dropped).</summary>
    public long Count { get { lock (gate) return count; } }

    // The clock messages, several a second, would push everything else out: C→S 0x1b ticks and S→C 0x2b5 frame ends.
    public static bool Kept(bool sent, ushort id) => sent ? id != 0x02b5 : id != 0x001b;

    public void Received(ushort id, ReadOnlySpan<byte> bytes) => Add(sent: false, id, bytes);

    /// <summary>An S→C message as written: its first two bytes are the id (big-endian).</summary>
    public void Sent(ReadOnlySpan<byte> bytes) => Add(sent: true, IdOf(bytes), bytes);

    public static ushort IdOf(ReadOnlySpan<byte> bytes) => bytes.Length >= 2 ? (ushort)(bytes[0] << 8 | bytes[1]) : (ushort)0;

    void Add(bool sent, ushort id, ReadOnlySpan<byte> bytes)
    {
        if (!Kept(sent, id)) return;
        var entry = new Entry(DateTime.UtcNow, sent, id, bytes.Length, bytes[..Math.Min(bytes.Length, KeptBytes)].ToArray());
        lock (gate)
        {
            entries[next] = entry;
            next = (next + 1) % Capacity;
            count++;
        }
    }

    /// <summary>The last <paramref name="last"/> messages, oldest first: "12:34:56.789 => 0x01ab 34 B 01AB…".</summary>
    public IReadOnlyList<string> Lines(int last = Capacity)
    {
        Entry[] copy;
        lock (gate)
        {
            int n = (int)Math.Min(Math.Min(count, Capacity), Math.Max(0, last));
            copy = new Entry[n];
            for (int i = 0; i < n; i++) copy[i] = entries[((next - n + i) % Capacity + Capacity) % Capacity];
        }
        return copy.Select(e => $"{e.At.ToLocalTime():HH:mm:ss.fff} {(e.Sent ? "=>" : "<=")} 0x{e.Id:x4} {e.Length,5} B " +
                                Convert.ToHexString(e.Head) + (e.Length > e.Head.Length ? "…" : "")).ToList();
    }

    /// <summary>Writes the log lines "[gateway] {remote} last packets (why): …" for the last <paramref name="last"/> messages.</summary>
    public void Print(string remote, string why, int last = 32)
    {
        if (Count == 0) return;
        var lines = Lines(last);
        var text = new StringBuilder($"[gateway] {remote} last {lines.Count} of {Count} packet(s) ({why}):");
        foreach (var line in lines) text.Append(Environment.NewLine).Append("    ").Append(line);
        Console.WriteLine(text.ToString());
    }

    /// <summary>
    /// Writes every kept message to &lt;folder&gt;/packets-&lt;time&gt;-&lt;port&gt;.txt with a header, keeps the newest
    /// KeptDumps files and returns the path; null when nothing was recorded or the folder can't be written.
    /// </summary>
    public string? Dump(string folder, string remote, string header)
    {
        if (Count == 0) return null;
        try
        {
            Directory.CreateDirectory(folder);
            string port = remote.Contains(':') ? remote[(remote.LastIndexOf(':') + 1)..] : remote;
            string path = Path.Combine(folder, $"packets-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{port}.txt");
            File.WriteAllLines(path, [header, $"{Count} message(s), the last {Math.Min(Count, Capacity)} kept, the first {KeptBytes} bytes of each", "", .. Lines()]);
            foreach (var old in new DirectoryInfo(folder).GetFiles("packets-*.txt").OrderByDescending(f => f.Name).Skip(KeptDumps))
                old.Delete();
            return path;
        }
        catch (Exception e)
        {
            // Only a diagnosis: a folder that can't be written (or a malformed Game_PACKET_DUMPS) must not end the disconnect.
            Console.WriteLine($"[gateway] {remote} packet dump not written to {folder}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }
}
