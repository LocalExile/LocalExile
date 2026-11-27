using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>Lethe Shade: "100% more Duration of Ailments on you" (a multiplicative doubling). Reflected ailments are
    /// excluded by the callers, which skip the factor for a reflection (poewiki /wiki/Lethe_Shade).</summary>
    public static double LetheShadeAilmentDuration(Func<uint, int> get) =>
        get(Stat.KeystoneLetheShade) != 0 ? 2 : 1;

    public static PlayerElementalAilments.HitSettings IncomingAilmentSettings(Func<uint, int> get) => new(
        get(Stat.AlwaysIgnite) != 0 ? 100 : get(Stat.BaseChanceToIgnitePct),
        get(Stat.AlwaysFreeze) != 0 ? 100 : get(Stat.BaseChanceToFreezePct),
        get(Stat.AlwaysShock) != 0 ? 100 : get(Stat.BaseChanceToShockPct),
        get(Stat.ActiveSkillChillEffectPlusPctFinal), get(Stat.ActiveSkillShockDurationPlusPctFinal),
        get(Stat.ActiveSkillIgniteDamagePlusPctFinal))
    {
        NeverFreeze = get(Stat.NeverFreeze) != 0,
        AllDamageCanIgnite = get(Stat.AllDamageCanIgnite) != 0 || get(Stat.AllDamageCanIgniteFreezeShock) != 0,
        AllDamageCanFreeze = get(Stat.AllDamageCanFreeze) != 0 || get(Stat.AllDamageCanIgniteFreezeShock) != 0,
        AllDamageCanShock = get(Stat.AllDamageCanShock) != 0 || get(Stat.AllDamageCanIgniteFreezeShock) != 0,
        // Scorch and Brittle (AlternateAilmentsOnPlayerLocked) read the boss skills' chance_to_scorch_% and the like.
        SourceStats = get,
    };

    public static void ApplyPlayerAilmentsLocked(Player player, DamagePacket baseHit, DamagePacket taken,
        bool critical, bool blocked, double now, IReadOnlyDictionary<uint, int> stats, uint? source = null) =>
        ApplyPlayerAilmentsLocked(player, baseHit, taken, critical, blocked, now, stats, default, source);

    public static void ApplyPlayerAilmentsLocked(Player player, DamagePacket baseHit, DamagePacket taken,
        bool critical, bool blocked, double now, IReadOnlyDictionary<uint, int> stats,
        PlayerElementalAilments.HitSettings settings, uint? source = null)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (Get(Stat.AllDamageTakenCanIgnite) != 0) settings = settings with { AllDamageCanIgnite = true };
        // Changing Seasons lets any hit chill (all_damage_taken_can_chill), so a physical one is not skipped, and so does
        // physical damage taken as an element (PlayerDamageTaken.Shift).
        if (blocked || player.Immune || player.Closed || player.LeavingArea || player.PlayerLife == 0 || InGracePeriod(player)
            || !baseHit.Components.Concat(taken.Components).Any(c => DamageTypes.IsElemental(c.Type))
                && !settings.AllDamageCanIgnite && !settings.AllDamageCanFreeze && !settings.AllDamageCanShock
                && Get(Stat.AllDamageTakenCanChill) == 0) return;
        // Ailment damage is a separate ordinary roll from the pre-hit range. Hit luck and the hit's critical
        // multiplier cannot alter its distribution; non-damaging ailments still use the actual mitigated hit.
        var ailmentBase = DamagePacket.Empty;
        if (critical || settings.IgniteChance > 0)
        {
            if (settings.AllDamageCanIgnite) ailmentBase = baseHit.Roll(player.IncomingHitRandom);
            else
            {
                int fire = baseHit[DamageType.Fire].Roll(player.IncomingHitRandom);
                ailmentBase = DamagePacket.Of(DamageType.Fire, fire, fire);
            }
        }
        double durationFactor = LetheShadeAilmentDuration(Get);
        ApplyPlayerAilmentsLocked(player, now, ailments =>
            ailments.Apply(ailmentBase, taken, critical, player.PlayerMaxLife, now, Get, player.IncomingHitRandom, settings, source,
                durationFactor));
    }

    /// <summary>Apply one source's ailments under the combat lock. Hit, ground and reflected sources retain their
    /// own admission, magnitude and duration rules; accepted changes share flask events, recent freeze and movement.</summary>
    public static void ApplyPlayerAilmentsLocked(Player player, double now, Action<PlayerElementalAilments> apply)
    {
        if (player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0) return;
        var previousAilments = player.ElementalAilments.Snapshot(now);
        double previousFreeze = player.ElementalAilments.FreezeStatusUntil(now);
        double previousAction = player.ElementalAilments.ActionFactor(now);
        apply(player.ElementalAilments);
        if (player.ElementalAilments.FreezeStatusUntil(now) > previousFreeze)
            player.RecentEvents.Record(StatConditions.BeenFrozen, now);
        QueueFlaskAilmentChanges(player, previousAilments, player.ElementalAilments.Snapshot(now));
        PlayerAilmentsChangedLocked(player, previousAction, now);
    }

    // A new freeze, or one that outlasts the last, cancels the player's action and channel and holds its walk (ShowFreeze
    // stops the client's too); any change of action speed re-times the walk.
    public static void PlayerAilmentsChangedLocked(Player player, double previousAction, double now)
    {
        double frozenUntil = player.ElementalAilments.FreezeUntil(now);
        if (player.ElementalAilments.ActionFactor(now) == 0 && (previousAction != 0 || frozenUntil > player.FreezeHeldUntil))
        {
            player.FreezeGeneration++;
            player.FreezeHeldUntil = frozenUntil;
            CancelMeleeRepeats(player, now);
            Interlocked.Increment(ref player.ActionCount);
            player.ChannelSkill = 0;
            // A lockstep client's walk ends with the freeze's 0x1d1 instead (ShowFreeze), as with a hit's recovery.
            if (!player.Connection.LockstepClient) player.Walk.Pause(now, frozenUntil - now);
        }
        if (previousAction != player.ElementalAilments.ActionFactor(now)) player.ApplyMovementSpeed();
    }

    public static int AdvancePlayerAilmentsLocked(Player player, double from, double until,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0)
        {
            player.ElementalAilments.Clear();
            player.IncomingAilments.Clear();
            ClearPlayerDecay(player);
            ClearCorruptedBlood(player);
            ClearEssenceAuraExposure(player);
            ClearRemnantAuraExposure(player);
            // The essence pulses' debuffs and Misery stacks end with the player too, or they would outlive a death in
            // the same area.
            MapEssences.ClearEffects(player);
            return 0;
        }
        double previousAction = player.ElementalAilments.ActionFactor(from);
        int Get(uint key) => stats.GetValueOrDefault(key);
        // Lethe Shade: "Take 50% less Damage over Time if you've started taking Damage over Time in the past second".
        // A damaging ailment's latest application is its onset; while one landed within the last second, all the
        // damaging-ailment DoT the player takes is halved (a new application refreshes the window).
        double dotStart = Math.Max(Math.Max(player.IncomingAilments.LatestStart, player.ElementalAilments.LatestIgniteStart), PlayerDecayStarted(player));
        var immunities = PlayerImmunities(player, stats);
        if (Blood.TryGetValue(player, out var blood)) dotStart = Math.Max(dotStart, blood.Started);
        int auraDamage = AdvanceEssenceAuraLocked(player, from, until, stats, immunities.Contains(DamageType.Chaos), dotStart);
        dotStart = Math.Max(dotStart, PlayerEssenceAuraStarted(player));
        auraDamage = (int)Math.Min(int.MaxValue, (long)auraDamage + AdvanceRemnantAuraLocked(player, from, until, stats, immunities.Contains(DamageType.Physical), dotStart));
        dotStart = Math.Max(dotStart, PlayerRemnantAuraStarted(player));
        double letheFactor = Get(Stat.KeystoneLetheShade) != 0 && until - dotStart < 1 ? .5 : 1;
        int damage = 0, lifeDamage = auraDamage;
        if (player.ElementalAilments.HasEffects)
        {
            damage = player.ElementalAilments.Advance(from, until, Get, Resistance(player, DamageType.Fire),
                MaxResistance(player, DamageType.Fire), immunities.Contains(DamageType.Fire),
                source => DamageTakenFromBlindedPct(player, source, stats)
                    + DamageTakenFromMonster(player.Area.Monsters.GetValueOrDefault(source), stats), letheFactor);
            if (Get(Stat.BaseCannotBeDamaged) > 0) damage = 0;
            damage = IncomingResourceDefences.TakenOverTime(damage, Get, ref player.NonChaosDotTakenCarry,
                IncomingResourceDefences.LessTakenPct(player.Pools, Get));
            lifeDamage = (int)Math.Min(int.MaxValue, (long)lifeDamage + (player.Immune ? 0 : IncomingResourceDefences.DamageOverTime(player.EnergyShield,
                damage, Get(IncomingResourceDefences.BypassStat), until, ref player.NonChaosDotBypassCarry)));
        }
        if (player.PlayerLife > 0 && !player.IncomingAilments.Idle)
        {
            var physical = DamageType.Physical;
            var chaos = DamageType.Chaos;
            int chaosDotRes = Resistance(player, chaos) + Get(Stat.AdditionalChaosResistanceAgainstDamageOverTimePct);
            lifeDamage = (int)Math.Min(int.MaxValue, (long)lifeDamage + player.IncomingAilments.Advance(from, until,
                player.Pools, player.EnergyShield, Get, Resistance(player, physical), MaxResistance(player, physical),
                chaosDotRes, MaxResistance(player, chaos), player.Immune || Get(Stat.BaseCannotBeDamaged) > 0,
                immunities.Contains(physical), immunities.Contains(chaos), player.Walk.MovingSeconds,
                ref player.NonChaosDotBypassCarry, ref player.ChaosDotBypassCarry, letheFactor,
                (poisonDmg, poisonAt) => DamagePlayerChaosOverTime(player, poisonDmg, poisonAt, stats),
                inCausticCloud: player.InCausticCloud));
        }
        lifeDamage = (int)Math.Min(int.MaxValue, (long)lifeDamage + AdvancePlayerDecayLocked(player, until, stats,
            immunities.Contains(DamageType.Chaos), letheFactor));
        if (blood is not null)
            lifeDamage = (int)Math.Min(int.MaxValue, (long)lifeDamage + AdvanceCorruptedBlood(player, blood, until, stats,
                immunities.Contains(DamageType.Physical), letheFactor));
        uint lifeAfter = VaalImmortalCallLife(player, (uint)Math.Max(0, (long)player.PlayerLife - lifeDamage), until);
        RecordRechargeLifeDamage(player, (long)player.PlayerLife - lifeAfter, until, stats);
        player.PlayerLife = lifeAfter;
        if (player.PlayerLife == 0)
        {
            player.InvalidateIncomingHits();
            ClearPlayerDecay(player);
            ClearCorruptedBlood(player);
            ClearEssenceAuraExposure(player);
            ClearRemnantAuraExposure(player);
            MapEssences.ClearEffects(player);
            // Commit the mode outcome inside lethal damage, before disconnects or network writes can intervene.
            RecordHardcoreDeath(player);
        }
        if (previousAction != player.ElementalAilments.ActionFactor(until)) player.ApplyMovementSpeed();
        return damage;
    }

    /// <summary>
    /// A hostile object's accepted, unblocked contact may inflict bleeding and poison. The chances,
    /// damage and duration come from that monster's own effects and damage packet. Immunity is handled
    /// by the shared incoming owner; an evaded, blocked or damage-immune contact never reaches here.
    /// </summary>
    public static void ApplyMonsterDamagingAilmentsLocked(Player player, uint source, SpawnedMonster m,
        DamagePacket hit, bool blocked, double now, bool attack = true, IReadOnlyDictionary<uint, int>? prepared = null,
        int skillPoisonChance = 0, double skillPoisonSeconds = 0, bool critical = false)
    {
        if (blocked || player.PlayerLife == 0 || player.Closed || player.LeavingArea || InGracePeriod(player)) return;
        var effects = m.Mods.Effects;
        var playerStats = prepared ?? CharacterPlayerStats(player);
        int Player(uint key) => playerStats.GetValueOrDefault(key);
        int critAvoid = critical ? Math.Max(0, Player(Stat.AvoidAilmentsPctFromCrit)) : 0;
        int bleedChance = Math.Max(effects[Stat.GlobalBleedOnHit] != 0 ? 100 : 0,
            attack ? effects[Stat.BleedOnHitWithAttacksPct] : 0);
        if (attack) bleedChance += Player(Stat.ReceiveBleedingChancePctWhenHitByAttack);
        int poisonChance = Math.Max(Math.Max(effects[Stat.GlobalPoisonOnHit] != 0 ? 100 : 0,
            effects[Stat.BaseChanceToPoisonOnHitPct]), skillPoisonChance);
        if (attack) poisonChance = Math.Max(poisonChance,
            effects[Stat.BaseAttacksPoison] != 0 ? 100 : effects[Stat.ChanceToPoisonOnHitWithAttacksPct]);
        if (bleedChance <= 0 && poisonChance <= 0) return;
        // Damaging ailments roll their base independently of the hit's roll, before victim mitigation.
        var rolled = hit.Roll(player.IncomingHitRandom);
        double physical = rolled[DamageType.Physical].Min;
        double chaos = rolled[DamageType.Chaos].Min;
        bool poisonable = effects[Stat.AllDamageCanPoison] != 0
            ? rolled.Components.Any(c => c.Range.Min > 0)
            : physical + chaos > 0;
        double poisonBase = effects[Stat.AllDamageCanPoison] != 0 ? rolled.MinTotal : physical + chaos;
        if (bleedChance > 0 && physical > 0 && Player(Stat.CannotGainBleeding) == 0
            && Player(Stat.BaseCannotGainBleeding) == 0
            && Player(Stat.VirtualImmuneToBleeding) == 0 && Player(Stat.ImmuneToBleeding) == 0
            && GameServer.Core.World.RandomRolls.Chance(bleedChance, 100, player.IncomingHitRandom)
            && !GameServer.Core.World.RandomRolls.Chance(Player(Stat.AvoidBleedPct) + critAvoid, 100, player.IncomingHitRandom))
        {
            double more = Math.Max(0, 1 + effects[Stat.BleedingDamagePlusPct] / 100.0);
            double seconds = Ailments.BleedDurationSeconds
                * Math.Max(0, 1 + effects[Stat.BaseBleedDurationPlusPct] / 100.0);
            player.IncomingAilments.ApplyBleed(now, physical * 0.70 * more
                * Math.Max(0, 1 + effects[Stat.MonsterBleedingDamagePlusPctFinal] / 100.0), seconds, source,
                victim: Player, durationFactor: LetheShadeAilmentDuration(Player));
        }
        if (poisonChance > 0 && poisonable && Player(Stat.CannotBePoisoned) == 0
            && Player(Stat.ImmuneToPoison) == 0
            && GameServer.Core.World.RandomRolls.Chance(poisonChance, 100, player.IncomingHitRandom)
            && !GameServer.Core.World.RandomRolls.Chance(Player(Stat.AvoidPoisonPct) + critAvoid, 100, player.IncomingHitRandom))
        {
            double more = Math.Max(0, 1 + effects[Stat.BasePoisonDamagePlusPct] / 100.0);
            double seconds = (skillPoisonSeconds > 0 ? skillPoisonSeconds : Ailments.PoisonDurationSeconds)
                * Math.Max(0, 1 + effects[Stat.BasePoisonDurationPlusPct] / 100.0);
            player.IncomingAilments.ApplyPoison(now, poisonBase * 0.30 * more
                * Math.Max(0, 1 + effects[Stat.MonsterPoisonDamagePlusPctFinal] / 100.0), seconds, source,
                victim: Player, durationFactor: LetheShadeAilmentDuration(Player));
        }
    }

    // The chilled, frozen and shocked buffs on the player, as on monsters (AilmentBuff, on the fixed top instances that
    // NextBuff never hands out); a kind whose magnitude changed is sent again. The player's ignited buff is not sent.
    static async Task SendPlayerAilmentBuffs(Player player,
        (bool Ignite, int Chill, bool Freeze, int Shock) before, (bool Ignite, int Chill, bool Freeze, int Shock) after)
    {
        PlayerAilmentBuffs(player, before, after);
        await Task.CompletedTask;
    }

    public static void PlayerAilmentBuffs(Player player, (bool Ignite, int Chill, bool Freeze, int Shock) before,
        (bool Ignite, int Chill, bool Freeze, int Shock) after)
    {
        uint id = player.Id;
        foreach (var (kind, old, now) in new[] { (MonsterElementalAilments.Kind.Chill, before.Chill, after.Chill),
            (MonsterElementalAilments.Kind.Freeze, before.Freeze ? 100 : 0, after.Freeze ? 100 : 0),
            (MonsterElementalAilments.Kind.Shock, before.Shock, after.Shock) })
        {
            if (old == now) continue;
            if (now > 0) SetAilmentBuff(player, id, kind, now, id);
            else player.Area.Buffs.ClearRow(id, (ushort)(kind + 1));
        }
    }

    // The bleeding and poisoned buffs on the player (one each, whoever caused them), set when they change.
    public static void IncomingAilmentBuffs(Player player, IncomingDamagingAilments.State before,
        IncomingDamagingAilments.State after, double now)
    {
        var buffs = player.Area.Buffs;
        uint id = player.Id;
        if ((before.Bleeding != after.Bleeding || before.BleedRevision != after.BleedRevision || before.BleedUntil != after.BleedUntil)
            && LabyrinthTrials.BleedingBuffRow is ushort row)
        {
            if (after.Bleeding) buffs.Set(id, new(row, U68: Buff.Seconds(after.BleedUntil - now)));
            else buffs.Clear(id, row);
        }
        if (before.Poisons != after.Poisons || before.PoisonRevision != after.PoisonRevision || before.PoisonUntil != after.PoisonUntil)
        {
            if (after.Poisons > 0) buffs.Set(id, new(PoisonBuffRow, id, Stacks: (ushort)Math.Clamp(after.Poisons, 1, ushort.MaxValue),
                U68: Buff.Seconds(after.PoisonUntil - now)));
            else buffs.Clear(id, PoisonBuffRow, id);
        }
    }

    public static bool PlayerActionBlocked(Player player, double requestedAt, double now, bool instant = false) =>
        player.HitRecovery.BlocksInput(requestedAt, now) || player.ElementalAilments.ActionFactor(now) == 0
        || !instant && VaalArcticArmourActive(player, now);

    /// <summary>The signed <c>action_speed_-%</c> from the character's raw sources and dev overrides: positive is slower.
    /// The client's own action-speed bucket, and the planned ActionSpeed/EffectClock contract (#246), keep it separate
    /// from attack/cast/movement increases so it is applied once.</summary>
    public static int ActionSpeedReduction(Player player)
    {
        uint key = Stat.ActionSpeedMinusPct;
                return StatSourceValues(player).GetValueOrDefault(key);
    }

    public static double PlayerActionFactor(Player player)
    {
        var sources = StatSourceValues(player);
        // The Spirit Leadership buff joins CharacterPlayerStats, not the raw source cache this path reads; Rakiata's
        // action-speed floor has to reach the server's own attack/cast timing too.
        SpiritChieftains.Leadership? leadership;
        leadership = player.PlayerLife > 0 ? player.SpiritChieftains.Active(AreaTime(player)) : null;
        return Math.Max(.01, PlayerActionFactorFrom(player, name =>
        {
            if (!(name is var key)) return 0;
            long value = sources.GetValueOrDefault(key);
            if (key == Stat.TemporalChainsActionSpeedPlusPctFinal)
                value += player.Pools.MapTemporalChainsActionSpeedPct;
            if (leadership is { } active)
                foreach (var (stat, amount) in active.Stats)
                    if (stat == key) value += amount;
            return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        }));
    }

    static int MapSkillActionSpeedPerUse(Player player) =>
        MapModsOf(player)?.Get(Stat.MapPlayerActionSpeedPlusPctPerRecentSkillUse) ?? 0;

    public static double PlayerActionFactorFrom(Player player, Func<uint, int> get)
    {
        double now = AreaTime(player);
        double extra = get(Stat.HasTailwind) == 0 ? 0 : Math.Floor(8 * Math.Max(0, 1 + get(Stat.TailwindEffectOnSelfPlusPct) / 100d)) - 8;
        extra += get(Stat.ActionSpeedPlusPctWhileAffectedByHaste);
        int mapSpeed = MapSkillActionSpeedPerUse(player);
        if (mapSpeed != 0) extra += (double)mapSpeed * player.RecentEvents.Amount(StatConditions.UsedSkill, now);
        bool unaffectedByTc = get(Stat.UnaffectedByTemporalChains) != 0
            || get(Stat.VirtualUnaffectedByTemporalChains) != 0
            || get(Stat.UnaffectedByCurses) != 0
            || get(Stat.VirtualUnaffectedByCurses) != 0
            || get(Stat.Hexproof) != 0
            || get(Stat.VirtualHexproof) != 0;
        double factor = player.ElementalAilments.TotalActionFactor(now, get(Stat.ActionSpeedMinusPct) - extra)
            * (unaffectedByTc ? 1 : TemporalChainsActionFactor(get(Stat.TemporalChainsActionSpeedPlusPctFinal)));
        if (get(Stat.ActionSpeedCannotBeReducedBelowBase) != 0) factor = Math.Max(1, factor);
        // Rakiata's Leadership: action speed cannot be modified below 100% + this value (the level 1 spirit's 10 is
        // the wiki's "at least 110% of base value").
        int minimum = get(Stat.ActionSpeedPlusPctMinimumValueFromRakiatasLeadership);
        // A frozen player's factor is 0 and stays 0; the floor only lifts a lowered (positive) action speed.
        if (minimum != 0 && factor > 0) factor = Math.Max(factor, 1 + minimum / 100.0);
        return factor;
    }

    // The client clamps the curse's own action-speed modifier at -75 before multiplying other slow effects.
    static double TemporalChainsActionFactor(double modifier) => 1 + Math.Max(-75, modifier) / 100;
}
