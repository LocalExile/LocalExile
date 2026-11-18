using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Self buffs a player casts by hand (CastGemSkill): the guard skills start the triggered-guard buff (TriggeredGuards),
    // Phase Run and Blood Rage their own buffs below. Their stats join the character's sources while they last
    // (WithSelfBuffs), as the guard's do. Phase Run is a Movement skill that stays in place, so its instant use is
    // accepted as a self buff's.
    static bool InPlaceSelfBuff(ClientState conn, int effect) => SkillsFor(conn).EffectId(effect) is "PhaseRun";

    // Refused before the cost: a guard is running, or the guards' shared recovery after the last one has not passed
    // (display_this_skill_cooldown_does_not_recover_during_buff).
    public static string? SelfBuffRefused(ClientState conn, int effect) =>
        GuardSpellEffect(SkillsFor(conn).EffectId(effect)) && !TriggerGuardReady(conn, conn.Clock.Elapsed.TotalSeconds)
            ? "a guard skill is active or recovering" : null;

    // After the paid use: true when this was one of the self buffs (its effect has started).
    public static async Task<bool> CastSelfBuff(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, int effect)
    {
        string? id = SkillsFor(conn).EffectId(effect);
        if (GuardSpellEffect(id))
        {
            var session = ProjectilesFor(remote, stream, ciphers, conn);
            lock (conn.Monsters) StartTriggeredGuard(conn, session, skillId, conn.Clock.Elapsed.TotalSeconds);
            return true;
        }
        if (id == "PhaseRun") { await StartPhaseRun(remote, stream, ciphers, conn, skillId); return true; }
        if (id == "BloodRage") { await StartBloodRage(remote, stream, ciphers, conn, skillId); return true; }
        return false;
    }

    sealed class SelfBuff
    {
        public ushort Buff, Skill;
        public int Token;
        public double Until, Seconds, Degen;
        public readonly Dictionary<string, int> Stats = [];
    }
    sealed class SelfBuffSet
    {
        public SelfBuff? PhaseRun, PhaseRunDamage, BloodRage;
    }
    static readonly RuleTable<ClientState, SelfBuffSet> SelfBuffs = new();

    static ushort BuffRow(string id, ushort fallback)
    {
        if (!ClientTables.Has("BuffDefinitions")) return fallback;
        foreach (var row in ClientTables.Table("BuffDefinitions"))
            if (row.Text("Id") == id) return (ushort)row.Index;
        return fallback;
    }
    static readonly Lazy<ushort> PhaseRunBuffRow = new(() => BuffRow("new_phase_run", 467));
    static readonly Lazy<ushort> PhaseRunDamageBuffRow = new(() => BuffRow("new_phase_run_damage", 468));
    static readonly Lazy<ushort> BloodRageBuffRow = new(() => BuffRow("blood_rage", 11));

    // Under the Monsters lock (WithSupportGemBuffs): the running self buffs' stats, from the accepted cast on.
    static Dictionary<uint, int> WithSelfBuffs(ClientState conn, Dictionary<uint, int> stats)
    {
        if (!SelfBuffs.TryGetValue(conn, out var set)) return stats;
        // On the item-stat lookup's path (performance.md, "Hot paths"): no allocation, nothing read when none runs.
        if (conn.PlayerLife <= 0) return stats;
        double now = conn.Clock.Elapsed.TotalSeconds;
        AddRunning(set.PhaseRun);
        AddRunning(set.PhaseRunDamage);
        AddRunning(set.BloodRage);
        return stats;

        void AddRunning(SelfBuff? buff)
        {
            if (buff is null || now >= buff.Until) return;
            foreach (var (name, value) in buff.Stats)
                if (Stats.Key(name) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
        }
    }

    // Shows a self buff (a new instance, so the client's timer restarts) with the pools its stats change, and removes
    // it when its time runs out unless a later start or refresh took it over (the token).
    static async Task ShowSelfBuff(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SelfBuff buff, ushort row, string label, Action? ended = null, bool restat = true)
    {
        int token;
        lock (conn.Monsters) token = ++buff.Token;
        using var fresh = new PlayerStatScope(conn, fresh: true);
        await Send(remote, stream, ciphers, [], $"0x01c1 {label} for {buff.Seconds:0.###} s", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || conn.RageRetired || buff.Token != token) return null;
                var bytes = new List<byte>();
                if (buff.Buff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, buff.Buff));
                buff.Buff = conn.NextBuff++;
                bytes.AddRange(InstanceEntities.AddBuff(PlayerId, buff.Buff, row, PlayerId, sourceSkill: buff.Skill,
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, buff.Until - conn.Clock.Elapsed.TotalSeconds))));
                if (restat)
                {
                    RecomputePoolsAndSpeed(conn);
                    bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                }
                return [.. bytes];
            }
        });
        SendAfter(Math.Max(0, buff.Until - conn.Clock.Elapsed.TotalSeconds), () => EndSelfBuff(remote, stream, ciphers, conn,
            buff, token, $"0x01c2 {label} ends", ended));
    }

    static async Task EndSelfBuff(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SelfBuff buff, int token, string label, Action? ended = null)
    {
        using var fresh = new PlayerStatScope(conn, fresh: true);
        await Send(remote, stream, ciphers, [], label, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (buff.Token != token || buff.Buff == 0) return null;
                var bytes = new List<byte>(InstanceEntities.RemoveBuff(PlayerId, buff.Buff));
                buff.Buff = 0;
                buff.Until = 0;
                ended?.Invoke();
                if (conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
                RecomputePoolsAndSpeed(conn);
                bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                return [.. bytes];
            }
        });
    }

    // Phase Run (BuffDefinitions new_phase_run): base_movement_velocity_+% and Phasing for base_skill_effect_duration,
    // 100% increased per removable Frenzy Charge (skill_effect_duration_+%_per_removable_frenzy_charge), which it
    // consumes. Using any skill replaces it with new_phase_run_damage for base_secondary_skill_effect_duration:
    // phase_run_melee_physical_damage_+%_final on melee physical damage (PhaseRunOnSkillUse).
    static async Task StartPhaseRun(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId)
    {
        var own = SupportedSkill(conn, skillId).Stats;
        var player = CharacterPlayerStats(conn);
        int Player(string name) => GolemStat(player, name);
        int Own(string name) => GolemStat(own, name);
        var set = SelfBuffs.GetValue(conn, _ => new());
        int removable;
        lock (conn.Monsters)
        {
            var charges = ChargesOf(conn);
            removable = charges.Converted(ChargeType.Frenzy) ? 0 : Math.Max(0,
                charges.Effective(ChargeType.Frenzy) - charges.Limits(ChargeType.Frenzy).Minimum);
            if (removable > 0) charges.State.Lose(ChargeType.Frenzy, removable, charges.Limits(ChargeType.Frenzy));
        }
        int perCharge = Own("skill_effect_duration_+%_per_removable_frenzy_charge");
        double seconds = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player,
            name => Own(name) + (name == SkillDurationModifiers.IncreasedDuration ? perCharge * removable : 0));
        var buff = set.PhaseRun ??= new();
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
        {
            buff.Skill = skillId;
            buff.Seconds = seconds;
            buff.Until = now + seconds;
            buff.Stats.Clear();
            buff.Stats["base_movement_velocity_+%"] = Own("base_movement_velocity_+%");
            buff.Stats["stealth_+%"] = Own("stealth_+%");
            if (Own("phase_through_objects") != 0) conn.PhasingUntil = Math.Max(conn.PhasingUntil, buff.Until);
        }
        if (removable > 0) ProjectilesFor(remote, stream, ciphers, conn).PresentCharges();
        Console.WriteLine($"[gateway] {remote} Phase Run: {seconds:0.###} s ({removable} Frenzy Charges consumed), "
            + $"+{buff.Stats["base_movement_velocity_+%"]}% movement speed, phasing");
        await ShowSelfBuff(remote, stream, ciphers, conn, buff, PhaseRunBuffRow.Value, "Phase Run");
    }

    // Any other skill the player uses while Phase Run lasts (CastGemSkill after its cost, the default attack's swing)
    // ends it and starts its damage buff. attackSeconds: the using attack's swing time (a gem attack's when null).
    // shortcut: hits resolve at contact, usually after the 0.2 s buff, so its more damage stays for the using attack's
    // swing too, longer than the buff icon shows.
    public static void PhaseRunOnSkillUse(ClientState conn, ushort skillId, double? attackSeconds = null)
    {
        if (!SelfBuffs.TryGetValue(conn, out var set) || set.PhaseRun is not { } run || run.Skill == skillId) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (now >= run.Until) return;
        var own = SupportedSkill(conn, run.Skill).Stats;
        var player = CharacterPlayerStats(conn);
        double damageSeconds = SkillDurationModifiers.Seconds(GolemStat(own, "base_secondary_skill_effect_duration"),
            name => GolemStat(player, name), name => GolemStat(own, name));
        var used = SupportedSkill(conn, skillId);
        double swing = attackSeconds ?? (used.Effect >= 0 && SkillsFor(conn).IsAttack(used.Effect)
            ? SupportedAttackSeconds(conn, skillId) : 0);
        var damage = set.PhaseRunDamage ??= new();
        lock (conn.Monsters)
        {
            if (Math.Abs(conn.PhasingUntil - run.Until) < 1e-9) conn.PhasingUntil = now;
            run.Until = now;
            damage.Skill = run.Skill;
            damage.Seconds = damageSeconds;
            damage.Until = now + damageSeconds + Math.Max(0, swing);
            damage.Stats.Clear();
            damage.Stats["phase_run_melee_physical_damage_+%_final"] = GolemStat(own, "phase_run_melee_physical_damage_+%_final");
        }
        var (remote, stream, ciphers) = conn.Wire;
        if (stream is null) return;
        _ = EndSelfBuff(remote, stream, ciphers, conn, run, run.Token, "0x01c2 Phase Run replaced by a skill use");
        _ = ShowSelfBuff(remote, stream, ciphers, conn, damage, PhaseRunDamageBuffRow.Value, "Phase Run damage");
    }

    // Blood Rage (BuffDefinitions blood_rage): for base_skill_effect_duration, attack_speed_+%_granted_from_skill as
    // attack speed, its physical attack life leech and Frenzy Charge chance on kill, and physical degeneration of its
    // base_physical_damage_%_of_maximum_life/energy_shield_to_deal_per_minute (lethal, as the trial traps' is; energy
    // shield first). A kill while it lasts refreshes its duration (ActiveSkills blood_rage); a new cast restarts it.
    // shortcut: increased buff effect and the degeneration's damage-over-time modifiers are not applied.
    static async Task StartBloodRage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId)
    {
        var own = SupportedSkill(conn, skillId).Stats;
        var player = CharacterPlayerStats(conn);
        int Own(string name) => GolemStat(own, name);
        double seconds = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), name => GolemStat(player, name), Own);
        var buff = SelfBuffs.GetValue(conn, _ => new()).BloodRage ??= new();
        lock (conn.Monsters)
        {
            buff.Skill = skillId;
            buff.Seconds = seconds;
            buff.Until = conn.Clock.Elapsed.TotalSeconds + seconds;
            buff.Stats.Clear();
            buff.Stats["attack_speed_+%"] = Own("attack_speed_+%_granted_from_skill");
            buff.Stats["life_leech_from_physical_attack_damage_permyriad"] = Own("life_leech_from_physical_attack_damage_permyriad");
            buff.Stats["add_frenzy_charge_on_kill_%_chance"] = Own("add_frenzy_charge_on_kill_%_chance");
            double degen = (conn.PlayerMaxLife * (double)Own("base_physical_damage_%_of_maximum_life_to_deal_per_minute")
                + conn.Pools.EnergyShield * (double)Own("base_physical_damage_%_of_maximum_energy_shield_to_deal_per_minute")) / 6000;
            conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - buff.Degen + degen);
            buff.Degen = degen;
        }
        Console.WriteLine($"[gateway] {remote} Blood Rage: {seconds:0.###} s, +{buff.Stats["attack_speed_+%"]}% attack speed, "
            + $"{buff.Degen:0.#} physical damage per second");
        await ShowSelfBuff(remote, stream, ciphers, conn, buff, BloodRageBuffRow.Value, "Blood Rage", () => EndBloodRageDegen(conn, buff));
    }

    // Under the Monsters lock, when the buff ends.
    static void EndBloodRageDegen(ClientState conn, SelfBuff buff)
    {
        conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - buff.Degen);
        buff.Degen = 0;
    }

    // A hostile monster the player killed (ChargesOnKill): a running Blood Rage starts its duration again.
    public static void SelfBuffsOnKill(ClientState conn)
    {
        if (!SelfBuffs.TryGetValue(conn, out var set) || set.BloodRage is not { } rage) return;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (now >= rage.Until) return;
            rage.Until = now + rage.Seconds;
        }
        var (remote, stream, ciphers) = conn.Wire;
        if (stream is null) return;
        // Per kill: the stats are the same while it runs, so only the buff's timer is shown again (no stat build).
        _ = ShowSelfBuff(remote, stream, ciphers, conn, rage, BloodRageBuffRow.Value, "Blood Rage refreshed by a kill",
            () => EndBloodRageDegen(conn, rage), restat: false);
    }
}
