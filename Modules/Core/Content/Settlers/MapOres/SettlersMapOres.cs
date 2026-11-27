using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Settlers.MapOres;

/// <summary>
/// Single registration entry point for all Map-Side Settlers systems:
/// Ore Deposits (Crimson Iron, Orichalcum, Petrified Amber, Bismuth, Verisium, Thaumaturgic Dust Hourglass),
/// Lost Shipments, Starfall Crater entrance, Kalguuran Scarabs (1..4), Atlas Passives (incl. Refiner's Bargain),
/// and the Prospecting Astrolabe.
/// </summary>
public static class SettlersMapOres
{
    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    public static MapOreEncounter Register(IModuleContext module, IVillageOreSink? sink = null, Func<double>? oreFoundMultiplier = null)
    {
        MapOreTables.LoadGameData();
        ProspectingAstrolabe.LoadGameData();

        var encounter = new MapOreEncounter();
        var astrolabe = new ProspectingAstrolabe();
        string id = module.Manifest.Id;

        if (sink is not null)
            module.Lifetime.Own(VillageResourceHooks.RegisterSink(sink, oreFoundMultiplier));
        else if (oreFoundMultiplier is not null)
            module.Lifetime.Own(VillageResourceHooks.RegisterOreFoundMultiplier(oreFoundMultiplier));

        module.Lifetime.Own(VillageResourceHooks.RegisterRefinersBargain(
            SettlersRefinersBargain.InterceptSulphite,
            SettlersRefinersBargain.GrantHarvestCrop,
            SettlersRefinersBargain.ConvertPossessedDrops));

        module.Lifetime.Own(new Cleanup(encounter.Clear));
        module.Lifetime.Own(RegisterDeliriumPauseSource(encounter.IsActivePause));

        // Map opening: claim all 4 Kalguuran Scarabs + apply Prospecting Astrolabe Shaped Region.
        GameEvents.MapDeviceAcceptsItem.Add(id, astrolabe.MapDeviceAccepts);
        GameEvents.MapOpening.Add(id, request =>
        {
            encounter.OnMapOpening(request);
            astrolabe.OnMapOpening(request);
        });

        // Area lifecycle & encounter ticks.
        GameEvents.AreaPopulated.Add(id, area =>
        {
            if (encounter.Populate(area) is { } why)
                module.Log($"Settlers MapOres: none in {area.Player.Layout.Id} ({why})");
            return Task.CompletedTask;
        });
        GameEvents.AreaGenerated.Add(id, encounter.PresentAll);
        GameEvents.PlayerEntered.Add(id, encounter.PresentAll);
        GameEvents.PlayerLeaving.Add(id, (player, _) => encounter.OnPlayerLeaving(player));
        GameEvents.AreaTick.Add(id, start =>
            !InTown(start) && HideoutCatalog.ByArea(start.Layout.Hash) is null
                ? (player, seconds) => encounter.Tick(player, seconds)
                : null);

        // Object clicks, warcry chest opens, kills, drops, stats, damage shield.
        GameEvents.ObjectUse.Add(id, encounter.OnUse);
        GameEvents.ChestsOpened.Add(id, encounter.OnChestsOpened);
        GameEvents.Kill.Add(id, encounter.OnKill);
        GameEvents.MonsterKilled.Add(id, astrolabe.OnMonsterKilled);
        GameEvents.Drops.Add(id, roll =>
        {
            encounter.OnDrops(roll);
            SettlersRefinersBargain.OnDrops(roll);
        });
        GameEvents.Stats.Add(id, encounter.OnStats);
        GameEvents.IncomingDamage.Add(id, encounter.OnIncomingDamage);

        // Prospecting Astrolabe Atlas node application (0x0075 / 0x0076).
        GameEvents.Packet(InstanceMapDevice.UseItemOnAtlasNodeId).Add(id, astrolabe.OnUseItemOnNode);
        GameEvents.Packet(InstanceMapDevice.UseItemTypeOnAtlasNodeId).Add(id, astrolabe.OnUseItemTypeOnNode);

        return encounter;
    }
}
