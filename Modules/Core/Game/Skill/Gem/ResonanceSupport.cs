using GameServer.Core.World;

public static partial class Game
{
    const int MaximumResonance = 50;
    const int ResonanceGainPerSecond = 25;
    const int ResonanceLossPerSecond = 5;
    const int ResonanceGainSeconds = 2;

    static readonly ResonanceRule ElementalResonance = new(Stat.GainResonanceOfMajorityDamageOnHitFor2Seconds,
        Stat.VirtualLowestResonance,
        [new(DamageType.Fire, Stat.FireResonance, 1677), new(DamageType.Cold, Stat.ColdResonance, 1678),
            new(DamageType.Lightning, Stat.LightningResonance, 1679)]);
    static readonly ResonanceRule UnholyResonance = new(Stat.GainUnholyResonanceOfMajorityDamageOnHitFor2Seconds,
        Stat.VirtualLowestUnholyResonance,
        [new(DamageType.Physical, Stat.PhysicalUnholyResonance, 1680), new(DamageType.Chaos, Stat.ChaosUnholyResonance, 1681),
            new(DamageType.Lightning, Stat.LightningUnholyResonance, 1682)]);

    readonly record struct ResonanceType(DamageType Damage, uint Stat, ushort Buff);
    readonly record struct ResonanceCounter(double Amount, double At, double GainUntil)
    {
        public double AmountAt(double now)
        {
            double accruing = Math.Clamp(Math.Min(GainUntil, now) - At, 0, ResonanceGainSeconds);
            return Math.Max(0, Math.Min(MaximumResonance, Amount + ResonanceGainPerSecond * accruing)
                - ResonanceLossPerSecond * Math.Max(0, now - Math.Max(At, GainUntil)));
        }
        public int WholeAt(double now) => (int)Math.Floor(AmountAt(now) + 1e-9);
        public double ReadAgainAt(double now)
        {
            double amount = AmountAt(now);
            int whole = WholeAt(now);
            if (now < GainUntil)
                return whole >= MaximumResonance ? GainUntil :
                    Math.Min(GainUntil, now + Math.Max(0, whole + 1 - amount) / ResonanceGainPerSecond);
            return whole > 0 ? now + Math.Max(0, amount - whole) / ResonanceLossPerSecond + 1e-6 : double.PositiveInfinity;
        }
    }

    sealed class ResonanceState(Player owner)
    {
        public readonly int Epoch = owner.ProjectileEpoch;
        public readonly ResonanceCounter[] Counters = new ResonanceCounter[3];
        public readonly bool[] BuffActive = new bool[3];
    }

    sealed class ResonanceRule(uint gain, uint lowest, ResonanceType[] types)
    {
        readonly Store<Player, ResonanceState> states = new();
        public bool Supports(IReadOnlyDictionary<uint, int> skill) => skill.GetValueOrDefault(gain) > 0;

        ResonanceState? Current(Player player) => player.PlayerLife > 0 && !player.LeavingArea
            && states.TryGetValue(player, out var state) && state.Epoch == player.ProjectileEpoch ? state : null;

        public int LowestAt(Player player, double now) => Current(player) is { } state ? Lowest(state, now) : 0;
        static int Lowest(ResonanceState state, double now) => Math.Min(state.Counters[0].WholeAt(now),
            Math.Min(state.Counters[1].WholeAt(now), state.Counters[2].WholeAt(now)));

        public bool Record(Player player, DamagePacket hit, double now)
        {
            double highest = 0;
            foreach (var type in DamageTypes.ConversionOrder) highest = Math.Max(highest, hit[type].Max);
            if (highest <= 0 || !types.Any(type => hit[type.Damage].Max == highest)) return false;
            var state = Current(player);
            bool changed = false;
            for (int i = 0; i < types.Length; i++)
            {
                if (hit[types[i].Damage].Max >= highest) continue;
                if (state is null) states.AddOrUpdate(player, state = new(player));
                var previous = state.Counters[i];
                if (now < previous.At || now + ResonanceGainSeconds <= previous.GainUntil) continue;

                state.Counters[i] = new(previous.AmountAt(now), now, now + ResonanceGainSeconds);
                changed = true;
                state.BuffActive[i] = true;
                player.Area.Buffs.Set(player.Id, new(types[i].Buff, player.Id));
            }
            return changed;
        }

