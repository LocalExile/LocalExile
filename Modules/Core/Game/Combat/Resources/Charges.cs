using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Core.Diagnostics;

public static partial class Game
{
    // Endurance, Frenzy and Power charges (issue #237, 11-skills.md §15). Each connection has a ChargeSession: the combat
    // code enqueues what may grant charges (ChargesOnHit, ChargesOnKill, ChargesWhenHit) and RunCharges rolls, times,
    // expires and presents them. The charges held count in the character's stats through WithCharges.
    public static readonly Store<Player, ChargeSession> ChargeSessions = new();

    // The charges an area change hands on, by the destination's ticket (Transfer): kept with the duration they had left.
    // A ticket nobody connects with (a failed load) is forgotten after a few minutes.
    public static readonly ConcurrentDictionary<uint, (DateTime At, List<Charges.Carried> Charges)> CarriedCharges = new();

    public static ChargeSession ChargesOf(Player player) => ChargeSessions.GetValue(player, _ => new ChargeSession(player.PlayerChargeState,
        (type, at) => player.RecentEvents.Record(type switch
        {
            ChargeType.Frenzy => StatConditions.GainedFrenzyCharge,
            ChargeType.Power => StatConditions.GainedPowerCharge,
            _ => StatConditions.GainedEnduranceCharge,
        }, at), () => ResourceEffectsTime(player), (from, until) => player.Walk.MovingSeconds(from, until)));

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SkillCatalog, System.Collections.Concurrent.ConcurrentDictionary<(int Effect, int Level, int Quality), Func<uint, int>>> ChargeHitSkillStats = new();

