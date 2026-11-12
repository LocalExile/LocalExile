using GameServer.Core.World;

/// <summary>"Killing Blows have X% chance to Consume Corpses to recover 10% of Maximum Life" (the Kitava Heart Eater
/// tattoo): the killing blow consumes the corpse and recovers the share of maximum life. Consuming the corpse also
/// takes it from summoning, explosions and warcries (SpawnedMonster.CorpseConsumed), and records the consumed-corpse
/// recently event the conditional items read.</summary>
static partial class Game
{
    internal const string CorpseKillChance = "killing_blow_consumes_corpse_restore_10%_life_chance_%";
    internal const int CorpseKillLifePct = 10;

    /// <summary>The killing blow's corpse consumption: true when the corpse was consumed (the caller removes the corpse
    /// object). A shattered or already consumed corpse is not consumed again, and the roll happens before the corpse
    /// is taken. Call under the Monsters lock: the corpse leaves conn.Monsters and the life change uses that lock.</summary>
    internal static bool ConsumeCorpseOnKillLocked(ClientState conn, uint id, SpawnedMonster m, double now)
    {
        if (conn.PlayerLife == 0 || m.CorpseConsumed || m.Ailments.Shattered) return false;
        var player = CharacterPlayerStats(conn);
        int chance = Stats.Key(CorpseKillChance) is uint key ? player.GetValueOrDefault(key) : 0;
        if (!RandomRolls.Chance(chance, 100, Random.Shared)) return false;
        m.CorpseConsumed = true;
        conn.Monsters.Remove(id);
        conn.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
        if (Stats.Key("cannot_recover_life") is uint cannot && player.GetValueOrDefault(cannot) != 0) return true;
        var session = LeechOf(conn);
        uint before = conn.PlayerLife;
        conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife,
            (long)conn.PlayerLife + conn.PlayerMaxLife * (long)CorpseKillLifePct / 100);
        session.InstantLifeChanged |= before != conn.PlayerLife;
        return true;
    }
}
