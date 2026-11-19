using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    sealed class TriggeredGuard(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public double Until, ReadyAt, RecoverySeconds;
        public int Percent, Remaining, Absorbed, Armour, Reflection;
        public double Physical = 1, Elemental = 1;
        public ushort Buff, Skill;
        public string Effect = "";
        public SupportedGem? Gem;
        public ProjectileSession? Session;
    }
    static readonly RuleTable<ClientState, TriggeredGuard> TriggeredGuards = new();
    static bool TriggerGuardSpell(ClientState conn, SupportedGem gem) => GuardSpellEffect(SkillsFor(conn).EffectId(gem.Effect));
    static bool GuardSpellEffect(string? effect) => effect is "QuickGuard" or "ImmortalCall" or "MoltenShell";

    static TriggeredGuard? CurrentTriggeredGuard(ClientState conn, double now)
    {
        if (!TriggeredGuards.TryGetValue(conn, out var guard)) return null;
        if (guard.Epoch != conn.ProjectileEpoch || !ReferenceEquals(guard.Rules, CombatLifetime)
            || conn.Closed || conn.LeavingArea || conn.PendingTransfer is not null || conn.PlayerLife == 0)
        { TriggeredGuards.Remove(conn); return null; }
        if (guard.Until > 0 && now >= guard.Until) EndTriggeredGuard(conn, guard, guard.Until);
        return guard;
    }
    public static bool TriggerGuardReady(ClientState conn, double now) => CurrentTriggeredGuard(conn, now)
        is not { } guard || guard.Until == 0 && now >= guard.ReadyAt;

    static void EndTriggeredGuard(ClientState conn, TriggeredGuard guard, double now)
    {
        guard.Until = 0;
        guard.ReadyAt = now + guard.RecoverySeconds;
        // The blackout is over: the bar entry becomes the skill's own cooldown and counts down from here.
        if (guard.Gem is { } ended)
            lock (conn.Monsters)
                SingleUseState(conn, ended.Effect, guard.Skill)?.Resume(now,
                    SkillsFor(conn).CooldownMs(ended.Effect, ended.Level));
        if (guard.Session is not { } session) return;
        ushort buff = guard.Buff; guard.Buff = 0;
        if (buff != 0) session.Packet(InstanceEntities.RemoveBuff(PlayerId, buff));
        if (guard.Effect == "MoltenShell" && guard.Absorbed > 0)
        {
            double reflected = guard.Absorbed * guard.Reflection / 100d;
            int radius = SkillRadius(conn, guard.Gem!.Stats, true, guard.Gem.Effect);
            session.Enqueue(async () =>
            {
                if (!session.Active || conn.PlayerLife == 0 || conn.LeavingArea) return;
                var deaths = new List<(uint Id, SpawnedMonster M)>();
                foreach (var (id, monster) in conn.Monsters.ToArray())
                {
                    MonsterHit? hit; int damage;
                    lock (conn.Monsters)
                    {
                        if (!session.Active || conn.Paused || conn.PlayerLife == 0) return;
                        if (!monster.Hostile || monster.Dead || monster.Life == 0
                            || Dist(monster.Walk.Position(AreaTime(conn)), conn.Walk.Position(AreaTime(conn))) > radius) continue;
                        // Reflected damage has no spell/attack scaling, crit, penetration, leech or on-hit procs.
                        var resisted = DamageModel.Mitigate(DamagePacket.Of(DamageType.Fire, reflected, reflected),
                            type => MonsterHitResistance(monster, type, default, AreaTime(conn)));
                        damage = (int)Math.Round(resisted.MinTotal * MonsterDamageTaken(monster, spell: false,
                            AreaTime(conn), elemental: true, hit: false));
                        hit = ApplyMonsterHitLocked(conn, monster, damage);
                    }
                    if (hit is { } applied)
                        await NotifyMonsterHit(session.remote, session.stream, session.ciphers, conn, id, monster,
                            applied, damage, "Molten Shell reflected damage", deaths: deaths);
                }
                foreach (var (id, monster) in deaths)
                    await MonsterKilled(session.remote, session.stream, session.ciphers, conn, id, monster);
            });
        }
        guard.Armour = 0;
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        // Built now and marked sent, so the stats send's own cooldown refresh does not publish the entry a second time:
        // every repeat restarts the bar's cooldown animation on this client (live 2026-11-18).
        byte[]? counted = guard.Gem is { } counting ? GuardCooldownEntry(conn, guard.Skill, counting.Effect) : null;
        session.Enqueue(() => SendCharacterStats(session.remote, session.stream, session.ciphers, conn, "guard ended"));
        // A triggered guard has no instant action entry of its own, so the resumed entry is published here as well.
        if (counted is { Length: > 0 })
            session.Enqueue(() => Send(session.remote, session.stream, session.ciphers, counted,
                "0x02bc guard cooldown counts after its buff"));
    }

    /// <summary>The client's stored-use entry for a guard skill as the bar shows it now: the skill's own cooldown at a
    /// zero recovery rate while the buff runs (the official bar's frozen entry, measured 3.00 / 3.00 / rate 0, which
    /// this client takes when it did not predict the entry itself), then the same cooldown at the character's recovery
    /// rate. One publisher, so a stats or skill refresh repeats the same numbers.</summary>
    public static byte[]? GuardCooldownEntry(ClientState conn, ushort skill, int effect)
    {
        lock (conn.Monsters)
        {
            if (SingleUseState(conn, effect, skill) is not { } state) return null;
            state.Refresh(conn.Clock.Elapsed.TotalSeconds, state.Rate);
            if (!state.Held && state.FullyRecovered) return null;
            float total = state.TotalBaseMilliseconds / 1000f;
            float remaining = state.Held ? total : (float)(state.RemainingBaseMilliseconds / 1000);
            var entry = InstanceEntities.PlayerSingleUseCooldowns(PlayerId,
                [new SingleUseCooldownUpdate(skill, remaining, total, state.Held ? 0f : (float)state.Rate, state.SpentUses)],
                canDetonateMines: CanDetonateMines(conn));
            state.NativeRefreshed();
            return entry;
        }
    }

    /// <summary>A guard's held rate-0 entry built from the skill's own base cooldown, published before its instant
    /// action so the client skips the press prediction it would otherwise start from a use (issue #1127). It mutates no
    /// server state: the use's acknowledgement and <see cref="StartTriggeredGuard"/>'s <see cref="SingleUseCooldownState.Hold"/>
    /// behind the instant action own the state, so a refused action cannot leave a held cooldown stuck.</summary>
    public static byte[]? GuardCooldownPreEntry(ClientState conn, ushort skill, int effect)
    {
        int ms = SkillsFor(conn).CooldownMs(effect, SupportedSkill(conn, skill).Level);
        if (ms <= 0) return null;
        float total = ms / 1000f;
        return InstanceEntities.PlayerSingleUseCooldowns(PlayerId,
            [new SingleUseCooldownUpdate(skill, total, total, 0f, 1)], canDetonateMines: CanDetonateMines(conn));
    }

    /// <summary>The held guard's rate-0 entry to re-publish after a skill-list refresh: the client's skill replacement
    /// refreshes an existing zero-rate cooldown entry (<c>0x141ef1573</c>), which would start the bar counting again while
    /// the buff runs (issue #1127, seen on movement, which refreshes the conditional item stats). Null when no guard buff
    /// is running.</summary>
    public static byte[]? HeldGuardCooldownRefresh(ClientState conn)
    {
        if (CurrentTriggeredGuard(conn, conn.Clock.Elapsed.TotalSeconds) is not { Until: > 0, Gem: { } gem } guard) return null;
        return GuardCooldownEntry(conn, guard.Skill, gem.Effect);
    }

    // "Guard Skills have X% increased Duration" is the same increased-duration sum as skill_effect_duration_+%.
    internal static double GuardDuration(int baseMilliseconds, Func<string, int> player, Func<string, int> own) =>
        SkillDurationModifiers.Seconds(baseMilliseconds,
            name => player(name) + (name == SkillDurationModifiers.IncreasedDuration ? player("guard_skill_effect_duration_+%") : 0),
            own);

    // Called immediately after the use's successful payment, under the connection lock: a guard skill cast from the
    // skill bar arrives here as well as a triggered one (Cast When Damage Taken).
    public static void StartTriggeredGuard(ClientState conn, ProjectileSession session, ushort skill, double now)
    {
        var gem = SupportedSkill(conn, skill);
        if (!TriggerGuardReady(conn, now)) return;
        var player = CharacterPlayerStats(conn);
        int Own(string name) => GolemStat(gem.Stats, name);
        int Get(string name) => Own(name) + GolemStat(player, name);
        var guard = TriggeredGuards.GetValue(conn, _ => new(conn.ProjectileEpoch, CombatLifetime));
        guard.Gem = gem; guard.Skill = skill; guard.Session = session;
        guard.Effect = SkillsFor(conn).EffectId(gem.Effect)!;
        guard.Physical = guard.Elemental = 1;
        guard.Percent = guard.Remaining = guard.Absorbed = guard.Armour = guard.Reflection = 0;
        double duration = GuardDuration(Own("base_skill_effect_duration"), name => GolemStat(player, name), Own);
        if (guard.Effect == "QuickGuard")
        {
            guard.Percent = Math.Clamp(Own("quick_guard_damage_absorbed_%"), 0, 100);
            guard.Remaining = Math.Max(0, Own("quick_guard_damage_absorb_limit"));
        }
        else if (guard.Effect == "ImmortalCall")
        {
            var charges = ChargesOf(conn);
            int removable = charges.Converted(ChargeType.Endurance) ? 0 : Math.Min(5,
                Math.Max(0, charges.Effective(ChargeType.Endurance) - charges.Limits(ChargeType.Endurance).Minimum));
            if (!RandomRolls.Chance(Get("immortal_call_%_chance_to_not_consume_endurance_charges"), 100, Random.Shared))
                charges.State.Lose(ChargeType.Endurance, removable, charges.Limits(ChargeType.Endurance));
            guard.Remaining = removable;
            // The gem's 20% and the helmet enchantment's "increased Immortal Call duration per Endurance Charge
            // removed" are increased duration, added to the other increases (PoB SkillStatMap: Duration INC).
            int perChargeDuration = Get("buff_effect_duration_+%_per_removable_endurance_charge_limited_to_5")
                + Get("immortal_call_buff_effect_duration_+%_per_removable_endurance_charge");
            duration = GuardDuration(Own("base_skill_effect_duration"), name => GolemStat(player, name),
                name => Own(name) + (name == SkillDurationModifiers.IncreasedDuration ? removable * perChargeDuration : 0));
            guard.Physical = Math.Max(0, 1 + Own("mortal_call_physical_damage_taken_+%_final") / 100d)
                * Math.Max(0, 1 + removable * Own("mortal_call_physical_damage_taken_per_endurance_charge_consumed_final_permyriad") / 10000d);
            guard.Elemental = Math.Max(0, 1 + Own("mortal_call_elemental_damage_taken_+%_final") / 100d)
                * Math.Max(0, 1 + removable * Own("immortal_call_elemental_damage_taken_+%_final_per_endurance_charge_consumed_permyriad") / 10000d);
            session.PresentCharges();
        }
        else if (guard.Effect == "MoltenShell")
        {
            guard.Armour = Own("base_physical_damage_reduction_rating");
            guard.Percent = Math.Clamp(Own("molten_shell_damage_absorbed_%"), 0, 100);
            guard.Reflection = Own("molten_shell_%_of_absorbed_damage_dealt_as_reflected_fire");
        }
        guard.Until = now + duration;
        double recovery = Math.Max(0, 1 + Get("virtual_cooldown_speed_+%") / 100d + Own("base_cooldown_speed_+%") / 100d);
        guard.RecoverySeconds = recovery > 0 ? SkillsFor(conn).CooldownMs(gem.Effect, gem.Level) / 1000d / recovery : double.PositiveInfinity;
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        if (guard.Effect == "MoltenShell")
            guard.Remaining = (int)Math.Min(Math.Max(0, Own("molten_shell_max_damage_absorbed")),
                (long)conn.Pools.Armour * Math.Max(0, Own("molten_shell_damage_absorb_limit_%_of_armour")) / 100);
        guard.Buff = conn.NextBuff++;
        session.Enqueue(() => SendCharacterStats(session.remote, session.stream, session.ciphers, conn, "guard started"));
        session.Packet(TriggeredGuardPresentation(conn, now), $"triggered guard {guard.Effect} ({duration:0.###} s)");
        // The bar entry goes last, after the stats send: a 0x53 rebuild re-creates the slot and resets its entry to the
        // skill's own recovery rate (measured 2026-11-18, a freeze sent before it was lost), so the held entry must be
        // the last cooldown message even if that send rebuilds the bar. Held, the bar shows the skill's own cooldown at a zero
        // recovery rate, as the official client shows a cooldown that does not recover during an effect; it resumes at
        // the character's rate when the buff ends (EndTriggeredGuard), which is when TriggerGuardReady accepts a new one.
        lock (conn.Monsters) SingleUseState(conn, gem.Effect, skill)?.Hold(SkillsFor(conn).CooldownMs(gem.Effect, gem.Level));
        // Built now and marked sent, so later refreshes do not publish it again: every repeat replays the bar animation.
        var held = GuardCooldownEntry(conn, skill, gem.Effect) ?? [];
        session.Enqueue(() => Send(session.remote, session.stream, session.ciphers, held,
            $"0x02bc guard cooldown: held for {duration:0.###} s"));
    }

    public static byte[] TriggeredGuardPresentation(ClientState conn, double now)
    {
        if (CurrentTriggeredGuard(conn, now) is not { Until: > 0 } guard) return [];
        ushort row = guard.Effect switch { "ImmortalCall" => 110, "MoltenShell" => 114, _ => 1302 };
        // Non-recovery definitions have no inline stats. The guard's native backend reads additional-data[0]
        // through Buff+0x80 (0x141f84000); an empty list leaves a null pointer and crashes the HUD.
        return InstanceEntities.AddBuff(PlayerId, guard.Buff, row, PlayerId, guard.Skill,
            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, guard.Until - now)),
            activeSkill: SkillsFor(conn).ActionHash(guard.Gem!.Effect) is ushort action ? (guard.Skill, action) : null,
            list: [(uint)guard.Remaining]);
    }

    public static Dictionary<uint, int> WithTriggeredGuard(ClientState conn, Dictionary<uint, int> stats)
    {
        if (CurrentTriggeredGuard(conn, conn.Clock.Elapsed.TotalSeconds) is not { Until: > 0 } guard) return stats;
        void Add(string name, int value)
        {
            if (Stats.Key(name) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
        }
        Add("affected_by_guard_skill", 1);
        Add("base_physical_damage_reduction_rating", guard.Armour);
        if (guard.Effect == "QuickGuard") Add("avoid_bleed_%", 100);
        return stats;
    }

    public static (int Physical, int Other, int Chaos) TriggeredGuardHit(ClientState conn,
        int physical, int other, int chaos, double at)
    {
        (physical, other, chaos) = MitigateTriggeredGuardHit(conn, physical, other, chaos, at);
        return AbsorbTriggeredGuardHit(conn, physical, other, chaos, at);
    }

    static (int Physical, int Other, int Chaos) MitigateTriggeredGuardHit(ClientState conn,
        int physical, int other, int chaos, double at) =>
        CurrentTriggeredGuard(conn, at) is { Until: > 0 } guard
            ? ((int)(physical * guard.Physical), (int)(other * guard.Elemental), chaos)
            : (physical, other, chaos);

    static (int Physical, int Other, int Chaos) AbsorbTriggeredGuardHit(ClientState conn,
        int physical, int other, int chaos, double at)
    {
        if (CurrentTriggeredGuard(conn, at) is not { Until: > 0 } guard) return (physical, other, chaos);
        int Absorb(int damage)
        {
            int absorbed = (int)Math.Min(guard.Remaining, (long)damage * guard.Percent / 100);
            guard.Remaining -= absorbed; guard.Absorbed += absorbed;
            return damage - absorbed;
        }
        physical = Absorb(physical); other = Absorb(other); chaos = Absorb(chaos);
        if (guard.Percent > 0)
        {
            if (guard.Remaining == 0) EndTriggeredGuard(conn, guard, at);
            else guard.Session?.Packet(TriggeredGuardPresentation(conn, at));
        }
        return (physical, other, chaos);
    }
}
