using GameServer.Core.World;

public static partial class Game
{
    public sealed record SyntheticEnemyHit(int Damage, string Mitigation, uint Life, uint Mana, uint ManaBefore,
        bool Blocked, int BlockChance, bool Suppressed, (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression);

    /// <summary>A synthetic incoming enemy contact of one damage type through the player's whole defence (avoidance, block when
    /// <paramref name="block"/>, resistance, armour, suppression when <paramref name="suppress"/>), as a monster's hit; for tests
    /// and testing tools. False when the player cannot be hit now.</summary>
    // Admission, prior-history decision, pool mutation and recording are a single combat commit.
    public static bool TrySyntheticEnemyHit(Player player, DamageType type, int amount, bool block, bool spell, bool suppress,
        out SyntheticEnemyHit hit, bool ranged = false)
    {
        {
            hit = null!;
            if (player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return false;
            double now = AreaTime(player);
            int priorContacts = player.RecentEnemyHits.Count(now);
            using var defenceTime = FortificationOf(player).Capture(now);
            // Refresh lazy aura/Fortification pools before reading ANY final defence at the accepted contact.
            var stats = CharacterPlayerStats(player);
            if (spell && DodgesSpellHit(player)) return false;
            GainLifeBeforeEnemyHitLocked(player, stats);
            int resistance = Resistance(player, type), maximum = MaxResistance(player, type);
            // Avoidance rolls before block, as for a monster hit; an avoided hit still lands. Elusive's is one
            // "Avoid All Damage from Hits" roll for the whole hit.
            int elusiveAvoid = ElusiveAvoidPct(player);
            bool avoided = (elusiveAvoid > 0 && PlayerDamageTaken.AvoidsAll(elusiveAvoid, player.IncomingHitRandom))
                || PlayerDamageTaken.Avoids(type, name => name is var key ? stats.GetValueOrDefault(key) : 0,
                    player.IncomingHitRandom);
            int blockChance = spell ? player.Pools.SpellBlockChance : player.Pools.BlockChance;
            bool luckyBlock = spell ? player.Pools.SpellBlockLucky : player.Pools.AttackBlockLucky;
            bool unluckyBlock = spell ? player.Pools.SpellBlockUnlucky : player.Pools.AttackBlockUnlucky;
            bool blocked = block && DamageModel.Blocks(blockChance, player.IncomingHitRandom.NextDouble() * 100, luckyBlock, unluckyBlock);
            if (!blocked && DamageTypes.IsElemental(type) && GamblerFor(stats).UsesNinetyResistance(player.IncomingHitRandom))
                resistance = maximum = 90;
            bool immune = PlayerImmunities(player, stats).Contains(type);
            bool armourRelevant = amount > 0
                && PlayerHitMitigation.ArmourPercent(player.Pools, type, stats.GetValueOrDefault) > 0
                && (type == DamageType.Physical || DamageModel.Mitigate(amount, resistance, maximum) > 0);
            var armour = IncomingArmourLocked(player, !blocked && !immune && !avoided && armourRelevant, priorContacts, stats);
            int dealt = immune || avoided ? 0 : MitigateTakenWithArmour(player, type, amount, resistance, maximum, armour.EffectiveArmour,
                stats, attack: !spell, ranged: ranged && !spell, spell: spell);
            // A blocked hit deals only its share ("You take X% of Damage from Blocked Hits", 0 by default).
            if (blocked) dealt = (int)Math.Round(dealt * PlayerDamageTaken.BlockedPct(type,
                name => name is var key ? stats.GetValueOrDefault(key) : 0) / 100.0);
            string mitigation = immune ? "immunity" : avoided ? "avoidance"
                : MitigationLabel(player, type, amount, resistance, maximum, armour.EffectiveArmour, stats);
            var suppression = suppress ? Suppression(player) : default;
            bool suppressed = suppress && DamageModel.Suppresses(suppression.Chance,
                player.IncomingHitRandom.Next(100), suppression.Lucky, suppression.Unlucky);
            if (suppressed) dealt = DamageModel.Suppress(dealt, suppression.Effect);
            uint manaBefore = player.PlayerMana;
            if (blocked) RecoverOnBlockLocked(player, stats);
            var taken = DamagePacket.Of(type, dealt, dealt);
            RecoupHitLocked(player, taken, now, stats);
            if (type == DamageType.Physical) PreventedDamageOf(player).Add(now, amount - dealt);
            uint lifeBefore = player.PlayerLife;
            int lifeDamage = DamagePlayerHitTyped(player, type == DamageType.Physical ? dealt : 0,
                type is not (DamageType.Physical or DamageType.Chaos) ? dealt : 0,
                type == DamageType.Chaos ? dealt : 0, now, stats, taken);
            player.PlayerLife = (uint)Math.Max(0, (long)lifeBefore - lifeDamage);
            TrimLeechLocked(player);
            RecordIncomingHitLocked(player, now, spell, blocked, suppressed, lifeDamage, dealt > 0);
            RecentHitLifeLossOnHitLocked(player, now);
            UnhingedHitRecoveryLocked(player, now, stats);
            if (player.PlayerLife == 0) { player.InvalidateIncomingHits(); ClearAntiquarianOnDeath(player); }
            RecordHardcoreDeath(player);
            hit = new(dealt, mitigation, player.PlayerLife, player.PlayerMana, manaBefore,
                blocked, blockChance, suppressed, suppression);
            return true;
        }
    }

    // What MitigateTaken applied, for a hit's report: armour or resistance, and the additional reduction.
    public static string MitigationLabel(Player player, DamageType type, int amount, int resistance, int maxResistance,
        int effectiveArmour, IReadOnlyDictionary<uint, int> stats)
    {
        var (physical, elemental) = PlayerDamageReduction(player, stats);
        int share = PlayerHitMitigation.ArmourPercent(player.Pools, type, stats.GetValueOrDefault);
        if (stats.GetValueOrDefault(Stat.KeystoneSacredBastion) != 0) effectiveArmour = ActorScale.Saturate(2L * effectiveArmour);
        if (type == DamageType.Physical)
            return $"{DamageModel.ArmourReduction(share > 0 ? effectiveArmour : 0, amount)}% armour"
                + (physical > 0 ? $" + {physical}% physical damage reduction" : "");
        return $"{DamageModel.Capped(resistance, maxResistance)}% resistance" +
            (share > 0
                ? $" + {DamageModel.ArmourReduction((int)((long)effectiveArmour * share / 100), DamageModel.Mitigate(amount, resistance, maxResistance))}% armour"
                : "") +
            (DamageTypes.IsElemental(type) && elemental > 0 ? $" + {elemental}% elemental damage reduction" : "");
    }
}
