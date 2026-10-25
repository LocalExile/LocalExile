using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static int AdditionalPhysicalReduction(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        return Stats.Key("base_additional_physical_damage_reduction_%") is uint key ? stats.GetValueOrDefault(key) : 0;
    }

    static int PlayerPhysicalReduction(ClientState conn, int damage) => Math.Clamp(
        GameServer.Core.World.DamageModel.ArmourReduction(conn.Pools.Armour, damage) + AdditionalPhysicalReduction(conn),
        0, GameServer.Core.World.DamageModel.DamageReductionCap);

    static IReadOnlyList<PassiveEffectCatalog.Buff> TemporaryBuffs(ClientState conn) =>
        TemporaryPassiveBuffs.For(CharacterName(conn)).Active(DateTimeOffset.UtcNow).Select(e => e.Buff).ToArray();

    static Dictionary<uint, int> TemporaryPassiveStats(ClientState conn)
    {
        var result = new Dictionary<uint, int>();
        foreach (var buff in TemporaryBuffs(conn))
            foreach (var (key, value) in buff.Stats) result[key] = result.GetValueOrDefault(key) + value;
        return result;
    }

    static bool GrantAdrenaline(ClientState conn, int milliseconds)
    {
        // Template 503 in this client's own data supplies all six Adrenaline values, not guessed display bonuses.
        if (milliseconds <= 0 || PassiveEffects.Template(503) is not { } buff) return false;
        return TemporaryPassiveBuffs.For(CharacterName(conn)).Grant(buff, TimeSpan.FromMilliseconds(milliseconds), DateTimeOffset.UtcNow);
    }

    static async Task RefreshTemporaryPassives(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
        {
            var active = TemporaryBuffs(conn).Select(b => b.Template).ToHashSet();
            if (conn.ActiveTemporaryBuffs.SetEquals(active)) return;
            conn.ActiveTemporaryBuffs.Clear(); conn.ActiveTemporaryBuffs.UnionWith(active);
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "temporary passive buffs changed");
    }
}
