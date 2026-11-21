using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using static Program;
using static HideoutEditingFeature;

namespace GameServer.Modules.M_hideoutediting;

/// <summary>Moving things in the hideout's editor.</summary>
public sealed class HideoutEditingModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async conn =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await SpawnDecorations(remote, stream, ciphers, conn);
            await SpawnMonsterStatues(remote, stream, ciphers, conn);
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, (conn, use, at) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            return UseDisplayCase(remote, stream, ciphers, conn, use, at);
        });
        GameEvents.Command(module.Manifest.Id, "save_hideout", "/save_hideout (save and report the current hideout layout)",
            (conn, _) => global::Game.SaveHideoutLayout(conn));
        foreach (ushort placeId in new[] { InstanceHideoutEditing.PlaceId, InstanceHideoutEditing.PlaceGuildId })
            GameEvents.Packet(placeId).Add(module.Manifest.Id, async (conn, bytes) =>
            {
                var (remote, stream, ciphers) = conn.Wire;
                await PlaceDecoration(remote, stream, ciphers, conn, InstanceHideoutEditing.ReadPlace(bytes));
                return true;
            });
        GameEvents.Packet(InstanceHideoutEditing.MoveId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await MoveHideoutObject(remote, stream, ciphers, conn, InstanceHideoutEditing.ReadMove(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.ReclaimId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await ReclaimDecoration(remote, stream, ciphers, conn, InstanceHideoutEditing.ReadReclaim(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.ResetId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await ResetHideout(remote, stream, ciphers, conn, InstanceHideoutEditing.ReadReset(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.MusicId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, _, _) = conn.Wire;
            await SetMusic(remote, conn, InstanceHideoutEditing.ReadMusic(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.DisplayCasePlaceId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await PlaceDisplayCaseItem(remote, stream, ciphers, conn, InstanceHideoutEditing.ReadDisplayCasePlace(bytes));
            return true;
        });
        GameEvents.Packet(InstanceHideoutEditing.DisplayCaseToggleId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await ToggleDisplayCase(remote, stream, ciphers, conn, InstanceHideoutEditing.ReadDisplayCaseToggle(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMonsterStatue.CreateId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await CreateMonsterStatue(remote, stream, ciphers, conn, InstanceMonsterStatue.ReadCreate(bytes.AsSpan()));
            return true;
        });
    }
}