    // A player hit on a monster that landed (Attack, AreaSkillHit, a projectile's impact): its "on hit", "on critical
    // strike" and "on non-critical strike" gains. effect: the gem skill's GrantedEffects row (-1: the default attack).
    // primary: the use's first target; once-per-use grants (the Frenzy gem) come only from it. skillStats: the used
    // skill's stats with its supports' (Power Charge on Critical); without them the gem's own.
    public static void ChargesOnHit(Player player, SpawnedMonster m, bool critical, bool spell, int effect = -1, int level = 1,
        int quality = 0, bool? melee = null, bool primary = true, IReadOnlyDictionary<uint, int>? skillStats = null)
    {
        if (critical)
            TryGainElusiveLocked(player, critical: true, kill: false, ProjectileWorldTime(player),
                random: null, skillStats: spell ? null : skillStats);
        if (m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique)
            QueueFlaskTrigger(player, FlaskEnchantments.Trigger.RareHit);
        Func<uint, int>? skill = null;
        if (skillStats is { Count: > 0 })
        {
            var own = skillStats;
            skill = key => own.GetValueOrDefault(key);
        }
        else if (effect >= 0)
        {
            var skills = SkillsFor(player);
            var cache = ChargeHitSkillStats.GetOrCreateValue(skills);
            skill = cache.GetOrAdd((effect, level, quality), key =>
            {
                var own = skills.EffectStats(key.Effect, key.Level, key.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
                return name => own.GetValueOrDefault(name);
            });
        }
        double now = ProjectileWorldTime(player);
        bool frozen = m.Ailments.Magnitude(MonsterElementalAilments.Kind.Freeze, now) > 0;
        bool bleeding = m.Bleeds.Burning;
        // "your Marked Enemy": a Mark this player cast, not another player's on the same monster.
        bool marked = false;
        foreach (var curse in m.Curses.All.Values)
            if (curse.Mark && curse.Caster == player.Id) { marked = true; break; }
        bool selfBleeding = player.IncomingAilments.Bleeding(now);
        bool selfPoisoned = player.IncomingAilments.PoisonStacks(now) > 0;
        ChargesOf(player).Enqueue(new ChargeEvent.Hit(critical, spell, melee, m.Rarity, effect >= 0 ? SkillsFor(player).EffectId(effect) : null,
            skill, Killed: m.Dead || m.Life == 0, Primary: primary, Frozen: frozen, Bleeding: bleeding, Marked: marked,
            SelfBleeding: selfBleeding, SelfPoisoned: selfPoisoned));
        CurseCharges(player, m, death: false, now);
        PlayerGrantsNearbyChargesLocked(player, kill: false, now);
    }

    // The player died (PlayerDied): every gained charge goes now, not at the ticker's next step, which a pause holds back.
    public static void ChargesOnDeath(Player player)
    {
        ChargesOf(player).Die(); ClearSiphoningOnDeath(player);
        if (SpiritCharges.TryGetValue(player, out var spirit)) ChangeSpiritCharges(player, spirit, 0, AreaTime(player));
    }

    // A hostile monster the player killed (MonsterKilled): its "on kill" gains, "on Killing a Frozen Enemy" too when the
    // corpse kept a freeze (its killing hit's included) or shattered.
    public static void ChargesOnKill(Player player, SpawnedMonster m)
    {
        if (player.PlayerLife == 0) return;
        bool frozen;
        double now = ProjectileWorldTime(player);
        frozen = m.Ailments.FrozenAtDeath(now);
        int poisons = m.Poisons.Stacks(now);
        player.RecentEvents.RecordAmount(StatConditions.KilledEnemy, now);
        if (player.TinctureEffects.BurnStacks > 0) TinctureOnKillLocked(player, CharacterPlayerStats(player));
        TryGainElusiveLocked(player, critical: false, kill: true, now);
        ChargesOf(player).Enqueue(new ChargeEvent.Kill(m.Rarity, frozen, poisons));
        SpiritChargeOnKill(player, AreaTime(player));
        SelfBuffsOnKill(player);
        CurseCharges(player, m, death: true, now);
        PlayerGrantsNearbyChargesLocked(player, kill: true, now);
    }

    // A monster's hit on the player that landed or was blocked (when hit), and on block when blocked: "if you've been hit
    // recently" counts blocked hits too (wiki Blocking, fixed in 3.11.1c). critical: the monster's hit was a critical
    // strike. Daresso's Defiance's "You gain Onslaught for X seconds per Endurance Charge when Hit" counts the charges
    // held before its own "You lose all Endurance Charges when Hit" (a ChargeGains rule) takes them.
    public static void ChargesWhenHit(Player player, bool blocked, bool spell = false, bool critical = false)
    {
        bool onslaught = false;
        var session = ChargesOf(player);
        int perCharge = session.ObservedStat(ChargeTriggers.OnslaughtPerEnduranceWhenHit);
        int held = session.Converted(ChargeType.Endurance) ? 0 : session.Effective(ChargeType.Endurance);
        double until = AreaTime(player) + perCharge / 1000.0 * held;
        if (perCharge > 0 && held > 0 && player.PlayerLife > 0 && until > KillOnslaughtOf(player).Until)
        {
            KillOnslaughtOf(player).Until = until;
            onslaught = true;
        }
        session.Enqueue(new ChargeEvent.WhenHit(critical));
        if (blocked) session.Enqueue(new ChargeEvent.Block(spell));
        if (onslaught) _ = AreaFrames.Start(() => SendKillOnslaught(player));
    }

    // A skill the player used, after its cost (CastGemSkill): the "when you use a Vaal Skill / Cast a Curse Spell /
    // Throw a Trap" gains.
    public static void ChargesOnSkillUse(Player player, int effect, bool vaal)
    {
        if (effect < 0) return;
        var skills = SkillsFor(player);
        bool curse = CurseCatalog.All.ContainsKey(skills.EffectId(effect) ?? "");
        ChargesOf(player).Enqueue(new ChargeEvent.SkillUse(vaal, curse, skills.HasType(effect, "Trapped")));
    }

    // Transfer: the charges go with the player to the area of the new ticket.
    // Whether the official server keeps charges across areas is not documented (poewiki's Charge page says nothing about
    // it); this one call is the rule. A dead player carries nothing (Resurrect in Town changes area while dead).
    public static void CarryCharges(Player player, uint ticket)
    {
        List<Charges.Carried> carried;
        if (player.PlayerLife == 0) ChargesOf(player).Die();
        carried = ChargesOf(player).State.Carry(AreaTime(player));
        foreach (var (old, entry) in CarriedCharges)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedCharges.TryRemove(old, out _);
        if (carried.Count > 0) CarriedCharges[ticket] = (DateTime.UtcNow, carried);
    }

    // The character's stat sources with its charges (CharacterStatSources): Character.ot's per-charge stats, what the
    // charges held add (ChargeEffects.Expand) and their counts for the panel and the buffs.
    public static Dictionary<uint, int> WithCharges(Player player, Dictionary<uint, int> sources)
    {
        var stats = new Dictionary<uint, int>(sources);
        foreach (var (stat, value) in ChargeEffects.Inherent)
            if (stat is var key) stats[key] = stats.GetValueOrDefault(key) + value;
        int Get(uint k) => stats.GetValueOrDefault(k);
        var session = ChargesOf(player);
        var limits = new Dictionary<ChargeType, Charges.Limits>();
        var held = new Dictionary<ChargeType, int>();
        var converted = new Dictionary<ChargeType, bool>();
        var minimumConverted = new Dictionary<ChargeType, bool>();
        // The session's limits, periodic stats and conversion flags follow every change of the character's stats
        // from here, so the ticker never polls them.
        session.Observe(Get);
        foreach (var type in Charges.Types)
        {
            limits[type] = session.Limits(type);
            held[type] = session.Effective(type);
            converted[type] = session.Converted(type);
            minimumConverted[type] = session.MinimumConverted(type);
        }
        // "Count as having maximum number of … Charges": the effects count the maximum; the rows and buffs keep the
        // charges really held.
        // Every stat snapshot comes here: copy the counts only when one of them changes, and read the two stats with Get
        // itself, not a delegate per type (performance.md, "Hot paths").
        var counted = held;
        foreach (var type in Charges.Types)
            if (limits[type].Maximum > held[type]
                && (Get(ChargeScaling.CountAsMaximumStat(type)) != 0 || Get(ChargeScaling.AllCountAsMaximum) != 0))
            {
                if (ReferenceEquals(counted, held)) counted = new(held);
                counted[type] = limits[type].Maximum;
            }
        // The special per-charge coefficients (Character.ot's 3% triple damage and 10% stun threshold per Brutal Charge)
        // are PanelStatDefaults, which BaseStats adds to the published sources after this: count them here too.
        int Coefficient(uint name) => (int)Math.Clamp((long)Get(name) + PanelStatDefaults.Default(name), int.MinValue, int.MaxValue);
        // Expand is a pure function of the coefficients (the stats), the counts and the conversions: the last
        // expansion is reused while they are the same (every stat snapshot expands twice, idle or not).
        int[] heldNow = [.. Charges.Types.Select(t => counted[t])];
        bool[] convertedNow = [.. Charges.Types.Select(t => converted[t])];
        var slot = ChargeExpansions.GetValue(player, _ => new());
        Dictionary<uint, int> added;
        if (slot.Last is { } last && last.Held.AsSpan().SequenceEqual(heldNow) && last.Converted.AsSpan().SequenceEqual(convertedNow)
            && SameInOrder(last.Stats, stats))
            added = VerifyStatSnapshotTail ? Verified(last.Added) : last.Added;
        else
        {
            added = Expand();
            slot.Last = new(new(stats), heldNow, convertedNow, added);
        }
        Dictionary<uint, int> Expand() => ChargeEffects.Expand(Coefficient, t => counted[t], t => converted[t],
            ChargeScaling.Rules, t => limits[t].Maximum);
        var finalAdded = new Dictionary<uint, int>(added);
        foreach (var (stat, value) in ItemChargeDefences.Expand(Coefficient, t => counted[t], t => converted[t]))
            finalAdded[stat] = ActorScale.Saturate((long)finalAdded.GetValueOrDefault(stat) + value);
        foreach (var (stat, value) in ItemChargeDamage.Expand(Coefficient, t => counted[t], t => converted[t]))
            finalAdded[stat] = ActorScale.Saturate((long)finalAdded.GetValueOrDefault(stat) + value);
        foreach (var (stat, value) in ItemChargeCosts.Expand(Coefficient, t => counted[t], t => converted[t]))
            finalAdded[stat] = ActorScale.Saturate((long)finalAdded.GetValueOrDefault(stat) + value);
        int poisonStacks;
        poisonStacks = player.IncomingAilments.PoisonStacks(AreaTime(player));
        foreach (var (stat, value) in IncomingAilmentEffects.Expand(Coefficient, poisonStacks))
            finalAdded[stat] = finalAdded.GetValueOrDefault(stat) + value;
        // Game_STAT_SNAPSHOT_VERIFY: a reused expansion must be the one computed now, in order.
        Dictionary<uint, int> Verified(Dictionary<uint, int> reused)
        {
            var fresh = Expand();
            if (!SameInOrder(fresh, reused))
            {
                Interlocked.Increment(ref ChargeReuseMismatches);
                throw new InvalidOperationException("[gateway] stat snapshot: a reused charge expansion differs from a fresh one");
            }
            return reused;
        }
        foreach (var (stat, value) in finalAdded.Select(kv => (Stat: kv.Key, kv.Value))
            .Concat(ChargeEffects.Display(t => held[t], t => limits[t], t => converted[t], t => minimumConverted[t])))
            if (stat is var key) stats[key] = stats.GetValueOrDefault(key) + value;
        return stats;
    }

    // One per connection: the last charge expansion and what it was expanded from. Never changed after it is published.
    sealed record ChargeExpansion(Dictionary<uint, int> Stats, int[] Held, bool[] Converted,
        Dictionary<uint, int> Added);
    sealed class ChargeExpansionSlot { public volatile ChargeExpansion? Last; }
    static readonly Store<Player, ChargeExpansionSlot> ChargeExpansions = new();
    static long ChargeReuseMismatches;

    // The more multiplier of the charges on a hit (AttackHit, SpellDamage) or a burn, from the final stats.
    public static double ChargeDamageMore(IReadOnlyDictionary<uint, int> stats, bool spell = false) =>
        ChargeEffects.DamageMore(name => name is var key ? stats.GetValueOrDefault(key) : 0, spell);

    // The player's additional physical and elemental damage reduction (Endurance Charges, passives) for hits taken.
    public static (int Physical, int Elemental) PlayerDamageReduction(Player player,
        IReadOnlyDictionary<uint, int>? stats = null)
    {
        stats ??= CharacterPlayerStats(player);
        int Get(uint key) => stats.GetValueOrDefault(key);
        var reduction = ChargeEffects.Reduction(Get);
        int perMinion = Get(Stat.AdditionalPhysicalDamageReductionPctPerMinionUpTo10Pct);
        long minionReduction = perMinion <= 0 ? 0 : Math.Min(10,
            (long)perMinion * player.Minions.All.Count(minion => !minion.Monster.Dead && minion.Monster.Life > 0
                && player.Area.Monsters.GetValueOrDefault(minion.Id) == minion.Monster));
        return (ActorScale.Saturate((long)reduction.Physical + Get(SiphoningHitReduction) + minionReduction), reduction.Elemental);
    }

    // A hit's damage taken after the player's mitigation (the dev /hit commands, MonsterSwing's rules): physical through
    // armour plus additional physical reduction; the other types take resistance before the shared hit-only pass.
    public static int MitigateTaken(Player player, DamageType type, int amount, int resistance, int maxResistance)
    {
        int priorContacts = player.RecentEnemyHits.Count(AreaTime(player));
        var stats = CharacterPlayerStats(player);
        bool relevant = amount > 0
            && PlayerHitMitigation.ArmourPercent(player.Pools, type, stats.GetValueOrDefault) > 0
            && (type == DamageType.Physical || DamageModel.Mitigate(amount, resistance, maxResistance) > 0);
        var armour = IncomingArmourLocked(player, relevant, priorContacts, stats);
        return MitigateTakenWithArmour(player, type, amount, resistance, maxResistance, armour.EffectiveArmour, stats);
    }

    // Then, as a monster's hit: the flat damage taken, a ranged attack's physical (Turtle Shell) and the increased damage
    // taken with the shock's (PlayerDamageTaken). An attack that is not ranged is melee.
    public static int MitigateTakenWithArmour(Player player, DamageType type, int amount, int resistance, int maxResistance,
        int effectiveArmour, IReadOnlyDictionary<uint, int> stats, bool attack = true, bool ranged = false) =>
        MitigateTakenWithArmour(player, type, amount, resistance, maxResistance, effectiveArmour, stats, attack, ranged, spell: false);

    public static int MitigateTakenWithArmour(Player player, DamageType type, int amount, int resistance, int maxResistance,
        int effectiveArmour, IReadOnlyDictionary<uint, int> stats, bool attack, bool ranged, bool spell)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        int increased = PlayerDamageTaken.IncreasedPct(type, melee: attack && !ranged, Get, spell: spell);
        var reduction = PlayerDamageReduction(player, stats);
        int resisted = type == DamageType.Physical ? amount : DamageModel.Mitigate(amount, resistance, maxResistance);
        int value = Math.Max(0, PlayerHitMitigation.AfterResistance(player.Pools, type, resisted,
            reduction.Physical, reduction.Elemental, effectiveArmour, PlayerHitMitigation.ArmourPercent(player.Pools, type, Get),
            imbalancedGuard: Get(Stat.KeystoneSacredBastion) != 0)
            + PlayerDamageTaken.Flat(type, attack, Get));
        if (type == DamageType.Physical) value = AntiquarianEffects.RangedPhysical(value, ranged, Get);
        return FortificationDamageTaken(player, (int)Math.Round(value
            * player.ElementalAilments.DamageTakenFactor(AreaTime(player), increased)));
    }

