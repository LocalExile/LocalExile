using GameServer.Core.World;

public static partial class Game
{
    public sealed record SyntheticEnemyHit(int Damage, string Mitigation, uint Life, uint Mana, uint ManaBefore,
        bool Blocked, int BlockChance, bool Suppressed, (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression);

    /// <summary>A synthetic incoming enemy contact of one damage type through the player's whole defence (avoidance, block when
    /// <paramref name="block"/>, resistance, armour, suppression when <paramref name="suppress"/>), as a monster's hit; for tests
    /// and testing tools. False when the player cannot be hit now.</summary>
    // Admission, prior-history decision, pool mutation and recording are a single combat commit.
    public static bool TrySyntheticEnemyHit(ClientState conn, DamageType type, int amount, bool block, bool spell, bool suppress,
        out SyntheticEnemyHit hit, bool ranged = false)
    {
        lock (conn.Monsters)
        {
            hit = null!;
            if (conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return false;
            double now = conn.Clock.Elapsed.TotalSeconds;
            int priorContacts = conn.RecentEnemyHits.Count(now);
            using var defenceTime = FortificationOf(conn).Capture(now);
            // Refresh lazy aura/Fortification pools before reading ANY final defence at the accepted contact.
            var stats = CharacterPlayerStats(conn);
            GainLifeBeforeEnemyHitLocked(conn, stats);
            int resistance = Resistance(conn, type), maximum = MaxResistance(conn, type);
            // Avoidance rolls before block, as for a monster hit; an avoided hit still lands. Elusive's is one
            // "Avoid All Damage from Hits" roll for the whole hit.
            int elusiveAvoid = ElusiveAvoidPct(conn);
            bool avoided = (elusiveAvoid > 0 && PlayerDamageTaken.AvoidsAll(elusiveAvoid, conn.IncomingHitRandom))
                || PlayerDamageTaken.Avoids(type, name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0,
                    conn.IncomingHitRandom);
            int blockChance = spell ? conn.Pools.SpellBlockChance : conn.Pools.BlockChance;
            bool luckyBlock = spell ? conn.Pools.SpellBlockLucky : conn.Pools.AttackBlockLucky;
            bool unluckyBlock = spell ? conn.Pools.SpellBlockUnlucky : conn.Pools.AttackBlockUnlucky;
            bool blocked = block && DamageModel.Blocks(blockChance, conn.IncomingHitRandom.NextDouble() * 100, luckyBlock, unluckyBlock);
            if (!blocked && DamageTypes.IsElemental(type) && GamblerFor(stats).UsesNinetyResistance(conn.IncomingHitRandom))
                resistance = maximum = 90;
            bool immune = PlayerImmunities(conn).Contains(type);
            bool armourRelevant = type == DamageType.Physical ? amount > 0
                : PlayerHitMitigation.ArmourPercent(conn.Pools, type, name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0) > 0
                    && DamageModel.Mitigate(amount, resistance, maximum) > 0;
            var armour = IncomingArmourLocked(conn, !blocked && !immune && !avoided && armourRelevant, priorContacts, stats);
            int dealt = immune || avoided ? 0 : MitigateTakenWithArmour(conn, type, amount, resistance, maximum, armour.EffectiveArmour,
                stats, attack: !spell, ranged: ranged && !spell);
            // A blocked hit deals only its share ("You take X% of Damage from Blocked Hits", 0 by default).
            if (blocked) dealt = (int)Math.Round(dealt * PlayerDamageTaken.BlockedPct(type,
                name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0) / 100.0);
            string mitigation = immune ? "immunity" : avoided ? "avoidance"
                : MitigationLabel(conn, type, amount, resistance, maximum, armour.EffectiveArmour, stats);
            var suppression = suppress ? Suppression(conn) : default;
            bool suppressed = suppress && DamageModel.Suppresses(suppression.Chance,
                conn.IncomingHitRandom.Next(100), suppression.Lucky, suppression.Unlucky);
            if (suppressed) dealt = DamageModel.Suppress(dealt, suppression.Effect);
            uint manaBefore = conn.PlayerMana;
            var onBlock = blocked ? BlockRecovery(conn, stats) : default;
            int gainedLife = blocked ? conn.Pools.LifeGainedOnBlock + onBlock.Life : 0;
            int gainedMana = blocked ? conn.Pools.ManaGainedOnBlock + onBlock.Mana : 0;
            var taken = DamagePacket.Of(type, dealt, dealt);
            RecoupHitLocked(conn, taken, now, stats);
            if (type == DamageType.Physical) PreventedDamageOf(conn).Add(now, amount - dealt);
            uint lifeBefore = conn.PlayerLife;
            conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, Math.Max(0, (long)conn.PlayerLife
                - DamagePlayerHitTyped(conn, type == DamageType.Physical ? dealt : 0, type is not (DamageType.Physical or DamageType.Chaos) ? dealt : 0, type == DamageType.Chaos ? dealt : 0, now, stats,
                    taken) + gainedLife));
            if (block) conn.PlayerMana = (uint)Math.Min((uint)UnreservedMana(conn), (long)conn.PlayerMana + gainedMana);
            conn.EnergyShield.Recover(onBlock.EnergyShield);
            TrimLeechLocked(conn);
            conn.RecentEnemyHits.Record(now);
            conn.RecentEvents.Record(StatConditions.BeenHit, now);
            if (blocked)
            {
                conn.RecentEvents.Record(StatConditions.Blocked, now);
                conn.RecentEvents.Record(spell ? StatConditions.BlockedSpell : StatConditions.BlockedAttack, now);
            }
            if (suppressed) conn.RecentEvents.Record(StatConditions.SuppressedSpell, now);
            if (lifeBefore > conn.PlayerLife && (long)(lifeBefore - conn.PlayerLife) * 100 >= (long)conn.PlayerMaxLife * 15)
                conn.RecentEvents.Record(StatConditions.TakenSavageHit, now);
            if (block) ChargesWhenHit(conn, blocked, spell);
            if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); ClearAntiquarianOnDeath(conn); }
            RecordHardcoreDeath(conn);
            hit = new(dealt, mitigation, conn.PlayerLife, conn.PlayerMana, manaBefore,
                blocked, blockChance, suppressed, suppression);
            return true;
        }
    }

    // What MitigateTaken applied, for a hit's report: armour or resistance, and the additional reduction.
    public static string MitigationLabel(ClientState conn, DamageType type, int amount, int resistance, int maxResistance,
        int effectiveArmour, IReadOnlyDictionary<uint, int> stats)
    {
        var (physical, elemental) = PlayerDamageReduction(conn, stats);
        if (type == DamageType.Physical)
            return $"{DamageModel.ArmourReduction(effectiveArmour, amount)}% armour" + (physical > 0 ? $" + {physical}% physical damage reduction" : "");
        int share = PlayerHitMitigation.ArmourPercent(conn.Pools, type, name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0);
        return $"{DamageModel.Capped(resistance, maxResistance)}% resistance" +
            (share > 0
                ? $" + {DamageModel.ArmourReduction((int)((long)effectiveArmour * share / 100), DamageModel.Mitigate(amount, resistance, maxResistance))}% armour"
                : "") +
            (DamageTypes.IsElemental(type) && elemental > 0 ? $" + {elemental}% elemental damage reduction" : "");
    }
}