        public void AddStats(Player player, StatMap stats, double now)
        {
            if (Current(player) is not { } state) return;
            double next = double.PositiveInfinity;
            for (int i = 0; i < types.Length; i++)
            {
                var counter = state.Counters[i];
                int value = counter.WholeAt(now);
                if (value > 0) stats[types[i].Stat] = value;
                else if (now >= counter.GainUntil && state.BuffActive[i])
                {
                    state.BuffActive[i] = false;
                    player.Area.Buffs.Clear(player.Id, types[i].Buff, player.Id);
                }
                next = Math.Min(next, counter.ReadAgainAt(now));
            }
            if (Lowest(state, now) is > 0 and var minimum) stats[lowest] = minimum;
            if (double.IsFinite(next)) StatsChangeAt(next + 1e-7, () => ProjectileWorldTime(player));
        }

        public bool Clear(Player player)
        {
            if (!states.Remove(player)) return false;
            foreach (var type in types) player.Area.Buffs.Clear(player.Id, type.Buff, player.Id);
            return true;
        }
    }

    static void AddResonanceStats(Player player, StatMap stats)
    {
        double now = ProjectileWorldTime(player);
        ElementalResonance.AddStats(player, stats, now);
        UnholyResonance.AddStats(player, stats, now);
    }

    internal static void ClearResonance(Player player)
    {
        bool cleared = ElementalResonance.Clear(player);
        cleared = UnholyResonance.Clear(player) || cleared;
        if (cleared) PublishByFrame(player, PlayerStatChange.Resonance);
    }

    // Only the owner's committed damaging contact can refresh a supported skill's channels.
    static void RecordResonanceHitLocked(Player player, SpawnedMonster target, IReadOnlyDictionary<uint, int> skill,
        DamagePacket hit, double now)
    {
        if (!target.Hostile || player.PlayerLife == 0 || player.LeavingArea) return;
        bool changed = ElementalResonance.Supports(skill) && ElementalResonance.Record(player, hit, now);
        changed = (UnholyResonance.Supports(skill) && UnholyResonance.Record(player, hit, now)) || changed;
        if (changed) PublishByFrame(player, PlayerStatChange.Resonance);
    }

    // Read the counters at contact: a projectile carries its support stats from release, but the counters keep moving.
    public static (DamagePacket Packet, HitResistances.Mods Mods, Penetration.PerType Penetration)
        WithResonanceHitLocked(Player player, IReadOnlyDictionary<uint, int> skill, DamagePacket packet,
        HitResistances.Mods mods, Penetration.PerType penetration, double now)
    {
        if (ElementalResonance.Supports(skill))
        {
            int lowest = ElementalResonance.LowestAt(player, now);
            int more = (lowest / 5) * skill.GetValueOrDefault(Stat.ElementalDamagePlusPctFinalPer5LowestResonance);
            if (more != 0)
                packet = packet.Select((type, range) => DamageTypes.IsElemental(type) ? range.Scale(1 + more / 100d) : range);
            if (lowest >= 25 && mods.CannotPenetrateOrIgnore == 0)
            {
                int extra = skill.GetValueOrDefault(Stat.DamagePenetratesPctElementalResistancesWhileAllResonanceIs25);
                penetration = penetration with { Fire = penetration.Fire + extra,
                    Cold = penetration.Cold + extra, Lightning = penetration.Lightning + extra };
            }
        }
        if (UnholyResonance.Supports(skill))
        {
            int lowest = UnholyResonance.LowestAt(player, now);
            int more = (lowest / 10) * skill.GetValueOrDefault(Stat.DamagePlusPctFinalPer10LowestUnholyResonance);
            if (more != 0) packet = packet.Scale(1 + more / 100d);
            if (lowest >= 25)
            {
                var rng = AreaRng(player);
                bool ignoreResist = (packet[DamageType.Lightning].Max > 0 || packet[DamageType.Chaos].Max > 0)
                    && RandomRolls.Chance(skill.GetValueOrDefault(
                        Stat.HitsIgnoreEnemyMonsterLightningAndChaosResistancePctChanceWhileAllUnholyResonanceIs25), 100, rng);
                bool ignorePhys = packet[DamageType.Physical].Max > 0
                    && RandomRolls.Chance(skill.GetValueOrDefault(
                        Stat.HitsIgnoreEnemyMonsterPhysicalDamageReductionPctChanceWhileAllUnholyResonanceIs25), 100, rng);
                mods = mods with { IgnoreLightning = mods.IgnoreLightning || ignoreResist,
                    IgnoreChaos = mods.IgnoreChaos || ignoreResist, IgnorePhysicalReduction = mods.IgnorePhysicalReduction || ignorePhys };
            }
        }
        return (packet, mods, penetration);
    }
}