    // A warcry (CastGemSkill), when its cast is over: it taunts the hostile monsters within its radius (wiki Warcry: every
    // warcry taunts, base radius 6 m, grown by area of effect; TauntLocked), and Enduring Cry gives an Endurance Charge per
    // 5 power (endurance_charge_granted_per_X_monster_power_during_endurance_warcry) of the hostile monsters within it and
    // its life regeneration (Path of Building: power 1 normal, 2 magic, 10 rare, 20 unique). warcries_have_infinite_power
    // (Bring the Battle) makes the power 999999 (PoB CalcPerform's override): the charges reach their maximum and the
    // regeneration its 25-power cap. Every warcry then runs the on-warcry effects (WarcryReleased). The other warcries'
    // supported buffs are granted by ApplyWarcryBuffLocked, except Intimidating Cry, which has its own
    // (IntimidatingCry.cs); their exerted attacks are still not modelled.
    // True when the skill was a warcry.
    public static bool UseWarcryInstance(Player player,
        ushort skillId, int effect)
        => SkillsFor(player).IsWarcry(effect) && UseWarcry(player, effect, SupportedSkill(player, skillId).Stats, skillId);

    const int InfiniteWarcryPower = 999999;

    // A warcry's use time (PoB CalcOffence calcWarcryCastTime): its cast time over warcry speed (the player's and the
    // gem's) and action speed, at least a server tick. The client times the cry from the same warcry_speed_+% (0x141abf4b0).
    public static double WarcryCastSeconds(Player player, int effect, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int> own)
    {
        long speed = (long)playerStats.GetValueOrDefault(Stat.WarcrySpeedPlusPct) + own.GetValueOrDefault(Stat.WarcrySpeedPlusPct)
            + (Supports.Of(effect)?.Types.Contains(120) == true
                ? (long)playerStats.GetValueOrDefault(Stat.RetaliationSkillSpeedPlusPct) + own.GetValueOrDefault(Stat.RetaliationSkillSpeedPlusPct) : 0);
        return Math.Max(TickSeconds, SkillsFor(player).CastTimeMs(effect) / 1000.0 / Math.Max(0.01, 1 + speed / 100.0) / PlayerActionFactor(player));
    }

