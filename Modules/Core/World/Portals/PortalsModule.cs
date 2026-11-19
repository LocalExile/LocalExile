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
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async conn =>
        {
            await SpawnHideoutDoodads(conn);
            await SpawnMapReturnPortal(conn);
            await SpawnTownPortals(conn);
            // Everyone in this exact device instance sees and spends the same run's portals.
            if (MapDevice.AtEntrance(conn.Layout.Hash, conn.InstanceNumber) is { PortalsLeft: > 0 } open)
                await SpawnMapPortals(conn, open, firstAfterMs: MapPortalEntryMs);
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (conn, use, requestedAt) =>
        {
            if (conn.BackPortal is { } home && use.TargetId == home.Id && OpenPortals.TryGetValue(CharacterName(conn), out var origin))
            {
                if (!TryBeginObjectWalk(conn, requestedAt, home.X, home.Y, AreaTransitionObjectSize, out var homeWalk)) return true;
                var (homeAction, homeNow, _, _, homeArrive) = homeWalk;
                if (homeArrive > homeNow) await Task.Delay(TimeSpan.FromSeconds(homeArrive - homeNow));
                if (!PlayerActionCurrent(conn, homeAction)) return true;
                if (await ExpireTownPortal(conn, origin))
                {
                    if (!conn.Closed && !conn.LeavingArea && conn.BackPortal?.Id == home.Id)
                    {
                        conn.BackPortal = null;
                        await global::Game.SendWorldRemoval(conn, home.Id, "expired town portal");
                    }
                    return true;
                }
                if (PlayerActionCurrent(conn, homeAction) && OpenPortals.TryGetValue(CharacterName(conn), out var current) && current == origin)
                    await ChangeArea(conn, new PendingTransfer(origin.Area, origin.Instance, false, ArriveAt: (origin.X, origin.Y)),
                        "back through the town portal");
                return true;
            }
            if (conn.ReturnPortal is { } back && use.TargetId == back.Id)
            {
                await UseMapReturnPortal(conn, use, (back.X, back.Y), requestedAt);
                return true;
            }
            (int X, int Y, int Index)? portal;
            lock (conn.MapPortals) portal = conn.MapPortals.TryGetValue(use.TargetId, out var at) ? at : null;
            if (portal is { } p)
            {
                await UseMapPortal(conn, use, p, requestedAt);
                return true;
            }
            HideoutCatalog.Doodad? doodad;
            lock (conn.HideoutDoodads) doodad = conn.HideoutDoodads.GetValueOrDefault(use.TargetId);
            if (doodad is null) return false;
            await UseMapDevice(conn, use, doodad, requestedAt);
            return true;
        });
        // The Portal skill: a town portal, as the Portal Scroll's, once its cast (2.5 s) is over.
        GameEvents.SkillCast.Add(module.Manifest.Id, (conn, effect, skillId, from, current) =>
        {
            if (effect != "Portal") return null;
            if (!PortalAllowed(conn)) { Console.WriteLine($"[gateway] {conn.Remote} skill 0x{skillId:x4} Portal: not here"); return false; }
            var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
            if (CastMs(conn, gem.Effect) is not int castMs) return false;
            _ = Task.Delay(castMs).ContinueWith(async _ =>
            {
                if (current()) await OpenTownPortal(conn, from.X, from.Y, "Portal skill");
            });
            return true;
        });
        GameEvents.ItemUse.Add(module.Manifest.Id, async (conn, use, item, type) =>
        {
            if (type.Id != PortalScroll) return false;
            await UsePortalScroll(conn, use, item, type);
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.ActivateId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await ActivateMapDevice(conn, InstanceMapDevice.ReadActivate(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.SelectMapId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await SelectAtlasMap(conn, InstanceMapDevice.ReadSelectMap(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.MoveId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await MoveToMapDevice(conn, InstanceMapDevice.ReadMove(bytes));
            return true;
        });
        // Ctrl + click with the map device open, for the items that are not Atlas Idols: a map moves with one of these, and
        // without them the click reached no one and the map had to be dragged in.
        foreach (ushort clicked in new[] { InstanceMapDevice.MoveIntoDeviceId, InstanceMapDevice.MoveOutOfDeviceId })
            GameEvents.Packet(clicked).Add(module.Manifest.Id, async (conn, bytes) =>
            {
                var click = InstanceMapDevice.ReadItemClick(bytes);
                // Which way it goes is where the item is, so the message's own direction flag is not needed here
                // (ItemMoves.MoveToMapDevice says so): a click in the inventory takes it in, one in the slots takes it out.
                Console.WriteLine($"[gateway] {conn.Remote} <= 0x{clicked:x4} Ctrl + click item {click.ItemId} on the map device " +
                    $"({(click.IntoDevice ? "into" : "out")}, device 0x{click.Device:x})");
                await MoveToMapDevice(conn,
                    new InstanceMapDevice.Move(click.ItemId, click.Flags, click.IntoDevice, 0, 0));
                return true;
            });
    }
}
