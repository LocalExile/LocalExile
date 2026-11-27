using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using System.Text.Json;

public static partial class Game
{
    /// <summary>
    /// A bundle: a set of stats with a name, each with the range it can roll in. It is what "the effect of this thing"
    /// means once the thing is no longer an item, a mod or a buff of its own: Kaom's Roots as a bundle is its lines'
    /// stats, and anything that can carry stats can be given it (EffectKinds "grant"). A bundle is read from the game's
    /// own tables, so a change to the unique or the mod changes the bundle with it.
    ///
    /// Written as JSON, one or more of these together (their stats add up):
    ///   "unique": a unique item's name: its own lines (and its own implicits, when the unique has ones that differ from
    ///             its base type's). "variant" names one of its variants; without it the first is taken.
    ///   "mod":    a Mods Id, or a list of them.
    ///   "stats":  stat name to a number, to [min, max], or to {"from": "roll", "scale": n}: the rolled value of the
    ///             line that gives the bundle (times the scale, 1 unless said), so "Nearby Enemies have -9% to Fire
    ///             Resistance" gives the -9 its own item rolled.
    ///   "template": one of the game's aura and buff templates (a BuffTemplates Id): the stats its buff grants, the
    ///             buff it shows as and its reach. "line" takes the template of the item line that gives the bundle
    ///             (Mods BuffTemplate1): that is how the game's data itself says what "Nearby Enemies have -9% to Fire
    ///             Resistance" gives, to whom and how far (60 cells). A template stat without a number of its own
    ///             takes the line's.
    ///   "shows":  a BuffDefinitions Id: the buff whoever carries the bundle is shown with (a template brings its own).
    ///   "name":   what to call it in logs and keys (otherwise the unique's or the mod's own name).
    /// A stat that describes the item itself (a local stat: "Has no Sockets", an armour piece's own armour) is left out:
    /// there is no item for it to describe. An unscalable number (ModScaling: "Cannot be Knocked Back" is 1 and stays
    /// 1) is given as it is whatever the bundle's effect; only an effect of 0 gives none of it.
    /// </summary>
    public sealed record StatBundle(string Name, (uint Stat, int Min, int Max, double PerOwn, ModCatalog.Mod? Line, int Index)[] Stats, int LeftOut)
    {
        /// <summary>The BuffDefinitions row its carrier is shown with; null for none.</summary>
        public ushort? Shows { get; init; }
        /// <summary>Its own reach in cells, when a template says one (0: none said).</summary>
        public int Radius { get; init; }
        /// <summary>"template": "line": read from the giving line when the rule starts (<see cref="LineBundle"/>).</summary>
        public bool OfLine { get; init; }

        /// <summary>How a list of live rules shows it.</summary>
        public override string ToString() => $"\"{Name}\" ({Stats.Length} stat(s))";

        /// <summary>The bundle's stats at one roll: <paramref name="roll"/> percent of the way through each range (0 the
        /// lowest, 100 the highest), or <paramref name="own"/> (the giving line's rolled value) times the stat's scale
        /// for a stat that takes it; then <paramref name="effect"/> percent of that. A stat that comes to 0 is dropped.</summary>
        public Dictionary<uint, int> At(double roll, double effect, double own = 0)
        {
            double t = Math.Clamp(roll, 0, 100) / 100, scale = effect / 100;
            var result = new Dictionary<uint, int>();
            foreach (var (stat, min, max, perOwn, line, index) in Stats)
            {
                // Asked now, not when the bundle was read: a mod may have opened the number up since (ModScaling.Override).
                bool unscalable = line is null ? ModScaling.Unscalable(stat) : ModScaling.Unscalable(line, index);
                double part = unscalable ? (scale == 0 ? 0 : 1) : scale;
                long value = (long)Math.Round((perOwn != 0 ? own * perOwn : min + (max - min) * t) * part, MidpointRounding.AwayFromZero);
                if (value != 0) result[stat] = ActorScale.Saturate(result.GetValueOrDefault(stat) + value);
            }
            return result;
        }
    }