    public static bool UseWarcry(Player player, int effect,
        IReadOnlyDictionary<uint, int> own, ushort skillId = 0, bool triggered = false)
    {
        if (!SkillsFor(player).IsWarcry(effect)) return false;
        bool enduring = SkillsFor(player).EffectId(effect) == "EnduringCry";
        int Own(uint key) => own.GetValueOrDefault(key);
        int per = Own(Stat.EnduranceChargeGrantedPerXMonsterPowerDuringEnduranceWarcry);
        // The warcry cannot be cancelled before its contact point (cannot_cancel_skill_before_contact_point), so a later
        // action does not stop it. Admission checks the actor restriction before this accepted release is scheduled;
        // changing its source later does not cancel this continuation. Warcry and action speed are taken at the use.
        var playerStats = AreaPlayerStats(player);
        int Player(uint key) => playerStats.GetValueOrDefault(key);
        double castSeconds = WarcryCastSeconds(player, effect, playerStats, own);
        double until = AreaTime(player) + castSeconds;
        int epoch = player.ProjectileEpoch;
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own, DurationKinds(player, effect))
            * SkillDurationModifiers.CallToArmsMore(Player);
        bool callToArms = Player(Stat.KeystoneCallToArms) != 0;
        bool suppressBuffs = Own(Stat.WarcriesDoNotApplyBuffsToSelfOrAllies) != 0
            || Player(Stat.WarcriesDoNotApplyBuffsToSelfOrAllies) != 0;
        double buffEffect = Math.Max(0, 1 + ((long)Player(Stat.WarcryBuffEffectPlusPct) + Own(Stat.WarcryBuffEffectPlusPct)) / 100d);
        _ = AreaFrames.Start(async () =>
        {
            // A triggered warcry has no cast windup. Ordinary casts still release on the area clock.
            while (!triggered && !player.Closed && AreaTime(player) < until) await AreaDelay(player, 0.016);
            if (!player.ProjectileActive(epoch) || player.PlayerLife == 0) return;
            double taunt = TauntSeconds(Player);
            foreach (var (k, v) in own) playerStats[k] = v;
            int radius = SkillRadius(player, WarcryRadius, own, spell: false, effect);
            double now = AreaTime(player);
            int power, taunted;
            (int X, int Y) here;
            if (!player.ProjectileActive(epoch) || player.PlayerLife == 0) return;
            here = player.Walk.Position(now);
            taunted = TauntLocked(player, here, radius, now, taunt);
            if (!enduring)
                Console.WriteLine($"[gateway] {SkillsFor(player).EffectId(effect)}: taunts {taunted} monster(s) within {radius} cells for {taunt:0.##} s");
            if (!enduring)
            {
                string effectId = SkillsFor(player).EffectId(effect) ?? "";
                if (!suppressBuffs)
                {
                    if (effectId == "IntimidatingCry")
                        ApplyIntimidatingCryLocked(player, own, CharacterPlayerStats(player), now);
                    else
                        ApplyWarcryBuffLocked(player, effectId, own, playerStats, here, radius, now, duration, buffEffect);
                }
                var warriors = skillId != 0 && IsGeneralsCry(player, effect)
                    ? GeneralsCryReleased(player, skillId, here, radius, epoch)
                    : Task.CompletedTask;
                await Task.WhenAll(warriors, WarcryReleased(player, here, radius, epoch, effectId));
                return;
            }
            if (!player.ProjectileActive(epoch) || player.PlayerLife == 0) return;
            power = Player(Stat.WarcriesHaveInfinitePower) != 0 ? InfiniteWarcryPower
                : player.Area.Monsters.Values.Where(m => m.Hostile && !m.Dead && m.Life > 0 && Dist(m.Walk.Position(now), here) <= radius)
                    .Sum(m => WarcryPower(m.Rarity));
            // The target's stat description caps regeneration at 25 Power. Charge generation retains its own rule.
            double percentPerMinute = Math.Min(25, power) / 5d
                * Own(Stat.EnduringCryLifeRegenerationRatePerMinutePctPer5PowerUpToCap)
                * buffEffect;
            if (!callToArms && !suppressBuffs) player.EnduringCryRecovery.Begin(now, duration, player.PlayerMaxLife * percentPerMinute / 6000d);
            Console.WriteLine($"[gateway] Enduring Cry recovery: {power} power, {duration:0.###} seconds, "
                + $"{percentPerMinute:0.###}% Life/min (Whaletooth included)");
            int charges = per > 0 ? power / per : 0;
            Console.WriteLine($"[gateway] Enduring Cry: {power} power within {radius} cells, {charges} endurance charge(s), {taunted} taunted");
            if (charges > 0 && !callToArms && !suppressBuffs) ChargesOf(player).Enqueue(new ChargeEvent.Grant(ChargeType.Endurance, charges, $"Enduring Cry ({power} power)"));
            await WarcryReleased(player, here, radius, epoch, "EnduringCry");
        });
        return true;
    }

    // A warcry's radius: the wiki's base 6 m, Path of Building's EnduringCry skill("radius", 60); one unit is one cell.
    public const int WarcryRadius = 60;

    // A monster's warcry power (Path of Building's ConfigOptions): normal 1, magic 2, rare 10, unique 20.
    public static int WarcryPower(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 2,
        MonsterRarity.Rare => 10,
        MonsterRarity.Unique => 20,
        _ => 1,
    };

    // The charges ticker of one connection: every frame of the area (a pause stops them) it
    // steps the session and sends what changed, the charge buffs (ChargeBuffs) and, when the counts the stats use changed,
    // the pools, the walk speed and the stats (SendChargeStats). It runs while the session has work (a charge held, an
    // event queued, a periodic gain's stat, a presentation or refresh pending) and stops otherwise; a queued event
    // (ChargeSession.Woken), a stat update or a pool refresh starts it again (WakeCharges). The character's stats are
    // worked out outside the area's frame, and only while events wait (their gain chances); the limits come with every
    // stats change (ChargeSession.Observe through WithCharges).
    static void StartCharges(Player player, PlayerUpdatesState updates)
    {
        var session = ChargesOf(player);
        if (CarriedCharges.TryRemove(player.Ticket, out var entry) && entry.Charges is var carried)
            {
                session.State.Restore(carried, updates.Schedule.Now(player));
                Console.WriteLine($"[gateway] {player.Connection.Remote} charges carried from the last area: " +
                    string.Join(", ", carried.Select(c => $"{c.Held} {Charges.Name(c.Type)} ({c.Remaining:0.0} s left)")));
            }
        session.Woken = () => WakeCharges(player);
        WakeCharges(player);
    }

    static readonly RuleTable<PlayerUpdatesState, object> ChargeTickers = new();

    public static void WakeCharges(Player player)
    {
        if (player.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(player, updates)) return;
        if (ChargeTickers.TryGetValue(updates, out _)) return;
        ChargeTickers.AddOrUpdate(updates, new());
        EveryFrameAsPlayer(player, () =>
        {
            var session = ChargesOf(player);
            if (PlayerUpdatesCurrent(player, updates) && (session.TimesCharges || ChargePresentationPending(session)
                || ChargePoolRefreshes.GetOrCreateValue(player).Pending)) return true;
            ChargeTickers.Remove(updates);
            return false;
        }, player => StepCharges(player, updates));
    }

    static async Task StepCharges(Player player, PlayerUpdatesState updates)
    {
        var session = ChargesOf(player);
        try
        {
            Func<uint, int>? stats = null;
            WeaponProfile weapon = default;
            if (session.Pending)
            {
                var final = CharacterPlayerStats(player);
                stats = name => name is var key ? final.GetValueOrDefault(key) : 0;
                weapon = WeaponProfileFor(player);
            }
            double now = updates.Schedule.Now(player);
            ChargeSession.StepResult step;
            bool presentationPending;
            using (ProjectileMutation(player))
            {
                if (!PlayerUpdatesCurrent(player, updates)) return;
                if (player.LeavingArea) return;
                now = ProjectileWorldTime(player);
                session.SettleDegeneration(now);
                // Idle charges have no deadline to retime; degeneration still settles on its own clock.
                if (session.TimesCharges)
                {
                    var recipient = CharacterPlayerStats(player);
                    session.State.SetExpiryRate(now, RecipientEffectTime.Rate(3,
                        name => name is var k ? recipient.GetValueOrDefault(k) : 0));
                }
                step = session.Step(now, stats, () => weapon, alive: player.PlayerLife > 0, moving: player.Walk.Moving(now),
                    inTown: InTown(player), AreaRng(player),
                    mineDetonatedRecently: player.RecentEvents.Happened(StatConditions.DetonatedMine, AreaTime(player)));
                presentationPending = ChargePresentationPending(session);
                ChargeSideEffectsLocked(player, session);
                var refresh = ChargePoolRefreshes.GetOrCreateValue(player);
                if (refresh.Pending) step = step with { CountsChanged = true };
                refresh.Pending = false;
                if (step.CountsChanged)
                {
                    player.Pools = PoolsFor(player, player.Pools.Level);
                    player.ApplyMovementSpeed();
                }
            }
            foreach (string line in step.Log) Console.WriteLine($"[gateway] {player.Connection.Remote} {line} (t={now:0.000})");
            bool countsChanged = step.CountsChanged;
            if (presentationPending)
                await Send(player, [], null, atGate: () =>
                {
                    if (!PlayerUpdatesCurrent(player, updates) || player.LeavingArea) return null;
                    var (packet, changed) = CurrentChargePresentationLocked(player, player.Connection.Remote);
                    countsChanged |= changed;
                    return packet;
                });
            if (!PlayerUpdatesCurrent(player, updates)) return;
            if (countsChanged)
            {
                await SendChargeStats(player,
                    string.Join(", ", Charges.Types.Select(t => $"{session.Effective(t)} {Charges.Label(t, session.Converted(t))}")));
            }
        }
        catch (OperationCanceledException) when (updates.Stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not (IOException or ObjectDisposedException))
        {
            if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} charges: {ServerLog.Describe(ex)}");
        }
        catch (Exception) { }   // the connection has gone
    }

    // Under the Monsters lock, after a step: the Life the loss triggers granted (Kingsguard), recovered as instant leech
    // is and sent with the next vitals update.
    static void ChargeSideEffectsLocked(Player player, ChargeSession session)
    {
        int life = session.TakeLifeGained();
        if (life > 0 && player.PlayerLife > 0)
        {
            var leech = LeechOf(player);
            uint before = player.PlayerLife;
            player.PlayerLife = PetrifiedBlood.RecoverLife(player, before, life, ref leech.LifeInstantCarry, minimum: 1);
            leech.InstantLifeChanged |= before != player.PlayerLife;
        }
    }

    // The stats after a charge count changed: 0x1b3 and the skills' stats (the client swings and casts with the speed in
    // its skill records), without SendSkills' skill-bar work (no skill came or went). A changed skill set (a gem moved at
    // the same moment) takes the full SendCharacterStats.
    public static async Task SendChargeStats(Player player, string why)
    {
        if (StatsPublishedByFrame(player)) return;
        await PublishStatValues(player, $"charges {why}");
    }

    /// <summary>The stats after a change of their values: 0x1b3, the skills whose records changed and the Energy Shield
    /// (a changed skill set takes the full publication).</summary>
    public static async Task PublishStatValues(Player player,
        string why = "changed")
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired) return;
        var skills = PlayerSkills(player);
        if (!player.ClientSkillIds.SetEquals(skills.Select(s => s.Id)))
        {
            await PublishCharacterStats(player, PlayerStatChange.Sources);
            return;
        }
        await SendPlayerStatSnapshot(player,
            () => PublishPlayerStats(player), $"0x01b3 player stats ({why})");
        bool skillsSent = false;
        await SendPlayerStatSnapshot(player,
            () =>
            {
                var bytes = PlayerSkillsIfChanged(player, skills);
                skillsSent = bytes.Length > 0;
                return bytes;
            },
            $"0x02bc player Actor: {skills.Count} skill(s), charge stats");
        if (skillsSent) await RestoreSkillBarAfterActorUpdate(player);
        await SendPlayerEnergyShield(player);
    }
}
