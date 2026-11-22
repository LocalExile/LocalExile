using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Skills an item grants that are triggered, not cast ("Trigger Level 10 Assassin's Mark when you Hit a Rare or Unique
/// Enemy and have no Mark"). The trigger is a stat of the granted skill itself, so each such skill is a carrier of the
/// effect rules ("carrier":"skill"): a row keyed by the trigger stat says when it fires, the skill's own data gives its
/// cooldown, and the "skill" effect kind releases it through an executor. An executor says which granted skills it
/// can release and does it; a skill with a rule and an executor is one of the Actor's skills (WornGrantedSkillsOf),
/// as the game lists it, and cannot be used by hand (TriggerOnlyReason). A module or mod adds executors with
/// <see cref="Game.RegisterTriggeredSkillExecutor"/>; a triggered skill without one stays unlisted and does nothing.
/// </summary>
public static partial class Game
{
    public const string SkillCarrier = "skill";

    /// <summary>Releases one kind of item-triggered skill. Accepts: whether it can release this granted effect.
    /// Run: release it for the event that fired the rule (often inside the Monsters lock: quick, no sends or waits).</summary>
    public sealed record TriggeredSkillExecutor(string Name, Func<ClientState, WornGrantedSkills.Grant, bool> Accepts,
        Action<EffectRules.Fire, WornGrantedSkills.Grant> Run);

    static TriggeredSkillExecutor[] _triggeredSkillExecutors = [];
    static readonly object TriggeredSkillGate = new();

    public static void RegisterTriggeredSkillExecutor(TriggeredSkillExecutor executor)
    {
        lock (TriggeredSkillGate)
            _triggeredSkillExecutors = [.. _triggeredSkillExecutors.Where(e => e.Name != executor.Name), executor];
    }

    static TriggeredSkillExecutor? TriggeredSkillExecutorFor(ClientState conn, WornGrantedSkills.Grant grant)
    {
        foreach (var executor in Volatile.Read(ref _triggeredSkillExecutors))
            if (executor.Accepts(conn, grant)) return executor;
        return null;
    }

    // A granted skill's own stats by name: read on every stat build for every triggered grant worn, so kept for each
    // (skill catalog, effect, level).
    static readonly ConcurrentDictionary<(SkillCatalog Skills, int Effect, int Level), Dictionary<uint, int>> GrantedSkillStats = new();
    static Dictionary<uint, int> GrantedSkillStatsOf(ClientState conn, WornGrantedSkills.Grant grant) =>
        GrantedSkillStats.GetOrAdd((SkillsFor(conn), grant.Effect, grant.Level), static key =>
        {
            var stats = new Dictionary<uint, int>();
            foreach (var (stat, value) in key.Skills.EffectStats(key.Effect, key.Level)) stats[stat] = value;
            return stats;
        });

    /// <summary>Whether this item-granted skill is triggered by an effect rule: an executor can release it and a
    /// loaded rule is turned on by one of its stats. (The client's "manually cast" flag does not tell: it is set on the
    /// triggered marks too.)</summary>
    public static bool RuleTriggeredGrant(ClientState conn, WornGrantedSkills.Grant grant)
    {
        if (grant.Support || !EffectRules.HasCarrierRules(SkillCarrier)
            || TriggeredSkillExecutorFor(conn, grant) is null) return false;
        // Asked for every worn grant whenever the skill list is read: the rule lookup is remembered for the loaded rules.
        var key = (SkillsFor(conn), grant.Effect, grant.Level, EffectRules.Revision);
        if (RuleTriggered.TryGetValue(key, out bool known)) return known;
        var stats = GrantedSkillStatsOf(conn, grant);
        bool triggered = EffectRules.CarrierRuleFor(SkillCarrier, name => Stats.Key(name) is uint stat ? stats.GetValueOrDefault(stat) : 0);
        if (RuleTriggered.Count > 4096) RuleTriggered.Clear();
        RuleTriggered[key] = triggered;
        return triggered;
    }
    static readonly ConcurrentDictionary<(SkillCatalog Skills, int Effect, int Level, int Revision), bool> RuleTriggered = new();

    /// <summary>The worn, active, rule-triggered granted skills as carriers of the effect rules. Empty, and nothing
    /// read, while no loaded rule names the carrier.</summary>
    public static IReadOnlyList<EffectRules.Source> TriggeredSkillSources(ClientState conn)
    {
        if (!EffectRules.HasCarrierRules(SkillCarrier)) return [];
        var grants = AllWornGrantedSkillsOf(conn);
        if (grants.Count == 0) return [];
        List<EffectRules.Source>? sources = null;
        foreach (var grant in grants)
        {
            if (grant.Support || IsInactive(conn, grant.Host)
                || !LeagueFor(conn).Rules.AllowsGemEffect(grant.EffectId) || TriggeredSkillExecutorFor(conn, grant) is null) continue;
            var stats = GrantedSkillStatsOf(conn, grant);
            (sources ??= []).Add(new(SkillCarrier, (grant.Identity, grant.EffectsPerLevelRow),
                name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0,
                new Dictionary<string, double>
                {
                    ["cooldown"] = SkillsFor(conn).CooldownMs(grant.Effect, grant.Level) / 1000.0,
                    ["uses"] = Math.Max(1, SkillsFor(conn).CooldownUses(grant.Effect, grant.Level)),
                    ["level"] = grant.Level,
                }, grant, $"{grant.EffectId} (level {grant.Level})"));
        }
        return sources ?? (IReadOnlyList<EffectRules.Source>)[];
    }

    // The Actor skill of a granted skill: its id is what the cost, the supports and the client know it by.
    static (ushort Id, SupportedGem Gem)? ActorSkillOf(ClientState conn, WornGrantedSkills.Grant grant)
    {
        foreach (var skill in GemSkillEffects(conn))
            if (skill.Effect == grant.Effect && SupportedSkill(conn, skill.Id) is { } gem && gem.WornGrant == grant) return (skill.Id, gem);
        return null;
    }

    /// <summary>A curse or mark by its family: an item's triggered variant ("AssassinsMarkTriggered") is the curse it
    /// names, with the same effect on its target.</summary>
    public static string CurseFamily(string? effectId)
    {
        const string triggered = "Triggered";
        if (effectId is null) return "";
        return !CurseCatalog.All.ContainsKey(effectId) && effectId.EndsWith(triggered, StringComparison.Ordinal)
            && CurseCatalog.All.ContainsKey(effectId[..^triggered.Length]) ? effectId[..^triggered.Length] : effectId;
    }

    static void EnsureTriggeredSkills()
    {
        EffectRules.RegisterCarrier(SkillCarrier);

        // Releases the granted skill whose stat turned the rule on, "at" the event's monster (the hit or killed one)
        // or at the character itself ("self": a skill that targets its user). What it releases can hit and kill, so the
        // loop guard applies (Chains): by default a triggered skill's own hits and kills trigger no further skill.
        EffectRules.Register(new("skill", [], ["at"], fire =>
        {
            if (fire.Source?.Thing is WornGrantedSkills.Grant grant) TriggeredSkillExecutorFor(fire.Conn, grant)?.Run(fire, grant);
        }, (_, word) => word is "monster" or "self")
        { Chains = true, NameDefaults = new Dictionary<string, string> { ["at"] = "monster" } });

        // A curse or mark on the monster of the event (the one hit): paid for like a cast, and never on a corpse.
        RegisterTriggeredSkillExecutor(new("curse", (_, grant) => CurseCatalog.All.ContainsKey(CurseFamily(grant.EffectId)),
            (fire, grant) =>
            {
                var conn = fire.Conn;
                if (fire.Event.Monster is not { } monster) return;
                lock (conn.Monsters)
                {
                    if (monster.Dead || monster.Life == 0 || !monster.Hostile || conn.PlayerLife == 0
                        || ActorSkillOf(conn, grant) is not var (id, gem) || !gem.RequirementsMet
                        || !PayMana(conn, id, out _, out _)) return;
                    ApplyCurseLocked(conn, monster, id, AreaTime(conn), false, gem, CharacterPlayerStats(conn));
                }
            }));

        // One area hit of a spell, where the event's monster stands (around the player for a skill that targets
        // itself, SpellCentre): the triggered action goes to the client, which draws the skill, and the hit lands at
        // once (AreaSkillHit's immediate form, as a trigger gem's Ice Nova). Only the skills named in
        // TriggeredAreaSkills: a ground effect, a chain or a storm is not one hit because its stat set has damage.
        RegisterTriggeredSkillExecutor(new("area", (_, grant) => TriggeredAreaSkills.Contains(grant.EffectId),
            (fire, grant) =>
            {
                var conn = fire.Conn;
                if (conn.Wire is not (var remote, { } stream, var ciphers)) return;
                ushort id; (int X, int Y) from, aim; uint mana, life;
                lock (conn.Monsters)
                {
                    if (conn.PlayerLife == 0 || conn.Paused || conn.Closed || conn.LeavingArea || InTown(conn)
                        || ActorSkillOf(conn, grant) is not var (actor, gem) || !gem.RequirementsMet
                        || GemSkillInstanceDisabled(conn, gem.Effect, actor, triggered: true) is not null) return;
                    double now = AreaTime(conn);
                    from = conn.Walk.Position(now);
                    aim = fire.Word("at") != "self" && fire.Event.Monster is { } monster ? monster.Walk.Position(now) : from;
                    if (!PayMana(conn, actor, out mana, out life)) return;
                    id = actor;
                }
                var skill = GemSkillEffects(conn).FirstOrDefault(s => s.Id == id);
                if (skill.Name is null) return;
                int depth = fire.Event.Depth + 1;
                var lifetime = CombatLifetime;
                var session = ProjectilesFor(remote, stream, ciphers, conn);
                bool Current() => session.Active && CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime)
                    && !conn.Closed && !conn.LeavingArea && conn.PendingTransfer is null && !conn.Paused && conn.PlayerLife > 0
                    && WornGrantedSkillOwner(conn).StillWorn(grant, ItemsOf(conn).EquipmentSnapshot());
                session.Enqueue(async () =>
                {
                    if (!Current()) return;
                    using var triggered = EnterTriggered(depth);
                    ushort sequence;
                    lock (conn.Monsters) sequence = conn.LockstepClient ? NextLockstepSequence(conn) : ++conn.Twister.Sequence;
                    await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.InstantAction(PlayerId, id, aim.X, aim.Y,
                        sequence: sequence, flags: conn.LockstepClient ? (ushort)0x101 : (ushort)0, triggered: true),
                        $"0x01ce {skill.Name} triggered by its item at ({aim.X}, {aim.Y})");
                    await AreaSkillHit(remote, stream, ciphers, conn, id, skill, from, aim, immediate: true, triggeredCurrent: Current);
                    if (mana > 0 || life > 0) await SendSkillCost(remote, stream, ciphers, conn, life, $"{skill.Name} cost");
                });
            }));
    }

    /// <summary>The item-triggered spells that are one area hit at their target, released by the "area" executor.
    /// A module or mod adds one it has checked with <see cref="AllowTriggeredAreaSkill"/>.</summary>
    static HashSet<string> TriggeredAreaSkills = ["LightningBolt", "FireBurstOnHit", "TriggeredExplosion"];

    public static void AllowTriggeredAreaSkill(string effectId)
    {
        lock (TriggeredSkillGate) TriggeredAreaSkills = [.. TriggeredAreaSkills, effectId];
    }
}
