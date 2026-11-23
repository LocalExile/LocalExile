using GameServer.Core.Protocol.Instance;

/// <summary>Snapshot rules before mutation: removing the protecting craft must not unlock its affixes mid-operation.</summary>
public sealed class MetacraftRules(Item item, ModCatalog catalog)
{
    bool Has(uint stat) => item.Mods.Where(m => m.Section == ModSection.Explicit).Any(m =>
        catalog.ByHash(m.Hash)?.Stats.Select((s, i) => s.Id == stat && i < m.Values.Count && m.Values[i] > 0).Any(v => v) == true);

    public bool Protected(int index) => item.FracturedIndexes().Contains(index) || catalog.ByHash(item.Mods[index].Hash) is { } mod &&
        (mod.GenerationType == ItemRolls.Prefix && Has(Stat.ItemGenerationCannotChangePrefixes) ||
         mod.GenerationType == ItemRolls.Suffix && Has(Stat.ItemGenerationCannotChangeSuffixes));

    public bool Allows(ModCatalog.Mod mod) =>
        !(Has(Stat.ItemGenerationCannotRollAttackAffixes) && mod.Tags.Contains("attack")) &&
        !(Has(Stat.ItemGenerationCannotRollCasterAffixes) && mod.Tags.Contains("caster"));
}
