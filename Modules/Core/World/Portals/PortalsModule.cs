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
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async conn =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await SpawnHideoutDoodads(remote, stream, ciphers, conn);
            await SpawnMapReturnPortal(remote, stream, ciphers, conn);
            await SpawnTownPortals(remote, stream, ciphers, conn);
            // A hideout's instance is gone six minutes after its last player leaves, and its device with it: a run whose
            // portals stood there follows the character into the instance they enter now, so the portals come up below.
            await PortalsRules.FollowDeviceToThisInstance(remote, conn);
            // Everyone in this exact device instance sees and spends the same run's portals.
            if (MapDevice.AtEntrance(conn.Layout.Hash, conn.InstanceNumber) is { PortalsLeft: > 0 } open)
                await SpawnMapPortals(remote, stream, ciphers, conn, open, firstAfterMs: MapPortalEntryMs, standing: true);
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (conn, use, requestedAt) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
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
                        await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, home.Id, "expired town portal");
                    }
                    return true;
                }
                if (PlayerActionCurrent(conn, homeAction) && OpenPortals.TryGetValue(CharacterName(conn), out var current) && current == origin)
                    await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(origin.Area, origin.Instance, false, ArriveAt: (origin.X, origin.Y)),
                        "back through the town portal");
                return true;
            }
            if (conn.ReturnPortal is { } back && use.TargetId == back.Id)
            {
                await UseMapReturnPortal(remote, stream, ciphers, conn, use, (back.X, back.Y), requestedAt);
                return true;
            }
            (int X, int Y, int Index)? portal;
            lock (conn.MapPortals) portal = conn.MapPortals.TryGetValue(use.TargetId, out var at) ? at : null;
            if (portal is { } p)
            {
                await UseMapPortal(remote, stream, ciphers, conn, use, p, requestedAt);
                return true;
            }
            HideoutCatalog.Doodad? doodad;
            lock (conn.HideoutDoodads) doodad = conn.HideoutDoodads.GetValueOrDefault(use.TargetId);
            if (doodad is null) return false;
            await UseMapDevice(remote, stream, ciphers, conn, use, doodad, requestedAt);
            return true;
        });
        // The Portal skill: a town portal, as the Portal Scroll's, once its cast (2.5 s) is over.
        GameEvents.SkillCast.Add(module.Manifest.Id, (conn, effect, skillId, from, current) =>
        {
            if (effect != "Portal") return null;
            var (remote, stream, ciphers) = conn.Wire;
            if (!PortalAllowed(conn)) { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} Portal: not here"); return false; }
            var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
            if (CastMs(conn, gem.Effect) is not int castMs) return false;
            _ = Task.Delay(castMs).ContinueWith(async _ =>
            {
                if (current()) await OpenTownPortal(remote, stream, ciphers, conn, from.X, from.Y, "Portal skill");
            });
            return true;
        });
        GameEvents.ItemUse.Add(module.Manifest.Id, async (conn, use, item, type) =>
        {
            if (type.Id != PortalScroll) return false;
            var (remote, stream, ciphers) = conn.Wire;
            await UsePortalScroll(remote, stream, ciphers, conn, use, item, type);
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.ActivateId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await ActivateMapDevice(remote, stream, ciphers, conn, InstanceMapDevice.ReadActivate(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.SelectMapId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await SelectAtlasMap(remote, stream, ciphers, conn, InstanceMapDevice.ReadSelectMap(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.MoveId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await MoveToMapDevice(remote, stream, ciphers, conn, InstanceMapDevice.ReadMove(bytes));
            return true;
        });
        // Ctrl + click with the map device open, for the items that are not Atlas Idols: a map moves with one of these, and
        // without them the click reached no one and the map had to be dragged in.
        foreach (ushort clicked in new[] { InstanceMapDevice.MoveIntoDeviceId, InstanceMapDevice.MoveOutOfDeviceId })
            GameEvents.Packet(clicked).Add(module.Manifest.Id, async (conn, bytes) =>
            {
                var (remote, stream, ciphers) = conn.Wire;
                var click = InstanceMapDevice.ReadItemClick(bytes);
                // Which way it goes is where the item is, so the message's own direction flag is not needed here
                // (ItemMoves.MoveToMapDevice says so): a click in the inventory takes it in, one in the slots takes it out.
                Console.WriteLine($"[gateway] {remote} <= 0x{clicked:x4} Ctrl + click item {click.ItemId} on the map device " +
                    $"({(click.IntoDevice ? "into" : "out")}, device 0x{click.Device:x})");
                await MoveToMapDevice(remote, stream, ciphers, conn,
                    new InstanceMapDevice.Move(click.ItemId, click.Flags, click.IntoDevice, 0, 0));
                return true;
            });
    }
}
