using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

// Remnant of Corruption's degeneration and slow aura: the RemnantOfCorruptionMonsterAura mod (BuffTemplates 237,
// radius 30, BuffDefinitions 768 remnant_of_corruption_aura), −30% movement speed and physical damage per minute equal
// to 360% of maximum life plus 180% of maximum energy shield (6%/s life + 3%/s ES). Kept in its own file beside
// EssenceAuras.cs so tuning either aura touches nothing the other reads.
public static partial class Game
{
    public const string RemnantAuraMod = "RemnantOfCorruptionMonsterAura";
    sealed record RemnantAura(double Radius, ushort Buff, ushort Mod, int MoveSpeedPct, int PhysLifePerMinute, int PhysEsPerMinute);
    sealed record RemnantAuraSource(uint Id, SpawnedMonster Monster, RemnantAura Aura);
    sealed class RemnantAuraExposure
    {
        public readonly List<RemnantAuraSource> Sources = [];
        public double Last = double.NaN, MinionLast = double.NaN, Started = double.NegativeInfinity, Carry, TakenCarry;
        public ushort? Buff;
        public uint Source;
        public int SlowPct;
        public bool Taking;
        public readonly Dictionary<uint, double> MinionCarry = [];
    }
    static readonly RuleTable<Player, RemnantAuraExposure> RemnantAuraExposures = new();
    static RemnantAura? RemnantAuraDefinition;
    internal static void LoadRemnantAuraData()
    {
        var mod = Items.Mods.ById(RemnantAuraMod);
        if (mod?.BuffTemplate is not int row || ClientTables.Table("BuffTemplates").Row(row) is not { } template
            || template.IntOrNull("BuffDefinitionsKey") is not int buff) { RemnantAuraDefinition = null; return; }
        int StatValue(uint id) => mod.Stats.FirstOrDefault(s => s.Id == id)?.Min ?? 0;
        RemnantAuraDefinition = new(template.Int("AuraRadius"), checked((ushort)buff), mod.Hash,
            StatValue(Stat.BaseMovementVelocityPlusPct),
            StatValue(Stat.BasePhysicalDamagePctOfMaximumLifeTakenPerMinute),
            StatValue(Stat.BasePhysicalDamagePctOfMaximumEnergyShieldTakenPerMinute));
    }

    public static void RefreshRemnantAuraSourcesLocked(Player conn)
    {
        var state = RemnantAuraExposures.GetOrCreateValue(conn);
        state.Sources.Clear();
        if (RemnantAuraDefinition is not { } aura) return;
        foreach (var (id, monster) in conn.Area.Monsters)
            if (monster.Hostile && !monster.Dead && monster.Life > 0 && HasMod(monster.Mods, aura.Mod))
                state.Sources.Add(new(id, monster, aura));
    }

    // No LINQ on the per-frame path: this runs over every monster of the area each tick.
    static bool HasMod(MonsterModSet set, ushort hash)
    {
        var mods = set.Mods;
        for (int i = 0; i < mods.Count; i++) if (mods[i].Hash == hash) return true;
        return false;
    }

    static RemnantAuraSource? RemnantAuraAt(Player conn, RemnantAuraExposure state, (int X, int Y) at, double now)
    {
        foreach (var source in state.Sources)
        {
            var m = source.Monster;
            if (m.Dead || m.Life == 0 || m.WorldHidden || !m.Hostile
                || !conn.Area.Monsters.TryGetValue(source.Id, out var held) || !ReferenceEquals(m, held)) continue;
            var from = m.Walk.Position(now);
            double dx = (double)from.X - at.X, dy = (double)from.Y - at.Y;
            if (dx * dx + dy * dy <= source.Aura.Radius * source.Aura.Radius) return source;
        }
        return null;
    }

