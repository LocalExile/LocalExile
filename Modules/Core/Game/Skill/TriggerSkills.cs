using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

static partial class Game
{
    sealed class DamageTakenTriggers(int epoch, object? rules)
    {
        internal readonly int Epoch = epoch;
        internal readonly object? Rules = rules;
        internal readonly Dictionary<ushort, TriggerCounter> Counters = [];
        internal readonly Dictionary<int, double> SharedReadyAt = [];
        internal readonly Queue<(ushort Skill, TriggerCounter Counter, (int X, int Y) Aim, double At)> Pending = [];
    }
    sealed class TriggerCounter(SupportedGem gem, Item? host)
    {
        internal readonly SupportedGem Gem = gem;
        internal readonly Item? Host = host;
        internal long Damage;
        internal double ReadyAt;
        internal bool Pending;
    }
    static readonly ConditionalWeakTable<ClientState, DamageTakenTriggers> DamageTriggers = new();

    internal static string? TriggerOnlyReason(SupportedGem gem)
    {
        if (gem.Supports.Any(s => s.Effect.Id == "SupportCastWhenDamageTaken"))
            return "this skill can only trigger when damage is taken";
        if (GolemStat(gem.Stats, "spell_uncastable_if_triggerable") != 0
            || gem.Supports.Any(s => s.Effect.Added.Contains(36)))
            return "this trigger has no server executor; manual use is unavailable";
        return null;
    }
    static bool SameTriggerSource(SupportedGem a, SupportedGem b) => a.Effect == b.Effect && a.Level == b.Level
        && a.Quality == b.Quality && a.Host == b.Host && a.Group == b.Group && ReferenceEquals(a.SourceGem, b.SourceGem)
        && a.Supports.Select(s => s.Gem).SequenceEqual(b.Supports.Select(s => s.Gem));

    static Item? TriggerHost(ClientState conn, SupportedGem gem) => ItemsOf(conn).EquipmentSnapshot()
        .FirstOrDefault(e => e.Item.Id == gem.Host).Item?.Item;

    // Only adopted projectile spells have an independent release executor here. Never approximate guards, curses,
    // reservations, minions or transformation supports with a generic area hit or a manual player action.
    static bool DamageTriggerExecutable(ClientState conn, ushort id, SupportedGem gem) => SkillsFor(conn).IsSpell(gem.Effect)
        && !SkillsFor(conn).IsAttack(gem.Effect) && !SkillsFor(conn).IsTravel(gem.Effect)
        && Supports.Of(gem.Effect)?.Types.Contains(47) != true && PlacedKind(gem) is (false, false)
        && gem.Supports.Count(s => s.Effect.Added.Contains(36)) == 1
        && SupportedReservation(conn, id) is null && SkillsFor(conn).VaalBudget(gem.Effect, gem.Level) is null
        && UsesProjectileRuntime(gem.Effect) && ResolveProjectileDefinition(gem.Effect) is not null
        && SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality).CanHit;

    // Called only by accepted hit damage, before Ward/ES/Mana absorb it. DoT and costs never enter this hook.
    internal static void RecordDamageTakenTriggers(ClientState conn, int damage, double at)
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
                if (support is null || !DamageTriggerExecutable(conn, skill.Id, gem)
                    || GemSkillInstanceDisabled(conn, gem.Effect, skill.Id, triggered: true) is not null) continue;
                live.Add(skill.Id);
                var host = TriggerHost(conn, gem);
                if (!state.Counters.TryGetValue(skill.Id, out var counter) || !SameTriggerSource(counter.Gem, gem)
                    || !ReferenceEquals(counter.Host, host))
                    state.Counters[skill.Id] = counter = new(gem, host);
                if (counter.Pending || at < counter.ReadyAt || at < state.SharedReadyAt.GetValueOrDefault(gem.Effect)) continue;
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

    internal static void ReleaseDamageTakenTriggers(ClientState conn, ProjectileSession session)
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
                if (now < state.SharedReadyAt.GetValueOrDefault(gem.Effect)) continue;
                if (!state.Counters.TryGetValue(request.Skill, out var current) || !ReferenceEquals(current, counter)
                    || !SameTriggerSource(counter.Gem, gem) || !ReferenceEquals(counter.Host, TriggerHost(conn, gem))
                    || GemSkillInstanceDisabled(conn, gem.Effect, request.Skill, triggered: true) is not null
                    || !ProjectilePathAvailable("CWDT", conn, request.Skill, gem.Effect)) continue;
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
                var use = CaptureGemProjectile(conn, session, request.Skill, skill, request.Aim, CaptureSkillCriticalRoll(conn, request.Skill));
                if (!PayMana(conn, request.Skill, out uint cost, out uint lifeCost)) continue;
                counter.ReadyAt = rate > 0 ? now + milliseconds / 1000d / rate : double.PositiveInfinity;
                state.SharedReadyAt[gem.Effect] = counter.ReadyAt;
                session.Schedule(session.Runtime.Now,
                    () => state.Epoch == conn.ProjectileEpoch && CurrentGameRules && ReferenceEquals(state.Rules, CombatLifetime)
                        && !conn.Paused && conn.PlayerLife > 0 && SameTriggerSource(gem, SupportedSkill(conn, skillId))
                        && ReferenceEquals(counter.Host, TriggerHost(conn, gem)),
                    () => use);
                if (cost > 0 || lifeCost > 0) session.Enqueue(() => SendSkillCost(session.remote, session.stream, session.ciphers, conn, lifeCost, "CWDT cost"));
            }
        }
    }
}
