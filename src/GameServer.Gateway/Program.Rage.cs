using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static RageState RageOf(ClientState conn) => RageState.For(CharacterName(conn));
    static Func<string, int> RageInputs(ClientState conn, IReadOnlyDictionary<uint, int> sources)
    {
        Dictionary<uint, int> replacements;
        lock (conn.Monsters) replacements = new(conn.PlayerStats);
        return name => Stats.Key(name) is uint key
            ? replacements.TryGetValue(key, out int value) ? value : sources.GetValueOrDefault(key) : 0;
    }

    static void RageStats(ClientState conn, Dictionary<uint, int> sources)
    {
        var value = RageOf(conn).Read(DateTimeOffset.UtcNow, RageInputs(conn, sources), conn.Paused);
        foreach (var (name, amount) in new[] { ("current_rage", value.Current), ("virtual_maximum_rage", value.Maximum),
            ("effective_rage_for_per_rage_effects", value.Effective), ("attack_damage_+%_final_from_rage", value.Effective),
            ("is_at_maximum_rage", value.Maximum > 0 && value.Current == value.Maximum ? 1 : 0) })
            if (Stats.Key(name) is uint key) sources[key] = amount;
    }

    static void GainRageOnHit(ClientState conn, IReadOnlyDictionary<uint, int> stats, bool attack, bool melee, bool critical) =>
        RageOf(conn).Hit(DateTimeOffset.UtcNow, RageInputs(conn, stats), attack, melee, critical, Random.Shared.Next(100));

    static async Task RefreshRage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        RageState.Value value;
        lock (conn.Monsters)
        {
            value = RageOf(conn).Read(DateTimeOffset.UtcNow, RageInputs(conn, CharacterStatSources(conn)), conn.Paused);
            if (conn.SentRage == value) return;
            conn.SentRage = value;
        }
        if (value.Current > 0 && conn.RageBuff is null)
        {
            conn.RageBuff = conn.AllocateBuff();
            // BuffDefinitions 1060: no recovery values/extra u16; its count reads current_rage (key 7594).
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, conn.RageBuff.Value, 1060, PlayerId), "Rage gained");
        }
        else if (value.Current == 0 && conn.RageBuff is ushort buff)
        {
            conn.RageBuff = null;
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, buff), "Rage empty");
        }
        await SendCharacterStats(remote, stream, ciphers, conn, $"Rage {value.Current}/{value.Maximum}");
    }
}