    static void RemnantAuraIcon(Player conn, RemnantAuraExposure state, RemnantAuraSource? source)
    {
        int newSlow = source?.Aura.MoveSpeedPct ?? 0;
        bool slowChanged = state.SlowPct != newSlow;
        state.SlowPct = newSlow;
        if (state.Source != (source?.Id ?? 0))
        {
            if (state.Buff is ushort old)
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(conn.Id, old), "Remnant of Corruption aura ends", WorldId: conn.Id));
            state.Buff = null;
            state.Source = source?.Id ?? 0;
            if (source is not null)
            {
                ushort buff = conn.NextBuff++;
                state.Buff = buff;
                conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(conn.Id, buff, source.Aura.Buff, source.Id),
                    "Remnant of Corruption aura", WorldId: conn.Id));
            }
        }
        if (slowChanged && !conn.Closed) conn.ApplyMovementSpeed();
    }

    /// <summary>GameEvents.PlayerStatSources: the −30% movement velocity slow while inside a Remnant of Corruption aura.
    /// Zero work when the player is not exposed.</summary>
    public static Dictionary<uint, int> RemnantAuraStats(Player conn, Dictionary<uint, int> stats)
    {
        if (!RemnantAuraExposures.TryGetValue(conn, out var state) || state.SlowPct == 0) return stats;
        uint key = Stat.BaseMovementVelocityPlusPct;
        stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + state.SlowPct, int.MinValue, int.MaxValue);
        return stats;
    }

    public static double PlayerRemnantAuraStarted(Player conn) =>
        RemnantAuraExposures.TryGetValue(conn, out var state) ? state.Started : double.NegativeInfinity;

    public static void ClearRemnantAuraExposure(Player conn)
    {
        if (!RemnantAuraExposures.TryGetValue(conn, out var state)) return;
        RemnantAuraIcon(conn, state, null);
        RemnantAuraExposures.Remove(conn);
    }

    public static IDisposable CleanupRemnantAuras() => new RemnantAuraCleanup();
    sealed class RemnantAuraCleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var (conn, _) in RemnantAuraExposures)
                ClearRemnantAuraExposure(conn);
        }
    }

    public static int AdvanceRemnantAuraLocked(Player conn, double from, double until,
        IReadOnlyDictionary<uint, int> player, bool physicalImmune, double otherDotStart)
    {
        if (!RemnantAuraExposures.TryGetValue(conn, out var state)) return 0;
        if (conn.Paused) { state.Last = until; return 0; }
        from = Math.Max(from, double.IsNaN(state.Last) ? from : state.Last);
        state.Last = Math.Max(from, until);
        var source = conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || InGracePeriod(conn)
            ? null : RemnantAuraAt(conn, state, conn.Walk.Position(until), until);
        RemnantAuraIcon(conn, state, source);
        if (source is null) { state.Taking = false; state.Carry = state.TakenCarry = 0; return 0; }
        int Get(uint key) => player.GetValueOrDefault(key);
        if (conn.Immune || physicalImmune || Get(Stat.BaseCannotBeDamaged) != 0)
        { state.Taking = false; state.Carry = state.TakenCarry = 0; return 0; }
        if (until <= from) return 0;
        if (!state.Taking) { state.Started = from; state.Taking = true; }
        double lethe = Get(Stat.KeystoneLetheShade) != 0 && until - Math.Max(otherDotStart, state.Started) < 1 ? .5 : 1;
        double raw = conn.PlayerMaxLife * source.Aura.PhysLifePerMinute / 6000d
            + conn.EnergyShield.Maximum * source.Aura.PhysEsPerMinute / 6000d;
        double physReduction = PlayerDamageReduction(conn, player).Physical;
        double amount = raw * Math.Max(0, until - from)
            * Math.Max(0, 1 - physReduction / 100d)
            * conn.ElementalAilments.DamageTakenFactor(until, 0) * lethe + state.Carry;
        int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount));
        state.Carry = amount - damage;
        damage = IncomingResourceDefences.TakenOverTime(damage, Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(conn.Pools, Get), typedIncreasedPct: Get(Stat.PhysicalDamageTakenPlusPct),
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        return IncomingResourceDefences.DamageOverTime(conn.EnergyShield, damage,
            Get(IncomingResourceDefences.BypassStat), until, ref conn.NonChaosDotBypassCarry);
    }

    public static void TickRemnantAurasLocked(Player conn, double now)
    {
        if (conn.Closed) return;
        RefreshRemnantAuraSourcesLocked(conn);
        var state = RemnantAuraExposures.GetOrCreateValue(conn);
        double seconds = double.IsNaN(state.MinionLast) ? 0 : Math.Max(0, now - state.MinionLast);
        state.MinionLast = now;
        if (seconds <= 0 || conn.Paused || conn.Closed || conn.LeavingArea || InGracePeriod(conn)) return;
        if (state.Sources.Count == 0) { state.MinionCarry.Clear(); return; }
        foreach (var g in conn.Golems.All.Values.Concat(conn.Reservations.Fighters.Values).Concat(conn.Summons.Fighters.Values).ToArray())
        {
            var m = g.Monster;
            if (m.Dead || m.Life == 0 || m.Invulnerable || m.Effects[Stat.BaseCannotBeDamaged] != 0
                || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning") continue;
            if (RemnantAuraAt(conn, state, m.Walk.Position(now), now) is not { } source)
            { state.MinionCarry.Remove(g.Id); continue; }
            double raw = m.MaxLife * source.Aura.PhysLifePerMinute / 6000d
                + g.ShieldPool.Maximum * source.Aura.PhysEsPerMinute / 6000d;
            double amount = raw * Math.Max(0, 1 - m.Effects.PhysicalReduction / 100d)
                * MonsterDotTaken(m, DamageType.Physical, now) * seconds + state.MinionCarry.GetValueOrDefault(g.Id);
            int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount));
            state.MinionCarry[g.Id] = amount - damage;
            int life = g.ShieldPool.Damage(damage, false, now);
            m.Life = (uint)Math.Max(0, (long)m.Life - life);
            if (damage > 0) conn.Minions.OutputQueue.Add(new([], "Remnant aura damages minion", g.Id, m, PlayerMinions.OutputKind.Life));
            if (m.Life == 0) GolemDieLocked(conn, g, now);
        }
    }
}
