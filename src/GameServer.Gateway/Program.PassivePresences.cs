using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    static IEnumerable<PassiveEffectCatalog.Buff> PassivePresences(ClientState conn, SpawnedMonster monster)
    {
        if (!monster.Hostile || monster.Dead || monster.Life == 0 || conn.PlayerLife == 0) return [];
        double now = conn.Clock.Elapsed.TotalSeconds;
        double distance = Dist(conn.Walk.Position(now), monster.Walk.Position(now));
        return PassiveEffects.Buffs(PhreciaNodes(conn)).Where(b => !b.AffectsOwner && b.Radius > 0 && distance <= b.Radius);
    }

    static int PassivePresenceStat(ClientState conn, SpawnedMonster monster, string name)
    {
        if (Stats.Key(name) is not uint key) return 0;
        return PassivePresences(conn, monster).Sum(b => b.Stats.Where(s => s.Key == key).Sum(s => s.Value));
    }

    static double PassiveDamageTakenFactor(ClientState conn, SpawnedMonster monster, DamageType type)
    {
        int increase = PassivePresenceStat(conn, monster, "base_damage_taken_+%");
        if (DamageTypes.IsElemental(type)) increase += PassivePresenceStat(conn, monster, "elemental_damage_taken_+%");
        return Math.Max(0, 1 + increase / 100.0);
    }

    static DamagePacket ApplyPassiveDamageTaken(ClientState conn, SpawnedMonster monster, DamagePacket damage) =>
        damage.Select((type, range) => range.Scale(PassiveDamageTakenFactor(conn, monster, type)));

    static DamagePacket IncomingPassiveConversion(ClientState conn, SpawnedMonster monster, DamagePacket damage)
    {
        int conversion = PassivePresenceStat(conn, monster, "base_physical_damage_%_to_convert_to_fire");
        if (conversion != 0) damage = damage.Convert(new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, conversion));
        var player = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int takenAsFire = Get("physical_hit_and_dot_damage_%_taken_as_fire");
        return takenAsFire == 0 ? damage : damage.Convert(new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, takenAsFire));
    }

    static async Task RefreshPassivePresences(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.PassivePresences.Count == 0 && !PhreciaNodes(conn).Any()) return;
        var packets = new List<(byte[] Packet, string Label)>();
        lock (conn.Monsters)
        {
            var wanted = conn.Monsters.SelectMany(m => PassivePresences(conn, m.Value).Select(b => (m.Key, Buff: b)))
                .ToDictionary(b => (Monster: b.Key, Template: b.Buff.Template), b => b.Buff);
            foreach (var (key, instance) in conn.PassivePresences.ToArray())
                if (!wanted.ContainsKey(key))
                {
                    // A dead object can still be drawn; remove the buff before the normal object teardown.
                    if (conn.Monsters.ContainsKey(key.Monster))
                        packets.Add((InstanceEntities.RemoveBuff(key.Monster, instance), "passive presence leaves enemy"));
                    conn.PassivePresences.Remove(key);
                }
            foreach (var (key, buff) in wanted)
                if (!conn.PassivePresences.ContainsKey(key))
                {
                    ushort instance = conn.AllocateBuff();
                    conn.PassivePresences.Add(key, instance);
                    packets.Add((InstanceEntities.AddBuff(key.Monster, instance, buff.Definition, PlayerId,
                        values: buff.Values.Select(v => unchecked((uint)v)).ToArray()), $"passive presence {buff.Id} on enemy"));
                }
        }
        foreach (var (packet, label) in packets) await Send(remote, stream, ciphers, packet, label);
    }
}
