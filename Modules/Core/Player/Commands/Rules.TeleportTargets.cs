using System.Globalization;
using System.Text.Json;


/// <summary>
/// Where the dev command /tp sends the player (Program.Teleport.cs): a cell, a cell relative to the player, or a named
/// thing of the area (a monster, an NPC, an area transition, the waypoint, the arrival point, or an object of the area's
/// walk-map capture, <c>data/walkability/&lt;area&gt;_seed&lt;seed&gt;.objects.json</c>, such as a quest chest).
/// </summary>
static class TeleportTargets
{
    /// <summary>A place /tp can go to: its name (matched by /tp &lt;name&gt;), id (monsters, NPCs, transitions) and cell.</summary>
    public sealed record Target(string Name, uint? Id, int X, int Y);

    /// <summary>Where the player lands: a cell, and what it is next to (null for a cell given by coordinates).</summary>
    public sealed record Destination(int X, int Y, string? Next);

    /// <summary>Every named place of the player's area, in a stable order: monsters, NPCs, the area transitions it sees,
    /// the waypoint, the arrival point, then the capture's objects in file order.</summary>
    public static List<Target> Candidates(ClientState conn, string folder = "data/walkability")
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        var list = new List<Target>();
        lock (conn.Monsters)
            foreach (var (id, m) in conn.Monsters)
            {
                var (x, y) = m.Walk.Position(now);
                list.Add(new(m.Label, id, x, y));
            }
        lock (conn.Npcs)
            foreach (var (id, n) in conn.Npcs)
            {
                // By name and by type (NPCs.dat Id's last part): several share a name (Lady Dialla, Sin, Nessa), the
                // campaign steps name the one they mean (DiallaRapture, SinTukohama, NessaMudFlats).
                list.Add(new(n.Npc.Name, id, n.X, n.Y));
                string type = n.Npc.Id[(n.Npc.Id.LastIndexOf('/') + 1)..];
                if (!type.Equals(n.Npc.Name, StringComparison.OrdinalIgnoreCase)) list.Add(new(type, id, n.X, n.Y));
            }
        // The exits this player sees: a quest's replaced, gated or league-scoped ones stay in the layout (TransitionShown).
        foreach (var (id, t) in conn.Layout.AreaTransitions.Where(e => TransitionShown(conn, e.Id, e.Transition)))
            list.Add(new($"transition to {AreaTransitionCatalog.IdOf(t.DestinationHash16) ?? $"0x{t.DestinationHash16:x4}"}", id, t.GridX, t.GridY));
        if (conn.Layout.Waypoint is { } w) list.Add(new("waypoint", null, w.GridX, w.GridY));
        list.Add(new("arrival", null, conn.Layout.SpawnX, conn.Layout.SpawnY));
        list.AddRange(CapturedObjects(conn.Layout.Id, conn.Seed, folder));
        return list;
    }

    /// <summary>The objects of an area's walk-map capture, named by the last part of their object type
    /// (<c>Metadata/Chests/QuestChests/RhoaChest</c> → RhoaChest). None without a capture.</summary>
    public static IEnumerable<Target> CapturedObjects(string area, uint seed, string folder = "data/walkability")
    {
        string file = Path.Combine(folder, $"{area}_seed{seed}.objects.json");
        if (!File.Exists(file)) return [];
        using var doc = GameTables.Open(file);
        if (!doc.RootElement.TryGetProperty("objects", out var objects)) return [];
        var list = new List<Target>();
        foreach (var o in objects.EnumerateArray())
            if (o.GetProperty("objectType").GetString() is { Length: > 0 } type)
                list.Add(new(type[(type.LastIndexOf('/') + 1)..], null, o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32()));
        return list;
    }

    /// <summary>
    /// Resolves /tp's arguments against the player's position and the area:
    /// <c>x y</c> (a cell; a value starting with + or - is relative to the player on that axis),
    /// <c>&lt;id&gt;</c> (decimal or 0x hex: a monster, NPC or area transition),
    /// <c>&lt;name&gt; [n]</c> (the n-th place, 1-based, whose name is the given one, else contains it; case-insensitive).
    /// A cell lands on itself when walkable, else on the nearest walkable cell; a named place lands next to it
    /// (<see cref="AreaLayout.Near"/>), so an object can be used from there. Null with <paramref name="error"/> otherwise.
    /// </summary>
    public static Destination? Resolve(ClientState conn, IReadOnlyList<string> args, IReadOnlyList<Target> candidates, out string? error)
    {
        error = null;
        if (args.Count == 0) { error = "Usage: /tp <x> <y> | /tp <+dx> <+dy> | /tp <id> | /tp <name> [n] | /tp list [filter]"; return null; }
        var layout = conn.Layout;
        if (args.Count == 2 && Coordinate(args[0]) is { } cx && Coordinate(args[1]) is { } cy)
        {
            var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            int x = cx.Relative ? px + cx.Value : cx.Value, y = cy.Relative ? py + cy.Value : cy.Value;
            if (x < 0 || y < 0 || (layout.GridW > 0 && x >= layout.GridW) || (layout.GridH > 0 && y >= layout.GridH))
            {
                error = $"({x}, {y}) is outside the area ({layout.GridW}x{layout.GridH}).";
                return null;
            }
            if (layout.Walkable is not { } walkable || walkable(x, y)) return new(x, y, null);
            var (nx, ny) = layout.Near((x, y));
            if (!walkable(nx, ny)) { error = $"No walkable cell near ({x}, {y})."; return null; }
            return new(nx, ny, $"({x}, {y}), not walkable");
        }
        Target? pick;
        if (args.Count == 1 && Id(args[0]) is { } id && candidates.FirstOrDefault(c => c.Id == id) is { } byId) pick = byId;
        else
        {
            int index = 1;
            var words = args.ToList();
            if (words.Count > 1 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1)
            {
                index = n;
                words.RemoveAt(words.Count - 1);
            }
            string name = string.Join(' ', words);
            var exact = candidates.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count > 0 ? exact : [.. candidates.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase))];
            if (matches.Count == 0) { error = $"Nothing called \"{name}\" here. /tp list shows the places."; return null; }
            if (index > matches.Count) { error = $"Only {matches.Count} place(s) called \"{name}\" here."; return null; }
            pick = matches[index - 1];
        }
        var (tx, ty) = layout.Near((pick.X, pick.Y));
        return new(tx, ty, $"{pick.Name} at ({pick.X}, {pick.Y})");
    }

    /// <summary>The /tp list reply: one line per place (its index among places of the same name, id and cell), optionally
    /// only those whose name contains <paramref name="filter"/>.</summary>
    public static string List(IReadOnlyList<Target> candidates, string? filter)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var c in candidates)
        {
            int n = seen[c.Name] = seen.GetValueOrDefault(c.Name) + 1;
            if (filter is { Length: > 0 } && !c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add($"{c.Name}{(n > 1 ? $" {n}" : "")}{(c.Id is { } id ? $" 0x{id:x}" : "")} ({c.X}, {c.Y})");
        }
        return lines.Count == 0 ? "No places." : string.Join('\n', lines);
    }

    static (int Value, bool Relative)? Coordinate(string s) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? (v, s[0] is '+' or '-') : null;

    static uint? Id(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h) ? h : null
            : uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint d) ? d : null;
}
