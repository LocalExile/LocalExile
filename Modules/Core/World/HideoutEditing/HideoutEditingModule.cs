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
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async player =>
        {
            await SpawnMonsterStatues(player);
        });
        GameEvents.Packet(InstanceHideoutEditing.MoveId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await MoveHideoutObject(player, InstanceHideoutEditing.ReadMove(bytes));
            return true;
        });
        GameEvents.Packet(InstanceMonsterStatue.CreateId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await CreateMonsterStatue(player, InstanceMonsterStatue.ReadCreate(bytes.AsSpan()));
            return true;
        });
    }
}
