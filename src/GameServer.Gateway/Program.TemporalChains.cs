using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // These four fixed variety flags have identical normal/Hardmode values in the pinned Mods table.
    static readonly Lazy<MonsterCurseSources> CurseSourceCatalog = new(() => new(Items.Mods));
    internal static MonsterCurseSources CurseSources => CurseSourceCatalog.Value;

    static int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    static TemporalChainsState.Source TemporalChainsSource(ClientState conn, ushort skill, bool self = false)
    {
        var source = SupportedSkill(conn, skill);
        var raw = CharacterPlayerStats(conn);
        var own = source.Stats;
        int Get(string name) => name is "temporal_chains_action_speed_+%_final"
            or "temporal_chains_action_speed_+%_vs_rare_or_unique_final"
            or "buff_time_passed_+%_other_than_temporal_chains" or "base_skill_effect_duration"
            or "curse_effect_+%_final_vs_players" ? Stat(own, name) : Stat(raw, name) + Stat(own, name);
        double effect = Math.Max(0, 1 + Get("curse_effect_+%") / 100.0)
            * Math.Max(0, 1 + Get("support_blasphemy_curse_effect_+%_final") / 100.0);
        if (self) effect *= Math.Max(0, 1 + Get("curse_effect_+%_final_vs_players") / 100.0)
            * Math.Max(0, 1 + Stat(raw, "curse_effect_on_self_+%") / 100.0);
        double duration = TemporalChainsState.Duration(Get("base_skill_effect_duration"),
            name => Stat(raw, name), name => Stat(own, name), self);
        return new(duration,
            TemporalChainsState.Scale(Get("temporal_chains_action_speed_+%_final"), effect),
            TemporalChainsState.Scale(Get("temporal_chains_action_speed_+%_vs_rare_or_unique_final"), effect),
            Math.Max(-75, TemporalChainsState.Scale(Get("buff_time_passed_+%_other_than_temporal_chains"), effect)));
    }

    static TemporalChainsState NewTemporalChains(ClientState conn, ushort skill, double now, bool rare,
        bool aura = false, bool self = false, TemporalChainsState.Source? snapshot = null)
    {
        var source = snapshot ?? TemporalChainsSource(conn, skill, self);
        var raw = self ? CharacterPlayerStats(conn) : [];
        return new(conn.AllocateBuff(), skill, new EffectClock(aura ? double.PositiveInfinity : source.Seconds, now,
                self ? EffectClock.RecipientRate(5, name => Stat(raw, name), temporalChains: true) : 1),
            source.NormalSlow, source.RareSlow, source.OtherEffects, aura) { Snapshot = source };
    }

    static bool TemporalChainsAllowed(ClientState conn, SpawnedMonster monster)
    {
        var own = CharacterPlayerStats(conn);
        return CurseSources.Get(monster, "immune_to_curses") == 0
            && (CurseSources.Get(monster, "hexproof") == 0 || Stat(own, "ignore_hexproof") != 0
                || Stat(own, "temporal_chains_ignores_hexproof") != 0);
    }

    static byte[] TemporalBuff(uint target, TemporalChainsState state, double now)
    {
        state.Clock.Advance(now);
        // The client derives the recipient rate from its stats; packet duration is the remaining base budget.
        return InstanceEntities.AddBuff(target, state.Instance, 69, PlayerId, state.Skill,
            u68: state.Aura ? null : BitConverter.SingleToUInt32Bits((float)state.Clock.Remaining));
    }

    static double MonsterActionFactor(ClientState conn, uint id, double? at = null) =>
        ActiveMonsterTemporalChains(conn, id, at) is { } curse
        && conn.Monsters.TryGetValue(id, out var monster)
        ? CurseSources.Get(monster, ActionSpeed.ActionFloor) != 0 ? 1
            : TemporalChainsState.Factor(curse.Slow(monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique)) : 1;

    // Buff/debuff clocks use the recipient's curse. This never changes damage per second or cooldown recovery.
    static TemporalChainsState? ActiveMonsterTemporalChains(ClientState conn, uint id, double? at = null) =>
        conn.TemporalChains.TryGetValue(id, out var curse) && curse.Clock.Remaining > 0
            && curse.Clock.Deadline > (at ?? conn.Clock.Elapsed.TotalSeconds) ? curse : null;

    internal static double MonsterEffectTimeRate(ClientState conn, uint id) =>
        ActiveMonsterTemporalChains(conn, id) is { } curse ? Math.Max(.25, 1 + curse.OtherEffects / 100.0) : 1;

    internal static void SetMonsterCurseSpeed(ClientState conn, uint id, double now)
    {
        if (conn.Monsters.TryGetValue(id, out var recipient)) ReconcileMonsterIgniteRate(conn, id, recipient, now);
        if (conn.Monsters.TryGetValue(id, out var monster) && monster.Variety is { } variety)
            monster.Walk.SetSpeed(monster.Mods.Effects.MovementSpeed(variety.MovementSpeed)
                * MonsterSpeedRoll.Factor(monster.SpeedRoll) * MonsterActionFactor(conn, id), now);
    }

    static byte[] MonsterCurseStats(ClientState conn, uint id, double? at = null)
    {
        int slow = (int)Math.Round((MonsterActionFactor(conn, id, at) - 1) * 100);
        int other = ActiveMonsterTemporalChains(conn, id, at) is { } curse ? curse.OtherEffects : 0;
        return InstanceEntities.StatsUpdate(id, [(Stats.Key(ActionSpeed.Virtual)!.Value, slow),
            (Stats.Key("buff_time_passed_+%_other_than_temporal_chains")!.Value, other)]);
    }

    static async Task CastTemporalChains(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skill, (int X, int Y) centre, int action, int epoch)
    {
        var source = SupportedSkill(conn, skill);
        if (CastMs(conn, source.Effect, skill) is not int cast) return;
        double until = conn.Clock.Elapsed.TotalSeconds + cast / 1000.0;
        if (!await WaitProjectileTime(conn, until, epoch)) return;
        await SendGenerated(remote, stream, ciphers, () =>
        {
            lock (conn.Monsters)
            {
                if (!conn.ProjectileActive(epoch) || conn.Paused || conn.PlayerLife == 0 || conn.ActionCount != action
                    || !GemSkillEffects(conn).Any(g => g.Id == skill && g.Effect == source.Effect)) return null;
                double now = conn.Clock.Elapsed.TotalSeconds;
                int radius = SkillRadius(conn, SupportedSkill(conn, skill).Stats, true, source.Effect);
                var packets = new List<byte>();
                bool reflected = false;
                foreach (var (id, monster) in conn.Monsters.Where(kv => kv.Value.Hostile && !kv.Value.Dead
                    && kv.Value.Life > 0 && Dist(kv.Value.Walk.Position(now), centre) <= radius))
                {
                    if (!TemporalChainsAllowed(conn, monster)) continue;
                    if (conn.TemporalChains.Remove(id, out var old))
                        packets.AddRange(InstanceEntities.RemoveBuff(id, old.Instance));
                    var curse = NewTemporalChains(conn, skill, now,
                        monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique);
                    conn.TemporalChains[id] = curse;
                    SetMonsterCurseSpeed(conn, id, now);
                    packets.AddRange(TemporalBuff(id, curse, now));
                    packets.AddRange(MonsterCurseStats(conn, id));
                    reflected |= CurseSources.Get(monster, "reflect_curses") != 0
                        || Stat(source.Stats, "curses_reflected_to_self") != 0;
                }
                if (reflected)
                {
                    if (conn.SelfTemporalChains is { } old) packets.AddRange(InstanceEntities.RemoveBuff(PlayerId, old.Instance));
                    conn.SelfTemporalChains = NewTemporalChains(conn, skill, now, false, self: true);
                    packets.AddRange(TemporalBuff(PlayerId, conn.SelfTemporalChains, now));
                }
                Console.WriteLine($"[gateway] Temporal Chains skill=0x{skill:x4}, radius={radius}, "
                    + $"targets={conn.TemporalChains.Count}, reflected={reflected}");
                return packets.Count == 0 ? null
                    : [.. PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), .. packets];
            }
        }, "Temporal Chains cast completes");
        await SendCharacterStats(remote, stream, ciphers, conn, "Temporal Chains");
    }

    static Dictionary<uint, int> WithTemporalChains(ClientState conn, Dictionary<uint, int> sources)
    {
        lock (conn.Monsters)
        {
            if (conn.SelfTemporalChains is not { } curse) return sources;
            int Get(string name) => Stats.Key(name) is uint key
                ? conn.PlayerStats.GetValueOrDefault(key, sources.GetValueOrDefault(key)) : 0;
            curse.Clock.ChangeRate(conn.Clock.Elapsed.TotalSeconds, EffectClock.RecipientRate(5, Get, temporalChains: true));
            if (curse.Clock.Remaining <= 0 || conn.PlayerLife == 0 || Get("unaffected_by_curses") != 0) return sources;
            sources[Stats.Key("temporal_chains_action_speed_+%_final")!.Value] = curse.Slow(false);
            sources[Stats.Key("buff_time_passed_+%_other_than_temporal_chains")!.Value] = curse.OtherEffects;
            return sources;
        }
    }

    static async Task RefreshTemporalChains(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
            if (conn.SelfTemporalChains is null && conn.TemporalChains.Count == 0
                && !conn.Auras.Values.Any(a => a.EffectId == "TemporalChains")) return;
        bool selfChanged = false;
        await SendGenerated(remote, stream, ciphers, () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.Paused) return null;
                double now = conn.Clock.Elapsed.TotalSeconds;
                var packets = new List<byte>();
                if (conn.SelfTemporalChains is { } self)
                {
                    _ = CharacterPlayerStats(conn);
                    if (self.Clock.Remaining <= 0 || conn.PlayerLife == 0)
                    {
                        packets.AddRange(InstanceEntities.RemoveBuff(PlayerId, self.Instance));
                        conn.SelfTemporalChains = null; selfChanged = true;
                    }
                    else if (self.PresentedDeadline != self.Clock.Deadline)
                    {
                        packets.AddRange(InstanceEntities.RemoveBuff(PlayerId, self.Instance));
                        self.Instance = conn.AllocateBuff();
                        self.PresentedDeadline = self.Clock.Deadline;
                        packets.AddRange(TemporalBuff(PlayerId, self, now));
                    }
                }
                if (conn.Auras.Values.Any(a => a.EffectId == "TemporalChains"))
                    ReconcileOwnAuras(conn, validateSkills: true);
                var aura = conn.PlayerLife > 0 ? conn.Auras.Values.FirstOrDefault(a => a.EffectId == "TemporalChains") : null;
                int auraRadius = aura is null ? 0
                    : SkillRadius(conn, SupportedSkill(conn, aura.SkillId).Stats, true, aura.Effect);
                var centre = conn.Walk.Position(now);
                TemporalChainsState.Source? auraSource = aura is null ? null : TemporalChainsSource(conn, aura.SkillId);
                foreach (var (id, curse) in conn.TemporalChains.ToArray())
                {
                    curse.Clock.Advance(now);
                    bool alive = conn.Monsters.TryGetValue(id, out var monster) && !monster.Dead && monster.Life > 0;
                    if (alive && (!curse.Aura && curse.Clock.Remaining > 0 || curse.Aura && aura?.SkillId == curse.Skill
                        && curse.Snapshot == auraSource && TemporalChainsAllowed(conn, monster!)
                        && Dist(monster!.Walk.Position(now), centre) <= auraRadius)) continue;
                    conn.TemporalChains.Remove(id);
                    if (alive)
                    {
                        SetMonsterCurseSpeed(conn, id, now);
                        packets.AddRange(InstanceEntities.RemoveBuff(id, curse.Instance));
                        packets.AddRange(MonsterCurseStats(conn, id));
                    }
                }
                if (aura is not null && SupportedReservation(conn, aura.SkillId) is not null)
                {
                    int radius = SkillRadius(conn, SupportedSkill(conn, aura.SkillId).Stats, true, aura.Effect);
                    foreach (var (id, monster) in conn.Monsters.Where(kv => kv.Value.Hostile && !kv.Value.Dead
                        && kv.Value.Life > 0 && Dist(kv.Value.Walk.Position(now), centre) <= radius))
                    {
                        if (conn.TemporalChains.ContainsKey(id) || !TemporalChainsAllowed(conn, monster)) continue;
                        var curse = NewTemporalChains(conn, aura.SkillId, now,
                            monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique, aura: true, snapshot: auraSource);
                        conn.TemporalChains[id] = curse;
                        SetMonsterCurseSpeed(conn, id, now);
                        packets.AddRange(TemporalBuff(id, curse, now));
                        packets.AddRange(MonsterCurseStats(conn, id));
                    }
                }
                return packets.Count == 0 ? null
                    : [.. PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), .. packets];
            }
        }, "Temporal Chains expiry/aura membership");
        if (selfChanged) await SendCharacterStats(remote, stream, ciphers, conn, "Temporal Chains expires");
    }
}
