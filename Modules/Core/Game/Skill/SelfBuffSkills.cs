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
    static bool InPlaceSelfBuff(Player player, int effect) => SkillsFor(player).EffectId(effect) is "PhaseRun";

    // Refused before the cost: a guard is running, or the guards' shared recovery after the last one has not passed
    // (display_this_skill_cooldown_does_not_recover_during_buff).
    public static string? SelfBuffRefused(Player player, int effect) =>
        GuardSpellEffect(SkillsFor(player).EffectId(effect)) && !TriggerGuardReady(player, AreaTime(player))
            ? "a guard skill is active or recovering" : null;

    // After the paid use: true when this was one of the self buffs (its effect has started).
    public static async Task<bool> CastSelfBuff(Player player, ushort skillId, int effect)
    {
        string? id = SkillsFor(player).EffectId(effect);
        if (GuardSpellEffect(id))
        {
            var session = ProjectilesFor(player);
            StartTriggeredGuard(player, session, skillId, AreaTime(player));
            return true;
        }
        if (id == "PhaseRun") { await StartPhaseRun(player, skillId); return true; }
        if (id == "BloodRage") { await StartBloodRage(player, skillId); return true; }
        return false;
    }

    sealed class SelfBuff
    {
        public ushort Buff, Skill;
        public int Token;
        public double Until, Seconds, Degen;
        public readonly Dictionary<uint, int> Stats = [];
    }
    sealed class SelfBuffSet
    {
        public SelfBuff? PhaseRun, PhaseRunDamage, BloodRage;
    }
    static readonly RuleTable<Player, SelfBuffSet> SelfBuffs = new();

    static ushort BuffRow(string id, ushort fallback)
    {
        if (!ClientTables.Has("BuffDefinitions")) return fallback;
        foreach (var row in ClientTables.Table("BuffDefinitions"))
            if (row.Text("Id") == id) return (ushort)row.Index;
        return fallback;
    }
    static ushort PhaseRunBuffRow = 467;
    static ushort PhaseRunDamageBuffRow = 468;
    static ushort BloodRageBuffRow = 11;

    internal static void LoadSelfBuffSkillsData()
    {
        PhaseRunBuffRow = BuffRow("new_phase_run", 467);
        PhaseRunDamageBuffRow = BuffRow("new_phase_run_damage", 468);
        BloodRageBuffRow = BuffRow("blood_rage", 11);
    }

    // Under the Monsters lock (WithSupportGemBuffs): the running self buffs' stats, from the accepted cast on.
    static Dictionary<uint, int> WithSelfBuffs(Player player, Dictionary<uint, int> stats)
    {
        if (!SelfBuffs.TryGetValue(player, out var set)) return stats;
        // On the item-stat lookup's path (performance.md, "Hot paths"): no allocation, nothing read when none runs.
        if (player.PlayerLife <= 0) return stats;
        double now = AreaTime(player);
        AddRunning(set.PhaseRun);
        AddRunning(set.PhaseRunDamage);
        AddRunning(set.BloodRage);
        return stats;

        void AddRunning(SelfBuff? buff)
        {
            if (buff is null || now >= buff.Until) return;
            foreach (var (key, value) in buff.Stats)
                stats[key] = stats.GetValueOrDefault(key) + value;
        }
    }

    // Shows a self buff (a new instance, so the client's timer restarts) with the pools its stats change, and removes
    // it when its time runs out unless a later start or refresh took it over (the token).
    static async Task ShowSelfBuff(Player player,
        SelfBuff buff, ushort row, string label, Action? ended = null, bool restat = true)
    {
        int token = ++buff.Token;
        using var fresh = new PlayerStatScope(player, fresh: true);
        await Send(player, [], $"0x01c1 {label} for {buff.Seconds:0.###} s", atGate: () =>
        {
            if (player.Closed || player.LeavingArea || player.RageRetired || buff.Token != token) return null;
            var bytes = new List<byte>();
            if (buff.Buff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(player.Id, buff.Buff));
            buff.Buff = player.NextBuff++;
            bytes.AddRange(InstanceEntities.AddBuff(player.Id, buff.Buff, row, player.Id, sourceSkill: buff.Skill,
                u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, buff.Until - AreaTime(player)))));
            if (restat)
            {
                RecomputePoolsAndSpeed(player);
                bytes.AddRange(PlayerStatsOffFrames(player));
            }
            return [.. bytes];
        });
        SendAfter(player, Math.Max(0, buff.Until - AreaTime(player)), () => EndSelfBuff(player,
            buff, token, $"0x01c2 {label} ends", ended));
    }

    static async Task EndSelfBuff(Player player,
        SelfBuff buff, int token, string label, Action? ended = null)
    {
        using var fresh = new PlayerStatScope(player, fresh: true);
        await Send(player, [], label, atGate: () =>
        {
            if (buff.Token != token || buff.Buff == 0) return null;
            var bytes = new List<byte>(InstanceEntities.RemoveBuff(player.Id, buff.Buff));
            buff.Buff = 0;
            buff.Until = 0;
            ended?.Invoke();
            if (player.Closed || player.LeavingArea || player.RageRetired) return null;
            RecomputePoolsAndSpeed(player);
            bytes.AddRange(PlayerStatsOffFrames(player));
            return [.. bytes];
        });
    }

    // Phase Run (BuffDefinitions new_phase_run): base_movement_velocity_+% and Phasing for base_skill_effect_duration,
    // 100% increased per removable Frenzy Charge (skill_effect_duration_+%_per_removable_frenzy_charge), which it
    // consumes. Using any skill replaces it with new_phase_run_damage for base_secondary_skill_effect_duration:
    // phase_run_melee_physical_damage_+%_final on melee physical damage (PhaseRunOnSkillUse).
    static async Task StartPhaseRun(Player player, ushort skillId)
    {
        var own = SupportedSkill(player, skillId).Stats;
        var playerStats = CharacterPlayerStats(player);
        int PlayerStat(uint key) => GolemStat(playerStats, key);
        int Own(uint key) => GolemStat(own, key);
        var set = SelfBuffs.GetValue(player, _ => new());
        var charges = ChargesOf(player);
        int removable = charges.Converted(ChargeType.Frenzy) ? 0 : Math.Max(0,
            charges.Effective(ChargeType.Frenzy) - charges.Limits(ChargeType.Frenzy).Minimum);
        if (removable > 0) charges.State.Lose(ChargeType.Frenzy, removable, charges.Limits(ChargeType.Frenzy));
        int perCharge = Own(Stat.SkillEffectDurationPlusPctPerRemovableFrenzyCharge);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), PlayerStat,
            key => Own(key) + (key == SkillDurationModifiers.IncreasedDuration ? perCharge * removable : 0));
        var buff = set.PhaseRun ??= new();
        double now = AreaTime(player);
        buff.Skill = skillId;
        buff.Seconds = seconds;
        buff.Until = now + seconds;
        buff.Stats.Clear();
        buff.Stats[Stat.BaseMovementVelocityPlusPct] = Own(Stat.BaseMovementVelocityPlusPct);
        buff.Stats[Stat.StealthPlusPct] = Own(Stat.StealthPlusPct);
        if (Own(Stat.PhaseThroughObjects) != 0) player.PhasingUntil = Math.Max(player.PhasingUntil, buff.Until);
        if (removable > 0) ProjectilesFor(player).PresentCharges();
        Console.WriteLine($"[gateway] {player.Connection.Remote} Phase Run: {seconds:0.###} s ({removable} Frenzy Charges consumed), "
            + $"+{buff.Stats[Stat.BaseMovementVelocityPlusPct]}% movement speed, phasing");
        await ShowSelfBuff(player, buff, PhaseRunBuffRow, "Phase Run");
    }

    // Any other skill the player uses while Phase Run lasts (CastGemSkill after its cost, the default attack's swing)
    // ends it and starts its damage buff. attackSeconds: the using attack's swing time (a gem attack's when null).
    // shortcut: hits resolve at contact, usually after the 0.2 s buff, so its more damage stays for the using attack's
    // swing too, longer than the buff icon shows.
    public static void PhaseRunOnSkillUse(Player player, ushort skillId, double? attackSeconds = null)
    {
        if (!SelfBuffs.TryGetValue(player, out var set) || set.PhaseRun is not { } run || run.Skill == skillId) return;
        double now = AreaTime(player);
        if (now >= run.Until) return;
        var own = SupportedSkill(player, run.Skill).Stats;
        var playerStats = CharacterPlayerStats(player);
        double damageSeconds = SkillDurationModifiers.Seconds(GolemStat(own, Stat.BaseSecondarySkillEffectDuration),
            key => GolemStat(playerStats, key), key => GolemStat(own, key));
        var used = SupportedSkill(player, skillId);
        double swing = attackSeconds ?? (used.Effect >= 0 && SkillsFor(player).IsAttack(used.Effect)
            ? SupportedAttackSeconds(player, skillId) : 0);
        var damage = set.PhaseRunDamage ??= new();
        if (Math.Abs(player.PhasingUntil - run.Until) < 1e-9) player.PhasingUntil = now;
        run.Until = now;
        damage.Skill = run.Skill;
        damage.Seconds = damageSeconds;
        damage.Until = now + damageSeconds + Math.Max(0, swing);
        damage.Stats.Clear();
        damage.Stats[Stat.PhaseRunMeleePhysicalDamagePlusPctFinal] = GolemStat(own, Stat.PhaseRunMeleePhysicalDamagePlusPctFinal);
        if (!player.Connection.Connected) return;
        _ = EndSelfBuff(player, run, run.Token, "0x01c2 Phase Run replaced by a skill use");
        _ = ShowSelfBuff(player, damage, PhaseRunDamageBuffRow, "Phase Run damage");
    }

    // Blood Rage (BuffDefinitions blood_rage): for base_skill_effect_duration, attack_speed_+%_granted_from_skill as
    // attack speed, its physical attack life leech and Frenzy Charge chance on kill, and physical degeneration of its
    // base_physical_damage_%_of_maximum_life/energy_shield_to_deal_per_minute (lethal, as the trial traps' is; energy
    // shield first). A kill while it lasts refreshes its duration (ActiveSkills blood_rage); a new cast restarts it.
    // shortcut: increased buff effect and the degeneration's damage-over-time modifiers are not applied.
    static async Task StartBloodRage(Player player, ushort skillId)
    {
        var own = SupportedSkill(player, skillId).Stats;
        var playerStats = CharacterPlayerStats(player);
        int Own(uint key) => GolemStat(own, key);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), key => GolemStat(playerStats, key), Own);
        var buff = SelfBuffs.GetValue(player, _ => new()).BloodRage ??= new();
        buff.Skill = skillId;
        buff.Seconds = seconds;
        buff.Until = AreaTime(player) + seconds;
        buff.Stats.Clear();
        buff.Stats[Stat.AttackSpeedPlusPct] = Own(Stat.AttackSpeedPlusPctGrantedFromSkill);
        buff.Stats[Stat.LifeLeechFromPhysicalAttackDamagePermyriad] = Own(Stat.LifeLeechFromPhysicalAttackDamagePermyriad);
        buff.Stats[Stat.AddFrenzyChargeOnKillPctChance] = Own(Stat.AddFrenzyChargeOnKillPctChance);
        double degen = (player.PlayerMaxLife * (double)Own(Stat.BasePhysicalDamagePctOfMaximumLifeToDealPerMinute)
            + player.Pools.EnergyShield * (double)Own(Stat.BasePhysicalDamagePctOfMaximumEnergyShieldToDealPerMinute)) / 6000;
        player.Degens[buff] = degen;
        buff.Degen = degen;
        Console.WriteLine($"[gateway] {player.Connection.Remote} Blood Rage: {seconds:0.###} s, +{buff.Stats[Stat.AttackSpeedPlusPct]}% attack speed, "
            + $"{buff.Degen:0.#} physical damage per second");
        await ShowSelfBuff(player, buff, BloodRageBuffRow, "Blood Rage", () => EndBloodRageDegen(player, buff));
    }

    // When the buff ends.
    static void EndBloodRageDegen(Player player, SelfBuff buff)
    {
        player.Degens[buff] = 0;
        buff.Degen = 0;
    }

    // A hostile monster the player killed (ChargesOnKill): a running Blood Rage starts its duration again.
    public static void SelfBuffsOnKill(Player player)
    {
        if (!SelfBuffs.TryGetValue(player, out var set) || set.BloodRage is not { } rage) return;
        double now = AreaTime(player);
        if (now >= rage.Until) return;
        rage.Until = now + rage.Seconds;
        if (!player.Connection.Connected) return;
        // Per kill: the stats are the same while it runs, so only the buff's timer is shown again (no stat build).
        _ = ShowSelfBuff(player, rage, BloodRageBuffRow, "Blood Rage refreshed by a kill",
            () => EndBloodRageDegen(player, rage), restat: false);
    }
}
