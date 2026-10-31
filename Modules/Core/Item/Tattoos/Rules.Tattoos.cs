using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class TattoosRules
{
    /// <summary>One tattoo per client limit group (Loyalty, Attribute Notable, Ancestral, Keystone, Ascendancy),
    /// apart from the global 50 cap. The same group on the same node is a replacement and is allowed.</summary>
    internal static string? TattooLimitRefusal(IReadOnlyDictionary<ushort, string> tattoos, ushort node,
        TattooCatalogue.Entry entry, LimitSettings? limits = null)
    {
        if (entry.Limit < 0 || TattooCatalogue.LimitName(entry.Limit) is not { } group) return null;
        int limit = (limits ?? LimitSettings.Default).Get($"tattoo:{group}", SystemSettings.Default.TattooGroupLimit);
        if (limit == 0) return null;
        int count = 0;
        foreach (var (other, name) in tattoos)
            if (other != node && TattooCatalogue.Find(name) is { } existing && existing.Limit == entry.Limit)
                count++;
        return count < limit ? null
            : limit == 1 ? $"only one {group} can be applied" : $"only {limit} {group} can be applied";
    }

    internal static bool TargetMatches(PassiveTree.Node node, string target)
    {
        bool Has(string stat, int value) => node.Stats.Any(s => Stats.Name(s.Key) == stat && s.Value == value);
        bool small = !node.IsNotable && !node.IsKeystone && !node.IsAscendancy && !node.IsStart
            && !node.IsJewelSocket && !node.IsMastery && !node.IsProxy && !node.IsExpansion;
        return target switch
        {
            "Small Strength" => small && Has("base_strength", 10),
            "Small Dexterity" => small && Has("base_dexterity", 10),
            "Small Intelligence" => small && Has("base_intelligence", 10),
            "Small Attribute" => small && (Has("base_strength", 10) || Has("base_dexterity", 10) || Has("base_intelligence", 10)),
            "Notable (+30 to Strength)" => node.IsNotable && Has("base_strength", 30),
            "Notable (+30 to Dexterity)" => node.IsNotable && Has("base_dexterity", 30),
            "Notable (+30 to Intelligence)" => node.IsNotable && Has("base_intelligence", 30),
            "Keystone" => node.IsKeystone,
            "Mastery" => node.IsMastery,
            "Ascendancy Notable" => node.IsAscendancy,
            _ => false,
        };
    }
}
