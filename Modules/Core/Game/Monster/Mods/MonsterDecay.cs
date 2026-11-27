using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Program;

public static partial class Game
{
    public const uint DecayOnHit = Stat.DealChaosDamagePerSecondFor10SecondsOnHit;
    // The stat id is historical: the pinned client's stat description says eight seconds.
    public const double MonsterDecaySeconds = 8;
    const ushort DecayBuffRow = 766;
    sealed class DecayState
    {
        public readonly MonsterIgnites Damage = new();
        public ushort? Buff;
        public double TakenCarry, Rate = 1;
    }
    static readonly RuleTable<Player, DecayState> Decays = new();

    static void ApplyMinionDecayLocked(Player player, GolemState.Golem fighter, GolemState.Ability ability,
        uint id, SpawnedMonster target, double now)
    {
        var effects = fighter.Monster.Effects;
        int basis = effects[DecayOnHit] + GolemStat(ability.Stats, DecayOnHit);
        if (basis <= 0 || DamageStats.Prohibited(DamageType.Chaos, key => effects[key])) return;
        var (_, supportedDps, _) = GolemHazardDamage(player, fighter, now, basis, area: false);
        double dps = effects.OverTime(supportedDps, DamageType.Chaos)
            * DoedreSewers.MinionDamageFactor(player) * MapMinionDamageFactor(player);
        var decay = target.Decay;
        decay.SetExpiryRate(now, target.RecipientDebuffRate);
        if (!decay.Add(now, dps, MonsterDecaySeconds)) return;
        player.Area.ActiveDecayMonsters.Add(id);
        ShowDot(player, id, ref decay.Buff, ref decay.BuffSource,
            new(DecayBuffRow, fighter.Id, U68: Buff.Seconds(decay.RemainingBase(now))));
    }

    internal static Task TickMinionDecays(Player player) =>
        TickStrongestDots(player, player.Area.ActiveDecayMonsters, m => m.Decay,
            (m, now) => MonsterDotTaken(m, DamageType.Chaos, now), "dies from minion Decay");

    public static void ApplyMonsterDecayLocked(Player player, uint source, SpawnedMonster monster, bool blocked,
        double now, IReadOnlyDictionary<uint, int> playerStats)
    {
        int carried = monster.Mods.Effects[DecayOnHit] + (monster.EncounterStats?.GetValueOrDefault(DecayOnHit) ?? 0);
        foreach (var temporary in monster.TemporaryStats.Values) carried += temporary.GetValueOrDefault(DecayOnHit);
        if (carried <= 0) return;
        var effects = monster.Effects;
        int basis = effects[DecayOnHit];
        if (basis <= 0 || blocked || player.Immune || player.PlayerLife == 0 || player.Closed || player.LeavingArea || InGracePeriod(player)
            || playerStats.GetValueOrDefault(Stat.BaseCannotBeDamaged) != 0) return;
        double dps = effects.OverTime(basis, DamageType.Chaos, MonsterOnLowLife(monster) ? effects[Stat.DamagePlusPctWhenOnLowLife] : 0);
        var state = Decays.GetValue(player, _ => new());
        RetimeDecay(player, state, now, playerStats);
        if (!state.Damage.Add(now, dps, MonsterDecaySeconds)) return;
        DecayIcon(player, state, now, source);
    }

    public static double PlayerDecayStarted(Player player) =>
        Decays.TryGetValue(player, out var state) ? state.Damage.LastApplied : double.NegativeInfinity;

    public static void ClearPlayerDecay(Player player)
    {
        if (!Decays.TryGetValue(player, out var state)) return;
        state.Damage.Clear();
        state.TakenCarry = 0;
        DecayIcon(player, state, 0);
        Decays.Remove(player);
    }

    static void DecayIcon(Player player, DecayState state, double now, uint source = 0)
    {
        if (state.Buff is ushort previous)
            player.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(player.Id, previous), "Decay ends", WorldId: player.Id));
        state.Buff = null;
        if (!state.Damage.Burning) return;
        ushort buff = player.NextBuff++;
        state.Buff = buff;
        player.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(player.Id, buff, DecayBuffRow, source,
            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.Damage.Until - now))), "Decay", WorldId: player.Id));
    }

    static void RetimeDecay(Player player, DecayState state, double now, IReadOnlyDictionary<uint, int> playerStats)
    {
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        double rate = RecipientEffectTime.Rate(2, Get);
        if (rate == state.Rate) return;
        state.Damage.SetExpiryRate(now, rate);
        state.Rate = rate;
        if (state.Buff is not null) DecayIcon(player, state, now);
    }

    public static int AdvancePlayerDecayLocked(Player player, double now, IReadOnlyDictionary<uint, int> playerStats,
        bool chaosImmune, double dotTakenFactor)
    {
        if (!Decays.TryGetValue(player, out var state) || !state.Damage.Burning) return 0;
        RetimeDecay(player, state, now, playerStats);
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        bool immune = player.Immune || chaosImmune || Get(Stat.BaseCannotBeDamaged) != 0;
        if (immune) { state.Damage.DiscardDamageCarry(); state.TakenCarry = 0; }
        double factor = immune ? 0 : DamageModel.Mitigate(1d, Resistance(player, DamageType.Chaos) + Get(Stat.AdditionalChaosResistanceAgainstDamageOverTimePct), MaxResistance(player, DamageType.Chaos))
            * player.ElementalAilments.DamageTakenFactor(now, 0) * dotTakenFactor;
        int damage = state.Damage.Advance(now, factor);
        if (!state.Damage.Burning) DecayIcon(player, state, now);
        damage = IncomingResourceDefences.TakenOverTime(damage, Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(player.Pools, Get), typedIncreasedPct: Get(Stat.ChaosDamageTakenPlusPct) + Get(Stat.ChaosDamageTakenOverTimePlusPct)
                + (player.InCausticCloud ? Get(Stat.ChaosDamageTakenOverTimePlusPctWhileInCausticCloud) : 0),
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        return DamagePlayerChaosOverTime(player, damage, now, playerStats);
    }
}
