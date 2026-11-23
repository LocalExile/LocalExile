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
        public bool AgainstBleedingWithWeapon { get; init; }

        public Cull WithWeapon(IReadOnlyDictionary<uint, int>? local) => local is null ? this
            : this with { AgainstBleedingWithWeapon = local.GetValueOrDefault(Stat.LocalCullingStrikeVsBleedingEnemies) != 0 };

        // The character's stats and the skill's own (support gems included); pass them apart, not merged.
        public static Cull Of(IReadOnlyDictionary<uint, int>? player, IReadOnlyDictionary<uint, int>? skill, bool critical,
            bool spell)
        {
            int Get(uint k) => (player?.GetValueOrDefault(k) ?? 0) + (skill?.GetValueOrDefault(k) ?? 0);
            int threshold = Get(Stat.KillEnemyOnHitIfUnder20PctLife) > 0 ? 20
                : Get(Stat.KillEnemyOnHitIfUnder15PctLife) > 0 ? 15
                : Get(Stat.KillEnemyOnHitIfUnder10PctLife) > 0 || critical && Get(Stat.CritsHaveCullingStrike) > 0
                    || spell && Get(Stat.SpellsHaveCullingStrike) > 0
                    || !spell && Get(Stat.CullingStrikeWithTincturedWeapons) > 0 && Get(Stat.TinctureApplied) > 0 ? 10 : 0;
            // "Kill Normal or Magic Enemies that have X% Life or lower when Hit by Supported Skills" (Cull the Weak).
            int normalMagic = Math.Clamp(Get(Stat.KillNormalOrMagicEnemyOnHitIfUnderXPctLife), 0, 100);
            bool cannotKill = Get(Stat.HitsCannotKillEnemies) != 0;
            return threshold == 0 && normalMagic == 0 && !cannotKill ? default
                : new(threshold, Get(Stat.RecoverPctMaximumLifeOnCull), normalMagic, cannotKill);
        }
    }

    // The hit's own damage, then the cull. A boss stage this hit starts, or one still running, keeps the boss alive.
    public static (uint Life, bool StageStarts) TakeOwnerHitLocked(Player player, SpawnedMonster m, int damage, Cull cull,
        bool? rampageSource = null, IReadOnlyDictionary<uint, int>? playerStats = null,
        IReadOnlyDictionary<uint, int>? cullPlayer = null)
    {
        m.LastHitBy = player;
        // The culling context is the attack's cached offensive snapshot and is passed for every victim of an area
        // skill. `player` is Rampage's freshness-scoped source: only the first target gets a rebuilt snapshot, so it
        // must stay separate from the stats the cull reads.
        var cullStats = cullPlayer ?? playerStats;
        int threshold = m.Rarity is MonsterRarity.Normal or MonsterRarity.Magic
            ? Math.Max(cull.Threshold, cull.NormalMagicThreshold) : cull.Threshold;
        if (cull.AgainstBleedingWithWeapon && m.Bleeds.IgnitedAt(ProjectileWorldTime(player)))
            threshold = Math.Max(10, threshold);
        // Conditional culling sources that depend on the target's state grant the standard 10% cull.
        // Pacifism (CannotKill) still governs the hit.
        if (threshold == 0 && !cull.CannotKill && cullStats is not null)
        {
            int Get(uint k) => cullStats.GetValueOrDefault(k);
            bool applies = false;
            if (Get(Stat.CullingStrikeOnBurningEnemies) != 0 && BurningTarget(player, m)) applies = true;
            if (Get(Stat.CullingStrikeOnFrozenEnemies) != 0
                && m.Ailments.Magnitude(MonsterElementalAilments.Kind.Freeze, AreaTime(player)) > 0) applies = true;
            if (Get(Stat.CullingStrikeVsCursedEnemies) != 0 && m.Curses.All.Count > 0) applies = true;
            if (Get(Stat.CullingStrikeVsMarkedEnemy) != 0 && m.Curses.All.Values.Any(c => c.Mark)) applies = true;
            if (Get(Stat.CullingStrikeOnEnemiesAffectedByPoachersMark) != 0
                && m.Curses.All.Values.Any(c => c.Effect == "PoachersMark")) applies = true;
            if (Get(Stat.CulledIfHitWhileUnder10PctLife) != 0 && m.Life * 100L <= m.MaxLife * 10L) applies = true;
            if (applies) threshold = 10;
        }
        if (cull.CannotKill)
        {
            // Pacifism: the hit cannot kill, so it leaves one Life and the cull below has nothing left to take.
            damage = (int)Math.Min(damage, Math.Max(0, (long)m.Life - 1));
            threshold = 0;
        }
        uint beforeHit = m.Life;
        var hit = TakeOwnerDamageLocked(player, m, damage, rampageSource, playerStats);
        // The direct hit's excess feeds overkill effects; a subsequent cull contributes no overkill.
        m.Overkill = beforeHit > 0 && hit.Life == 0
            ? (int)Math.Clamp((long)damage - beforeHit, 0, int.MaxValue) : 0;
        if (threshold == 0 || hit.Life == 0 || hit.StageStarts || m.StageRunning
            || hit.Life * 100L > m.MaxLife * (long)threshold || m.Mods.Effects[Stat.ImmuneToCullingStrike] != 0) return hit;
        m.Life = hit.Life;
        hit = TakeOwnerDamageLocked(player, m, (int)hit.Life, rampageSource, playerStats);
        bool cannotRecoverLife = cullStats is not null && LifeRecovery.NonLeechBlocked(
            key => cullStats.GetValueOrDefault(key));
        if (!cannotRecoverLife && hit.Life == 0 && cull.RecoverLifePct > 0 && player.PlayerLife > 0)
        {
            // Instant recovery, sent with the next vitals update like instant leech.
            var leech = LeechOf(player);
            uint before = player.PlayerLife;
            player.PlayerLife = PetrifiedBlood.RecoverLife(player, before, player.PlayerMaxLife * cull.RecoverLifePct / 100d,
                ref leech.LifeInstantCarry, minimum: 1);
            leech.InstantLifeChanged |= before != player.PlayerLife;
        }
        if (hit.Life == 0 && cullStats is not null)
        {
            int Get(uint k) => cullStats.GetValueOrDefault(k);
            int lifeGained = Get(Stat.LifeGainedOnCull);
            int manaGained = Get(Stat.ManaGainedOnCull);
            if (!cannotRecoverLife && lifeGained > 0 && player.PlayerLife > 0)
            {
                // The same capped recovery the other instant Life sources use: reserved Life and Petrified Blood apply.
                var leech = LeechOf(player);
                uint before = player.PlayerLife;
                player.PlayerLife = PetrifiedBlood.RecoverLife(player, before, lifeGained,
                    ref leech.LifeInstantCarry, minimum: 1);
                leech.InstantLifeChanged |= before != player.PlayerLife;
            }
            if (manaGained > 0)
            {
                // Carry and client update follow the same rule as recovery on kill, including a gain up to full Mana.
                var leech = LeechOf(player);
                uint before = player.PlayerMana;
                player.PlayerMana = RecoveryPool.Advance(before, (uint)UnreservedMana(player), manaGained,
                    ref leech.ManaInstantCarry);
                leech.InstantManaChanged |= before != player.PlayerMana;
            }
        }
        return hit;
    }

    // Burning covers the supported non-ignite sources too. Righteous Fire only puts its aura on the monsters in range
    // and ticks their burn; the DoT itself never reaches this hit path, so it cannot cull on its own. `m.Burn` is only
    // the DoT's fractional carry and is never cleared, so it is not a burning signal.
    static bool BurningTarget(Player player, SpawnedMonster m)
    {
        if (m.Ignites.Burning || m.HeraldBurns.Burning
            || InfernalLegionFields.TryGetValue(player, out var field) && field.Burning.Contains(m)) return true;
        foreach (var other in player.Instance?.Presence.PlayersExcept(player.Id) ?? [])
            if (other.PlayerLife > 0 && other.Instance == player.Instance
                && InfernalLegionFields.TryGetValue(other, out var otherField) && otherField.Burning.Contains(m)) return true;
        if (player.Placed.Grounds.Count > 0)
        {
            double now = AreaTime(player);
            var at = m.Walk.Position(now);
            foreach (var ground in player.Placed.Grounds)
                if (now < ground.Until && Dist(at, ground.At) <= ground.Radius) return true;
        }
        return player.RighteousFire is { } rf && player.Area.Monsters.Any(pair =>
            pair.Value == m && rf.Auras.ContainsKey(pair.Key));
    }
}
