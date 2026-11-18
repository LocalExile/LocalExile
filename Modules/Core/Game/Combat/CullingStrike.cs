using GameServer.Core.Protocol.Instance;

// Culling Strike: a hit that leaves an enemy at or below a share of its maximum Life kills it (poedb SupportCullingStrike:
// "Kill Enemies that have 10% Life or lower when Hit by Supported Skills"). The client's hit logic never reads these
// stats, so the server owns the kill and the death goes out the usual way.
public static partial class Game
{
    // Threshold: the highest share that applies to this hit, in percent (0: no culling). RecoverLifePct: the support's
    // quality, "Recover (0-2)% of Life when Supported Skills Cull an Enemy". NormalMagicThreshold: Cull the Weak's share,
    // which only normal and magic enemies are culled at. CannotKill: Pacifism, whose hits leave the enemy at 1 Life.
    public readonly record struct Cull(int Threshold, int RecoverLifePct, int NormalMagicThreshold = 0, bool CannotKill = false)
    {
        // The character's stats and the skill's own (support gems included); pass them apart, not merged.
        public static Cull Of(IReadOnlyDictionary<uint, int>? player, IReadOnlyDictionary<uint, int>? skill, bool critical,
            bool spell)
        {
            int Get(string name) => Stats.Key(name) is uint k
                ? (player?.GetValueOrDefault(k) ?? 0) + (skill?.GetValueOrDefault(k) ?? 0) : 0;
            int threshold = Get("kill_enemy_on_hit_if_under_20%_life") > 0 ? 20
                : Get("kill_enemy_on_hit_if_under_15%_life") > 0 ? 15
                : Get("kill_enemy_on_hit_if_under_10%_life") > 0 || critical && Get("crits_have_culling_strike") > 0
                    || spell && Get("spells_have_culling_strike") > 0 ? 10 : 0;
            // "Kill Normal or Magic Enemies that have X% Life or lower when Hit by Supported Skills" (Cull the Weak).
            int normalMagic = Math.Clamp(Get("kill_normal_or_magic_enemy_on_hit_if_under_x%_life"), 0, 100);
            bool cannotKill = Get("hits_cannot_kill_enemies") != 0;
            return threshold == 0 && normalMagic == 0 && !cannotKill ? default
                : new(threshold, Get("recover_%_maximum_life_on_cull"), normalMagic, cannotKill);
        }
    }

