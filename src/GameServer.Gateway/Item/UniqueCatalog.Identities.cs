using GameServer.Core;

public sealed partial class UniqueCatalog
{
    public sealed partial record Definition
    {
        public string? VisualIdentityId { get; init; }
    }

    UniqueName? Identity(Definition definition)
    {
        if (definition.VisualIdentityId is not { Length: > 0 } visualId)
            return _names.Value.GetValueOrDefault(NameKey(definition.Name));
        if (items.Visual(visualId) is not ushort visual || !ClientTables.Has("Words")) return null;
        uint? word = ClientTables.Table("Words").Where(r => r.Int("Wordlist") == 6
            && NameKey(r.Text("Text") ?? "") == NameKey(definition.Name)).Select(r => (uint?)r.UInt("HASH32")).FirstOrDefault();
        return word is null ? null : new(word.Value, visual);
    }
}
