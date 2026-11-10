using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using static Program;
using static HideoutEditingFeature;

namespace GameServer.Modules.M_hideoutediting;

/// <summary>Moving things in the hideout's editor.</summary>
public sealed class HideoutEditingModule : IModule, IDisposable
{
    readonly List<IDisposable> _events = [];
    void On<T>(GameEvent<T> ev, T run) where T : Delegate => _events.Add(ev.Add("HideoutEditing", run));

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var e in _events) e.Dispose();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async conn =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await SpawnMonsterStatues(remote, stream, ciphers, conn);
        });
        GameEvents.Packet(InstanceHideoutEditing.MoveId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            await MoveHideoutObject(remote, stream, ciphers, conn, InstanceHideoutEditing.ReadMove(bytes));
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
