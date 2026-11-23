using System.Text.Json;
using GameServer.Core;

/// <summary>
/// Stat names and the client's stat keys: key = Stats.dat row + 1 (Agents/docs/protocol/11-skills.md §5). Reads
/// the Stats client table (read when the catalog is made); without it only numeric keys work.
/// </summary>
public sealed class StatCatalog
{
    public StatCatalog()
    {
        _names = LoadNames();
    }

    private readonly List<string> _names;

    List<string> LoadNames()
    {
        if (!ClientTables.Has("Stats")) return [];
        return [.. ClientTables.Table("Stats").Select(r => r.Text("Id") ?? "")];
    }

    // Name -> key, built once; also caches names that are absent (null), because DamageStats asks for ~100 names per
    // hit and a repeated scan of 23 000 names would be wasteful.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint?> _keys = new(StringComparer.Ordinal);
    private bool _keysBuilt;

    /// <summary>A key from a name ("attack_speed_+%"), a decimal key or a 0x hex key.</summary>
    /// <summary>Every stat's name, by client key − 1.</summary>
    public IReadOnlyList<string> Names => _names;

    public uint? Key(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out uint hex)) return hex;
        if (uint.TryParse(text, out uint key)) return key;
        if (_keys.TryGetValue(text, out uint? found)) return found;
        if (!_keysBuilt)
            lock (_keys)
                if (!_keysBuilt)
                {
                    for (int i = 0; i < _names.Count; i++) _keys.TryAdd(_names[i], (uint)i + 1);
                    _keysBuilt = true;
                }
        if (_keys.TryGetValue(text, out found)) return found;
        _keys.TryAdd(text, null);
        return null;
    }

    public string Name(uint key) => key >= 1 && key <= _names.Count ? _names[(int)key - 1]
        : key >= FirstServerId && key - FirstServerId < (uint)_serverNames.Count ? _serverNames[(int)(key - FirstServerId)] : $"0x{key:x}";

    /// <summary>Ids from here up name a stat the server keeps that the client's table does not have; never sent.</summary>
    public const uint FirstServerId = 0x4000_0000;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint> _serverIds = new(StringComparer.Ordinal);
    readonly List<string> _serverNames = [];

    /// <summary>The id of any stat name: the client's key, or one of the server's own (<see cref="FirstServerId"/>) for a
    /// name the client does not have. Resolve it once, not per use.</summary>
    public uint Id(string name) => Key(name) ?? _serverIds.GetOrAdd(name, n =>
    {
        lock (_serverNames) { _serverNames.Add(n); return FirstServerId + (uint)_serverNames.Count - 1; }
    });

    /// <summary>Whether an id is the client's (sent in packets) rather than the server's own.</summary>
    public static bool IsClientId(uint id) => id < FirstServerId;

    /// <summary>FNV-1a over the names joined by newlines: the table <see cref="Stat"/> was generated from has the same.</summary>
    public ulong Checksum
    {
        get
        {
            ulong h = 0xcbf29ce484222325;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(string.Join('\n', _names))) h = (h ^ b) * 0x100000001b3;
            return h;
        }
    }

    /// <summary>Whether this build has a stat by this name. Uses the same name cache as <see cref="Key(string)"/>, so a
    /// repeated question costs a dictionary lookup; a name that is absent stays cached as a miss.</summary>
    public bool Has(string name) => Key(name) is not null;
}
