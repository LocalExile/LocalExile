using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageOverTime = GameServer.Core.World.DamageOverTime;
using DamageType = GameServer.Core.World.DamageType;

static partial class CommandsRules
{
    // The /mob_mods command: a monster's mods, or the monster created again with the given affixes.
    internal static async Task MonsterModsCommand(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string[] args)
    {
        var rolls = MonsterModRolls.Value;
        uint? target = MonsterArg(conn, args, 0);
        string[] wanted = [.. args.Where(a => !a.StartsWith("0x", StringComparison.OrdinalIgnoreCase))];
        if (wanted is ["pool", ..])
        {
            // The pool a level-N rare monster of the last spawned variety rolls from.
            SpawnedMonster? last;
            lock (conn.Monsters) last = target is { } t ? conn.Monsters[t] : null;
            if (last?.Variety is not { } v) { await conn.Commands!.Reply("No monster."); return; }
            var pool = rolls.Pool(rolls.TagsOf(v, last.Rarity, alone: true), last.Level, rolls.RollUnsupported);
            await conn.Commands!.Reply($"{pool.Count} mods for {v.Name}@{last.Level}: " +
                string.Join(", ", pool.Select(p => $"{p.Mod.Name.Trim()} {p.Weight}")));
            return;
        }
        if (target is not { } id) { await conn.Commands!.Reply("No such monster."); return; }
        SpawnedMonster m;
        lock (conn.Monsters) m = conn.Monsters[id];
        if (wanted.Length == 0)
        {
            await conn.Commands!.Reply($"0x{id:x} {m.Label} {m.Rarity}" +
                (m.Mods.NameWords.Count > 0 ? $" \"{rolls.NameOf(m.Mods.NameWords)}\"" : "") +
                $": {(m.Mods.Affixes.Any() ? rolls.Describe(m.Mods) : "no affixes")}; life {m.Life}/{m.MaxLife}");
            return;
        }
        if (m.Variety is not { } variety || m.Rarity is not (MonsterRarity.Magic or MonsterRarity.Rare))
        {
            await conn.Commands!.Reply("Only a magic or rare monster takes affixes.");
            return;
        }
        var forced = new List<ModCatalog.Mod>();
        foreach (string w in wanted)
        {
            var mod = rolls.Mods.All.FirstOrDefault(x => x.Domain == MonsterModCatalog.Domain && x.GenerationType is 1 or 2
                && (x.Id.Equals(w, StringComparison.OrdinalIgnoreCase) || x.Id.Equals("MonsterMod" + w, StringComparison.OrdinalIgnoreCase)
                    || x.Name.Trim().Equals(w.Replace('_', ' '), StringComparison.OrdinalIgnoreCase)));
            if (mod is null) { await conn.Commands!.Reply($"No monster mod {w}."); return; }
            forced.Add(mod);
        }
        var set = rolls.Roll(variety, m.Level, m.Rarity, Random.Shared, alone: true, count: forced.Count, forced: forced);
        var (x, y) = m.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        lock (conn.Monsters) conn.Monsters.Remove(id);
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove monster 0x{id:x} (/mob_mods)");
        await SpawnMonster(remote, stream, ciphers, conn, variety, m.Level, m.Rarity, x, y, friendly: !m.Hostile, mods: set);
    }
}
