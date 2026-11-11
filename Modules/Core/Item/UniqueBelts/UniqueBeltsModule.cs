using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using static Program;
using static UniqueBeltsFeature;

namespace GameServer.Modules.M_uniquebelts;

/// <summary>Headhunter's stolen modifiers, Mageblood's flasks, damage against rarity.</summary>
public sealed class UniqueBeltsModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.Kill.Add(module.Manifest.Id, kill => { if (kill.PlayerKill) StealMonsterMods(kill.Conn, kill.Monster); });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, WithStolenMods);
        GameEvents.OutgoingDamage.Add(module.Manifest.Id, (_, player, monster, rolled) => WithDamageVsRarity(player, monster, rolled));
        GameEvents.AreaGenerated.Add(module.Manifest.Id, conn =>
        {
            var (remote, stream, ciphers) = conn.Wire;
            _ = RunUniqueBeltEffects(remote, stream, ciphers, conn);
            return Task.CompletedTask;
        });
    }
}
