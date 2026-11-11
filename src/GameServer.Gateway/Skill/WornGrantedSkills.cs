using GameServer.Core.Protocol.Instance;

/// <summary>
/// Active effects granted by mods on worn items. The mod's GrantedEffectsPerLevelKeys name exact rows; those rows,
/// not translated item text, supply the effect, level and mode identity. Supports are retained for the support
/// resolver, while <see cref="For"/> exposes every grant so later trigger, minion, aspect and temporary-state owners
/// can use the same source identity without placing a non-executable skill in the player's Actor.
/// </summary>
public sealed class WornGrantedSkills(SkillCatalog skills, ModCatalog mods, bool ruthless = false, string folder = "data")
{
    /// <summary>
    /// One item-granted effect source. <paramref name="Socket"/> is a negative synthetic host slot, so an active gem
    /// in the same item, another grant on the item and the same effect on another host cannot collide. It remains
    /// stable while the host item and its mod order remain present.
    /// </summary>
    public readonly record struct Grant(uint Host, int Socket, int ModRow, string ModId, int EffectsPerLevelRow,
        int Effect, string EffectId, int Level, bool ManuallyCast, bool Support, bool Ruthless)
    {
        public (uint Host, int Socket, int Effect) Identity => (Host, Socket, Effect);
    }

    readonly record struct Template(int ModRow, string ModId, int EffectsPerLevelRow, int Effect,
        string EffectId, int Level, bool ManuallyCast, bool Support);

    readonly Lazy<Dictionary<ushort, Template[]>> _byHash = new(() => Load(skills, mods, ruthless, folder));

    /// <summary>
    /// Every active and support grant on these equipped hosts, in equipment then mod order. The caller decides which
    /// sources are active and executable; keeping all of them here preserves their identities while an item is inactive.
    /// </summary>
    public IReadOnlyList<Grant> For(IReadOnlyList<(byte Row, InventoryItem Item)> equipment)
    {
        var result = new List<Grant>();
        foreach (var (_, item) in equipment.OrderBy(e => e.Row))
        {
            int sourceSlot = 0;
            for (int modIndex = 0; modIndex < item.Item.Mods.Count; modIndex++)
            {
                if (!_byHash.Value.TryGetValue(item.Item.Mods[modIndex].Hash, out var templates)) continue;
                foreach (var template in templates)
                {
                    // One synthetic socket per host/GEPL occurrence. It is unaffected by another host, active gem
                    // socket or passive node and is unique even if an item carries the same mod twice.
                    int socket = -1 - sourceSlot++;
                    result.Add(new(item.Id, socket, template.ModRow, template.ModId, template.EffectsPerLevelRow,
                        template.Effect, template.EffectId, template.Level, template.ManuallyCast, template.Support, ruthless));
                }
            }
        }
        return result;
    }

    /// <summary>Whether this exact host/mod/GEPL source is still on the supplied equipment snapshot.</summary>
    public bool StillWorn(Grant grant, IReadOnlyList<(byte Row, InventoryItem Item)> equipment) =>
        For(equipment).Any(current => current.Identity == grant.Identity
            && current.EffectsPerLevelRow == grant.EffectsPerLevelRow);

    static Dictionary<ushort, Template[]> Load(SkillCatalog skills, ModCatalog mods, bool ruthless, string folder)
    {
        var result = new Dictionary<ushort, Template[]>();
        foreach (var mod in mods.All)
        {
            var rows = ruthless && mod.RuthlessGrantedEffectLevels.Count > 0
                ? mod.RuthlessGrantedEffectLevels : mod.GrantedEffectLevels;
            if (rows.Count == 0) continue;
            var grants = new List<Template>();
            foreach (int row in rows)
            {
                // Mods reference exact GEPL rows. HardMode uses GrantedEffectRuthless when it is nonempty and otherwise
                // preserves the normal rows; GemEffects' regular-to-HardMode map resolves gem names, not item mods.
                if (skills.EffectsPerLevel(row) is not { } level || skills.EffectId(level.Effect) is not { } effectId)
                    continue;
                grants.Add(new(mod.Row, mod.Id, row, level.Effect, effectId, level.Level,
                    skills.IsManuallyCasted(level.Effect), skills.IsSupportEffect(level.Effect)));
            }
            if (grants.Count > 0) result.TryAdd(mod.Hash, [.. grants]);
        }
        return result;
    }
}
