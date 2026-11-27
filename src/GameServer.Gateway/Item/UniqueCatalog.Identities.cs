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
            || definition.VisualIdentityHash is not null
            || definition.CustomName is { Length: > 0 };

        string key = NameKey(definition.Name);
        if (!overlay && definition.VisualIdentityId is not { Length: > 0 })
            return _names.GetValueOrDefault(key);

        UniqueName? stock = _names.GetValueOrDefault(key);
        ushort? visual = definition.VisualIdentityHash is > 0 ? definition.VisualIdentityHash : null;
        if (visual is null && definition.VisualIdentityId is { Length: > 0 } visualId)
            visual = items.Visual(visualId) is > 0 and var v ? v : null;
        if (visual is null && stock is { VisualIdentity: > 0 })
            visual = stock.VisualIdentity;
        if (visual is null && overlay && (baseType ?? items.Find(definition.BaseTypeId)) is { VisualIdentity: > 0 } resolvedBase)
            visual = resolvedBase.VisualIdentity;
        if (visual is null or 0)
            return null;

        uint? word = definition.NameWordHash ?? stock?.Word;
        if (word is null && _uniqueWords.TryGetValue(key, out uint foundWord))
            word = foundWord;
        bool syntheticWord = false;
        if (word is null && overlay)
        {
            word = ItemCatalog.ComputeHash32(definition.Name);
            syntheticWord = true;
        }
        return word is null ? null : new(word.Value, visual.Value, syntheticWord);
    }
}
