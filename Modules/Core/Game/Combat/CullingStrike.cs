// Culling Strike: a hit that leaves an enemy at or below a share of its maximum Life kills it (poedb SupportCullingStrike:
// "Kill Enemies that have 10% Life or lower when Hit by Supported Skills"). The client's hit logic never reads these
// stats, so the server owns the kill and the death goes out the usual way.
static partial class Game
{
    // Threshold: the highest share that applies to this hit, in percent (0: no culling). RecoverLifePct: the support's
    // quality, "Recover (0-2)% of Life when Supported Skills Cull an Enemy".
    internal readonly record struct Cull(int Threshold, int RecoverLifePct)
    {
        // The character's stats and the skill's own (support gems included); pass them apart, not merged.
        internal static Cull Of(IReadOnlyDictionary<uint, int>? player, IReadOnlyDictionary<uint, int>? skill, bool critical,
            bool spell)
        {
            int Get(string name) => Stats.Key(name) is uint k
                ? (player?.GetValueOrDefault(k) ?? 0) + (skill?.GetValueOrDefault(k) ?? 0) : 0;
            int threshold = Get("kill_enemy_on_hit_if_under_20%_life") > 0 ? 20
                : Get("kill_enemy_on_hit_if_under_15%_life") > 0 ? 15
                : Get("kill_enemy_on_hit_if_under_10%_life") > 0 || critical && Get("crits_have_culling_strike") > 0
                    || spell && Get("spells_have_culling_strike") > 0 ? 10 : 0;
            return threshold == 0 ? default : new(threshold, Get("recover_%_maximum_life_on_cull"));
        }
    }

    // The hit's own damage, then the cull. A boss stage this hit starts, or one still running, keeps the boss alive.
    internal static (uint Life, bool StageStarts) TakeOwnerHitLocked(ClientState conn, SpawnedMonster m, int damage, Cull cull,
        bool? rampageSource = null, IReadOnlyDictionary<uint, int>? player = null)
    {
        var hit = TakeOwnerDamageLocked(conn, m, damage, rampageSource, player);
        if (cull.Threshold == 0 || hit.Life == 0 || hit.StageStarts || m.StageRunning
            || hit.Life * 100L > m.MaxLife * (long)cull.Threshold || m.Mods.Effects["immune_to_culling_strike"] != 0) return hit;
        m.Life = hit.Life;
        hit = TakeOwnerDamageLocked(conn, m, (int)hit.Life, rampageSource, player);
        if (hit.Life == 0 && cull.RecoverLifePct > 0 && conn.PlayerLife > 0)
        {
            // Instant recovery, sent with the next vitals update like instant leech.
            var leech = LeechOf(conn);
            uint before = conn.PlayerLife;
            conn.PlayerLife = RecoveryPool.Advance(before, conn.PlayerMaxLife, conn.PlayerMaxLife * cull.RecoverLifePct / 100d,
                ref leech.LifeInstantCarry, minimum: 1);
            leech.InstantLifeChanged |= before != conn.PlayerLife;
        }
        return hit;
    }
}
