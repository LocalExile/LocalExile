static partial class TattoosRules
{
    internal static string? TattooLimitRefusal(IReadOnlyDictionary<ushort, string> tattoos, ushort node,
        TattooCatalogue.Entry entry, LimitSettings? limits = null) => TattooCatalogue.TattooLimitRefusal(tattoos, node, entry, limits);
    internal static bool TargetMatches(PassiveTree.Node node, string target) => TattooCatalogue.TargetMatches(node, target);
}
