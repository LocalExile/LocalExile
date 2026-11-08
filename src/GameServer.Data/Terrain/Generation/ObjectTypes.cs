namespace GameServer.Data.Terrain.Generation;

/// <summary>The BaseEvents flags of an object type (.ot) that decide who creates a level's object line
/// (<c>0x1420481b0</c>): the client makes a line's object itself when its type is <c>no_serialise</c> or <c>client_only</c>
/// and not <c>server_only</c>; every other line is the server's, created by it and (unless <c>no_serialise</c>) sent in a
/// 0x2bb spawn. A type that is <c>server_only</c> and <c>no_serialise</c> exists inside the server alone (spawners,
/// ambush and teleport markers, the town portal). A type with no .ot file counts as the server's.</summary>
public sealed record ObjectTypeFlags(bool Found, bool NoSerialise, bool ClientOnly, bool ServerOnly)
{
    /// <summary>The server creates the object (the client's rule inverted).</summary>
    public bool ServerCreates => ServerOnly || !(NoSerialise || ClientOnly);

    /// <summary>The server creates the object and puts it on the wire.</summary>
    public bool ServerSends => ServerCreates && !NoSerialise;
}

/// <summary>The object types' flags, read from the .ot files (<see cref="TerrainCache.Text"/>) and kept.</summary>
public sealed class ObjectTypes(TerrainCache files)
{
    readonly Dictionary<string, ObjectTypeFlags> _flags = new(StringComparer.OrdinalIgnoreCase);
    static readonly ObjectTypeFlags None = new(false, false, false, false);

    /// <summary>The flags of a type: its parents' (along <c>extends</c>, a set flag of a parent carried down) under its own
    /// BaseEvents / Positioned block's values (a child's <c>false</c> overrides).</summary>
    public ObjectTypeFlags Flags(string type) => Flags(type, 0);

    ObjectTypeFlags Flags(string type, int depth)
    {
        if (_flags.TryGetValue(type, out var known)) return known;
        _flags[type] = None;                                           // a cycle ends here
        var flags = None;
        if (depth < 16 && type is { Length: > 0 } && type != "nothing" && files.Text(type + ".ot") is { } text)
        {
            bool noSerialise = false, clientOnly = false, serverOnly = false;
            var lines = TerrainText.Lines(text).Select(l => l.Trim()).ToList();
            foreach (var parent in lines.Where(l => l.StartsWith("extends ", StringComparison.Ordinal))
                         .Select(l => TerrainText.Tokenize(l).Skip(1).FirstOrDefault().Text).Where(p => p is { Length: > 0 }))
            {
                var p = Flags(parent!, depth + 1);
                noSerialise |= p.NoSerialise;
                clientOnly |= p.ClientOnly;
                serverOnly |= p.ServerOnly;
            }
            // The type's own BaseEvents and Positioned blocks (top level), each "key = value" line inside.
            int braces = 0;
            string block = "";
            foreach (var l in lines)
            {
                if (braces == 0 && l.Length > 0 && l != "{" && l != "}") block = l;
                if (braces == 1 && block is "BaseEvents" or "Positioned")
                {
                    string kv = l.Replace(" ", "").Replace("\t", "");
                    int eq = kv.IndexOf('=');
                    if (eq > 0)
                    {
                        string key = kv[..eq], value = kv[(eq + 1)..].TrimEnd(';');
                        bool on = value.StartsWith("true", StringComparison.OrdinalIgnoreCase);
                        switch (key)
                        {
                            case "no_serialise": noSerialise = on; break;
                            case "client_only": clientOnly = on; break;
                            case "server_only": serverOnly = on; break;
                        }
                    }
                }
                braces += l.Count(c => c == '{') - l.Count(c => c == '}');
            }
            flags = new ObjectTypeFlags(true, noSerialise, clientOnly, serverOnly);
        }
        return _flags[type] = flags;
    }
}
