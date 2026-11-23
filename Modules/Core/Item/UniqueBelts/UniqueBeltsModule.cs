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
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            // "When you Kill": the wearer's own kills and their minions', totems', traps' and mines' (the Headhunter
            // exception to "you"), never another player's.
            if (global::Game.KilledByPlayerOrOwned(kill)) StealMonsterMods(kill.Player, kill.Monster);
        });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, WithStolenMods);
        GameEvents.OutgoingDamage.Add(module.Manifest.Id, (_, player, monster, rolled) => WithDamageVsRarity(player, monster, rolled));
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => StepUniqueBeltEffects(player)));
    }
}
