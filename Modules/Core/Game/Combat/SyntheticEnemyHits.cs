using GameServer.Core.World;

public static partial class Game
{
    public sealed record SyntheticEnemyHit(int Damage, string Mitigation, uint Life, uint Mana, uint ManaBefore,
        bool Blocked, int BlockChance, bool Suppressed, (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression,
        DamagePacket Taken);

    /// <summary>A synthetic incoming enemy contact of one damage type through the player's whole defence (shift, avoidance, block when
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
            uint manaBefore = player.PlayerMana;
            GainManaBeforeEnemyHitLocked(player, stats);
            // The order a monster's hit takes (Monsters.cs): damage taken as shifts the packet, each type taken is avoided
            // (Elusive's one roll for the whole hit first), the Gambler's one 90% resistance roll and each type's resistance,
            // then block, one armour decision for the hit, and each part through armour and the rest.
            var shifted = PlayerDamageTaken.Shift(DamagePacket.Of(type, amount, amount), stats.GetValueOrDefault);
            var kept = PlayerDamageTaken.Avoid(shifted, stats.GetValueOrDefault, player.IncomingHitRandom, out var avoided,
                ElusiveAvoidPct(player));
            var mitigated = HitRollsFor(stats).MitigateIncomingHit(kept, t =>
                new DamageModel.ResistanceValue(Resistance(player, t), MaxResistance(player, t)), player.IncomingHitRandom, out bool ninety);
            var immune = PlayerImmunities(player, stats);
            int blockChance = spell ? player.Pools.SpellBlockChance : player.Pools.BlockChance;
            bool luckyBlock = spell ? player.Pools.SpellBlockLucky : player.Pools.AttackBlockLucky;
            bool unluckyBlock = spell ? player.Pools.SpellBlockUnlucky : player.Pools.AttackBlockUnlucky;
            bool blocked = block && DamageModel.Blocks(blockChance, player.IncomingHitRandom.NextDouble() * 100, luckyBlock, unluckyBlock);
            bool armourRelevant = mitigated.Components.Any(c => !immune.Contains(c.Type)
                && Math.Round(c.Range.Min) > 0 && PlayerHitMitigation.ArmourPercent(player.Pools, c.Type, stats.GetValueOrDefault) > 0);
            var armour = IncomingArmourLocked(player, !blocked && armourRelevant, priorContacts, stats);
            string mitigation = MitigationLabels(player, shifted, avoided, immune, ninety, armour.EffectiveArmour, stats);
            var suppression = suppress ? Suppression(player) : default;
            bool suppressed = suppress && DamageModel.Suppresses(suppression.Chance,
                player.IncomingHitRandom, suppression.Lucky, suppression.Unlucky);
            // Each part through armour and the rest, kept as the damage taken the ailments and recoup read.
            var ailmentTaken = DamagePacket.Empty;
            int physicalDealt = 0, chaosDealt = 0, dealt = 0;
            foreach (var (part, range) in mitigated.Components)
            {
                if (immune.Contains(part)) continue;
                int value = TakenAfterResistance(player, part, (int)Math.Round(range.Min), armour.EffectiveArmour, stats,
                    attack: !spell, ranged: ranged && !spell, spell: spell);
                // A blocked hit deals only its share ("You take X% of Damage from Blocked Hits", 0 by default).
                if (blocked) value = (int)Math.Round(value * PlayerDamageTaken.BlockedPct(part, stats.GetValueOrDefault) / 100.0);
                if (suppressed) value = DamageModel.Suppress(value, suppression.Effect);
                ailmentTaken = ailmentTaken.Add(part, new(value, value));
                dealt += value;
                if (part == DamageType.Physical) physicalDealt += value;
                else if (part == DamageType.Chaos) chaosDealt += value;
            }
            // Fortification once, on the totals, as for a monster's hit.
            dealt = FortificationDamageTakenLocked(player, dealt, now, stats);
            chaosDealt = FortificationDamageTakenLocked(player, chaosDealt, now, stats);
            physicalDealt = Math.Min(FortificationDamageTakenLocked(player, physicalDealt, now, stats), Math.Max(0, dealt - chaosDealt));
            if (blocked) RecoverOnBlockLocked(player, stats, now, attack: !spell);
            var recouped = ailmentTaken.Select((_, r) => new(FortificationDamageTakenLocked(player, (int)r.Min, now, stats),
                FortificationDamageTakenLocked(player, (int)r.Max, now, stats)));
            RecoupHitLocked(player, recouped, now, stats);
            if (shifted[DamageType.Physical].Min > 0 || type == DamageType.Physical)
                PreventedDamageOf(player).Add(now, shifted[DamageType.Physical].Min - physicalDealt);
            uint lifeBefore = player.PlayerLife;
            int lifeDamage = DamagePlayerHitTyped(player, physicalDealt, Math.Max(0, dealt - physicalDealt - chaosDealt), chaosDealt,
                now, stats, recouped);
            player.PlayerLife = (uint)Math.Max(0, (long)lifeBefore - lifeDamage);
            TrimLeechLocked(player);
            RecordIncomingHitLocked(player, now, spell, blocked, suppressed, lifeDamage, dealt > 0, attack: !spell);
            RecentHitLifeLossOnHitLocked(player, now);
            UnhingedHitRecoveryLocked(player, now, stats);
            if (player.PlayerLife == 0) { player.InvalidateIncomingHits(); ClearWardBreakOnDeath(player); }
            RecordHardcoreDeath(player);
            hit = new(dealt, mitigation, player.PlayerLife, player.PlayerMana, manaBefore,
                blocked, blockChance, suppressed, suppression, recouped);
            return true;
        }
    }

    // The report of a hit: one label per type taken (avoided, immune, or what mitigated it); the type is named when the
    // hit has several.
    static string MitigationLabels(Player player, DamagePacket shifted, DamageType[] avoided, HashSet<DamageType> immune,
        bool ninety, int effectiveArmour, IReadOnlyDictionary<uint, int> stats)
    {
        var labels = new List<string>();
        foreach (var (part, range) in shifted.Components)
        {
            bool ninetyPart = ninety && DamageTypes.IsElemental(part);
            labels.Add(avoided.Contains(part) ? "avoidance" : immune.Contains(part) ? "immunity"
                : MitigationLabel(player, part, (int)Math.Round(range.Min), ninetyPart ? 90 : Resistance(player, part),
                    ninetyPart ? 90 : MaxResistance(player, part), effectiveArmour, stats));
        }
        return labels.Count == 1 ? labels[0]
            : string.Join("; ", shifted.Components.Select((c, i) => DamageTypes.Name(c.Type) + ": " + labels[i]));
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
