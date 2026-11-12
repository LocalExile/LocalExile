using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Delve;

public sealed partial class DelveModule
{
    const string StartCircleModel = "Metadata/Effects/Spells/monsters_effects/labyrinth/skeleton_king/circles_of_virtue/aoe_circle.ao";
    const string CompletionModel = "Metadata/Terrain/Doodads/Leagues/Delve/DelveWaypointObject_paua.ao";

    static async Task ResetPresentation(IPlayer player, State state)
    {
        // Crawler ids are stable across module reloads; replace these effects instead of accumulating them.
        foreach (uint offset in new uint[] { 6, 7, 8 }) await RemoveSceneObject(player, state.Crawler + offset);
        state.StartTrigger = state.DestinationLight = state.CompletionEffect = 0;
    }

    static async Task ShowStartBoundary(IPlayer player, State state, (int X, int Y) at)
    {
        await RemoveStartBoundary(player, state);
        uint id = state.Crawler + 6;
        state.StartTrigger = id;
        // The native mesh's radius is 509.017 world units; match the server boundary in grid cells.
        var blob = new EffectObject(at.X, at.Y, StartCircleModel)
            { Scale = DelveJourney.StartRadius * (250f / 23) / 509.017f }.Blob();
        await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(EffectObject.TypePath), blob), "Delve starting circle: waiting for player to leave");
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id));
    }

    static async Task RemoveStartBoundary(IPlayer player, State state)
    {
        if (state.StartTrigger == 0) return;
        uint id = state.StartTrigger;
        state.StartTrigger = 0;
        await RemoveSceneObject(player, id);
    }

    static async Task EnsureDestinationLight(IPlayer player, State state, (int X, int Y) at)
    {
        if (state.DestinationLight != 0) return;
        uint id = state.Crawler + 7;
        state.DestinationLight = id;
        await RemoveSceneObject(player, id);
        // One ordinary path lamp fills the parked cart's immediate shadow without flooding the arena.
        await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(DelveTrailLightObject.MetadataPath), new DelveTrailLightObject(at.X, at.Y).Blob()),
            "Permanent completed Delve checkpoint light");
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id));
    }

    static async Task PlayCompletionEffect(IPlayer player, State state, (int X, int Y) at)
    {
        if (state.CompletionEffect != 0) return;
        uint id = state.Crawler + 8;
        state.CompletionEffect = id;
        await RemoveSceneObject(player, id);
        await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(at.X, at.Y, CompletionModel).Blob()),
            "Delve checkpoint activation effect");
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id));
        // Native Waypoint_rig clip 0, activate (966 ms), plays Audio/.../RobotCart/End once at time zero.
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 1), "Delve completion sound: RobotCart/End");
    }
}
