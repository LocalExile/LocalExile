using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    internal sealed class TriggeredGuard(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public double Until, NextPulseAt;
        public int CooldownVersion = -1;
        public ushort[] PublishedGuardSkills = [];
        public int Percent, Remaining, Absorbed, AbsorbedSincePulse, Armour, Reflection;
        public double Physical = 1, Elemental = 1;
        public ushort Buff, Skill;
        public string Effect = "";
        public SupportedGem? Gem;
        public ProjectileSession? Session;
    }
    const string VaalMoltenShellEffect = "VaalMoltenShell";
    internal static readonly Store<Player, TriggeredGuard> TriggeredGuards = new();
    static bool TriggerGuardSpell(Player player, SupportedGem gem) =>
        GuardSpellEffect(SkillsFor(player).EffectId(gem.Effect)) && !SkillsFor(player).IsVaal(gem.Effect);
    static bool GuardSpellEffect(string? effect) => effect is "QuickGuard" or "ImmortalCall" or "MoltenShell" or VaalMoltenShellEffect;
    static bool MoltenShellGuard(string effect) => effect is "MoltenShell" or VaalMoltenShellEffect;

    static bool TriggeredGuardCurrent(Player player, TriggeredGuard guard) =>
        guard.Epoch == player.ProjectileEpoch && ReferenceEquals(guard.Rules, CombatLifetime)
        && !player.Closed && !player.LeavingArea && player.PendingTransfer is null && player.PlayerLife > 0;

    static TriggeredGuard? CurrentTriggeredGuard(Player player, double now, bool advancePulse = false)
    {
        if (!TriggeredGuards.TryGetValue(player, out var guard)) return null;
        if (!TriggeredGuardCurrent(player, guard)) { TriggeredGuards.Remove(player); return null; }
        if (advancePulse && guard.NextPulseAt > 0 && guard.NextPulseAt < guard.Until && now >= guard.NextPulseAt)
        {
            int absorbed = guard.AbsorbedSincePulse;
            guard.AbsorbedSincePulse = 0;
            guard.NextPulseAt += Math.Floor(now - guard.NextPulseAt) + 1;
            ReflectMoltenShellDamage(player, guard, absorbed);
        }
        if (guard.Until > 0 && now >= guard.Until) EndTriggeredGuard(player, guard, guard.Until);
        return guard;
    }
    public static bool TriggerGuardReady(Player player, double now)
    {
        var guard = CurrentTriggeredGuard(player, now);
        if (guard is null) return true;
        if (guard.Until > 0 && guard.Effect != VaalMoltenShellEffect) return false;
        if (player.SingleUseCooldowns.GetValueOrDefault(GuardCooldownKey) is not { } state
            || state.UseVersion != guard.CooldownVersion) return true;
        state.Refresh(now, state.Rate);
        return state.Ready;
    }

    static bool GuardAvailableForTrigger(Player player, double now)
    {
        if (!TriggerGuardReady(player, now)) return false;
        if (player.SingleUseCooldowns.GetValueOrDefault(GuardCooldownKey) is not { } state) return true;
        state.Refresh(now, state.Rate);
        return state.Ready;
    }

    // Vaal Molten Shell's native cooldown is separate from the regular guard group. The new buff replaces the old one.
    static bool GuardReadyFor(Player player, string? effect, double now) =>
        effect == VaalMoltenShellEffect || TriggerGuardReady(player, now);

    static void EndTriggeredGuard(Player player, TriggeredGuard guard, double now, bool triggerExplosion = true)
    {
        guard.Until = guard.NextPulseAt = 0;
        bool regular = guard.Effect != VaalMoltenShellEffect;
        var cooldown = player.SingleUseCooldowns.GetValueOrDefault(GuardCooldownKey);
        bool resume = regular && cooldown is not null && cooldown.UseVersion == guard.CooldownVersion;
        if (resume) cooldown!.Resume(now, cooldown.TotalBaseMilliseconds);
        if (guard.Session is not { } session) return;
        ushort buff = guard.Buff; guard.Buff = 0;
        if (buff != 0) player.Area.Buffs.Clear(player.Id, GuardRow(guard.Effect), player.Id);
        if (triggerExplosion && MoltenShellGuard(guard.Effect))
            ReflectMoltenShellDamage(player, guard, guard.Absorbed);
        guard.Armour = 0;
        player.Pools = PoolsFor(player, player.Pools.Level);
        // Built now and marked sent, so the stats send's own cooldown refresh does not publish the entry a second time:
        // every repeat restarts the bar's cooldown animation on this client (live 2026-11-18).
        byte[]? counted = resume
            ? GuardCooldownBatch(player, guard, cooldown!, Math.Max(now, AreaTime(player)), includeRecovered: true) : null;
        session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
        // A triggered guard has no instant action entry of its own, so the resumed entry is published here as well.
        if (counted is { Length: > 0 })
            session.Enqueue(() => Send(player, counted,
                "0x02bc guard cooldown counts after its buff"));
    }

    static void ReflectMoltenShellDamage(Player player, TriggeredGuard guard, int absorbed)
    {
        if (absorbed == 0 || guard.Reflection <= 0 || guard.Session is not { } session || guard.Gem is not { } gem)
            return;
        double reflected = absorbed * (guard.Reflection / 100d);
        int radius = SkillRadius(player, gem.Stats, true, gem.Effect);
        string name = guard.Effect == VaalMoltenShellEffect ? "Vaal Molten Shell" : "Molten Shell";
        session.Enqueue(async () =>
        {
            if (!session.Active || player.PlayerLife == 0 || player.LeavingArea) return;
            double at = AreaTime(player);
            var centre = player.Walk.Position(at);
            var deaths = new List<(uint Id, SpawnedMonster M)>();
            foreach (var (id, monster) in player.Area.Monsters.ToArray())
            {
                if (!session.Active || player.Paused || player.PlayerLife == 0) return;
                if (!monster.Hostile || monster.Dead || monster.Life == 0 || Dist(monster.Walk.Position(at), centre) > radius)
                    continue;
                // Reflected damage has no spell/attack scaling, crit, penetration, leech or on-hit procs.
                var resisted = DamageModel.Mitigate(DamagePacket.Of(DamageType.Fire, reflected, reflected),
                    type => MonsterHitResistance(monster, type, default, at));
                int damage = (int)Math.Round(resisted.MinTotal * MonsterDamageTaken(monster, spell: false,
                    at, elemental: true, hit: false));
                if (ApplyMonsterHitLocked(player, monster, damage) is { } applied)
                    await NotifyMonsterHit(player, id, monster, applied, damage, $"{name} reflected damage", deaths: deaths);
            }
            foreach (var (id, monster) in deaths)
                await MonsterKilled(player, id, monster);
        });
    }

    static void ScheduleGuardPulse(Player player, TriggeredGuard guard, double now)
    {
        if (guard.NextPulseAt == 0 || guard.NextPulseAt >= guard.Until) return;
        ushort buff = guard.Buff;
        AfterSeconds(player, guard.NextPulseAt - now, () =>
        {
            if (guard.Buff != buff) return;
            double at = AreaTime(player);
            if (ReferenceEquals(CurrentTriggeredGuard(player, at, advancePulse: true), guard))
                ScheduleGuardPulse(player, guard, at);
        });
    }

    /// <summary>The client's stored-use entry for a guard skill as the bar shows it now: the skill's own cooldown at a
    /// zero recovery rate while the buff runs (the official bar's frozen entry, measured 3.00 / 3.00 / rate 0, which
    /// this client takes when it did not predict the entry itself), then the same cooldown at the character's recovery
    /// rate. One publisher, so a stats or skill refresh repeats the same numbers.</summary>
    public static byte[]? GuardCooldownEntry(Player player, ushort skill, int effect)
    {
        if (SingleUseState(player, effect, skill) is not { } state) return null;
        return GuardCooldownPacket(player, state, AreaTime(player), [skill]);
    }

    static byte[]? GuardCooldownPacket(Player player, SingleUseCooldownState state, double now, ushort[] skills,
        bool includeRecovered = false)
    {
        state.Refresh(now, state.Rate);
        if (skills.Length == 0 || !includeRecovered && !state.Held && state.FullyRecovered) return null;
        float total = state.TotalBaseMilliseconds / 1000f;
        float remaining = state.Held ? total : (float)(state.RemainingBaseMilliseconds / 1000);
        var entry = InstanceEntities.PlayerSingleUseCooldowns(player.Id,
            skills.Select(skill => new SingleUseCooldownUpdate(skill, remaining, total,
                state.Held ? 0f : (float)state.Rate, state.SpentUses)).ToArray(),
            canDetonateMines: CanDetonateMines(player));
        state.NativeRefreshed();
        return entry;
    }

    static ushort[] GuardCooldownSkills(Player player) => [.. GemSkillEffects(player)
        .Where(gem => CooldownKey(player, gem.Effect, gem.Level) == GuardCooldownKey).Select(gem => gem.Id)];

    static byte[]? GuardCooldownBatch(Player player, TriggeredGuard guard, SingleUseCooldownState state, double now,
        bool includeRecovered = false)
    {
        guard.PublishedGuardSkills = GuardCooldownSkills(player);
        return GuardCooldownPacket(player, state, now, guard.PublishedGuardSkills, includeRecovered);
    }

    /// <summary>A guard's held rate-0 entry built from the skill's own base cooldown, published before its instant
    /// action so the client skips the press prediction it would otherwise start from a use (issue #1127). It mutates no
    /// server state: the use's acknowledgement and <see cref="StartTriggeredGuard"/>'s <see cref="SingleUseCooldownState.Hold"/>
    /// behind the instant action own the state, so a refused action cannot leave a held cooldown stuck.</summary>
    public static byte[]? GuardCooldownPreEntry(Player player, ushort skill, int effect)
    {
        if (SkillsFor(player).EffectId(effect) == VaalMoltenShellEffect) return null;
        int ms = SkillsFor(player).CooldownMs(effect, SupportedSkill(player, skill).Level);
        if (ms <= 0) return null;
        float total = ms / 1000f;
        return InstanceEntities.PlayerSingleUseCooldowns(player.Id,
            [new SingleUseCooldownUpdate(skill, total, total, 0f, 1)], canDetonateMines: CanDetonateMines(player));
    }

    /// <summary>The held guard's rate-0 entry to re-publish after a skill-list refresh: the client's skill replacement
    /// refreshes an existing zero-rate cooldown entry (<c>0x141ef1573</c>), which would start the bar counting again while
    /// the buff runs (issue #1127, seen on movement, which refreshes the conditional item stats). Null when no guard buff
    /// is running.</summary>
    public static byte[]? HeldGuardCooldownRefresh(Player player)
    {
        double now = AreaTime(player);
        if (CurrentTriggeredGuard(player, now) is not { } guard
            || player.SingleUseCooldowns.GetValueOrDefault(GuardCooldownKey) is not { FullyRecovered: false } state
            || state.UseVersion != guard.CooldownVersion) return null;
        var skills = GuardCooldownSkills(player);
        if (!state.Held && guard.PublishedGuardSkills.AsSpan().SequenceEqual(skills)) return null;
        guard.PublishedGuardSkills = skills;
        return GuardCooldownPacket(player, state, now, skills);
    }

    // "Guard Skills have X% increased Duration" is the same increased-duration sum as skill_effect_duration_+%.
    internal static double GuardDuration(int baseMilliseconds, Func<uint, int> player, Func<uint, int> own) =>
        SkillDurationModifiers.Seconds(baseMilliseconds,
            name => player(name) + (name == SkillDurationModifiers.IncreasedDuration ? player(Stat.GuardSkillEffectDurationPlusPct) : 0),
            own);

    // Called immediately after the use's successful payment, under the connection lock: a guard skill cast from the
    // skill bar arrives here as well as a triggered one (Cast When Damage Taken).
    public static void StartTriggeredGuard(Player player, ProjectileSession session, ushort skill, double now)
    {
        var gem = SupportedSkill(player, skill);
        string? effect = SkillsFor(player).EffectId(gem.Effect);
        if (!GuardSpellEffect(effect) || !GuardReadyFor(player, effect, now)) return;
        bool vaal = effect == VaalMoltenShellEffect;
        SingleUseCooldownState? cooldown = null;
        var playerStats = CharacterPlayerStats(player);
        if (!vaal)
        {
            var plan = CooldownForSkill(player, skill) ?? new SingleUseCooldown(
                SkillsFor(player).CooldownMs(gem.Effect, gem.Level),
                (int)Math.Clamp((long)playerStats.GetValueOrDefault(Stat.VirtualCooldownSpeedPlusPct)
                    + gem.Stats.GetValueOrDefault(Stat.BaseCooldownSpeedPlusPct)
                    + playerStats.GetValueOrDefault(Stat.GuardSkillCooldownRecoveryPlusPct), -100, int.MaxValue - 100),
                SharedGroup: GuardCooldownGroup);
            cooldown = CooldownStateLocked(player, GuardCooldownKey, plan, now);
            if (cooldown.Ready)
            {
                if (!cooldown.TryUse(now, plan)) return;
                player.CooldownGroups[GuardCooldownKey] = GuardCooldownGroup;
                player.CooldownSources[GuardCooldownKey] = skill;
            }
            else if (player.CooldownSources.GetValueOrDefault(GuardCooldownKey) != skill) return;
            cooldown.ConfirmNativeUse(cooldown.UseVersion, skill, synchronizeProgress: true);
            cooldown.Hold(plan.BaseMilliseconds);
        }
        if (CurrentTriggeredGuard(player, now) is { Until: > 0 } previous)
            EndTriggeredGuard(player, previous, now);
        int Own(uint name) => GolemStat(gem.Stats, name);
        int Get(uint name) => Own(name) + GolemStat(playerStats, name);
        var guard = TriggeredGuards.GetValue(player, _ => new(player.ProjectileEpoch, CombatLifetime));
        guard.Gem = gem; guard.Skill = skill; guard.Session = session;
        guard.Effect = effect!;
        guard.Physical = guard.Elemental = 1;
        guard.Percent = guard.Remaining = guard.Absorbed = guard.AbsorbedSincePulse = guard.Armour = guard.Reflection = 0;
        if (cooldown is not null) guard.CooldownVersion = cooldown.UseVersion;
        double duration = GuardDuration(Own(Stat.BaseSkillEffectDuration), name => GolemStat(playerStats, name), Own);
        if (guard.Effect == "QuickGuard")
        {
            guard.Percent = Math.Clamp(Own(Stat.QuickGuardDamageAbsorbedPct), 0, 100);
            guard.Remaining = Math.Max(0, Own(Stat.QuickGuardDamageAbsorbLimit));
        }
        else if (guard.Effect == "ImmortalCall")
        {
            var charges = ChargesOf(player);
            int removable = charges.Converted(ChargeType.Endurance) ? 0 : Math.Min(5,
                Math.Max(0, charges.Effective(ChargeType.Endurance) - charges.Limits(ChargeType.Endurance).Minimum));
            if (!RandomRolls.Chance(Get(Stat.ImmortalCallPctChanceToNotConsumeEnduranceCharges), 100, AreaRng(player)))
                charges.State.Lose(ChargeType.Endurance, removable, charges.Limits(ChargeType.Endurance));
            guard.Remaining = removable;
            // The gem's 20% and the helmet enchantment's "increased Immortal Call duration per Endurance Charge
            // removed" are increased duration, added to the other increases (PoB SkillStatMap: Duration INC).
            int perChargeDuration = Get(Stat.BuffEffectDurationPlusPctPerRemovableEnduranceChargeLimitedTo5)
                + Get(Stat.ImmortalCallBuffEffectDurationPlusPctPerRemovableEnduranceCharge);
            duration = GuardDuration(Own(Stat.BaseSkillEffectDuration), name => GolemStat(playerStats, name),
                name => Own(name) + (name == SkillDurationModifiers.IncreasedDuration ? removable * perChargeDuration : 0));
            guard.Physical = Math.Max(0, 1 + Own(Stat.MortalCallPhysicalDamageTakenPlusPctFinal) / 100d)
                * Math.Max(0, 1 + removable * Own(Stat.MortalCallPhysicalDamageTakenPerEnduranceChargeConsumedFinalPermyriad) / 10000d);
            guard.Elemental = Math.Max(0, 1 + Own(Stat.MortalCallElementalDamageTakenPlusPctFinal) / 100d)
                * Math.Max(0, 1 + removable * Own(Stat.ImmortalCallElementalDamageTakenPlusPctFinalPerEnduranceChargeConsumedPermyriad) / 10000d);
            session.PresentCharges();
        }
        else if (MoltenShellGuard(guard.Effect))
        {
            guard.Armour = Own(Stat.BasePhysicalDamageReductionRating);
            guard.Percent = Math.Clamp(Own(Stat.MoltenShellDamageAbsorbedPct), 0, 100);
            guard.Reflection = Own(Stat.MoltenShellPctOfAbsorbedDamageDealtAsReflectedFire);
        }
        duration /= RecipientEffectTime.Rate(1, key => GolemStat(playerStats, key));
        guard.Until = now + duration;
        guard.NextPulseAt = vaal ? now + 1 : 0;
        // It ends at Until (Molten Shell reflects then), whoever reads it first.
        AfterSeconds(player, duration, () => { _ = CurrentTriggeredGuard(player, AreaTime(player)); });
        player.Pools = PoolsFor(player, player.Pools.Level);
        if (MoltenShellGuard(guard.Effect))
            guard.Remaining = (int)Math.Min(Math.Max(0, Own(Stat.MoltenShellMaxDamageAbsorbed)),
                (long)player.Pools.Armour * Math.Max(0, Own(Stat.MoltenShellDamageAbsorbLimitPctOfArmour)) / 100);
        guard.Buff = player.NextBuff++;
        ScheduleGuardPulse(player, guard, now);
        session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
        PresentTriggeredGuard(player, now);
        // An instant skill's 0x1ce runs its action without an animation, so the client plays none of the cast art of
        // its own: the activation sound of Immortal Call is in that art's sound events.
        if (guard.Effect == "ImmortalCall") session.Enqueue(() => PresentImmortalCallCast(player));
        Note($"triggered guard {guard.Effect} ({duration:0.###} s)");
        if (cooldown is null) return;
        // The bar entry goes last, after the stats send: a 0x53 rebuild re-creates the slot and resets its entry to the
        // skill's own recovery rate (measured 2026-11-18, a freeze sent before it was lost), so the held entry must be
        // the last cooldown message even if that send rebuilds the bar. Held, the bar shows the skill's own cooldown at a zero
        // recovery rate, as the official client shows a cooldown that does not recover during an effect; it resumes at
        // the character's rate when the buff ends (EndTriggeredGuard), which is when TriggerGuardReady accepts a new one.
        // Built now and marked sent, so the native cooldown refresh does not publish it again.
        var held = GuardCooldownBatch(player, guard, cooldown, now) ?? [];
        session.Enqueue(() => Send(player, held,
            $"0x02bc guard cooldown: held for {duration:0.###} s"));
    }

    static ushort GuardRow(string effect) => effect switch { "ImmortalCall" => 110, "MoltenShell" or VaalMoltenShellEffect => 114, _ => 1302 };

    /// <summary>Immortal Call's cast art (MiscAnimated <c>ImmortalCallCast</c>), whose sound events are the activation
    /// sound.</summary>
    public const string ImmortalCallCastAo = "Metadata/Effects/Spells/immortal_call/rig.ao";

    /// <summary>
    /// The cast art of Immortal Call, which the client does not play by itself: its use is an instant skill, and
    /// S→C 0x1ce runs such an action without an animation. An effect object with Animated bit3 plays the .ao's own
    /// default animation (and so its SoundEvents); attached to the player's root bone it rides the client's predicted
    /// position, as Righteous Fire's ring does, and it is removed when the art is over.
    /// </summary>
    static async Task PresentImmortalCallCast(Player player)
    {
        if (player.Closed || player.LeavingArea) return;
        int epoch = player.ProjectileEpoch;
        var at = player.Walk.Position(AreaTime(player));
        uint id = player.TakeObjectId();
        var art = new EffectObject(at.X, at.Y, ImmortalCallCastAo)
        {
            AnimatedFlags = 9,
            Parent = player.Id,
        };
        if (!await SendWorldCreate(player, id,
            () => !player.Closed && !player.LeavingArea && player.ProjectileActive(epoch)
                ? InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), art.Blob())
                : null,
            "Immortal Call cast art", wake: true, awaitBirth: false))
            return;
        SendAfter(player, 2, async () =>
        {
            // The object belongs to the area it was made in: a transition to another area must not remove its id there.
            if (player.Closed || player.LeavingArea || !player.ProjectileActive(epoch)) return;
            await SendWorldRemoval(player, id, null);
        });
    }

    // The guard's buff: its countdown and what it has left to absorb (list[0]); a new guard or a hit sets it again.
    public static void PresentTriggeredGuard(Player player, double now)
    {
        if (CurrentTriggeredGuard(player, now, advancePulse: true) is not { Until: > 0 } guard) return;
        // Non-recovery definitions have no inline stats. The guard's native backend reads additional-data[0]
        // through Buff+0x80 (0x141f84000); an empty list leaves a null pointer and crashes the HUD.
        player.Area.Buffs.Set(player.Id, new(GuardRow(guard.Effect), player.Id, guard.Skill, U68: Buff.Seconds(guard.Until - now),
            ActiveSkill: SkillsFor(player).ActionHash(guard.Gem!.Effect) is ushort action ? (guard.Skill, action) : null,
            List: [(uint)guard.Remaining], KeepInstance: true));
    }

    public static Dictionary<uint, int> WithTriggeredGuard(Player player, Dictionary<uint, int> stats)
    {
        // A stats refresh can run from the output queue. The area timer and admitted hits own the guard's lifetime.
        if (!TriggeredGuards.TryGetValue(player, out var guard) || !TriggeredGuardCurrent(player, guard)
            || guard.Until <= AreaTime(player)) return stats;
        void Add(uint name, int value)
        {
            if (name is var key) stats[key] = stats.GetValueOrDefault(key) + value;
        }
        Add(Stat.AffectedByGuardSkill, 1);
        Add(Stat.BasePhysicalDamageReductionRating, guard.Armour);
        if (guard.Effect == "QuickGuard") Add(Stat.AvoidBleedPct, 100);
        return stats;
    }

    public static (int Physical, int Other, int Chaos) TriggeredGuardHit(Player player,
        int physical, int other, int chaos, double at)
    {
        (physical, other, chaos) = MitigateTriggeredGuardHit(player, physical, other, chaos, at);
        return AbsorbTriggeredGuardHit(player, physical, other, chaos, at);
    }

    static (int Physical, int Other, int Chaos) MitigateTriggeredGuardHit(Player player,
        int physical, int other, int chaos, double at) =>
        CurrentTriggeredGuard(player, at) is { Until: > 0 } guard
            ? ((int)(physical * guard.Physical), (int)(other * guard.Elemental), chaos)
            : (physical, other, chaos);

    static (int Physical, int Other, int Chaos) AbsorbTriggeredGuardHit(Player player,
        int physical, int other, int chaos, double at)
    {
        if (CurrentTriggeredGuard(player, at, advancePulse: true) is not { Until: > 0 } guard) return (physical, other, chaos);
        int Absorb(int damage)
        {
            int absorbed = (int)Math.Min(guard.Remaining, (long)damage * guard.Percent / 100);
            guard.Remaining -= absorbed;
            guard.Absorbed += absorbed;
            if (guard.NextPulseAt > 0) guard.AbsorbedSincePulse += absorbed;
            return damage - absorbed;
        }
        physical = Absorb(physical); other = Absorb(other); chaos = Absorb(chaos);
        if (guard.Percent > 0)
        {
            if (guard.Remaining == 0) EndTriggeredGuard(player, guard, at);
            else PresentTriggeredGuard(player, at);
        }
        return (physical, other, chaos);
    }
}
