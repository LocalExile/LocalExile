using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

// Hostile monster Endurance, Frenzy and Power charges.
// Retail 3.29 values verified directly from Metadata/Monsters/Monster.ot:
//   - Max charges: 3 of each, 10 s base duration
//   - Endurance (hostile): +15% all elemental resistances, +15% additional physical damage reduction per charge
//   - Frenzy (hostile):    +15% attack speed, +15% cast speed, +5% movement speed, +4% more damage per charge
//   - Power (hostile):     +200% increased critical strike chance per charge
// Unlocks MonsterModEnduranceChargeGain ("of Endurance"), MonsterModFrenzyChargeGain ("of Frenzy"),
// MonsterModPowerChargeGain ("of Power"), MonsterModGainCharges ("Charged"), MonsterModFrenzied ("Enraging"),
// and species charge mods such as MonsterImplicitGainsPowerChargeOnHit1.
public static partial class Game
{
    const int MonsterMaxCharges = 3;
    const double MonsterChargeDurationSeconds = 10.0;
    const double MonsterDaemonChargeIntervalSeconds = 3.0;
    const double MonsterDaemonChargeRadiusCells = 45.0;
    const ushort EnduranceChargeBuffRow = 28;
    const ushort FrenzyChargeBuffRow = 27;
    const ushort PowerChargeBuffRow = 29;
    const string MonsterChargeStatsKey = "monster_charges";

    sealed class MonsterChargeState
    {
        public int Endurance, Frenzy, Power;
        public double EnduranceUntil = double.NegativeInfinity;
        public double FrenzyUntil = double.NegativeInfinity;
        public double PowerUntil = double.NegativeInfinity;
        public double LastEnduranceTickAt = double.NegativeInfinity;
        public double LastDaemonPulseAt = double.NegativeInfinity;
        public bool HasChargedMod, HasEnragingMod, ModFlagsChecked;
        public int AppliedEndurance, AppliedFrenzy, AppliedPower;
        public readonly Dictionary<uint, int> StatBuffer = new(6);
    }

    static readonly RuleTable<SpawnedMonster, MonsterChargeState> MonsterCharges = new();

    static ushort _chargedModHash;
    static ushort _enragingModHash;

    /// <summary>Pre-resolves daemon charge mod hashes once at startup (<see cref="LoadMonsterModsData"/>).</summary>
    public static void LoadMonsterChargesData()
    {
        _chargedModHash = MonsterModRolls.Mods.ById("MonsterModGainCharges")?.Hash ?? 0;
        _enragingModHash = MonsterModRolls.Mods.ById("MonsterModFrenzied")?.Hash ?? 0;
    }

    static MonsterChargeState GetOrCreateChargeState(SpawnedMonster m) =>
        MonsterCharges.GetValue(m, _ => new MonsterChargeState());

    static void EnsureChargeModFlags(SpawnedMonster m, MonsterChargeState state)
    {
        if (state.ModFlagsChecked) return;
        state.ModFlagsChecked = true;
        ushort charged = _chargedModHash, enraging = _enragingModHash;
        var list = m.Mods.Mods;
        for (int i = 0; i < list.Count; i++)
        {
            ushort h = list[i].Hash;
            if (charged != 0 && h == charged) state.HasChargedMod = true;
            if (enraging != 0 && h == enraging) state.HasEnragingMod = true;
        }
    }

    /// <summary>Damage multiplier from active Frenzy charges on <paramref name="m"/> (+4% more damage per charge,
    /// from Monster.ot <c>object_inherent_damage_+%_final_per_frenzy_charge = 4</c>).</summary>
    public static double MonsterFrenzyDamageFactor(SpawnedMonster m) =>
        MonsterCharges.TryGetValue(m, out var s) && s.Frenzy > 0
            ? 1.0 + 0.04 * s.Frenzy : 1.0;

    /// <summary>Active (Endurance, Frenzy, Power) charge counts on <paramref name="m"/>.</summary>
    public static (int Endurance, int Frenzy, int Power) MonsterChargesOf(SpawnedMonster m) =>
        MonsterCharges.TryGetValue(m, out var s)
            ? (s.Endurance, s.Frenzy, s.Power) : (0, 0, 0);

    static int MonsterChargeMaximum(SpawnedMonster monster, ChargeType type) =>
        Math.Max(0, ActorScale.Saturate(MonsterMaxCharges + (long)(type switch
        {
            ChargeType.Endurance => monster.Effects[Stat.MapMonsterMaximumEnduranceCharges],
            ChargeType.Frenzy => monster.Effects[Stat.MapMonsterMaximumFrenzyCharges],
            ChargeType.Power => monster.Effects[Stat.MapMonsterMaximumPowerCharges],
            _ => 0
        })));

