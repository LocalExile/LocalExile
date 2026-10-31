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
        module.OnAreaGenerated(async player =>
        {
            var conn = ConnOf(player); var (remote, stream, ciphers) = conn.Wire;
            await SpawnMonsterStatues(remote, stream, ciphers, conn);
        });
        module.OnPacket(InstanceHideoutEditing.MoveId, async p =>
        {
            var conn = ConnOf(p.Player); var (remote, stream, ciphers) = conn.Wire; byte[] bytes = p.Bytes.ToArray();
            await MoveHideoutObject(remote, stream, ciphers, conn, InstanceHideoutEditing.ReadMove(bytes));
            return true;
        });
        module.OnPacket(InstanceMonsterStatue.CreateId, async p =>
        {
            var conn = ConnOf(p.Player); var (remote, stream, ciphers) = conn.Wire;
            await CreateMonsterStatue(remote, stream, ciphers, conn, InstanceMonsterStatue.ReadCreate(p.Bytes.Span));
            return true;
        });
    }
}
