using GameServer.Core;

public sealed partial class UniqueCatalog
{
    public sealed partial record Definition
    {
        public string? VisualIdentityId { get; init; }
    }

    UniqueName? Identity(Definition definition, ItemCatalog.BaseType? baseType = null)
    {
        bool overlay = IsOverlayUnique(definition)
            || definition.NameWordHash is not null
            || definition.VisualIdentityHash is not null;

        if (!overlay && definition.VisualIdentityId is not { Length: > 0 })
            return _names.GetValueOrDefault(NameKey(definition.Name));

        UniqueName? stock = _names.GetValueOrDefault(NameKey(definition.Name));
        ushort? visual = definition.VisualIdentityHash;
        if (visual is null && definition.VisualIdentityId is { Length: > 0 } visualId)
            visual = items.Visual(visualId);
        if (visual is null)
            visual = stock?.VisualIdentity;
        if (visual is null && overlay)
            visual = (baseType ?? items.Find(definition.BaseTypeId))?.VisualIdentity;
        if (visual is null)
            return null;

        uint? word = definition.NameWordHash ?? stock?.Word;
        if (word is null && ClientTables.Has("Words"))
            word = ClientTables.Table("Words").Where(r => r.Int("Wordlist") == 6
                && NameKey(r.Text("Text") ?? "") == NameKey(definition.Name)).Select(r => (uint?)r.UInt("HASH32")).FirstOrDefault();
        if (word is null && overlay)
            word = ItemCatalog.ComputeHash32(definition.Name);
        return word is null ? null : new(word.Value, visual.Value);
    }
}
