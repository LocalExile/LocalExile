using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Ritual;

/// <summary>
/// Ritual as a LocalExile module: campaign &amp; map Ritual altars, Favour reward window (buy/defer/reroll),
/// Ritual Vessel / Blood-filled Vessel capture and Map Device injection, Scarabs, Atlas passives, and Omens.
/// </summary>
public sealed class RitualModule : IModule
{
    readonly RitualEncounter _encounter = new();

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>This module's game data, built when it loads: before the Gateway listens.</summary>
    public static void LoadData() => RitualTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(() =>
        {
            _encounter.Clear();
            RitualFavours.Clear();
            RitualOmens.Clear();
        }));

        // Encounter lifecycle & area integration (campaign + maps).
        GameEvents.AreaPopulated.Add(id, async area =>
        {
            if (_encounter.Populate(area) is { } why) module.Log($"Ritual: none in {area.Player.Layout.Id} ({why})");
            else await _encounter.Placed(area.Player);
        });
        GameEvents.EnterMessages.Add(id, _encounter.EnterMessages);
        GameEvents.PlayerEntered.Add(id, _encounter.Entered);
        GameEvents.AreaQuestFlags.Add(id, _encounter.QuestFlags);
        GameEvents.AreaTick.Add(id, start => !InTown(start) && HideoutCatalog.ByArea(start.Layout.Hash) is null
            ? async (player, seconds) =>
            {
                if (_encounter.Of(player) is not null) await _encounter.Advance(player, seconds);
                await RitualOmens.Tick(player);
            }
            : null);
        GameEvents.ObjectUse.Add(id, _encounter.OnUse);
        GameEvents.MapOpening.Add(id, _encounter.OnMapOpening);
        GameEvents.Stats.Add(id, sheet =>
        {
            _encounter.ApplyStats(sheet);
            RitualOmens.ApplyStats(sheet);
        });

        // Favour window C→S packets (0x0169 open, 0x0168 reroll, 0x0283 buy, 0x0284 defer).
        GameEvents.Packet(RitualWire.OpenFavoursId).Add(id, (player, _) => RitualFavours.HandleOpen(player, _encounter));
        GameEvents.Packet(RitualWire.RerollFavoursId).Add(id, (player, _) => RitualFavours.HandleReroll(player, _encounter));
        GameEvents.Packet(RitualWire.BuyFavourId).Add(id, (player, msg) => RitualFavours.HandleBuy(player, msg, _encounter));
        GameEvents.Packet(RitualWire.DeferFavourId).Add(id, (player, msg) => RitualFavours.HandleDefer(player, msg, _encounter));

        // Ritual Vessel bottling on completed altar (0x0074 / 0x0079).
        GameEvents.Packet(InstanceItems.UseItemOnObjectId).Add(id, _encounter.OnUseItemOnObject);
        GameEvents.Packet(InstanceItems.UseItemTypeOnObjectId).Add(id, _encounter.OnUseItemTypeOnObject);

        // Ritual Omens (crafting, death, low life, level up).
        GameEvents.CurrencyCrafting.Add(id, RitualOmens.OnCurrencyCrafting);
        GameEvents.DeathPenaltyPct.Add(id, RitualOmens.OnDeathPenaltyPct);
        GameEvents.PlayerDied.Add(id, RitualOmens.OnPlayerDied);
        GameEvents.PlayerLevelUp.Add(id, RitualOmens.OnLevelUp);
        module.Lifetime.Own(MonsterModEffects.HandOverMore([], [], [Stat.DamageTakenPlusPctFinalFromAncestralOmen]));
    }
}
