using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    internal sealed record TemporaryAura(AuraCatalog.Aura Aura, double Until, SupportedGem Source, bool Blessing);
    internal sealed class TemporaryAuraState
    {
        public readonly Dictionary<ushort, TemporaryAura> Active = [];
        public bool StatsPending;
    }
    internal static readonly RuleTable<ClientState, TemporaryAuraState> TemporaryAuraStates = new();

    internal static bool IsBlessing(SupportedGem skill) => GolemStat(skill.Stats, "skill_is_blessing_skill") != 0
        && GolemStat(skill.Stats, "support_aura_duration_base_buff_duration") > 0;

    internal static uint BlessingBaseCost(uint maximumMana, int flat, int percent, int added, decimal multiplier) =>
        (uint)Math.Clamp(decimal.Floor((flat + maximumMana * percent / 10000m + added) * multiplier), 0, uint.MaxValue);

    static IReadOnlyDictionary<uint, int> WithBlessingCost(ClientState conn, SupportedGem skill)
    {
        if (!IsBlessing(skill) || SkillsFor(conn).Reservation(skill.Effect, skill.Level) is not { } reserve) return skill.Stats;
        var own = new Dictionary<uint, int>(skill.Stats);
        decimal factor = skill.Supports.Aggregate(1m, (v, s) => v * s.CostMultiplier / 100m);
        int cost = (int)Math.Min(int.MaxValue, BlessingBaseCost(conn.Pools.Mana, reserve.ManaFlat, reserve.ManaPercent,
            GolemStat(skill.Stats, "base_mana_cost_+"), factor));
        foreach (string name in new[] { "base_mana_cost", "mana_cost" })
            if (Stats.Key(name) is uint key) own[key] = cost;
        return own;
    }

    static bool IsTemporaryAura(ClientState conn, AuraCatalog.Aura aura) =>
        TemporaryAuraStates.TryGetValue(conn, out var state) && state.Active.TryGetValue(aura.SkillId, out var current)
        && current.Aura.Buff == aura.Buff;

    static void RemoveTemporaryAura(ClientState conn, TemporaryAuraState state, ushort id)
    {
        if (!state.Active.Remove(id, out var old)) return;
        if (conn.Auras.TryGetValue(id, out var aura) && aura.Buff == old.Aura.Buff) conn.Auras.Remove(id);
        state.StatsPending = true;
        conn.AuraStatsDirty = true;
    }

    // Called by the existing aura/tick lifecycle, including while idle. No per-aura worker or timer.
    internal static void ExpireTemporaryAuras(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!TemporaryAuraStates.TryGetValue(conn, out var state)) return;
            int before = state.Active.Count;
            foreach (var (id, value) in state.Active.ToArray())
                if (conn.PlayerLife == 0 || InTown(conn) || AreaTime(conn) >= value.Until
                    || !conn.Auras.TryGetValue(id, out var aura) || aura.Buff != value.Aura.Buff
                    || !SameTriggerSource(value.Source, SupportedSkill(conn, id)))
                    RemoveTemporaryAura(conn, state, id);
            if (before != state.Active.Count)
            {
                conn.Pools = BuildPoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
            }
        }
    }

    static double? TemporaryAuraSecondsLeft(ClientState conn, AuraCatalog.Aura aura) =>
        TemporaryAuraStates.TryGetValue(conn, out var state) && state.Active.TryGetValue(aura.SkillId, out var value)
            && value.Aura.Buff == aura.Buff ? Math.Max(0, value.Until - AreaTime(conn)) : null;

    internal static AuraCatalog.Aura StartTemporaryAuraLocked(ClientState conn, ushort id, string family, ushort row,
        double seconds, bool blessing)
    {
        var source = SupportedSkill(conn, id);
        var state = TemporaryAuraStates.GetOrCreateValue(conn);
        foreach (var (other, entry) in state.Active.ToArray())
            if (other == id || blessing && entry.Blessing || entry.Aura.EffectId == family)
                RemoveTemporaryAura(conn, state, other);
        var support = AuraSupport(conn, id);
        var (artVariation, hideVisual, sourceVisual, buffVisual) =
            ReservationCosmeticVisuals(conn, id, family, hasSource: true);
        var aura = new AuraCatalog.Aura(id, source.Effect, family, family,
            conn.NextBuff++, row, 0, 0, source.Level, source.Quality, SourceBuff: conn.NextBuff++,
            SkillStats: [.. source.Stats.Select(p => (p.Key, p.Value))],
            SupportEffectPct: support.EffectPct, AffectsSelf: support.AffectsSelf,
            ArtVariation: artVariation, HideVisual: hideVisual, SourceVisual: sourceVisual, BuffVisual: buffVisual);
        state.Active[id] = new(aura, AreaTime(conn) + seconds, source, blessing);
        state.StatsPending = false; // The activation publishes its new character snapshot itself.
        conn.Auras[id] = aura;
        conn.AuraStatsDirty = true;
        return aura;
    }

    static async Task SendTemporaryAuraExpiry(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!TemporaryAuraStates.TryGetValue(conn, out var state)) return;
        ExpireTemporaryAuras(conn);
        lock (conn.Monsters)
        {
            if (!state.StatsPending || conn.PlayerLife == 0) return;
            state.StatsPending = false;
        }
        await SendOwnAuraChanges(remote, stream, ciphers, conn);
        await SendCharacterStats(remote, stream, ciphers, conn, "temporary aura expired");
    }

    static async Task<bool> ActivateBlessing(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort id, Func<bool> current)
    {
        lock (conn.Monsters)
        {
            if (!current()) return false;
            var skill = SupportedSkill(conn, id);
            string effect = SkillsFor(conn).EffectId(skill.Effect) ?? "";
            if (!IsBlessing(skill) || !AuraCatalog.Supported(effect)) return false;
            double seconds = VaalAuraSeconds(GolemStat(skill.Stats, "support_aura_duration_base_buff_duration"),
                CharacterPlayerStats(conn), skill.Stats);
            if (seconds <= 0) return false;
            StartTemporaryAuraLocked(conn, id, effect, AuraCatalog.BuffRow(effect), seconds, blessing: true);
        }
        await SendOwnAuraChanges(remote, stream, ciphers, conn);
        await SendCharacterStats(remote, stream, ciphers, conn, "temporary Blessing");
        return true;
    }

    static bool ScheduleBlessing(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort id, int recovery, Func<bool> current)
    {
        var source = SupportedSkill(conn, id);
        if (CastMs(conn, source.Effect, id) is not int cast) return false;
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        bool Valid() => session.Active && current() && conn.ActionInterruptionGeneration == recovery
            && PlayerStateAllowsAction(conn) && SameTriggerSource(source, SupportedSkill(conn, id));
        session.Runtime.Schedule(ProjectileRuntime.Time(AreaTime(conn) + cast / 1000d), () =>
        {
            if (Valid()) session.Enqueue(async () => { await ActivateBlessing(remote, stream, ciphers, conn, id, Valid); });
        });
        return true;
    }

    internal static IDisposable CleanupTemporaryAuras() => new TemporaryAuraCleanup();

    static async Task<bool> ActivateVaalHaste(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort id, Func<bool> current)
    {
        lock (conn.Monsters)
        {
            if (!current()) return false;
            var skill = SupportedSkill(conn, id);
            double duration = VaalAuraSeconds(GolemStat(skill.Stats, "base_skill_effect_duration"),
                CharacterPlayerStats(conn), skill.Stats);
            if (duration <= 0) return false;
            StartTemporaryAuraLocked(conn, id, "VaalHaste", 161, duration, blessing: false);
        }
        await SendOwnAuraChanges(remote, stream, ciphers, conn);
        await SendCharacterStats(remote, stream, ciphers, conn, "Vaal Haste");
        return true;
    }

    static bool SharedPlayerAura(string effect) => AuraCatalog.Shared(effect) || effect is "Smite" or "VaalHaste";

    static int TemporaryAuraValue(AuraCatalog.Aura aura, string stat, int effect)
    {
        int value = (aura.SkillStats ?? Skills.EffectStats(aura.Effect, aura.Level, aura.Quality))
            .FirstOrDefault(s => s.Item1 == Stats.Key(stat)).Item2;
        return (int)Math.Clamp(decimal.Truncate(value * (100m + Math.Max(-100,
            aura.ExternalEffectPct ?? effect + aura.SupportEffectPct)) / 100m), int.MinValue, int.MaxValue);
    }

    static CharacterStats.Bonus WithTemporaryAuraBonus(IEnumerable<AuraCatalog.Aura> auras, int effect, CharacterStats.Bonus bonus)
    {
        var haste = auras.Where(a => a.AffectsSelf && a.EffectId == "VaalHaste").ToArray();
        int Best(string stat) => haste.Select(a => TemporaryAuraValue(a, stat, effect)).DefaultIfEmpty().Max();
        return bonus with { AttackSpeedPct = bonus.AttackSpeedPct + Best("attack_speed_+%_granted_from_skill"),
            MovementVelocityPct = bonus.MovementVelocityPct + Best("base_movement_velocity_+%") };
    }

    static void WithTemporaryAuraStats(ClientState conn, Dictionary<uint, int> stats)
    {
        static bool Temporary(AuraCatalog.Aura aura) => aura.EffectId is "Smite" or "VaalHaste";
        if (!conn.Auras.Values.Any(Temporary) && !conn.ReceivedAuras.Values.Any(Temporary)) return;
        WithTemporaryAuraStats(AffectingAuras(conn), conn.Pools.AuraEffectPct, stats);
    }

    static void WithTemporaryAuraStats(IEnumerable<AuraCatalog.Aura> auras, int effect, Dictionary<uint, int> stats)
    {
        void Add(string to, IEnumerable<AuraCatalog.Aura> group, string from)
        {
            if (Stats.Key(to) is not uint key) return;
            int value = group.Select(a => TemporaryAuraValue(a, from, effect)).DefaultIfEmpty().Max();
            if (value != 0) stats[key] = stats.GetValueOrDefault(key) + value;
        }
        var smite = auras.Where(a => a.AffectsSelf && a.EffectId == "Smite").ToArray();
        foreach (string kind in new[] { "attack", "spell" })
        {
            Add(kind + "_minimum_added_lightning_damage", smite, "minimum_added_lightning_damage_from_skill");
            Add(kind + "_maximum_added_lightning_damage", smite, "maximum_added_lightning_damage_from_skill");
        }
        Add("base_chance_to_shock_%", smite, "base_chance_to_shock_%_from_skill");
        // This is a count supplied by the Vaal buff, not a scalable damage stat.
        int extra = smite.Select(a =>
                Skills.EffectStats(a.Effect, a.Level, a.Quality).FirstOrDefault(p =>
                    p.Item1 == Stats.Key("buff_grants_smite_additional_lightning_targets")).Item2).DefaultIfEmpty().Max();
        if (extra != 0 && Stats.Key("base_smite_number_of_targets") is uint targets)
            stats[targets] = stats.GetValueOrDefault(targets) + extra;
        Add("cast_speed_+%", auras.Where(a => a.AffectsSelf && a.EffectId == "VaalHaste"), "cast_speed_+%_granted_from_skill");
    }

    sealed class TemporaryAuraCleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var (conn, state) in TemporaryAuraStates)
                lock (conn.Monsters)
                {
                    foreach (ushort id in state.Active.Keys.ToArray()) RemoveTemporaryAura(conn, state, id);
                    conn.Pools = BuildPoolsFor(conn, conn.Pools.Level);
                    conn.ApplyMovementSpeed();
                }
        }
    }
}