    /// <summary>Grants <paramref name="count"/> charges of <paramref name="type"/> to <paramref name="m"/>, capped by its maximum
    /// and lasting 10 s, and immediately syncs its temporary stats and client charge buff (refreshing duration even
    /// when already at max stacks).</summary>
    public static void GrantMonsterChargeLocked(GameArea? area, uint id, SpawnedMonster m, ChargeType type, double now, int count = 1)
    {
        if (count <= 0 || m.Dead || m.Life == 0 || !m.Hostile) return;
        var state = GetOrCreateChargeState(m);
        double until = now + MonsterChargeDurationSeconds;
        int maximum = MonsterChargeMaximum(m, type);
        switch (type)
        {
            case ChargeType.Endurance:
                state.Endurance = (int)Math.Min(maximum, (long)state.Endurance + count);
                state.EnduranceUntil = until;
                break;
            case ChargeType.Frenzy:
                state.Frenzy = (int)Math.Min(maximum, (long)state.Frenzy + count);
                state.FrenzyUntil = until;
                break;
            case ChargeType.Power:
                state.Power = (int)Math.Min(maximum, (long)state.Power + count);
                state.PowerUntil = until;
                break;
        }
        SyncMonsterChargeStatsLocked(area, id, m, state, now, refreshType: type);
    }

    /// <summary>Called when a monster deals a hit (or casts a spell) on the player: triggers <c>of Frenzy</c>,
    /// <c>of Power</c>, <c>MonsterImplicitGainsPowerChargeOnHit1</c>, and <c>of Enervation</c> charge removal.</summary>
    public static void MonsterChargesOnHitDealtLocked(Player player, uint id, SpawnedMonster m, bool spell, bool blocked, double now, Random random)
    {
        if (m.Dead || m.Life == 0 || !m.Hostile) return;
        var e = m.Effects;
        if (!blocked)
        {
            StealMapChargesLocked(player, id, m, victim: null, now, random);
            GainMonsterHitChargesLocked(player.Area, id, m, now, random);
        }

        if (spell && e[Stat.GainXPowerChargesOnCastingASpell] is int spellPower and > 0)
            GrantMonsterChargeLocked(player.Area, id, m, ChargeType.Power, now, spellPower);

        if (!blocked && e[Stat.RemoveChargesOnHitPct] is int removePct and > 0
            && RandomRolls.Chance(removePct, 100, random))
        {
            // of Enervation: removes one Endurance, Frenzy and Power charge from the hit player (respecting converted
            // charges, minimum charges, loss triggers and client presentation sync via ChargeSession).
            var charges = ChargesOf(player);
            foreach (var type in Charges.Types)
                if (!charges.Converted(type) && charges.CanSpend(type))
                    charges.Enqueue(new ChargeEvent.Spend(type, 1, "of Enervation"));
        }
    }

    static void GainMonsterHitChargesLocked(GameArea area, uint id, SpawnedMonster monster, double now, Random random)
    {
        if (ItemEffectRules.Enabled)
        {
            foreach (var (stat, type) in ItemEffectRules.Current.MonsterHitCharges)
                if (RandomRolls.Chance(monster.Effects[stat], 100, random))
                    GrantMonsterChargeLocked(area, id, monster, type, now);
            return;
        }
        if (monster.Effects[Stat.AddFrenzyChargeOnSkillHitPct] is > 0 and var frenzy
            && RandomRolls.Chance(frenzy, 100, random))
            GrantMonsterChargeLocked(area, id, monster, ChargeType.Frenzy, now);
        if (monster.Effects[Stat.AddPowerChargeOnSkillHitPct] is > 0 and var power
            && RandomRolls.Chance(power, 100, random))
            GrantMonsterChargeLocked(area, id, monster, ChargeType.Power, now);
    }

    static void GainMonsterChargeWhenHitLocked(Player player, SpawnedMonster monster, double now)
    {
        if (monster.Effects[Stat.ChanceToGainEnduranceChargeWhenHitPct] is > 0 and var chance
            && RandomRolls.Chance(chance, 100, AreaRng(player)))
            GrantMonsterChargeLocked(player.Area, monster.ObjectId, monster, ChargeType.Endurance, now);
    }

