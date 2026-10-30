using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// The Spirit chieftain skills that Ancestral Commander's Ancestral Spirit options trigger (Summon Spirit of Kahuturoa,
/// Utula, Kiloava, Rakiata and Kaom, Agents/docs/game/ancestral-commander.md): one cooldown that the five share
/// (GrantedEffectsPerLevel CooldownGroup 24, 20 s, one use) and the Leadership buff the summoned chieftain gives the
/// player for the skill's duration. The buff's <c>ancestral_buff_cooldown_does_not_tick</c> stops that cooldown while it
/// lasts. The chieftain minion itself is not summoned: the server has no allied minions. Guarded by the Monsters lock;
/// times are the connection's game seconds.
/// </summary>
sealed class SpiritChieftains
{
    /// <summary>A Leadership buff: its BuffDefinitions row, its stats by client key and when it ends.</summary>
    public sealed record Leadership(ushort Row, IReadOnlyList<(uint Stat, int Value)> Stats, double Until);

    SingleUseCooldownState? cooldown;
    Leadership? buff;
    /// <summary>The buff the client was last sent and its instance (Program.SpiritChieftainPresentation).</summary>
    public Leadership? Sent;
    public ushort SentInstance;

    public Leadership? Active(double now) => buff is { } b && now < b.Until ? b : null;

    /// <summary>A trigger: true when the shared cooldown was ready, which it then spends, and the buff starts. The cooldown
    /// recovers at the plan's rate except while a buff lasts.</summary>
    public bool TryTrigger(double now, SingleUseCooldown plan, ushort row, IReadOnlyList<(uint Stat, int Value)> stats, double seconds)
    {
        Settle(now, plan.RecoveryRate);
        cooldown ??= new(now, plan.RecoveryRate);
        // While a buff lasts the cooldown is frozen, so it is not ready; TryUse would also reset its rate.
        if (Active(now) is not null || !cooldown.TryUse(now, plan)) return false;
        buff = new(row, stats, now + seconds);
        cooldown.Refresh(now, 0);
        return true;
    }

    /// <summary>The buff ends now (the player died: the chieftain dies with him); the cooldown recovers from here.</summary>
    public void End(double now)
    {
        if (buff is { } b && b.Until > now) buff = b with { Until = now };
    }

    void Settle(double now, double rate)
    {
        if (cooldown is null) return;
        if (buff is { } b && b.Until <= now) { cooldown.Refresh(b.Until, rate); buff = null; }
        cooldown.Refresh(now, buff is null ? rate : 0);
    }

    /// <summary>The Leadership a Spirit skill's summon grants: the BuffTemplates row whose value stats (StatsKey) the skill
    /// has (Kahuturoa's <c>ancestral_buff_armour_%_applies_to_all_damage_from_hits</c> 10 gives RongokuraisEmbrace), each of
    /// its BuffDefinitions StatsKeys taking the skill's value of the matching StatsKey stat. Null when none.</summary>
    public static (ushort Row, IReadOnlyList<(uint Stat, int Value)> Stats)? LeadershipOf(IReadOnlyDictionary<uint, int> own)
    {
        foreach (var (row, values, stats) in Templates.Value)
            if (values.All(own.ContainsKey))
                return (row, [.. stats.Zip(values, (stat, value) => (stat, own[value]))]);
        return null;
    }

    static readonly Lazy<(ushort Row, uint[] Values, uint[] Stats)[]> Templates = new(() => Load("data/tables"));

    static (ushort Row, uint[] Values, uint[] Stats)[] Load(string tables)
    {
        string Path(string name) => System.IO.Path.Combine(tables, name + ".json");
        if (!File.Exists(Path("BuffTemplates")) || !File.Exists(Path("BuffDefinitions"))) return [];
        using var templates = JsonDocument.Parse(File.ReadAllBytes(Path("BuffTemplates")));
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Path("BuffDefinitions")));
        static uint[] Keys(JsonElement row, string column) => row.TryGetProperty(column, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => (uint)e.GetInt32() + 1)] : [];
        var result = new List<(ushort, uint[], uint[])>();
        foreach (var template in templates.RootElement.EnumerateArray())
        {
            if (template.GetProperty("BuffDefinitionsKey") is not { ValueKind: JsonValueKind.Number } key) continue;
            uint[] values = Keys(template, "StatsKey"), stats = Keys(definitions.RootElement[key.GetInt32()], "StatsKeys");
            if (values.Length > 0 && values.Length == stats.Length) result.Add(((ushort)key.GetInt32(), values, stats));
        }
        return [.. result];
    }
}

partial class Program
{
    // The node stats that name a Spirit chieftain trigger with an event the server has. Spirit of Rakiata's
    // has_trigger_on_crit_vs_marked_unique needs Marks, which the server does not have.
    internal const string CritByUniqueTrigger = "has_trigger_on_crit_by_unique_enemy",
        SavageHitFromUniqueTrigger = "has_trigger_on_taking_savage_hit_from_unique",
        BlockFromUniqueTrigger = "has_trigger_on_block_hit_from_unique",
        TotemDeathInPresenceOfUniqueTrigger = "has_trigger_on_totem_death_in_presence_of_unique";

