using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamageOverTime = GameServer.Core.World.DamageOverTime;

static partial class CommandsRules
{
    internal static async Task SetGod(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool on)
    {
        lock (conn.Monsters)
        {
            conn.God = on;
            if (on) GodCharacters[CharacterName(conn)] = true; else GodCharacters.TryRemove(CharacterName(conn), out _);
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.PlayerLife = on ? conn.PlayerMaxLife : Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
            conn.PlayerMana = on ? (uint)UnreservedMana(conn) : Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
            TrimLeechLocked(conn);
        }
        Console.WriteLine($"[gateway] {remote} god mode {(on ? "on" : "off")}: life {conn.Pools.Life}, mana {conn.Pools.Mana}, {ReservedMana(conn)} reserved");
        await SendCharacterStats(remote, stream, ciphers, conn, on ? "god mode on" : "god mode off");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife), $"0x01b1 player life {conn.PlayerLife}");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
        // UI built on entering caches the attributes (see SendPlayer): send them once more.
        await SendPlayerStatSnapshot(remote, stream, ciphers, conn,
            () => InstanceEntities.StatsUpdate(PlayerId, [.. BaseStats(conn)]), "0x01b3 player attributes");
    }

    internal static DamageType? ParseDamageType(string text) => text.ToLowerInvariant() switch
    {
        "fire" => DamageType.Fire,
        "cold" => DamageType.Cold,
        "lightning" or "light" => DamageType.Lightning,
        "chaos" => DamageType.Chaos,
        "physical" or "phys" => DamageType.Physical,
        _ => null,
    };
}
