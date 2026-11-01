using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>What the player's debuffs on monsters need from his stats: taunted_enemies_damage_taken_+% (Bring the Battle),
/// as last sent to the client (Program.PlayerStatsWithCleared). His passives' enemy auras come from the allocated nodes
/// at each step (PassiveBuffAuras.EnemyAurasFor).</summary>
sealed record EnemyDebuffs(int TauntedDamageTakenPct)
{
    public static readonly EnemyDebuffs None = new(0);
}