    // The hit's own damage, then the cull. A boss stage this hit starts, or one still running, keeps the boss alive.
    public static (uint Life, bool StageStarts) TakeOwnerHitLocked(ClientState conn, SpawnedMonster m, int damage, Cull cull,
        bool? rampageSource = null, IReadOnlyDictionary<uint, int>? player = null,
        IReadOnlyDictionary<uint, int>? cullPlayer = null)
    {
        m.LastHitBy = conn;
        // The culling context is the attack's cached offensive snapshot and is passed for every victim of an area
        // skill. `player` is Rampage's freshness-scoped source: only the first target gets a rebuilt snapshot, so it
        // must stay separate from the stats the cull reads.
        var cullStats = cullPlayer ?? player;
        int threshold = m.Rarity is MonsterRarity.Normal or MonsterRarity.Magic
            ? Math.Max(cull.Threshold, cull.NormalMagicThreshold) : cull.Threshold;
        // Conditional culling sources that depend on the target's state (poedb Culling Strike / passives): each grants
        // the standard 10% cull when its condition holds. The weapon-local (local/main_hand/off_hand), flask, stun and
        // "crit recently" sources need extra context and stay outside this. Pacifism (CannotKill) is never overridden.
        if (threshold == 0 && !cull.CannotKill && cullStats is not null)
        {
            int Get(string name) => Stats.Key(name) is uint k ? cullStats.GetValueOrDefault(k) : 0;
            bool applies = false;
            if (Get("culling_strike_on_burning_enemies") != 0 && BurningTarget(conn, m)) applies = true;
            if (Get("culling_strike_on_frozen_enemies") != 0
                && m.Ailments.Magnitude(MonsterElementalAilments.Kind.Freeze, conn.Clock.Elapsed.TotalSeconds) > 0) applies = true;
            if (Get("culling_strike_vs_cursed_enemies") != 0 && m.Curses.All.Count > 0) applies = true;
            if (Get("culling_strike_vs_marked_enemy") != 0 && m.Curses.All.Values.Any(c => c.Mark)) applies = true;
            if (Get("culling_strike_on_enemies_affected_by_poachers_mark") != 0
                && m.Curses.All.Values.Any(c => c.Effect == "PoachersMark")) applies = true;
            if (Get("culled_if_hit_while_under_10%_life") != 0 && m.Life * 100L <= m.MaxLife * 10L) applies = true;
            if (applies) threshold = 10;
        }
        if (cull.CannotKill)
        {
            // Pacifism: the hit cannot kill, so it leaves one Life and the cull below has nothing left to take.
            damage = (int)Math.Min(damage, Math.Max(0, (long)m.Life - 1));
            threshold = 0;
        }
        uint beforeHit = m.Life;
        var hit = TakeOwnerDamageLocked(conn, m, damage, rampageSource, player);
        // The direct hit's excess feeds overkill effects; a subsequent cull contributes no overkill.
        m.Overkill = beforeHit > 0 && hit.Life == 0
            ? (int)Math.Clamp((long)damage - beforeHit, 0, int.MaxValue) : 0;
        if (threshold == 0 || hit.Life == 0 || hit.StageStarts || m.StageRunning
            || hit.Life * 100L > m.MaxLife * (long)threshold || m.Mods.Effects["immune_to_culling_strike"] != 0) return hit;
        m.Life = hit.Life;
        hit = TakeOwnerDamageLocked(conn, m, (int)hit.Life, rampageSource, player);
        bool cannotRecoverLife = cullStats is not null && Stats.Key("cannot_recover_life") is uint ck
            && cullStats.GetValueOrDefault(ck) != 0;
        if (!cannotRecoverLife && hit.Life == 0 && cull.RecoverLifePct > 0 && conn.PlayerLife > 0)
        {
            // Instant recovery, sent with the next vitals update like instant leech.
            var leech = LeechOf(conn);
            uint before = conn.PlayerLife;
            conn.PlayerLife = PetrifiedBlood.RecoverLife(conn, before, conn.PlayerMaxLife * cull.RecoverLifePct / 100d,
                ref leech.LifeInstantCarry, minimum: 1);
            leech.InstantLifeChanged |= before != conn.PlayerLife;
        }
        if (hit.Life == 0 && cullStats is not null)
        {
            int Get(string name) => Stats.Key(name) is uint k ? cullStats.GetValueOrDefault(k) : 0;
            int lifeGained = Get("life_gained_on_cull");
            int manaGained = Get("mana_gained_on_cull");
            if (!cannotRecoverLife && lifeGained > 0 && conn.PlayerLife > 0)
            {
                // The same capped recovery the other instant Life sources use: reserved Life and Petrified Blood apply.
                var leech = LeechOf(conn);
                uint before = conn.PlayerLife;
                conn.PlayerLife = PetrifiedBlood.RecoverLife(conn, before, lifeGained,
                    ref leech.LifeInstantCarry, minimum: 1);
                leech.InstantLifeChanged |= before != conn.PlayerLife;
            }
            if (manaGained > 0)
            {
                // Carry and client update follow the same rule as recovery on kill, including a gain up to full Mana.
                var leech = LeechOf(conn);
                uint before = conn.PlayerMana;
                conn.PlayerMana = RecoveryPool.Advance(before, (uint)UnreservedMana(conn), manaGained,
                    ref leech.ManaInstantCarry);
                leech.InstantManaChanged |= before != conn.PlayerMana;
            }
        }
        return hit;
    }

    // Burning covers the supported non-ignite sources too. Righteous Fire only puts its aura on the monsters in range
    // and ticks their burn; the DoT itself never reaches this hit path, so it cannot cull on its own. `m.Burn` is only
    // the DoT's fractional carry and is never cleared, so it is not a burning signal.
    static bool BurningTarget(ClientState conn, SpawnedMonster m)
    {
        if (m.Ignites.Burning || m.HeraldBurns.Burning) return true;
        return conn.RighteousFire is { } rf && conn.Monsters.Any(pair =>
            pair.Value == m && rf.Auras.ContainsKey(pair.Key));
    }
}
