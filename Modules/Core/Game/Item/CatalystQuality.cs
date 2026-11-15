using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Game;

// Jewellery quality targets the native modifier tags and lists in ModEffectStats.
// It adds to other increased modifier magnitudes, once even if several tags match.
static class CatalystQuality
{
    internal sealed record Rule(IReadOnlySet<string> Tags, bool Explicit, bool Implicit, bool Prefix, bool Suffix, bool Enchant)
    {
        public bool Matches(ItemMod mod, ModCatalog.Mod row) => (mod.Section switch
        {
            ModSection.Implicit => Implicit,
            ModSection.Explicit => Explicit || Prefix && row.GenerationType == 1 || Suffix && row.GenerationType == 2,
            ModSection.Enchant => Enchant,
            _ => false,
        }) && (Tags.Count == 0 || row.Tags.Any(Tags.Contains));
    }
    static object? revision;
    static IReadOnlyDictionary<byte, Rule> rules = new Dictionary<byte, Rule>();
    static readonly object gate = new();

    public static Rule? For(Item item)
    {
        if (item.Quality == 0 || item.AlternateQualityType is not byte quality) return null;
        lock (gate)
        {
            if (!Equals(revision, ClientTables.CacheKey))
            {
                string[] tags = ClientTables.Table("Tags").Select(r => r.Text("Id")!).ToArray();
                var effects = ClientTables.Table("ModEffectStats").ToArray();
                var next = new Dictionary<byte, Rule>();
                int index = 0;
                foreach (var row in ClientTables.Table("AlternateQualityTypes"))
                {
                    int at = index++;
                    if (!row.Bool("IsCatalyst") || row.IntOrNull("ModEffectStat") is not int effect || effect < 0 || effect >= effects.Length) continue;
                    var rule = effects[effect];
                    next.Add(checked((byte)at), new(rule.Ints("TagsKeys").Select(t => tags[t]).ToHashSet(StringComparer.Ordinal),
                        rule.Bool("ApplyToExplicit"), rule.Bool("ApplyToImplicit"), rule.Bool("ApplyToPrefix"), rule.Bool("ApplyToSuffix"), rule.Bool("ApplyToEnchant")));
                }
                rules = next; revision = ClientTables.CacheKey;
            }
            return rules.GetValueOrDefault(quality);
        }
    }
}
