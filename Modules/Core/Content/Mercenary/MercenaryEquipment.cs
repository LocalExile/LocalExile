using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Mercenary;

/// <summary>Rolled equipment retained with a mercenary, including the gear its wild version can drop.</summary>
public static class MercenaryEquipment
{
    public sealed record Worn(byte Slot, Item Item, VisibleItem Look);

    public static List<Worn> Roll(MercenaryTables.Class kind, MercenaryTables.Build build, int level, Random rng,
        bool allUnique, UniqueCatalog uniques)
    {
        var result = new List<Worn>();
        var looks = MercenaryGear.Of(kind, build, level, rng);
        ushort? arrow = Program.Items.Visual("DefaultArrow");
        foreach (var look in looks)
        {
            if (look.Slot == MercenaryGear.MainHand && look.VisualIdentity == arrow) continue;
            int row = look.Type is > 0 and < 255 ? look.Type - 1
                : Program.Items.All.FirstOrDefault(t => t.VisualIdentity == look.VisualIdentity)?.ClassRow ?? -1;
            var pool = Program.Items.All.Where(t => t.Supported && t.CanBeMirrored && t.ClassRow == row && t.DropLevel <= level
                && !t.Id.Contains("Royale", StringComparison.Ordinal) && !t.Id.Contains("Demigod", StringComparison.Ordinal)
                && !t.Id.Contains("Talisman", StringComparison.Ordinal)).OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
            if (pool.Length == 0) continue;
            Item item;
            if (allUnique)
            {
                var candidates = uniques.All.Where(d => !d.EncounterOnly && !d.Corrupted && !d.Mirrored
                    && Program.Items.Find(d.BaseTypeId) is { } t && pool.Any(p => p.Row == t.Row)).OrderBy(d => d.Name, StringComparer.Ordinal).ToArray();
                // Some client definitions have no supported unique identity. Try the remainder rather than silently making a rare.
                item = null!;
                while (candidates.Length > 0 && item is null)
                {
                    int pick = rng.Next(candidates.Length);
                    item = uniques.Create(candidates[pick], rng)!;
                    candidates = [.. candidates.Where((_, i) => i != pick)];
                }
                if (item is null) throw new InvalidOperationException($"No mercenary unique for equipment class {row}");
            }
            else item = Program.Rolls.Roll(ItemCatalog.Create(pool[rng.Next(pool.Length)]) with { ItemLevel = (byte)level }, ItemRarity.Rare, rng);
            var type = Program.Items.ByHash(item.TypeHash)!;
            var wornLook = look with { VisualIdentity = item.VisualIdentity };
            result.Add(new(look.Slot, item with { ItemLevel = (byte)level }, wornLook));
        }
        return result;
    }

    public static IReadOnlyList<VisibleItem> Looks(IReadOnlyList<Worn> equipment, IReadOnlyList<VisibleItem> fallback) =>
        [.. fallback.Select(l => equipment.FirstOrDefault(e => e.Slot == l.Slot)?.Look ?? l)];

    public static MonsterModSet Effects(IReadOnlyList<Worn> equipment, int difficulty = 0)
    {
        var stats = ItemModStats.Sum(equipment.Select(e => e.Item), Program.Items.Mods);
        stats[Stat.MaximumLifePlusPct] = stats.GetValueOrDefault(Stat.MaximumLifePlusPct) + difficulty;
        stats[Stat.DamagePlusPct] = stats.GetValueOrDefault(Stat.DamagePlusPct) + difficulty;
        return new([], [], new MonsterModEffects(stats));
    }
}
