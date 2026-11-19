using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // BuffDefinitions "vaal_immortal_call" (row 109): cannot_die and cannot_gain_souls. Vaal Immortal Call is not a
    // Guard skill: it has its own 60 s cooldown, 100 souls per use and 8 s of soul gain prevention (GrantedEffectsPerLevel;
    // the prevention takes skill effect duration, its stat set says so).
    const ushort VaalImmortalCallBuffRow = 109;
    const string VaalImmortalCallEffect = "VaalImmortalCall";

    sealed class VaalImmortalCallState(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public double Until;
        public int Charges;
        public ushort Buff, Skill;
    }
    static readonly RuleTable<ClientState, VaalImmortalCallState> VaalImmortalCalls = new();

    static bool VaalImmortalCallCurrent(ClientState conn, VaalImmortalCallState state) =>
        state.Epoch == conn.ProjectileEpoch && ReferenceEquals(state.Rules, CombatLifetime)
        && !conn.Closed && !conn.LeavingArea && conn.PendingTransfer is null && conn.PlayerLife > 0;

    static bool VaalImmortalCallSpell(ClientState conn, int effect) =>
        SkillsFor(conn).EffectId(effect) == VaalImmortalCallEffect;

    /// <summary>"You cannot die": while the buff runs, Life stops at 1 instead of 0 (wiki: the health cannot drop
    /// below 1). The hit, ailment tick or hazard still lands and its other effects still happen.</summary>
    public static bool VaalImmortalCallActive(ClientState conn, double at) =>
        VaalImmortalCalls.TryGetValue(conn, out var state) && VaalImmortalCallCurrent(conn, state) && at < state.Until;

    /// <summary>Life after a damage tick that already took it: held at 1 while Vaal Immortal Call runs.</summary>
    public static uint VaalImmortalCallLife(ClientState conn, uint life, double at) =>
        life > 0 || !VaalImmortalCallActive(conn, at) ? life : 1;

    /// <summary>The Life a hit takes: no more than it leaves the player at 1 while Vaal Immortal Call runs. Read
    /// before the caller subtracts it, so it is capped by the Life the player has left.</summary>
    static int VaalImmortalCallHitLife(ClientState conn, int lifeDamage, double at) =>
        lifeDamage > 0 && lifeDamage >= conn.PlayerLife && VaalImmortalCallActive(conn, at)
            ? Math.Max(0, (int)conn.PlayerLife - 1) : lifeDamage;

    // Called immediately after the Vaal use's payment, under the connection lock.
    static void StartVaalImmortalCall(ClientState conn, ProjectileSession session, ushort skill, double now)
    {
        var gem = SupportedSkill(conn, skill);
        var player = CharacterPlayerStats(conn);
        int Own(string name) => GolemStat(gem.Stats, name);
        int Get(string name) => Own(name) + GolemStat(player, name);
        var charges = ChargesOf(conn);
        var limits = charges.Limits(ChargeType.Endurance);
        int removable = charges.Converted(ChargeType.Endurance) ? 0
            : Math.Max(0, charges.Effective(ChargeType.Endurance) - limits.Minimum);
        if (removable > 0) charges.State.Lose(ChargeType.Endurance, removable, limits);
        // Base 0.4 s with 100..157% increased duration per Endurance Charge removed (the gem's own per-level stat,
        // level 1..20), added to the other increases (PoB SkillStatMap: Duration INC). Not a Guard skill: its Vaal and
        // Buff types pick the scoped increases.
        int perCharge = removable * Get("buff_effect_duration_+%_per_removable_endurance_charge");
        double duration = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), name => GolemStat(player, name),
            name => Own(name) + (name == SkillDurationModifiers.IncreasedDuration ? perCharge : 0),
            DurationKinds(conn, gem.Effect));
        if (!VaalImmortalCalls.TryGetValue(conn, out var state) || !VaalImmortalCallCurrent(conn, state))
        {
            VaalImmortalCalls.Remove(conn);
            state = VaalImmortalCalls.GetValue(conn, _ => new(conn.ProjectileEpoch, CombatLifetime));
        }
        state.Skill = skill;
        state.Until = now + Math.Max(0, duration);
        state.Charges = removable;
        if (state.Buff == 0) state.Buff = conn.NextBuff++;
        session.PresentCharges();
        session.Packet(VaalImmortalCallPresentation(conn, now),
            $"Vaal Immortal Call {duration:0.###} s, {removable} Endurance Charge(s) removed");
    }

    public static byte[] VaalImmortalCallPresentation(ClientState conn, double now)
    {
        if (!VaalImmortalCalls.TryGetValue(conn, out var state) || state.Buff == 0) return [];
        // The immortal call backend reads additional-data element 0 (the removed charge count), as Immortal Call's does.
        // No skill-bar action: a Vaal skill's buff is presented with its source skill alone, as the Vaal auras' are.
        return InstanceEntities.AddBuff(PlayerId, state.Buff, VaalImmortalCallBuffRow, PlayerId, state.Skill,
            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.Until - now)),
            list: [(uint)state.Charges]);
    }

    // The buff ends with its duration, the area, a transfer or death: the icon goes and the state with it.
    public static async Task VaalImmortalCallTick(ClientState conn)
    {
        VaalImmortalCallState? done;
        lock (conn.Monsters)
        {
            if (!VaalImmortalCalls.TryGetValue(conn, out var state)) return;
            if (VaalImmortalCallCurrent(conn, state) && conn.Clock.Elapsed.TotalSeconds < state.Until) return;
            VaalImmortalCalls.Remove(conn);
            done = state.Buff == 0 ? null : state;
        }
        if (done is null) return;
        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, done.Buff), "Vaal Immortal Call ended");
    }

    /// <summary>A new area is a new connection: the leaving one forgets its buff.</summary>
    public static Task LeavingVaalImmortalCall(ClientState conn)
    {
        VaalImmortalCalls.Remove(conn);
        return Task.CompletedTask;
    }
}