    /// <summary>
    /// After a Unique monster's hit on the living player, under the Monsters lock: blocking it triggers Spirit of Kiloava,
    /// taking it as a critical strike Spirit of Kahuturoa, and taking it as a Savage Hit (it removed at least 15% of
    /// maximum Life; Energy Shield, Ward and Mana do not count, wiki Savage hit) Spirit of Utula. True when one triggered.
    /// </summary>
    internal static bool SpiritChieftainsWhenHitLocked(ClientState conn, IReadOnlyDictionary<uint, int> stats, bool blocked,
        bool critical, long lifeLost)
    {
        if (conn.PlayerLife == 0) return false;
        if (blocked) return TriggerSpiritChieftainLocked(conn, BlockFromUniqueTrigger, stats);
        return critical && TriggerSpiritChieftainLocked(conn, CritByUniqueTrigger, stats)
            || lifeLost * 100 >= (long)conn.PlayerMaxLife * 15 && TriggerSpiritChieftainLocked(conn, SavageHitFromUniqueTrigger, stats);
    }

    // The allocated Spirit skill with this trigger, if its shared cooldown is ready: its Leadership for its duration
    // (base_skill_effect_duration with the player's skill effect duration), the cooldown with the player's recovery.
    static bool TriggerSpiritChieftainLocked(ClientState conn, string trigger, IReadOnlyDictionary<uint, int> stats)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        foreach (var grant in TriggeredGrantsLocked(conn, trigger))
        {
            var own = SkillsFor(conn).EffectStats(grant.Effect, grant.Level).ToDictionary(s => s.Item1, s => s.Item2);
            if (SpiritChieftains.LeadershipOf(own) is not { } leadership
                || SingleUseCooldown.ForSkill(SkillsFor(conn), Stats, grant.Effect, grant.Level, stats, own) is not { } plan) continue;
            int Player(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            int Own(string name) => Stats.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
            double seconds = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own);
            if (!conn.SpiritChieftains.TryTrigger(now, plan, leadership.Row, leadership.Stats, seconds)) continue;
            Console.WriteLine($"[gateway] {CharacterName(conn)}: {grant.EffectId} triggered ({trigger}): buff {leadership.Row} "
                + $"for {seconds:0.##} s, cooldown {plan.BaseMilliseconds} ms at {plan.RecoveryRate:0.##}x after it");
            return true;
        }
        return false;
    }

    // The running Leadership's stats join the player's (CharacterPlayerStatsCore), while he lives.
    static void AddSpiritLeadershipStats(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
            if (conn.PlayerLife > 0 && conn.SpiritChieftains.Active(conn.Clock.Elapsed.TotalSeconds) is { } buff)
                foreach (var (key, value) in buff.Stats)
                    stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
    }

    // "Recover X% of Life / Mana / Energy Shield when you Block" (Kiloava's Leadership): a percent of each maximum
    // (PoB LifeOnBlock, ManaOnBlock and EnergyShieldOnBlock PercentStat), rounded down.
    static (int Life, int Mana, uint EnergyShield) BlockRecovery(ClientState conn, IReadOnlyDictionary<uint, int> stats)
    {
        long Pct(string name, long maximum) => Stats.Key(name) is uint key ? maximum * Math.Max(0, stats.GetValueOrDefault(key)) / 100 : 0;
        return ((int)Pct("recover_%_of_maximum_life_on_block", conn.PlayerMaxLife), (int)Pct("mana_%_gained_on_block", conn.Pools.Mana),
            (uint)Pct("energy_shield_%_gained_on_block", conn.EnergyShield.Maximum));
    }

    // The Leadership buff on the player (S→C 0x1c1 with its remaining seconds, 0x1c2 when it ends or the player dies) and
    // the player's stats with or without its stats. Sent after a trigger and from the player tick.
    internal static byte[]? SpiritChieftainPresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) return null;
            var spirits = conn.SpiritChieftains;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var active = conn.PlayerLife > 0 ? spirits.Active(now) : null;
            if (ReferenceEquals(active, spirits.Sent)) return null;
            var bytes = new List<byte>();
            if (spirits.SentInstance != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, spirits.SentInstance));
            spirits.SentInstance = 0;
            if (active is not null)
                bytes.AddRange(InstanceEntities.AddBuff(PlayerId, spirits.SentInstance = conn.NextBuff++, active.Row, PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)(active.Until - now))));
            spirits.Sent = active;
            bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
            Console.WriteLine($"[gateway] {CharacterName(conn)}: Leadership buff "
                + (active is null ? "ends" : $"{active.Row} for {active.Until - now:0.##} s"));
            return [.. bytes];
        }
    }

    static Task SendSpiritChieftain(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Send(remote, stream, ciphers, [], null, atGate: () => SpiritChieftainPresentation(conn));
}