    static void StealMapChargesLocked(Player player, uint attacker, SpawnedMonster monster, GolemState.Golem? victim,
        double now, Random random)
    {
        if (!monster.Hostile || monster.Dead || monster.Life == 0) return;
        int chance = monster.Effects[Stat.MapMonstersStealCharges];
        if (chance <= 0 || !RandomRolls.Chance(chance, 100, random)) return;
        if (victim is null)
        {
            var charges = ChargesOf(player);
            foreach (var type in Charges.Types)
                if (!charges.Converted(type) && charges.Spend(type))
                    GrantMonsterChargeLocked(player.Area, attacker, monster, type, now);
            return;
        }
        if (victim.ChargesFollowOwner || victim.CombatCharges is not { } local) return;
        int Get(uint key) => victim.Monster.Effects[key];
        foreach (var type in Charges.Types)
        {
            int? maximum = GolemStat(victim.Player, Stat.MinionsHaveSameMaximumNumOfChargesAsOwner) != 0
                ? ChargeMaxima.Of(type, key => GolemStat(victim.Player, key)) : null;
            var limits = Charges.LimitsOf(type, Get, local.Held(type), maximum);
            var (before, count) = local.Lose(type, 1, limits);
            if (count == before) continue;
            PublishMinionChargeLocked(player, victim, type, count, local.ExpiresAt(type), now);
            GrantMonsterChargeLocked(player.Area, attacker, monster, type, now);
        }
    }

    /// <summary>Steps monster charge expiry and periodic generators (<c>of Endurance</c>, <c>Charged</c>,
    /// <c>Enraging</c>) once per recovery tick (every 0.5 s).</summary>
    public static void StepMonsterChargesLocked(GameArea area, IReadOnlyList<(uint Id, SpawnedMonster Monster)> living, double now, Random rng)
    {
        ushort chargedHash = _chargedModHash, enragingHash = _enragingModHash;
        for (int i = 0; i < living.Count; i++)
        {
            var (id, m) = living[i];
            if (!m.Hostile || m.Dead || m.Life == 0) continue;

            bool hasEnduranceGen = m.Effects[Stat.GainEnduranceChargePerSecondIfHaveBeenHitRecently] > 0;
            bool hasState = MonsterCharges.TryGetValue(m, out var state);
            if (!hasState)
            {
                bool hasDaemonMod = false;
                if (m.Rarity is MonsterRarity.Magic or MonsterRarity.Rare)
                {
                    var modList = m.Mods.Mods;
                    for (int j = 0; j < modList.Count; j++)
                    {
                        ushort h = modList[j].Hash;
                        if ((chargedHash != 0 && h == chargedHash) || (enragingHash != 0 && h == enragingHash))
                        {
                            hasDaemonMod = true;
                            break;
                        }
                    }
                }
                if (!hasEnduranceGen && !hasDaemonMod) continue;
                state = GetOrCreateChargeState(m);
            }

            EnsureChargeModFlags(m, state!);

            // Expire elapsed charges.
            if (state!.Endurance > 0 && now >= state.EnduranceUntil) state.Endurance = 0;
            if (state.Frenzy > 0 && now >= state.FrenzyUntil) state.Frenzy = 0;
            if (state.Power > 0 && now >= state.PowerUntil) state.Power = 0;

            ChargeType? refreshed = null;
            // of Endurance: +1 Endurance charge every 1 s while hit within the last 4 s.
            if (hasEnduranceGen && now - m.PlayerHitAt <= RecentlySeconds && now - state.LastEnduranceTickAt >= 1.0)
            {
                state.LastEnduranceTickAt = now;
                int gain = m.Effects[Stat.GainEnduranceChargePerSecondIfHaveBeenHitRecently];
                state.Endurance = (int)Math.Min(MonsterChargeMaximum(m, ChargeType.Endurance), (long)state.Endurance + gain);
                state.EnduranceUntil = now + MonsterChargeDurationSeconds;
                refreshed = ChargeType.Endurance;
            }

            // Charged / Enraging: periodically pulse charges to self and nearby living hostile allies.
            if ((state.HasChargedMod || state.HasEnragingMod) && now - state.LastDaemonPulseAt >= MonsterDaemonChargeIntervalSeconds)
            {
                state.LastDaemonPulseAt = now;
                var origin = m.Walk.Position(now);
                double r2 = MonsterDaemonChargeRadiusCells * MonsterDaemonChargeRadiusCells;
                ChargeType pulseType = state.HasChargedMod
                    ? (ChargeType)rng.Next(3)
                    : ChargeType.Frenzy;
                for (int k = 0; k < living.Count; k++)
                {
                    var (allyId, ally) = living[k];
                    if (!ally.Hostile || ally.Dead || ally.Life == 0) continue;
                    var at = ally.Walk.Position(now);
                    double dx = at.X - origin.X, dy = at.Y - origin.Y;
                    if (dx * dx + dy * dy <= r2)
                        GrantMonsterChargeLocked(area, allyId, ally, pulseType, now);
                }
            }

            SyncMonsterChargeStatsLocked(area, id, m, state, now, refreshType: refreshed);
        }
    }

