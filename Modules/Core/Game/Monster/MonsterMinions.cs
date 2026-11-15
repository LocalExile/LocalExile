using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

/// <summary>
/// Minions a monster's skill brings, for any monster: what a SummonSpecificMonsters skill names
/// (SkillCatalog.MonsterSummon), or, for a minion skill the player also has, the player's minion of that skill
/// (SummonCatalog.ForActiveSkill) with the monster skill's own count, limit and duration. Monsters.MonsterSummon brings
/// them; this retires them when their duration ends or a newer use takes their place.
/// </summary>
public static partial class Game
{
    /// <summary>What a monster's skill summons at a level, or null for a skill that brings none the server can name.
    /// <para>A player minion skill's monster version brings the player's minion only when its stats name no
    /// <c>alternate_minion</c>: ExileSummonSkeletons' stat set has none, as the gem's has none. Its monster versions that
    /// set one (ExileRaiseZombie, ExileSummonRagingSpirit, ExileSummonIceGolem: 1) pick a variant the client never
    /// resolves (outside the SummonSpecificMonsters classes the value names no data row; the server alone creates the
    /// minion), so they bring nothing. A skill that raises a corpse needs one (MonsterSkills.OnCorpse).</para></summary>
    public static SkillCatalog.MonsterSummonInfo? MonsterSummonOf(SkillCatalog catalog, int effect, byte level)
    {
        if (catalog.MonsterSummon(effect, level) is { } specific) return specific;
        if (SummonData.ForActiveSkill(effect) is not { Corpse: false } definition) return null;
        var stats = GolemData.Skill(catalog, effect, level).Stats;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (Get("alternate_minion") != 0 || Monsters.Find(definition.Variety) is not { Supported: true } variety) return null;
        int ms = Get("base_skill_effect_duration");
        return new(variety.Row, SummonCount(definition, Get), 0, false, catalog.CastTimeMs(effect) / 1000.0,
            catalog.CooldownMs(effect, catalog.MaxLevelAt(effect, level)) / 1000.0)
        {
            Limit = definition.LimitStat.Length == 0 ? 0 : Math.Max(0, Get(definition.LimitStat)),
            Lifetime = definition.Persistent || ms <= 0 ? double.PositiveInfinity : ms / 1000.0,
            AtTarget = true,
        };
    }

    /// <summary>Under the Monsters lock: the summoner's living minions of a variety beyond its limit, oldest (lowest
    /// object id) first, to make room for <paramref name="incoming"/> more.</summary>
    static List<uint> SurplusMinionsLocked(ClientState conn, uint summoner, SkillCatalog.MonsterSummonInfo summon, int incoming)
    {
        if (summon.Limit <= 0) return [];
        var living = conn.Monsters.Where(p => p.Value.SummonerId == summoner && !p.Value.Dead && p.Value.Life > 0
            && p.Value.Variety?.Row == summon.Variety).Select(p => p.Key).Order().ToList();
        int surplus = living.Count + Math.Min(incoming, summon.Limit) - summon.Limit;
        return surplus > 0 ? living.GetRange(0, Math.Min(surplus, living.Count)) : [];
    }

    /// <summary>Takes summoned monsters out of the area (as a player's expired minion is: Golems.RemoveGolemLocked).</summary>
    static async Task RetireSummonedMonsters(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IReadOnlyCollection<uint> ids, string why)
    {
        List<uint> gone = [];
        lock (conn.Monsters)
            foreach (uint id in ids)
                if (conn.Monsters.Remove(id, out var m))
                {
                    m.Dead = true; m.Life = 0; m.ActionGeneration++;
                    conn.Instance.World.MonsterOrigins.TryRemove(id, out _);
                    gone.Add(id);
                }
        foreach (uint id in gone)
            await SendWorldRemoval(remote, stream, ciphers, conn, id, $"0x0013 summoned monster 0x{id:x} {why}");
    }

    /// <summary>The summoned monsters whose duration has run out leave (MonsterSummonInfo.Lifetime).</summary>
    static Task ExpireSummonedMonsters(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        uint[] expired;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            expired = [.. conn.Monsters.Where(p => p.Value.ExpiresAt <= now && !p.Value.Dead).Select(p => p.Key)];
        }
        return expired.Length == 0 ? Task.CompletedTask
            : RetireSummonedMonsters(remote, stream, ciphers, conn, expired, "expires");
    }
}
