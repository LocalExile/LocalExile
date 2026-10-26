using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static IEnumerable<ushort> PhreciaNodes(ClientState conn) =>
        conn.MainAscendancy.Selection?.Family == AscendancyFamily.Phrecia ? conn.MainAscendancy.Allocated : [];

    static bool ReservesLife(ClientState conn)
    {
        var sources = CharacterStatSources(conn);
        return Stats.Key("base_skill_reserve_life_instead_of_mana") is uint key && sources.GetValueOrDefault(key) != 0;
    }

    static int DamageEnergyShield(ClientState conn, int amount, bool chaos, double now)
    {
        var stats = CharacterPlayerStats(conn);
        return conn.EnergyShield.Damage(amount, chaos, now,
            name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0);
    }

    static (int Flat, int Percent)? ReservationFor(ClientState conn, int effect, int level)
    {
        var reservation = Skills.Reservation(effect, level);
        if (reservation is null) return null;
        var sources = CharacterStatSources(conn);
        int Get(string name) => Stats.Key(name) is uint key ? sources.GetValueOrDefault(key) : 0;
        string? flag = Skills.EffectId(effect) switch
        {
            "Envy" => "envy_reserves_no_mana",
            "AspectOfTheAvian" => "bird_aspect_reserves_no_mana",
            "AspectOfTheCat" => "cat_aspect_reserves_no_mana",
            "AspectOfTheSpider" => "spider_aspect_reserves_no_mana",
            "TemporalChains" => "temporal_chains_no_reservation",
            _ => null
        };
        return flag is not null && Get(flag) != 0 ? (0, 0) : reservation;
    }

    static async Task RefreshPassiveBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var wanted = PassiveEffects.Buffs(PhreciaNodes(conn)).Concat(TemporaryBuffs(conn))
            .Where(b => b.AffectsOwner).DistinctBy(b => b.Template).ToDictionary(b => b.Template);
        var packets = new List<(byte[] Bytes, string Label)>();
        lock (conn.Monsters)
        {
            foreach (var (template, instance) in conn.PassiveBuffs.ToArray())
                if (!wanted.ContainsKey(template))
                {
                    packets.Add((InstanceEntities.RemoveBuff(PlayerId, instance), "passive buff removed"));
                    conn.PassiveBuffs.Remove(template);
                }
            foreach (var buff in wanted.Values)
                if (!conn.PassiveBuffs.ContainsKey(buff.Template))
                {
                    ushort instance = conn.AllocateBuff();
                    conn.PassiveBuffs.Add(buff.Template, instance);
                    // Native 0x1c1 reads uncounted values only for IsRecovery rows. All Phrecia passive buffs use none.
                    var values = buff.Values.Select(v => unchecked((uint)v)).ToArray();
                    packets.Add((InstanceEntities.AddBuff(PlayerId, instance, buff.Definition, PlayerId, values: values),
                        $"passive buff {buff.Id}"));
                }
        }
        foreach (var (bytes, label) in packets) await Send(remote, stream, ciphers, bytes, label);
    }
}