    /// <summary>Reads a bundle (see <see cref="StatBundle"/>). Throws FormatException for a name the data does not have.</summary>
    public static StatBundle BundleOf(JsonElement written)
    {
        if (written.ValueKind != JsonValueKind.Object) throw new FormatException("a bundle is an object: \"unique\", \"mod\" or \"stats\"");
        var stats = new List<(uint, int, int, double, ModCatalog.Mod?, int)>();
        string? name = null;
        int leftOut = 0, radius = 0;
        ushort? shows = null;
        bool ofLine = false;
        void Mod(string id, UniqueCatalog.Definition? of, bool isImplicit)
        {
            var mod = Program.Items.Mods.ById(id) ?? throw new FormatException($"a bundle names the mod \"{id}\", which Mods does not have");
            for (int i = 0; i < mod.Stats.Count; i++)
            {
                if (mod.Stats[i].Local) { leftOut++; continue; }
                // The unique's own range for the stat where it has one, as the item itself would roll.
                var (min, max) = !mod.RangeOverridden(i) && of?.Ranges?.FirstOrDefault(r => r.Mod == mod.Id && r.Stat == i && r.Implicit == isImplicit) is { } own
                    ? (own.Min, own.Max) : mod.RangeAt(i);
                stats.Add((mod.Stats[i].Id, Math.Min(min, max), Math.Max(min, max), 0, mod, i));
            }
        }
        foreach (var part in written.EnumerateObject())
            switch (part.Name)
            {
                case "name": name = part.Value.GetString(); break;
                case "shows":
                {
                    string wanted = part.Value.GetString() ?? "";
                    shows = (ushort)(ClientRowById("BuffDefinitions", wanted)?.Index
                        ?? throw new FormatException($"a bundle shows as the buff \"{wanted}\", which BuffDefinitions does not have"));
                    break;
                }
                case "template" when part.Value.GetString() == "line": ofLine = true; name ??= "the line's own"; break;
                case "template":
                {
                    string wanted = part.Value.GetString() ?? "";
                    var template = ClientRowById("BuffTemplates", wanted)
                        ?? throw new FormatException($"a bundle names the template \"{wanted}\", which BuffTemplates does not have");
                    var read = TemplateBundle(template, null, null);
                    stats.AddRange(read.Stats);
                    (name, shows, radius) = (name ?? read.Name, shows ?? read.Shows, read.Radius);
                    break;
                }
                case "variant": break;
                case "unique":
                {
                    string wanted = part.Value.GetString() ?? "";
                    string key = UniqueCatalog.NameKey(wanted);
                    var unique = GameDataCatalog.Uniques.FirstOrDefault(d => UniqueCatalog.NameKey(d.Name).Equals(key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FormatException($"a bundle names the unique \"{wanted}\", which the unique items do not have");
                    name ??= unique.Name;
                    IEnumerable<string> mods = unique.Mods;
                    if (unique.Variants is { Length: > 0 } variants)
                    {
                        string? asked = written.TryGetProperty("variant", out var v) ? v.GetString() : null;
                        var variant = asked is null ? variants[0]
                            : variants.FirstOrDefault(x => x.Name.Equals(asked, StringComparison.OrdinalIgnoreCase))
                              ?? throw new FormatException($"\"{unique.Name}\" has no variant \"{asked}\"");
                        var ofVariants = variants.SelectMany(x => x.Mods).ToHashSet(StringComparer.Ordinal);
                        mods = [.. unique.Mods.Where(id => !ofVariants.Contains(id)), .. variant.Mods];
                    }
                    foreach (string id in unique.Implicits ?? []) Mod(id, unique, true);
                    foreach (string id in mods) Mod(id, unique, false);
                    break;
                }
                case "mod":
                    foreach (string id in part.Value.ValueKind == JsonValueKind.Array
                        ? part.Value.EnumerateArray().Select(e => e.GetString() ?? "") : [part.Value.GetString() ?? ""])
                    {
                        name ??= id;
                        Mod(id, null, false);
                    }
                    break;
                case "stats":
                    if (part.Value.ValueKind != JsonValueKind.Object) throw new FormatException("a bundle's \"stats\" is stat name to a number or to [min, max]");
                    foreach (var stat in part.Value.EnumerateObject())
                    {
                        uint id = Program.Stats.Key(stat.Name) ?? throw new FormatException($"unknown stat \"{stat.Name}\"");
                        if (stat.Value.ValueKind == JsonValueKind.Object)
                        {
                            // The giving line's own rolled value: {"from": "roll"} with an optional "scale".
                            if (!stat.Value.TryGetProperty("from", out var from) || from.GetString() is not ("roll" or "stat")
                                || stat.Value.EnumerateObject().Any(p => p.Name is not ("from" or "scale")))
                                throw new FormatException($"the stat \"{stat.Name}\" of a bundle takes {{\"from\": \"roll\"}} with an optional \"scale\"");
                            double perOwn = stat.Value.TryGetProperty("scale", out var scaled) ? scaled.GetDouble() : 1;
                            if (perOwn == 0) throw new FormatException($"the stat \"{stat.Name}\" of a bundle has a scale of 0");
                            stats.Add((id, 0, 0, perOwn, null, 0));
                            continue;
                        }
                        var (min, max) = stat.Value.ValueKind == JsonValueKind.Array && stat.Value.GetArrayLength() == 2
                            ? (stat.Value[0].GetInt32(), stat.Value[1].GetInt32())
                            : stat.Value.ValueKind == JsonValueKind.Number ? (stat.Value.GetInt32(), stat.Value.GetInt32())
                            : throw new FormatException($"the stat \"{stat.Name}\" of a bundle needs a number, [min, max] or {{\"from\": \"roll\"}}");
                        stats.Add((id, Math.Min(min, max), Math.Max(min, max), 0, null, 0));
                    }
                    break;
                default: throw new FormatException($"a bundle has no \"{part.Name}\"");
            }
        if (stats.Count == 0 && !ofLine) throw new FormatException("the bundle has no stats to give" + (leftOut > 0 ? $" ({leftOut} describe the item itself)" : ""));
        return new(name ?? "stats", [.. stats], leftOut) { Shows = shows, Radius = radius, OfLine = ofLine };
    }

    static ClientRow? ClientRowById(string table, string id)
    {
        foreach (var row in ClientTables.Table(table))
            if (row.Text("Id") == id) return row;
        return null;
    }

    // One of the game's templates as a bundle: its buff's granted stats, each with the template's own number, or the
    // line's rolled number for that stat when the template leaves it to the line (0), or the giving line's own roll.
    static StatBundle TemplateBundle(ClientRow template, ModCatalog.Mod? line, IReadOnlyList<int>? rolled)
    {
        string id = template.Text("Id") ?? "";
        var definition = ClientTables.Table("BuffDefinitions").Row((int)template.Long("BuffDefinitionsKey"))
            ?? throw new FormatException($"the template \"{id}\" names no buff");
        int[] granted = definition.Ints("GrantedStats"), values = template.Ints("Buff_StatValues");
        var names = ClientTables.Table("Stats");
        var stats = new List<(uint, int, int, double, ModCatalog.Mod?, int)>();
        for (int i = 0; i < granted.Length; i++)
        {
            if (names.Row(granted[i])?.Text("Id") is not { } stat || Program.Stats.Key(stat) is not uint key) continue;
            int value = i < values.Length ? values[i] : 0;
            int at = -1;
            if (line is not null)
                for (int n = 0; n < line.Stats.Count; n++)
                    if (line.Stats[n].Id == key) { at = n; break; }
            if (value == 0 && at >= 0 && rolled is not null && at < rolled.Count) value = rolled[at];
            // No number anywhere: the giving line's own roll.
            stats.Add(value != 0 ? (key, value, value, 0, at >= 0 ? line : null, Math.Max(0, at)) : (key, 0, 0, 1, null, 0));
        }
        return new(id, [.. stats], 0) { Shows = (ushort)definition.Index, Radius = template.Int("AuraRadius") };
    }

    /// <summary>The bundle of a worn line's own template ("template": "line"): the line of <paramref name="item"/> that
    /// has <paramref name="stat"/> and a template. Null when the item has no such line.</summary>
    public static StatBundle? LineBundle(Item item, uint stat, ModCatalog mods)
    {
        var templates = ClientTables.Table("BuffTemplates");
        foreach (var mod in item.Mods)
            if (mods.ByHash(mod.Hash) is { BuffTemplate: int row } line && line.Stats.Any(s => s.Id == stat)
                && templates.Row(row) is { } template)
                return TemplateBundle(template, line, mod.Values);
        return null;
    }
}
