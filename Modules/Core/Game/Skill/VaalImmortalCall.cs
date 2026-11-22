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
    static readonly RuleTable<Player, VaalImmortalCallState> VaalImmortalCalls = new();

    static bool VaalImmortalCallCurrent(Player player, VaalImmortalCallState state) =>
        state.Epoch == player.ProjectileEpoch && ReferenceEquals(state.Rules, CombatLifetime)
        && !player.Closed && !player.LeavingArea && player.PendingTransfer is null && player.PlayerLife > 0;

    static bool VaalImmortalCallSpell(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) == VaalImmortalCallEffect;

    /// <summary>"You cannot die": while the buff runs, Life stops at 1 instead of 0 (wiki: the health cannot drop
    /// below 1). The hit, ailment tick or hazard still lands and its other effects still happen.</summary>
    public static bool VaalImmortalCallActive(Player player, double at) =>
        VaalImmortalCalls.TryGetValue(player, out var state) && VaalImmortalCallCurrent(player, state) && at < state.Until;

    /// <summary>Life after a damage tick that already took it: held at 1 while Vaal Immortal Call runs.</summary>
    public static uint VaalImmortalCallLife(Player player, uint life, double at) =>
        life > 0 || !VaalImmortalCallActive(player, at) ? life : 1;

    /// <summary>The Life a hit takes: no more than it leaves the player at 1 while Vaal Immortal Call runs. Read
    /// before the caller subtracts it, so it is capped by the Life the player has left.</summary>
    static int VaalImmortalCallHitLife(Player player, int lifeDamage, double at) =>
        lifeDamage > 0 && lifeDamage >= player.PlayerLife && VaalImmortalCallActive(player, at)
            ? Math.Max(0, (int)player.PlayerLife - 1) : lifeDamage;

    // Called immediately after the Vaal use's payment, under the connection lock.
    static void StartVaalImmortalCall(Player player, ProjectileSession session, ushort skill, double now)
    {
        var gem = SupportedSkill(player, skill);
        var playerStats = CharacterPlayerStats(player);
        int Own(uint key) => GolemStat(gem.Stats, key);
        int Get(uint key) => Own(key) + GolemStat(playerStats, key);
        var charges = ChargesOf(player);
        var limits = charges.Limits(ChargeType.Endurance);
        int removable = charges.Converted(ChargeType.Endurance) ? 0
            : Math.Max(0, charges.Effective(ChargeType.Endurance) - limits.Minimum);
        if (removable > 0) charges.State.Lose(ChargeType.Endurance, removable, limits);
        // Base 0.4 s with 100..157% increased duration per Endurance Charge removed (the gem's own per-level stat,
        // level 1..20), added to the other increases (PoB SkillStatMap: Duration INC). Not a Guard skill: its Vaal and
        // Buff types pick the scoped increases.
        int perCharge = removable * Get(Stat.BuffEffectDurationPlusPctPerRemovableEnduranceCharge);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), key => GolemStat(playerStats, key),
            key => Own(key) + (key == SkillDurationModifiers.IncreasedDuration ? perCharge : 0),
            DurationKinds(player, gem.Effect));
        if (!VaalImmortalCalls.TryGetValue(player, out var state) || !VaalImmortalCallCurrent(player, state))
        {
            VaalImmortalCalls.Remove(player);
            state = VaalImmortalCalls.GetValue(player, _ => new(player.ProjectileEpoch, CombatLifetime));
        }
        state.Skill = skill;
        state.Until = now + Math.Max(0, duration);
        state.Charges = removable;
        if (state.Buff == 0) state.Buff = player.NextBuff++;
        session.PresentCharges();
        session.Packet(VaalImmortalCallPresentation(player, now),
            $"Vaal Immortal Call {duration:0.###} s, {removable} Endurance Charge(s) removed");
    }

    public static byte[] VaalImmortalCallPresentation(Player player, double now)
    {
        if (!VaalImmortalCalls.TryGetValue(player, out var state) || state.Buff == 0) return [];
        // The immortal call backend reads additional-data element 0 (the removed charge count), as Immortal Call's does.
        // No skill-bar action: a Vaal skill's buff is presented with its source skill alone, as the Vaal auras' are.
        return InstanceEntities.AddBuff(player.Id, state.Buff, VaalImmortalCallBuffRow, player.Id, state.Skill,
            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.Until - now)),
            list: [(uint)state.Charges]);
    }

    // The buff ends with its duration, the area, a transfer or death: the icon goes and the state with it.
    public static async Task VaalImmortalCallTick(Player player)
    {
        if (!VaalImmortalCalls.TryGetValue(player, out var state)) return;
        if (VaalImmortalCallCurrent(player, state) && AreaTime(player) < state.Until) return;
        VaalImmortalCalls.Remove(player);
        var done = state.Buff == 0 ? null : state;
        if (done is null) return;
        await Send(player, InstanceEntities.RemoveBuff(player.Id, done.Buff), "Vaal Immortal Call ended");
    }

    /// <summary>A new area is a new connection: the leaving one forgets its buff.</summary>
    public static Task LeavingVaalImmortalCall(Player player)
    {
        VaalImmortalCalls.Remove(player);
        return Task.CompletedTask;
    }
}
