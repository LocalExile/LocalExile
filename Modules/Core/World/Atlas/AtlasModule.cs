using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>The Atlas passive tree's choices.</summary>
public sealed class AtlasModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        module.OnPacket(InstanceEntities.AtlasAllocateId, async p =>
        {
            var conn = ConnOf(p.Player);
            var (preset, node) = InstanceEntities.ReadAtlasAllocate(p.Bytes.ToArray());
            await AtlasFeature.ChangeAtlasPassive(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, preset, node, allocate: true);
            return true;
        });
        module.OnPacket(InstanceEntities.AtlasRefundId, async p =>
        {
            var conn = ConnOf(p.Player);
            var (preset, node, _) = InstanceEntities.ReadAtlasRefund(p.Bytes.ToArray());
            await AtlasFeature.ChangeAtlasPassive(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, preset, node, allocate: false);
            return true;
        });
        module.OnChatCommand("atlas_complete", "/atlas_complete (dev: mark the current map layout complete and award its temporary point)", async command =>
        {
            var conn = ConnOf(command.Player);
            if (MapDevices.Value.Atlas?.ByHash16(conn.Layout.Hash) is null)
            {
                await conn.Commands!.Reply("This area is not an Atlas map layout.");
                return;
            }
            await AtlasFeature.CompleteAtlasMap(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
            await conn.Commands!.Reply($"Atlas: {conn.AtlasCompletedLayouts.Count} completed, {conn.AtlasPassivePoints} point(s).");
        });
        module.OnMonsterKilled(async kill =>
        {
            if (kill.Monster.Rarity != GameServer.Modding.MonsterRarity.Unique) return;
            var conn = ConnOf(kill.Player);
            if (MapDevice.OpenOf(CharacterName(conn)) is { } openMap && openMap.Area.Hash16 == conn.Layout.Hash)
                await AtlasFeature.CompleteAtlasMap(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
        });
    }
}

static class AtlasFeature
{
    // A map boss was killed in an Atlas layout: remember the first completion and award the temporary one-point budget
    // (bonus objectives and the real point rules are still to come).
    internal static async Task CompleteAtlasMap(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (MapDevices.Value.Atlas?.ByHash16(conn.Layout.Hash) is not { } layout) return;
        if (CharacterName(conn) is not { } character) return;
        if (!conn.AtlasCompletedLayouts.Add(layout.Id)) return;
        conn.AtlasPassivePoints = (ushort)Math.Min(ushort.MaxValue, conn.AtlasPassivePoints + 1);
        CharacterSaves.SetAtlasCompletedLayouts(character, conn.AtlasCompletedLayouts);
        CharacterSaves.SetAtlasPassives(character, conn.AtlasPassives, conn.AtlasPassivePoints, conn.AtlasActivePreset);
        Console.WriteLine($"[gateway] {remote} Atlas map completed: {layout.Name} ({layout.Id}), " +
            $"{conn.AtlasCompletedLayouts.Count} completed, {conn.AtlasPassivePoints} passive points");
        await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas completion");
    }

    // C→S 0x3e/0x3f: a click in the Atlas passive tree. Validate the node and budget, save the preset and send all three
    // presets back so the client and the save agree. The client presents the tree's own connectivity; the server does
    // not yet have the Atlas graph, so it does not re-check pathing.
    internal static async Task ChangeAtlasPassive(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        byte preset, uint node, bool allocate)
    {
        if (preset >= conn.AtlasPassives.Length || node > ushort.MaxValue
            || AtlasPassiveNodes.Find((ushort)node) is not { } passive || passive.Id == "atlas_start_node")
        {
            Console.WriteLine($"[gateway] {remote} <= Atlas passive {(allocate ? "allocate" : "refund")} {node} refused: " +
                "preset or node unknown");
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas refused");
            return;
        }
        var nodes = conn.AtlasPassives[preset];
        var character = CharacterName(conn);
        if (allocate)
        {
            if (nodes.Contains((ushort)node))
            {
                await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas already allocated");
                return;
            }
            if (nodes.Count >= conn.AtlasPassivePoints)
            {
                Console.WriteLine($"[gateway] {remote} <= Atlas allocate {passive.Name} ({node}) refused: no points left");
                await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas no points");
                return;
            }
            nodes.Add((ushort)node);
        }
        else if (!nodes.Remove((ushort)node))
        {
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas not allocated");
            return;
        }
        conn.AtlasActivePreset = preset;
        if (character is not null)
            CharacterSaves.SetAtlasPassives(character, conn.AtlasPassives, conn.AtlasPassivePoints, conn.AtlasActivePreset);
        Console.WriteLine($"[gateway] {remote} <= Atlas preset {preset} {(allocate ? "allocate" : "refund")} " +
            $"{passive.Name} ({node}), {nodes.Count}/{conn.AtlasPassivePoints} points");
        await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas changed");
    }
}
