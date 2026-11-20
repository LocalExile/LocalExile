using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Idols;

/// <summary>
/// Legacy of Phrecia's Idols: items with map modifiers that affect every map the character opens while they lie in
/// the Idol inventory. The 3.29.3 clients have no Idol panel (Agents/docs/game/idols.md), so the Idol inventory is the
/// map device's storage grid in the Atlas screen's side panel.
/// </summary>
public sealed class IdolsModule : IModule
{
    sealed record Choices(bool EnableAtlasTree, double DropRate);

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        var choices = module.Settings<Choices>();
        string id = module.Manifest.Id;
        module.Log($"Atlas passive tree {(choices.EnableAtlasTree ? "kept beside Idols" : "replaced by Idols")}, drop rate ×{choices.DropRate:0.##}");
        // The league's stat: with it the client hides the Atlas passive tree. Nothing else the Idols need depends on it.
        if (!choices.EnableAtlasTree)
            InstanceEvents.AreaStats.Add(id, _ =>
                Stats.Key(IdolRules.LeagueStat) is uint key ? new Dictionary<uint, int> { [key] = 1 } : null);
        GameEvents.InventorySlot.Add(id, (_, slot) =>
            slot.Row == IdolRules.Row ? slot with { Kind = IdolRules.PanelKind } : null);
        GameEvents.InventoryPlacementRefusal.Add(id, p =>
            p.Row != IdolRules.Row ? null
            : !IdolRules.IsIdol(ItemsFor(p.Conn).ByHash(p.Item.TypeHash)) ? "only Idols go in the Idol inventory"
            : IdolRules.LimitRefusal(ItemsOf(p.Conn), p.Item, p.Replaced));
        GameEvents.MapOpening.Add(id, request =>
        {
            if (request.Campaign || request.Opening is not { } opening) return;
            var conn = request.Conn;
            var idols = IdolRules.Placed(ItemsOf(conn), ItemsFor(conn));
            if (idols.Count == 0) return;
            var stats = ItemModStats.Sum(idols, ItemsFor(conn).Mods);
            request.Opening = opening with { Mods = IdolRules.With(opening.Mods, stats) };
            Console.WriteLine($"[gateway] {conn.Wire.Remote} {CharacterName(conn)}: {idols.Count} Idol(s) add {stats.Count} stat(s) to {opening.Area.Name}");
        });
        // Faustus's gamble window has an Idols tab.
        GameEvents.Gambles.Add(id, window =>
        {
            if (!IdolRules.GamblesIdols(window.Npc)) return;
            var conn = window.Conn;
            foreach (var (shown, idol, gold) in IdolRules.GambleOffers(ItemsFor(conn), window.CharacterLevel, window.PriceLevel))
                window.Offers.Add(new(shown, gold,
                    random => IdolRules.Gamble(ItemsFor(conn), RollsFor(conn), UniquesFor(conn), idol, window.CharacterLevel, random), "Idols"));
        });
        if (choices.DropRate > 0)
            GameEvents.Drops.Add(id, roll =>
            {
                var conn = roll.Conn;
                if (roll.Cancel || MapRunOf(conn) is not { Atlas: true } || MapModsOf(conn) is not { } mapMods) return;
                double rate = choices.DropRate * mapMods.QuantityFactor * Math.Max(0, roll.Quantity);
                if (rate > 0 && IdolRules.Drop(ItemsFor(conn), RollsFor(conn), UniquesFor(conn), roll.Monster.Level, roll.Monster.Rarity, rate, Random.Shared) is { } idol)
                    roll.Made.Add(idol);
            });
    }
}
