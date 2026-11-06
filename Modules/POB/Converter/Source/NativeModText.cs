using System.Globalization;
using System.Text.RegularExpressions;

// Built at runtime from the supported client's extracted English descriptions, never from a bundled game-data dump.
static class NativeModText
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static Dictionary<string, string[]> Build()
    {
        var descriptions = StatDescriptions.Load();
        if (descriptions.All.Count == 0) throw new InvalidDataException("Missing data/stat-descriptions.txt; run extract-data.ps1.");
        var byStat = descriptions.All.SelectMany(d => d.Stats.Select(s => (Stat:s, Description:d))).ToLookup(p => p.Stat, p => p.Description);
        var result = new Dictionary<string, string[]>();
        foreach (var mod in new ModCatalog("data").All)
        {
            var values = mod.Stats.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => ((double)g.Last().Min, (double)g.Last().Max));
            var done = new HashSet<string>(); var lines = new List<string>();
            foreach (var stat in mod.Stats)
            {
                if (done.Contains(stat.Id)) continue;
                foreach (var description in byStat[stat.Id])
                {
                    var ranges = description.Stats.Select(s => values.GetValueOrDefault(s)).ToArray();
                    foreach (var variant in description.Variants)
                    {
                        if (!variant.Conditions.Select((c,i) => Holds(c, Math.Abs(ranges[i].Item1) > Math.Abs(ranges[i].Item2) ? ranges[i].Item1 : ranges[i].Item2)).All(x => x)) continue;
                        var adjusted = ranges.ToArray(); bool supported = true;
                        foreach (var (name,index) in variant.Handlers)
                        {
                            if (index < 1 || index > adjusted.Length) { supported = false; break; }
                            double? factor = name switch
                            {
                                "negate" => -1, "divide_by_one_hundred" or "divide_by_one_hundred_2dp_if_required" => .01,
                                "per_minute_to_per_second" or "per_minute_to_per_second_2dp" or "per_minute_to_per_second_0dp" or "per_minute_to_per_second_1dp" or "per_minute_to_per_second_2dp_if_required" => 1d/60,
                                "milliseconds_to_seconds" or "milliseconds_to_seconds_2dp" or "milliseconds_to_seconds_2dp_if_required" => .001,
                                "divide_by_ten" or "divide_by_ten_0dp" or "deciseconds_to_seconds" => .1,
                                "divide_by_two_0dp" => .5, "divide_by_five" => .2, "divide_by_one_thousand" => .001,
                                "double" => 2, "times_twenty" => 20, _ => null,
                            };
                            if (factor is null) { supported = false; break; }
                            var v = adjusted[index-1]; adjusted[index-1] = (v.Item1*factor.Value,v.Item2*factor.Value);
                        }
                        if (!supported) continue;
                        string text = Regex.Replace(variant.Text, @"\{(\d+)(?::([^}]+))?\}", m =>
                        {
                            int i = int.Parse(m.Groups[1].Value, Invariant);
                            if (i >= adjusted.Length) return m.Value;
                            var (lo,hi) = adjusted[i]; string Sign(double n) => m.Groups[2].Value.Contains('+') && n >= 0 ? "+" : "";
                            string Num(double n) => Math.Round(n,2).ToString("0.##",Invariant);
                            return lo == hi ? Sign(lo)+Num(lo) : Sign(lo)+"("+Num(lo)+"-"+Num(hi)+")";
                        });
                        if (text.Contains('{')) continue;
                        lines.AddRange(text.Replace("\\n","\n").Split('\n'));
                        done.UnionWith(description.Stats); break;
                    }
                    if (done.Contains(stat.Id)) break;
                }
            }
            if (lines.Count > 0 && mod.Stats.All(s => done.Contains(s.Id))) result[mod.Id] = lines.ToArray();
        }
        return result;
    }
    static bool Holds(string condition, double value)
    {
        bool Bound(string s, bool lower) => s == "#" || double.TryParse(s, NumberStyles.Float, Invariant, out var n) && (lower ? value >= n : value <= n);
        if (condition == "#") return true;
        if (condition.StartsWith('!')) return double.TryParse(condition[1..], NumberStyles.Float, Invariant,out var n) && value != n;
        var range = condition.Split('|');
        return range.Length == 2 ? Bound(range[0],true) && Bound(range[1],false) : double.TryParse(condition, NumberStyles.Float, Invariant,out var exact) && value == exact;
    }
}
