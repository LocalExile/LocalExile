using GameServer.Core.World;

static partial class CommandsRules
{
    internal sealed record DevEnemyHit(int Damage, string Mitigation, uint Life, uint Mana, uint ManaBefore,
        bool Blocked, int BlockChance, bool Suppressed, (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression);

    // /hit and /spellhit are synthetic incoming enemy contacts. /blockhit can additionally block.
    // Admission, prior-history decision, pool mutation and recording are a single combat commit.
    internal static bool TryDevEnemyHit(ClientState conn, DamageType type, int amount, bool block, bool spell, bool suppress,
        out DevEnemyHit hit, bool ranged = false)
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
            // Avoidance rolls before block, as for a monster hit; an avoided hit still lands.
            bool avoided = PlayerDamageTaken.Avoids(type, name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0,
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
            conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, Math.Max(0, (long)conn.PlayerLife
                - DamagePlayerHit(conn, type == DamageType.Chaos ? 0 : dealt, type == DamageType.Chaos ? dealt : 0, now, stats,
                    taken) + gainedLife));
            if (block) conn.PlayerMana = (uint)Math.Min((uint)UnreservedMana(conn), (long)conn.PlayerMana + gainedMana);
            conn.EnergyShield.Recover(onBlock.EnergyShield);
            TrimLeechLocked(conn);
            conn.RecentEnemyHits.Record(now);
            if (block) ChargesWhenHit(conn, blocked, spell);
            if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); ClearAntiquarianOnDeath(conn); }
            RecordHardcoreDeath(conn);
            hit = new(dealt, mitigation, conn.PlayerLife, conn.PlayerMana, manaBefore,
                blocked, blockChance, suppressed, suppression);
            return true;
        }
    }
}
