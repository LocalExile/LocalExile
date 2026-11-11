using GameServer.Core;


/// <summary>Native OT footprints for NPCs and functional scenery. Loot and waypoints stay nonblocking.</summary>
public static class StationaryCollision
{
    public sealed record Profile(int Size, bool Blocking, bool Static, string? Model)
    {
        public string? BlockingLeagueFlag { get; init; }
        public int BlockerSize(double? hull) => Blocking ? Math.Max(0, Size - 1) : hull > 0 ? 0 : -1;
    }

    static readonly Lazy<Dictionary<string, Profile>> Types = new(() =>
    {
        var types = Load();
        // NPCs.dat registers Id aliases on Metadata's OT (e.g. later-act versions of a town NPC).
        foreach (var npc in ClientTables.Table("NPCs"))
            if (npc.TextOrNull("Id") is { } id && !types.ContainsKey(id)
                && npc.TextOrNull("Metadata") is { } metadata
                && types.TryGetValue(metadata, out var shape)) types[id] = shape;
        return types;
    });
    static readonly Lazy<Dictionary<string, double>> Radii = new(() =>
    {
        var radii = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("hideout_collision_radii")) radii.TryAdd(r.Text("model")!, r.Real("radius"));
        return radii;
    });

    public static Profile For(string type) => Types.Value.GetValueOrDefault(type)
        ?? throw new InvalidOperationException($"Missing collision metadata for {type}; run scripts/dev/extract-data.ps1");

    public static (int Size, double? Hull) Resolve(string type, string? model = null)
    {
        var p = For(type);
        double? hull = p.Static && Radii.Value.TryGetValue(model ?? p.Model ?? "", out double r) && r > 0 ? r : null;
        return (p.BlockerSize(hull), hull);
    }

    // The object_types rows with Positioned (positioned: the type has the component, whether or not it sets a field).
    public static Dictionary<string, Profile> Load()
    {
        var result = new Dictionary<string, Profile>(StringComparer.Ordinal);
        foreach (var (type, t) in ObjectTypes.All())
        {
            if (t.BoolOrNull("positioned") != true) continue;
            int size = int.TryParse(t.TextOrNull("positioned_object_size"), out int n) ? n : 1;
            result[type] = new(Math.Max(1, size), ObjectTypes.Flag(t, "positioned_blocking") == true,
                ObjectTypes.Flag(t, "positioned_static") == true, t.TextOrNull("animated_object"))
                { BlockingLeagueFlag = t.TextOrNull("positioned_block_league_flag") };
        }
        return result;
    }
}
