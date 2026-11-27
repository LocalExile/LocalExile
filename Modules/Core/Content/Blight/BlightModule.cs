using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Blight;

/// <summary>Blight's pump defence (<see cref="BlightEncounter"/>), Sister Cassia, anointing, Oil Extractor, and Blight unique spores.</summary>
public sealed class BlightModule : IModule
{
    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>This module's game data, built when it loads, before the Gateway listens. Tests that use the module call it once.</summary>
    public static void LoadData() => BlightTables.Load();

    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        var encounter = new BlightEncounter();
        module.Lifetime.Own(new Cleanup(() =>
        {
            encounter.Runs.Clear();
            BlightTowers.ClearAllImbuements();
            BlightEncounter.ClearBlightreach();
            BlightSpores.ClearAll();
        }));
        GameEvents.MapOpening.Add(id, BlightEncounter.OnMapOpening);
        GameEvents.HideoutReady.Add(id, BlightCassia.SpawnInHideout);
        GameEvents.NpcTalk.Add(id, BlightCassia.OnNpcTalk);
        GameEvents.NpcOption.Add(id, BlightCassia.OnNpcOption);
        GameEvents.PlayerEntered.Add(id, encounter.Enter);
        // Pump defence tick only runs in maps or areas with a BlightPump; spore/fungal tick skips in O(1) when unused.
        GameEvents.AreaTick.Add(id, start =>
        {
            bool mayDefend = encounter.MayHaveDefence(start);
            return async (player, dt) =>
            {
                if (mayDefend) await encounter.Step(player);
                await BlightSpores.Tick(player, dt);
            };
        });
        GameEvents.ObjectUse.Add(id, encounter.Use);
        GameEvents.Packet(InstanceBlight.TowerBuildId).Add(id, (player, bytes) => encounter.Build(player, bytes));
        GameEvents.Packet(InstanceBlight.FastForwardId).Add(id, (player, bytes) => encounter.FastForward(player, bytes));
        GameEvents.Packet(InstanceBlight.AnointId).Add(id, BlightAnointing.HandleAnointPacket);
        GameEvents.Packet(InstanceItems.UseItemOnId).Add(id, BlightAnointing.HandleOilExtractorPacket);
        GameEvents.InventoryPlacementRefusal.Add(id, BlightAnointing.ValidateCraftingPlacement);
        GameEvents.Kill.Add(id, kill =>
        {
            BlightSpores.OnKill(kill);
            kill.Later(() => encounter.Killed(kill));
        });
        GameEvents.OutgoingDamage.Add(id, encounter.OnOutgoingDamage);
        GameEvents.Drops.Add(id, encounter.OnDrops);
        GameEvents.FinalStatsBuilding.Add(id, (player, stats) =>
        {
            BlightTowers.ApplyPlayerImbuement(player, stats);
            BlightSpores.ApplyStats(player, stats);
        });
        GameEvents.PlayerLeaving.Add(id, async (player, _) =>
        {
            await BlightTowers.ClearPlayerImbuement(player);
            await BlightSpores.ClearPlayer(player);
        });
    }
}
