using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using static Program;
using static PortalsFeature;

namespace GameServer.Modules.M_portals;

/// <summary>Town portals, Portal Scrolls, the map device and map portals.</summary>
public sealed class PortalsModule : IModule
{
    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        global::PortalsRules.LoadRulesSettingsData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        // The portals a player can take are their own view (each keeps its own portal state): theirs alone to see.
        GameEvents.PlayerEntered.Add(module.Manifest.Id, async player =>
        {
            Program.ViewOf.Value = player;
            try
            {
                await SpawnHideoutDoodads(player);
                await SpawnMapReturnPortal(player);
                await SpawnTownPortals(player);
                // A hideout's instance is gone six minutes after its last player leaves, and its device with it: a run whose
                // portals stood there follows the character into the instance they enter now, so the portals come up below.
                await PortalsRules.FollowDeviceToThisInstance(player);
                // Everyone in this exact device instance sees and spends the same run's portals.
                if (MapDevice.AtEntrance(player.Layout.Hash, player.InstanceNumber) is { PortalsLeft: > 0 } open)
                    await SpawnMapPortals(player, open, firstAfterMs: MapPortalEntryMs, standing: true);
            }
            finally { Program.ViewOf.Value = null; }
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (player, use, requestedAt) =>
        {
            if (player.BackPortal is { } home && use.TargetId == home.Id && OpenPortals.TryGetValue(CharacterName(player), out var origin))
            {
                if (!TryBeginObjectWalk(player, requestedAt, home.X, home.Y, AreaTransitionObjectSize, out var homeWalk)) return true;
                var (homeAction, homeNow, _, _, homeArrive) = homeWalk;
                if (homeArrive > homeNow) await AreaDelay(player, homeArrive - homeNow);
                if (!PlayerActionCurrent(player, homeAction)) return true;
                if (await ExpireTownPortal(player, origin))
                {
                    if (!player.Closed && !player.LeavingArea && player.BackPortal?.Id == home.Id)
                    {
                        player.BackPortal = null;
                        await global::Game.SendWorldRemoval(player, home.Id, "expired town portal");
                    }
                    return true;
                }
                if (PlayerActionCurrent(player, homeAction) && OpenPortals.TryGetValue(CharacterName(player), out var current) && current == origin)
                    await ChangeArea(player, new PendingTransfer(origin.Area, origin.Instance, false, ArriveAt: (origin.X, origin.Y)),
                        "back through the town portal");
                return true;
            }
            if (player.ReturnPortal is { } back && use.TargetId == back.Id)
            {
                await UseMapReturnPortal(player, use, (back.X, back.Y), requestedAt);
                return true;
            }
            (int X, int Y, int Index)? portal;
            lock (player.MapPortals) portal = player.MapPortals.TryGetValue(use.TargetId, out var at) ? at : null;
            if (portal is { } p)
            {
                await UseMapPortal(player, use, p, requestedAt);
                return true;
            }
            HideoutCatalog.Doodad? doodad;
            lock (player.HideoutDoodads) doodad = player.HideoutDoodads.GetValueOrDefault(use.TargetId);
            if (doodad is null) return false;
            await UseMapDevice(player, use, doodad, requestedAt);
            return true;
        });
        // The Portal skill: a town portal, as the Portal Scroll's, once its cast (2.5 s) is over.
        GameEvents.SkillCast.Add(module.Manifest.Id, (player, effect, skillId, from, current) =>
        {
            if (effect != "Portal") return null;
            if (!PortalAllowed(player)) { Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} Portal: not here"); return false; }
            var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
            if (CastMs(player, gem.Effect) is not int castMs) return false;
            _ = AreaFrames.Start(async () =>
            {
                await AreaDelay(player, castMs / 1000.0);
                if (current()) await OpenTownPortal(player, from.X, from.Y, "Portal skill");
            });
            return true;
        });
        GameEvents.ItemUse.Add(module.Manifest.Id, async (player, use, item, type) =>
        {
            if (type.Id != PortalScroll) return false;
            await UsePortalScroll(player, use, item, type);
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.ActivateId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await ActivateMapDevice(player, InstanceMapDevice.ReadActivate(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.SelectMapId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await SelectAtlasMap(player, InstanceMapDevice.ReadSelectMap(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.MoveId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await MoveToMapDevice(player, InstanceMapDevice.ReadMove(bytes));
            return true;
        });
        // Ctrl + click with the map device open, for the items that are not Atlas Idols: a map moves with one of these, and
        // without them the click reached no one and the map had to be dragged in.
        foreach (ushort clicked in new[] { InstanceMapDevice.MoveIntoDeviceId, InstanceMapDevice.MoveOutOfDeviceId })
            GameEvents.Packet(clicked).Add(module.Manifest.Id, async (player, bytes) =>
            {
                var click = InstanceMapDevice.ReadItemClick(bytes);
                // Which way it goes is where the item is, so the message's own direction flag is not needed here
                // (ItemMoves.MoveToMapDevice says so): a click in the inventory takes it in, one in the slots takes it out.
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x{clicked:x4} Ctrl + click item {click.ItemId} on the map device " +
                    $"({(click.IntoDevice ? "into" : "out")}, device 0x{click.Device:x})");
                await MoveToMapDevice(player,
                    new InstanceMapDevice.Move(click.ItemId, click.Flags, click.IntoDevice, 0, 0));
                return true;
            });
    }
}
