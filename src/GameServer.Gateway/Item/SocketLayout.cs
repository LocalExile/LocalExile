using GameServer.Core.Protocol.Instance;

/// <summary>
/// The dev command /sockets: a socket layout written as colour letters (R, G, B, W), linked within a word and unlinked
/// between words ("RRGB" a 4-link, "RR GB" two 2-links, "R G B" three single sockets; dashes inside a word are ignored, so
/// "R-R-G-B" is a 4-link too).
/// </summary>
static class SocketLayout
{
    public const int MaximumSockets = 6;

    /// <summary>Game_SOCKET_CAPACITY=1..6 (dev): /sockets may give any socketable base that many sockets, ignoring its
    /// class and dimensions, e.g. a 6-link wand (a wand's own capacity is 3). 0 when unset, so the base's capacity
    /// applies as before. Read on every use: the flag only changes the dev command's own limit.</summary>
    public static int CapacityOverride =>
        int.TryParse(Environment.GetEnvironmentVariable("Game_SOCKET_CAPACITY"), out int sockets) && sockets > 0
            ? Math.Clamp(sockets, 1, MaximumSockets) : 0;

    /// <summary>The colours and link groups of the words, or an error.</summary>
    public static (IReadOnlyList<SocketColour> Colours, IReadOnlyList<byte> Links)? Parse(IEnumerable<string> words, out string? error)
    {
        var colours = new List<SocketColour>();
        var links = new List<byte>();
        foreach (string word in words)
        {
            int group = 0;
            foreach (char c in word)
            {
                if (c == '-') continue;
                SocketColour? colour = char.ToUpperInvariant(c) switch
                {
                    'R' => SocketColour.Red, 'G' => SocketColour.Green, 'B' => SocketColour.Blue, 'W' => SocketColour.White,
                    _ => null,
                };
                if (colour is not { } k) { error = $"'{c}' is not a socket colour (R, G, B or W)"; return null; }
                colours.Add(k);
                group++;
            }
            if (group > 0) links.Add((byte)group);
        }
        error = colours.Count == 0 ? "which sockets? e.g. RRGB (linked), RR GB (two groups)"
            : colours.Count > MaximumSockets ? $"at most {MaximumSockets} sockets" : null;
        return error is null ? (colours, links) : null;
    }

    /// <summary>
    /// The item with these sockets and links. Socketed gems stay in their sockets (the socket takes the new colour); a
    /// layout that would drop a socket holding a gem is refused, as is one over the base's capacity
    /// (<see cref="CurrencyEffects.SocketCapacity"/>; the item level does not limit it). A positive
    /// <paramref name="capacityOverride"/> replaces that capacity (the dev flag, <see cref="CapacityOverride"/>): a
    /// socketable base takes up to that many sockets whatever its class and dimensions.
    /// </summary>
    public static Item? Apply(Item item, ItemCatalog.BaseType type, IReadOnlyList<SocketColour> colours, IReadOnlyList<byte> links,
        out string? error, int capacityOverride = 0)
    {
        int capacity = CurrencyEffects.SocketCapacity(item, type);
        if (capacity == 0) { error = $"{type.Name} can't have sockets"; return null; }
        if (capacityOverride > 0) capacity = Math.Min(capacityOverride, MaximumSockets);
        if (colours.Count > capacity) { error = $"{type.Name} has room for at most {capacity} sockets"; return null; }
        if (item.Sockets.Skip(colours.Count).Any(s => s.Gem is not null)) { error = "take out the gems of the sockets that would go first"; return null; }
        error = null;
        return item with
        {
            Sockets = [.. colours.Select((c, i) => new ItemSocket(c, i < item.Sockets.Count ? item.Sockets[i].Gem : null))],
            Links = [.. links],
        };
    }

    /// <summary>The item's layout the way /sockets takes it: "RRGB", "RR GB"; "none" without sockets.</summary>
    public static string Describe(Item item)
    {
        if (item.Sockets.Count == 0) return "none";
        var groups = new List<string>();
        int at = 0;
        foreach (int size in item.Links ?? [.. Enumerable.Repeat((byte)1, item.Sockets.Count)])
        {
            groups.Add(string.Concat(item.Sockets.Skip(at).Take(size).Select(s => Letter(s.Colour))));
            at += size;
        }
        if (at < item.Sockets.Count) groups.AddRange(item.Sockets.Skip(at).Select(s => Letter(s.Colour).ToString()));
        return string.Join(' ', groups);
    }

    static char Letter(SocketColour colour) => colour switch
    {
        SocketColour.Red => 'R', SocketColour.Green => 'G', SocketColour.Blue => 'B', SocketColour.White => 'W',
        SocketColour.Abyss => 'A', SocketColour.Delve => 'D', _ => '?',
    };
}
