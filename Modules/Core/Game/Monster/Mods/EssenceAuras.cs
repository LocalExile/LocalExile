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
    static readonly RuleTable<Player, EssenceAuraExposure> EssenceAuraExposures = new();
    static EssenceAura? EssenceDegenDefinition;
    internal static void LoadEssenceAurasData()
    {
        var mod = Items.Mods.ById(EssenceLifeDegen);
        if (mod?.BuffTemplate is not int row || ClientTables.Table("BuffTemplates").Row(row) is not { } template
            || template.IntOrNull("BuffDefinitionsKey") is not int buff) { EssenceDegenDefinition = null; return; }
        EssenceDegenDefinition = new(template.Int("AuraRadius"), checked((ushort)buff), mod.Hash,
            mod.Stats.First(s => s.Id == Stat.BaseChaosDamagePctOfMaximumLifeTakenPerMinute).Min);
    }

    // Companion cadence gathers sources once; resource recovery examines only aura carriers, with its prepared stats.
    // Rebuilding also admits retained monsters after module replacement and notices changed mod sets.
    public static void RefreshEssenceAuraSourcesLocked(Player player)
    {
        var state = EssenceAuraExposures.GetOrCreateValue(player);
        state.Sources.Clear();
        if (EssenceDegenDefinition is not { } aura) return;
        foreach (var (id, monster) in player.Area.Monsters)
            if (monster.Hostile && !monster.Dead && monster.Life > 0
                && monster.Mods.Effects[Stat.BaseChaosDamagePctOfMaximumLifeTakenPerMinute] > 0
                && monster.Mods.Mods.Any(m => m.Hash == aura.Mod)) state.Sources.Add(new(id, monster, aura));
    }

    static EssenceAuraSource? EssenceAuraAt(Player player, EssenceAuraExposure state, (int X, int Y) at, double now)
    {
        foreach (var source in state.Sources)
        {
            var m = source.Monster;
            if (m.Dead || m.Life == 0 || m.WorldHidden || !m.Hostile
                || !player.Area.Monsters.TryGetValue(source.Id, out var held) || !ReferenceEquals(m, held)) continue;
            var from = m.Walk.Position(now);
            double dx = (double)from.X - at.X, dy = (double)from.Y - at.Y;
            if (dx * dx + dy * dy <= source.Aura.Radius * source.Aura.Radius) return source;
        }
        return null;
    }

    static void EssenceAuraIcon(Player player, EssenceAuraExposure state, EssenceAuraSource? source)
    {
        if (state.Source == (source?.Id ?? 0)) return;
        if (state.Buff is ushort old)
            player.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(player.Id, old), "Essence degeneration aura ends", WorldId: player.Id));
        state.Buff = null;
        state.Source = source?.Id ?? 0;
        if (source is null) return;
        ushort buff = player.NextBuff++;
        state.Buff = buff;
        player.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(player.Id, buff, source.Aura.Buff, source.Id),
            "Essence degeneration aura", WorldId: player.Id));
    }

    public static double PlayerEssenceAuraStarted(Player player) =>
        EssenceAuraExposures.TryGetValue(player, out var state) ? state.Started : double.NegativeInfinity;

    public static void ClearEssenceAuraExposure(Player player)
    {
        if (!EssenceAuraExposures.TryGetValue(player, out var state)) return;
        EssenceAuraIcon(player, state, null);
        EssenceAuraExposures.Remove(player);
    }

    public static IDisposable CleanupEssenceAuras() => new EssenceAuraCleanup();
    sealed class EssenceAuraCleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var (player, _) in EssenceAuraExposures)
                ClearEssenceAuraExposure(player);
        }
    }

    public static int AdvanceEssenceAuraLocked(Player player, double from, double until,
        IReadOnlyDictionary<uint, int> playerStats, bool chaosImmune, double otherDotStart)
    {
        if (!EssenceAuraExposures.TryGetValue(player, out var state)) return 0;
        if (player.Paused) { state.Last = until; return 0; }
        from = Math.Max(from, double.IsNaN(state.Last) ? from : state.Last);
        state.Last = Math.Max(from, until);
        var source = player.Closed || player.LeavingArea || player.PlayerLife == 0 || InGracePeriod(player)
            ? null : EssenceAuraAt(player, state, player.Walk.Position(until), until);
        EssenceAuraIcon(player, state, source);
        if (source is null) { state.Taking = false; state.Carry = state.TakenCarry = 0; return 0; }
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        if (player.Immune || chaosImmune || Get(Stat.BaseCannotBeDamaged) != 0)
        { state.Taking = false; state.Carry = state.TakenCarry = 0; return 0; }
        if (until <= from) return 0;
        if (!state.Taking) { state.Started = from; state.Taking = true; }
        double lethe = Get(Stat.KeystoneLetheShade) != 0 && until - Math.Max(otherDotStart, state.Started) < 1 ? .5 : 1;
        double raw = player.PlayerMaxLife * source.Aura.ChaosLifePerMinute / 6000d;
        double amount = raw * Math.Max(0, until - from)
            * DamageModel.Mitigate(1d, Resistance(player, DamageType.Chaos) + Get(Stat.AdditionalChaosResistanceAgainstDamageOverTimePct), MaxResistance(player, DamageType.Chaos))
            * player.ElementalAilments.DamageTakenFactor(until, 0) * lethe + state.Carry;
        int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount));
        state.Carry = amount - damage;
        damage = IncomingResourceDefences.TakenOverTime(damage, Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(player.Pools, Get), typedIncreasedPct: Get(Stat.ChaosDamageTakenPlusPct),
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        return DamagePlayerChaosOverTime(player, damage, until, playerStats);
    }

    public static void TickEssenceAurasLocked(Player player, double now)
    {
        if (player.Closed) return;
        RefreshEssenceAuraSourcesLocked(player);
        var state = EssenceAuraExposures.GetOrCreateValue(player);
        double seconds = double.IsNaN(state.MinionLast) ? 0 : Math.Max(0, now - state.MinionLast);
        state.MinionLast = now;
        if (seconds <= 0 || player.Paused || player.Closed || player.LeavingArea || InGracePeriod(player)) return;
        if (state.Sources.Count == 0) { state.MinionCarry.Clear(); return; }
        foreach (var g in player.Golems.All.Values.Concat(player.Reservations.Fighters.Values).Concat(player.Summons.Fighters.Values).ToArray())
        {
            var m = g.Monster;
            if (m.Dead || m.Life == 0 || m.Invulnerable || m.Effects[Stat.BaseCannotBeDamaged] != 0
                || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning") continue;
            if (EssenceAuraAt(player, state, m.Walk.Position(now), now) is not { } source)
            { state.MinionCarry.Remove(g.Id); continue; }
            double raw = m.MaxLife * source.Aura.ChaosLifePerMinute / 6000d;
            double amount = raw * MonsterDotTaken(m, DamageType.Chaos, now)
                * seconds + state.MinionCarry.GetValueOrDefault(g.Id);
            int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount));
            state.MinionCarry[g.Id] = amount - damage;
            bool bypass = m.Effects[Stat.ChaosDamageDoesNotBypassEnergyShield] == 0
                && m.Effects[Stat.VirtualChaosDamageDoesNotBypassEnergyShield] == 0;
            int life = g.ShieldPool.Damage(damage, bypass, now);
            m.Life = (uint)Math.Max(0, (long)m.Life - life);
            if (damage > 0) player.Minions.OutputQueue.Add(new([], "Essence aura damages minion", g.Id, m, PlayerMinions.OutputKind.Life));
            if (m.Life == 0) GolemDieLocked(player, g, now);
        }
    }
}
