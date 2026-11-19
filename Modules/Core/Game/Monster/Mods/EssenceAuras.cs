using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Core;

public static partial class Game
{
    public const string EssenceLifeDegen = "MonsterAuraEssenceEnemyLifeDegen1";
    sealed record EssenceAura(double Radius, ushort Buff, ushort Mod, int ChaosLifePerMinute);
    sealed record EssenceAuraSource(uint Id, SpawnedMonster Monster, EssenceAura Aura);
    sealed class EssenceAuraExposure
    {
        public readonly List<EssenceAuraSource> Sources = [];
        public double Last = double.NaN, MinionLast = double.NaN, Started = double.NegativeInfinity, Carry, TakenCarry;
        public ushort? Buff;
        public uint Source;
        public bool Taking;
        public readonly Dictionary<uint, double> MinionCarry = [];
    }
    static readonly RuleTable<ClientState, EssenceAuraExposure> EssenceAuraExposures = new();
    static readonly Lazy<EssenceAura?> EssenceDegenDefinition = new(() =>
    {
        var mod = Items.Mods.ById(EssenceLifeDegen);
        if (mod?.BuffTemplate is not int row || ClientTables.Table("BuffTemplates").Row(row) is not { } template
            || template.IntOrNull("BuffDefinitionsKey") is not int buff) return null;
        return new(template.Int("AuraRadius"), checked((ushort)buff), mod.Hash,
            mod.Stats.First(s => s.Id == "base_chaos_damage_%_of_maximum_life_taken_per_minute").Min);
    });

    // Companion cadence gathers sources once; resource recovery examines only aura carriers, with its prepared stats.
    // Rebuilding also admits retained monsters after module replacement and notices changed mod sets.
    public static void RefreshEssenceAuraSourcesLocked(ClientState conn)
    {
        var state = EssenceAuraExposures.GetOrCreateValue(conn);
        state.Sources.Clear();
        if (EssenceDegenDefinition.Value is not { } aura) return;
        foreach (var (id, monster) in conn.Monsters)
            if (monster.Hostile && !monster.Dead && monster.Life > 0
                && monster.Mods.Effects["base_chaos_damage_%_of_maximum_life_taken_per_minute"] > 0
                && monster.Mods.Mods.Any(m => m.Hash == aura.Mod)) state.Sources.Add(new(id, monster, aura));
    }

    static EssenceAuraSource? EssenceAuraAt(ClientState conn, EssenceAuraExposure state, (int X, int Y) at, double now)
    {
        foreach (var source in state.Sources)
        {
            var m = source.Monster;
            if (m.Dead || m.Life == 0 || m.WorldHidden || !m.Hostile
                || !conn.Monsters.TryGetValue(source.Id, out var held) || !ReferenceEquals(m, held)) continue;
            var from = m.Walk.Position(now);
            double dx = (double)from.X - at.X, dy = (double)from.Y - at.Y;
            if (dx * dx + dy * dy <= source.Aura.Radius * source.Aura.Radius) return source;
        }
        return null;
    }

