using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Program;

public static partial class Game
{
    public const string DecayOnHit = "deal_chaos_damage_per_second_for_10_seconds_on_hit";
    // The stat id is historical: the pinned client's stat description says eight seconds.
    public const double MonsterDecaySeconds = 8;
    const ushort DecayBuffRow = 766;
    sealed class DecayState
    {
        public readonly MonsterIgnites Damage = new();
        public ushort? Buff;
        public double TakenCarry;
    }
    static readonly RuleTable<ClientState, DecayState> Decays = new();

    public static void ApplyMonsterDecayLocked(ClientState conn, uint source, SpawnedMonster monster, bool blocked,
        double now, IReadOnlyDictionary<uint, int> player)
    {
        int carried = monster.Mods.Effects[DecayOnHit] + (monster.EncounterStats?.GetValueOrDefault(DecayOnHit) ?? 0);
        foreach (var temporary in monster.TemporaryStats.Values) carried += temporary.GetValueOrDefault(DecayOnHit);
        if (carried <= 0) return;
        var effects = monster.Effects;
        int basis = effects[DecayOnHit];
        if (basis <= 0 || blocked || conn.Immune || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea || InGracePeriod(conn)
            || Stats.Key("base_cannot_be_damaged") is uint cannot && player.GetValueOrDefault(cannot) != 0) return;
        double dps = effects.OverTime(basis, DamageType.Chaos, EnemyOnLowLife(monster) ? effects["damage_+%_when_on_low_life"] : 0);
        var state = Decays.GetValue(conn, _ => new());
        if (!state.Damage.Add(now, dps, MonsterDecaySeconds)) return;
        DecayIcon(conn, state, now, source);
    }

    public static double PlayerDecayStarted(ClientState conn) =>
        Decays.TryGetValue(conn, out var state) ? state.Damage.LastApplied : double.NegativeInfinity;

    public static void ClearPlayerDecay(ClientState conn)
    {
        if (!Decays.TryGetValue(conn, out var state)) return;
        state.Damage.Clear();
        state.TakenCarry = 0;
        DecayIcon(conn, state, 0);
        Decays.Remove(conn);
    }

    static void DecayIcon(ClientState conn, DecayState state, double now, uint source = 0)
    {
        if (state.Buff is ushort previous)
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, previous), "Decay ends", WorldId: PlayerId));
        state.Buff = null;
        if (!state.Damage.Burning) return;
        ushort buff = conn.NextBuff++;
        state.Buff = buff;
        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, buff, DecayBuffRow, source,
            values: [(uint)Math.Clamp(state.Damage.PerSecond(now) * 60, 0, uint.MaxValue)],
            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.Damage.Until - now))), "Decay", WorldId: PlayerId));
    }

    public static int AdvancePlayerDecayLocked(ClientState conn, double now, IReadOnlyDictionary<uint, int> player,
        bool chaosImmune, double dotTakenFactor)
    {
        if (!Decays.TryGetValue(conn, out var state) || !state.Damage.Burning) return 0;
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        bool immune = conn.Immune || chaosImmune || Get("base_cannot_be_damaged") != 0;
        if (immune) { state.Damage.DiscardDamageCarry(); state.TakenCarry = 0; }
        double factor = immune ? 0 : DamageModel.Mitigate(1, Resistance(conn, DamageType.Chaos), MaxResistance(conn, DamageType.Chaos))
            * conn.ElementalAilments.DamageTakenFactor(now, 0) * dotTakenFactor;
        int damage = state.Damage.Advance(now, factor);
        if (!state.Damage.Burning) DecayIcon(conn, state, now);
        damage = IncomingResourceDefences.TakenOverTime(damage, Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(conn.Pools, Get), typedIncreasedPct: Get("chaos_damage_taken_+%"),
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        bool bypass = Get("chaos_damage_does_not_bypass_energy_shield") == 0 && Get("virtual_chaos_damage_does_not_bypass_energy_shield") == 0
            && !(Get("chaos_damage_does_not_bypass_energy_shield_while_not_low_life") != 0
                && (ulong)conn.PlayerLife * 100 > (ulong)conn.PlayerMaxLife * (uint)LowLifeThresholdPct(player))
            && !(Get("chaos_damage_does_not_bypass_energy_shield_while_not_low_mana") != 0 && (ulong)conn.PlayerMana * 2 > conn.Pools.Mana);
        return IncomingResourceDefences.DamageOverTime(conn.EnergyShield, damage, bypass ? 100 : 0, now, ref conn.ChaosDotBypassCarry);
    }
}