    /// <summary>Clears monster charges, temporary stats, and charge buff icons (rows 27/28/29) on death.</summary>
    public static void ClearMonsterChargesLocked(GameArea area, uint id, SpawnedMonster m)
    {
        if (!MonsterCharges.TryGetValue(m, out var state)) return;
        bool hadEndurance = state.AppliedEndurance > 0 || state.Endurance > 0;
        bool hadFrenzy = state.AppliedFrenzy > 0 || state.Frenzy > 0;
        bool hadPower = state.AppliedPower > 0 || state.Power > 0;
        state.Endurance = state.Frenzy = state.Power = 0;
        state.AppliedEndurance = state.AppliedFrenzy = state.AppliedPower = 0;
        if (hadEndurance || hadFrenzy || hadPower)
            m.RemoveTemporaryStats(MonsterChargeStatsKey);
        if (id == 0) return;
        if (hadEndurance) area.Buffs.ClearRow(id, EnduranceChargeBuffRow);
        if (hadFrenzy) area.Buffs.ClearRow(id, FrenzyChargeBuffRow);
        if (hadPower) area.Buffs.ClearRow(id, PowerChargeBuffRow);
    }

    static void SyncMonsterChargeStatsLocked(GameArea? area, uint id, SpawnedMonster m, MonsterChargeState state, double now, ChargeType? refreshType = null)
    {
        bool countsChanged = state.Endurance != state.AppliedEndurance
            || state.Frenzy != state.AppliedFrenzy
            || state.Power != state.AppliedPower;
        if (!countsChanged && refreshType is null)
            return;

        bool hadEndurance = state.AppliedEndurance > 0;
        bool hadFrenzy = state.AppliedFrenzy > 0;
        bool hadPower = state.AppliedPower > 0;

        if (countsChanged)
        {
            state.AppliedEndurance = state.Endurance;
            state.AppliedFrenzy = state.Frenzy;
            state.AppliedPower = state.Power;

            if (state.Endurance == 0 && state.Frenzy == 0 && state.Power == 0)
            {
                m.RemoveTemporaryStats(MonsterChargeStatsKey);
            }
            else
            {
                var buf = state.StatBuffer;
                buf.Clear();
                if (state.Endurance > 0)
                {
                    buf[Stat.BaseResistAllElementsPct] = 15 * state.Endurance;
                    buf[Stat.BaseAdditionalPhysicalDamageReductionPct] = 15 * state.Endurance;
                }
                if (state.Frenzy > 0)
                {
                    buf[Stat.AttackSpeedPlusPct] = 15 * state.Frenzy;
                    buf[Stat.BaseCastSpeedPlusPct] = 15 * state.Frenzy;
                    buf[Stat.BaseMovementVelocityPlusPct] = 5 * state.Frenzy;
                }
                if (state.Power > 0)
                {
                    buf[Stat.CriticalStrikeChancePlusPct] = 200 * state.Power;
                }
                m.SetTemporaryStats(MonsterChargeStatsKey, new Dictionary<uint, int>(buf));
            }
        }

        if (area is null || id == 0) return;
        if (countsChanged || refreshType == ChargeType.Endurance)
            SyncChargeBuff(area, id, EnduranceChargeBuffRow, state.Endurance, hadEndurance, state.EnduranceUntil - now);
        if (countsChanged || refreshType == ChargeType.Frenzy)
            SyncChargeBuff(area, id, FrenzyChargeBuffRow, state.Frenzy, hadFrenzy, state.FrenzyUntil - now);
        if (countsChanged || refreshType == ChargeType.Power)
            SyncChargeBuff(area, id, PowerChargeBuffRow, state.Power, hadPower, state.PowerUntil - now);
    }

    static void SyncChargeBuff(GameArea area, uint id, ushort row, int count, bool had, double remaining)
    {
        if (count > 0)
        {
            area.Buffs.Set(id, new(row, id, Stacks: (ushort)count,
                U68: remaining > 0 ? Buff.Seconds(remaining) : null));
        }
        else if (had)
        {
            area.Buffs.ClearRow(id, row);
        }
    }
}