    static void EssenceAuraIcon(ClientState conn, EssenceAuraExposure state, EssenceAuraSource? source)
    {
        if (state.Source == (source?.Id ?? 0)) return;
        if (state.Buff is ushort old)
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, old), "Essence degeneration aura ends", WorldId: PlayerId));
        state.Buff = null;
        state.Source = source?.Id ?? 0;
        if (source is null) return;
        ushort buff = conn.NextBuff++;
        state.Buff = buff;
        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, buff, source.Aura.Buff, source.Id),
            "Essence degeneration aura", WorldId: PlayerId));
    }

    public static double PlayerEssenceAuraStarted(ClientState conn) =>
        EssenceAuraExposures.TryGetValue(conn, out var state) ? state.Started : double.NegativeInfinity;

    public static void ClearEssenceAuraExposure(ClientState conn)
    {
        if (!EssenceAuraExposures.TryGetValue(conn, out var state)) return;
        EssenceAuraIcon(conn, state, null);
        EssenceAuraExposures.Remove(conn);
    }

    public static IDisposable CleanupEssenceAuras() => new EssenceAuraCleanup();
    sealed class EssenceAuraCleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var (conn, _) in EssenceAuraExposures)
                lock (conn.Monsters) ClearEssenceAuraExposure(conn);
        }
    }

    public static int AdvanceEssenceAuraLocked(ClientState conn, double from, double until,
        IReadOnlyDictionary<uint, int> player, bool chaosImmune, double otherDotStart)
    {
        if (!EssenceAuraExposures.TryGetValue(conn, out var state)) return 0;
        if (conn.Paused) { state.Last = until; return 0; }
        from = Math.Max(from, double.IsNaN(state.Last) ? from : state.Last);
        state.Last = Math.Max(from, until);
        var source = conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || InGracePeriod(conn)
            ? null : EssenceAuraAt(conn, state, conn.Walk.Position(until), until);
        EssenceAuraIcon(conn, state, source);
        if (source is null) { state.Taking = false; state.Carry = state.TakenCarry = 0; return 0; }
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        if (conn.Immune || chaosImmune || Get("base_cannot_be_damaged") != 0)
        { state.Taking = false; state.Carry = state.TakenCarry = 0; return 0; }
        if (until <= from) return 0;
        if (!state.Taking) { state.Started = from; state.Taking = true; }
        double lethe = Get("keystone_lethe_shade") != 0 && until - Math.Max(otherDotStart, state.Started) < 1 ? .5 : 1;
        double raw = conn.PlayerMaxLife * source.Aura.ChaosLifePerMinute / 6000d;
        double amount = raw * Math.Max(0, until - from)
            * DamageModel.Mitigate(1d, Resistance(conn, DamageType.Chaos), MaxResistance(conn, DamageType.Chaos))
            * conn.ElementalAilments.DamageTakenFactor(until, 0) * lethe + state.Carry;
        int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount));
        state.Carry = amount - damage;
        damage = IncomingResourceDefences.TakenOverTime(damage, Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(conn.Pools, Get), typedIncreasedPct: Get("chaos_damage_taken_+%"),
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        return IncomingResourceDefences.DamageOverTime(conn.EnergyShield, damage,
            ChaosDamageBypassesEnergyShield(conn, player) ? 100 : 0, until, ref conn.ChaosDotBypassCarry);
    }

    public static void TickEssenceAurasLocked(ClientState conn, double now)
    {
        RefreshEssenceAuraSourcesLocked(conn);
        var state = EssenceAuraExposures.GetOrCreateValue(conn);
        double seconds = double.IsNaN(state.MinionLast) ? 0 : Math.Max(0, now - state.MinionLast);
        state.MinionLast = now;
        if (seconds <= 0 || conn.Paused || conn.Closed || conn.LeavingArea || InGracePeriod(conn)) return;
        if (state.Sources.Count == 0) { state.MinionCarry.Clear(); return; }
        foreach (var g in conn.Golems.All.Values.Concat(conn.Reservations.Fighters.Values).Concat(conn.Summons.Fighters.Values).ToArray())
        {
            var m = g.Monster;
            if (m.Dead || m.Life == 0 || m.Invulnerable || m.Effects["base_cannot_be_damaged"] != 0
                || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning") continue;
            if (EssenceAuraAt(conn, state, m.Walk.Position(now), now) is not { } source)
            { state.MinionCarry.Remove(g.Id); continue; }
            double raw = m.MaxLife * source.Aura.ChaosLifePerMinute / 6000d;
            double amount = raw * MonsterDotTaken(m, DamageType.Chaos, now)
                * seconds + state.MinionCarry.GetValueOrDefault(g.Id);
            int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount));
            state.MinionCarry[g.Id] = amount - damage;
            bool bypass = m.Effects["chaos_damage_does_not_bypass_energy_shield"] == 0
                && m.Effects["virtual_chaos_damage_does_not_bypass_energy_shield"] == 0;
            int life = g.ShieldPool.Damage(damage, bypass, now);
            m.Life = (uint)Math.Max(0, (long)m.Life - life);
            if (damage > 0) conn.Minions.OutputQueue.Add(new([], "Essence aura damages minion", g.Id, m, PlayerMinions.OutputKind.Life));
            if (m.Life == 0) GolemDieLocked(conn, g, now);
        }
    }
}
