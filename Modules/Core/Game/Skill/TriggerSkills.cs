using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    sealed class DamageTakenTriggers(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public readonly Dictionary<ushort, TriggerCounter> Counters = [];
        public readonly Queue<(ushort Skill, TriggerCounter Counter, (int X, int Y) Aim, double At)> Pending = [];
    }
    sealed class TriggerCounter(SupportedGem gem)
    {
        public readonly SupportedGem Gem = gem;
        public long Damage;
        public double ReadyAt;
        public bool Pending;
    }
    static readonly ConditionalWeakTable<ClientState, DamageTakenTriggers> DamageTriggers = new();

    public static string? TriggerOnlyReason(SupportedGem gem)
    {
        if (gem.Supports.Any(s => s.Effect.Id == "SupportCastWhenDamageTaken"))
            return "this skill can only trigger when damage is taken";
        if (GolemStat(gem.Stats, "cast_spell_on_linked_attack_crit") != 0)
            return "this skill can only trigger on a linked attack critical strike";
        if (GolemStat(gem.Stats, "cast_spell_while_linked_skill_channelling") != 0)
            return "this skill can only trigger while a linked skill is channelled";
        if (GolemStat(gem.Stats, "spell_uncastable_if_triggerable") != 0
            || gem.Supports.Any(s => s.Effect.Added.Contains(36)))
            return "this trigger has no server executor; manual use is unavailable";
        return null;
    }
    static bool SameTriggerSource(SupportedGem a, SupportedGem b) => a.Effect == b.Effect && a.Level == b.Level
        && a.Quality == b.Quality && a.Host == b.Host && a.Group == b.Group && (a.SourceGem is null ? b.SourceGem is null
            : b.SourceGem is not null && ReferenceEquals(PlayerInventories.GemIdentity(a.SourceGem), PlayerInventories.GemIdentity(b.SourceGem)))
        && a.Supports.Select(s => s.Gem).SequenceEqual(b.Supports.Select(s => s.Gem));

    // Every payload needs an independent executor; unsupported mechanics stay unavailable.
    static bool TriggeredSpellExecutable(ClientState conn, ushort id, SupportedGem gem) => SkillsFor(conn).IsSpell(gem.Effect)
        && !SkillsFor(conn).IsAttack(gem.Effect) && !SkillsFor(conn).IsTravel(gem.Effect)
        && Supports.Of(gem.Effect)?.Types.Contains(47) != true && PlacedKind(gem) is (false, false)
        && gem.Supports.Count(s => s.Effect.Added.Contains(36)) == 1
        && SupportedReservation(conn, id) is null && SkillsFor(conn).VaalBudget(gem.Effect, gem.Level) is null
        && TriggerPayloadAvailable(conn, gem);

    // Called only by accepted hit damage, before Ward/ES/Mana absorb it. DoT and costs never enter this hook.
    public static void RecordDamageTakenTriggers(ClientState conn, int damage, double at)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.PendingTransfer is not null
                || conn.PlayerLife == 0) { DamageTriggers.Remove(conn); return; }
            if (conn.Paused) return;
            if (damage <= 0 || InTown(conn)) return;
            if (!DamageTriggers.TryGetValue(conn, out var state) || state.Epoch != conn.ProjectileEpoch
                || !ReferenceEquals(state.Rules, CombatLifetime))
            {
                DamageTriggers.Remove(conn);
                DamageTriggers.Add(conn, state = new(conn.ProjectileEpoch, CombatLifetime));
            }
            var live = new HashSet<ushort>();
            foreach (var skill in GemSkillEffects(conn)) // native equipment/socket order
            {
                var gem = SupportedSkill(conn, skill.Id);
                var support = gem.Supports.FirstOrDefault(s => s.Effect.Id == "SupportCastWhenDamageTaken");
                if (support is null || !TriggeredSpellExecutable(conn, skill.Id, gem)
                    || GemSkillInstanceDisabled(conn, gem.Effect, skill.Id, triggered: true) is not null) continue;
                live.Add(skill.Id);
                if (!state.Counters.TryGetValue(skill.Id, out var counter) || !SameTriggerSource(counter.Gem, gem))
                    state.Counters[skill.Id] = counter = new(gem);
                if (TriggerGuardSpell(conn, gem) && !TriggerGuardReady(conn, at)) continue;
                if (counter.Pending || at < counter.ReadyAt || at < LinkedTriggersOf(conn).ReadyAt.GetValueOrDefault(gem.Effect)) continue;
                int threshold = GolemStat(gem.Stats, "cast_on_damage_taken_threshold");
                if (threshold <= 0) continue;
                threshold = (int)Math.Clamp((long)threshold * Math.Max(0L, 100L
                    + GolemStat(gem.Stats, "cast_when_damage_taken_trigger_threshold_+%")) / 100, 1, int.MaxValue);
                counter.Damage += damage;
                if (counter.Damage < threshold) continue;
                if (state.Pending.Count >= 128) continue;
                counter.Damage = 0; counter.Pending = true;
                var origin = conn.Walk.Position(at);
                var aim = conn.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0)
                    .OrderBy(e => Dist(origin, e.Value.Walk.Position(at))).ThenBy(e => e.Key)
                    .Select(e => e.Value.Walk.Position(at)).FirstOrDefault((origin.X + 20, origin.Y));
                state.Pending.Enqueue((skill.Id, counter, aim, at));
            }
            foreach (var id in state.Counters.Keys.Where(id => !live.Contains(id)).ToArray()) state.Counters.Remove(id);
        }
    }

    public static void ReleaseDamageTakenTriggers(ClientState conn, ProjectileSession session)
    {
        lock (conn.Monsters)
        {
            if (!DamageTriggers.TryGetValue(conn, out var state)) return;
            if (!CurrentGameRules || !ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch
                || conn.Closed || conn.LeavingArea || conn.PendingTransfer is not null || conn.PlayerLife == 0)
            { DamageTriggers.Remove(conn); return; }
            if (conn.Paused) return;
            while (state.Pending.TryDequeue(out var request))
            {
                ushort skillId = request.Skill;
                var counter = request.Counter; counter.Pending = false;
                var gem = SupportedSkill(conn, request.Skill);
                double now = Math.Max(request.At, conn.Clock.Elapsed.TotalSeconds);
                if (now < LinkedTriggersOf(conn).ReadyAt.GetValueOrDefault(gem.Effect)) continue;
                if (!state.Counters.TryGetValue(request.Skill, out var current) || !ReferenceEquals(current, counter)
                    || !SameTriggerSource(counter.Gem, gem)
                    || GemSkillInstanceDisabled(conn, gem.Effect, request.Skill, triggered: true) is not null
                    || !TriggerPayloadAvailable(conn, gem)) continue;
                var skill = GemSkillEffects(conn).FirstOrDefault(g => g.Id == request.Skill);
                if (skill.Name is null) continue;
                var support = gem.Supports.Single(s => s.Effect.Id == "SupportCastWhenDamageTaken");
                var player = CharacterPlayerStats(conn);
                int milliseconds = Math.Max(SkillsFor(conn).CooldownMs(gem.Effect, gem.Level),
                    SkillsFor(conn).CooldownMs(support.Gem.Effect, support.Gem.Level));
                double rate = Math.Max(0, 1 + ((Stats.Key("virtual_cooldown_speed_+%") is uint recoveryKey
                    ? player.GetValueOrDefault(recoveryKey) : 0)
                    + (Stats.Key("base_cooldown_speed_+%") is uint baseRecovery ? gem.Stats.GetValueOrDefault(baseRecovery) : 0)
                    + (Stats.Key("virtual_cooldown_speed_+%") is uint ownRecovery ? gem.Stats.GetValueOrDefault(ownRecovery) : 0)) / 100d);
                bool Current() => state.Epoch == conn.ProjectileEpoch && CurrentGameRules
                    && ReferenceEquals(state.Rules, CombatLifetime) && session.Active && !conn.Paused
                    && conn.PlayerLife > 0 && !conn.LeavingArea && conn.PendingTransfer is null
                    && SameTriggerSource(gem, SupportedSkill(conn, skillId));
                var release = PrepareTriggeredPayload(conn, session, skillId, request.Aim, Current);
                if (release is null || !PayMana(conn, request.Skill, out uint cost, out uint lifeCost)) continue;
                counter.ReadyAt = rate > 0 ? now + milliseconds / 1000d / rate : double.PositiveInfinity;
                LinkedTriggersOf(conn).ReadyAt[gem.Effect] = counter.ReadyAt;
                release();
                if (cost > 0 || lifeCost > 0) session.Enqueue(() => SendSkillCost(session.remote, session.stream, session.ciphers, conn, lifeCost, "CWDT cost"));
            }
        }
    }
}
