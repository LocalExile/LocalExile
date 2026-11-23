using GameServer.Core.World;
using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

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
    public sealed record TriggeredSkillExecutor(string Name, Func<Player, WornGrantedSkills.Grant, bool> Accepts,
        Action<EffectRules.Fire, WornGrantedSkills.Grant> Run);

    static TriggeredSkillExecutor[] _triggeredSkillExecutors = [];
    static readonly object TriggeredSkillGate = new();

    public static void RegisterTriggeredSkillExecutor(TriggeredSkillExecutor executor)
    {
        lock (TriggeredSkillGate)
            _triggeredSkillExecutors = [.. _triggeredSkillExecutors.Where(e => e.Name != executor.Name), executor];
    }

    static TriggeredSkillExecutor? TriggeredSkillExecutorFor(Player player, WornGrantedSkills.Grant grant)
    {
        foreach (var executor in Volatile.Read(ref _triggeredSkillExecutors))
            if (executor.Accepts(player, grant)) return executor;
        return null;
    }

    // A granted skill's own stats by name: read on every stat build for every triggered grant worn, so kept for each
    // (skill catalog, effect, level).
    static readonly ConcurrentDictionary<(SkillCatalog Skills, int Effect, int Level), Dictionary<uint, int>> GrantedSkillStats = new();
    static Dictionary<uint, int> GrantedSkillStatsOf(Player player, WornGrantedSkills.Grant grant) =>
        GrantedSkillStats.GetOrAdd((SkillsFor(player), grant.Effect, grant.Level), static key =>
        {
            var stats = new Dictionary<uint, int>();
            foreach (var (stat, value) in key.Skills.EffectStats(key.Effect, key.Level)) stats[stat] = value;
            return stats;
        });

    /// <summary>Whether this item-granted skill is triggered by an effect rule: an executor can release it and a
    /// loaded rule is turned on by one of its stats. (The client's "manually cast" flag does not tell: it is set on the
    /// triggered marks too.)</summary>
    public static bool RuleTriggeredGrant(Player player, WornGrantedSkills.Grant grant)
    {
        if (grant.Support || !EffectRules.HasCarrierRules(SkillCarrier)
            || TriggeredSkillExecutorFor(player, grant) is null) return false;
        // Asked for every worn grant whenever the skill list is read: the rule lookup is remembered for the loaded rules.
        var key = (SkillsFor(player), grant.Effect, grant.Level, EffectRules.Revision);
        if (RuleTriggered.TryGetValue(key, out bool known)) return known;
        var stats = GrantedSkillStatsOf(player, grant);
        bool triggered = EffectRules.CarrierRuleFor(SkillCarrier, stat => stats.GetValueOrDefault(stat), grant.EffectId);
        if (RuleTriggered.Count > 4096) RuleTriggered.Clear();
        RuleTriggered[key] = triggered;
        return triggered;
    }
    static readonly ConcurrentDictionary<(SkillCatalog Skills, int Effect, int Level, int Revision), bool> RuleTriggered = new();

    /// <summary>The worn, active, rule-triggered granted skills as carriers of the effect rules. Empty, and nothing
    /// read, while no loaded rule names the carrier.</summary>
    public static IReadOnlyList<EffectRules.Source> TriggeredSkillSources(Player player)
    {
        if (!EffectRules.HasCarrierRules(SkillCarrier)) return [];
        var grants = AllWornGrantedSkillsOf(player);
        if (grants.Count == 0) return [];
        List<EffectRules.Source>? sources = null;
        foreach (var grant in grants)
        {
            if (grant.Support || IsInactive(player, grant.Host)
                || !LeagueFor(player).Rules.AllowsGemEffect(grant.EffectId) || TriggeredSkillExecutorFor(player, grant) is null) continue;
            var stats = GrantedSkillStatsOf(player, grant);
            (sources ??= []).Add(new(SkillCarrier, (grant.Identity, grant.EffectsPerLevelRow),
                key => stats.GetValueOrDefault(key),
                new Dictionary<string, double>
                {
                    ["cooldown"] = SkillsFor(player).CooldownMs(grant.Effect, grant.Level) / 1000.0,
                    ["uses"] = Math.Max(1, SkillsFor(player).CooldownUses(grant.Effect, grant.Level)),
                    ["level"] = grant.Level,
                }, grant, $"{grant.EffectId} (level {grant.Level})") { Name = grant.EffectId });
        }
        return sources ?? (IReadOnlyList<EffectRules.Source>)[];
    }

    // The Actor skill of a granted skill: its id is what the cost, the supports and the client know it by.
    static (ushort Id, SupportedGem Gem)? ActorSkillOf(Player player, WornGrantedSkills.Grant grant)
    {
        foreach (var skill in GemSkillEffects(player))
            if (skill.Effect == grant.Effect && SupportedSkill(player, skill.Id) is { } gem && gem.WornGrant == grant) return (skill.Id, gem);
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
                var player = fire.Conn;
                if (fire.Event.Monster is not { } monster) return;
                
                {
                    if (monster.Dead || monster.Life == 0 || !monster.Hostile || player.PlayerLife == 0
                        || ActorSkillOf(player, grant) is not var (id, gem) || !gem.RequirementsMet
                        || !PayMana(player, id, out _, out _)) return;
                    ApplyCurseLocked(player, monster, id, AreaTime(player), false, gem, CharacterPlayerStats(player));
                }
            }));

        // A minion the item's skill summons, beside the character or where the event's monster stood: the summon
        // routine of the gems (CastSummon through ReleaseTriggeredSummon), for a skill the summon catalog knows.
        RegisterTriggeredSkillExecutor(new("summon", (_, grant) => SummonData.Find(grant.EffectId) is not null,
            (fire, grant) =>
            {
                var player = fire.Conn;
                ushort id; (int X, int Y) aim; uint mana, life;
                
                {
                    if (player.PlayerLife == 0 || player.Paused || player.Closed || player.LeavingArea || InTown(player)
                        || ActorSkillOf(player, grant) is not var (actor, gem) || !gem.RequirementsMet
                        || GemSkillInstanceDisabled(player, gem.Effect, actor, triggered: true) is not null) return;
                    double now = AreaTime(player);
                    aim = fire.Word("at") == "self" ? player.Walk.Position(now)
                        : fire.Event.Monster is { } monster ? monster.Walk.Position(now)
                        : fire.Event is ChargeEvent.SkillUse { At: { } spot } ? spot : player.Walk.Position(now);
                    if (!PayMana(player, actor, out mana, out life)) return;
                    id = actor;
                }
                int depth = fire.Event.Depth + 1;
                var lifetime = CombatLifetime;
                var session = ProjectilesFor(player);
                bool Current() => session.Active && CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime)
                    && !player.Closed && !player.LeavingArea && player.PendingTransfer is null && !player.Paused && player.PlayerLife > 0
                    && WornGrantedSkillOwner(player).StillWorn(grant, ItemsOf(player).EquipmentSnapshot());
                session.Enqueue(async () =>
                {
                    if (!Current()) return;
                    using var triggered = EnterTriggered(depth);
                    await ReleaseTriggeredSummon(player, session, id, aim, Current);
                    if (mana > 0 || life > 0) await SendSkillCost(player, life, $"{grant.EffectId} cost");
                });
            }));

        // One area hit of a spell, where the event's monster stands (around the player for a skill that targets
        // itself, SpellCentre): the triggered action goes to the client, which draws the skill, and the hit lands at
        // once (AreaSkillHit's immediate form, as a trigger gem's Ice Nova). Only the skills named in
        // effect_skills table (how "area"): a ground effect, a chain or a storm is not one hit because its stat set
        // has damage. A row may ask for several hits (hits, every, spread): a storm's bolts around the spot.
        RegisterTriggeredSkillExecutor(new("area", (_, grant) => TriggeredAreaOf(grant.EffectId) is not null,
            (fire, grant) =>
            {
                var player = fire.Conn;
                if (TriggeredAreaOf(grant.EffectId) is not { } area) return;
                ushort id; (int X, int Y) from, aim; uint mana, life; uint? targetId = null; int? radius = null; DamagePacket? baseExtra;
                
                {
                    if (player.PlayerLife == 0 || player.Paused || player.Closed || player.LeavingArea || InTown(player)
                        || ActorSkillOf(player, grant) is not var (actor, gem) || !gem.RequirementsMet
                        || GemSkillInstanceDisabled(player, gem.Effect, actor, triggered: true) is not null) return;
                    double now = AreaTime(player);
                    from = player.Walk.Position(now);
                    aim = from;
                    if (fire.Word("at") != "self" && fire.Event.Monster is { } monster)
                    {
                        aim = monster.Walk.Position(now);
                        foreach (var (objectId, known) in player.Area.Monsters)
                            if (ReferenceEquals(known, monster)) { targetId = objectId; break; }
                    }
                    // A skill use has no monster: the skill goes off where that use was aimed.
                    else if (fire.Word("at") != "self" && fire.Event is ChargeEvent.SkillUse { At: { } spot }) aim = spot;
                    baseExtra = TriggeredBaseExtra(player, gem);
                    // Where the skill itself goes off: a self-targeted one around the character wherever it was aimed
                    // (SpellCentre), so its art and each of a storm's hits are placed from there.
                    aim = SpellCentre(player, gem.Effect, from, aim);
                    // A row's radius is a base radius: the character's area stats grow it as they do the skill's own.
                    if (area.Radius is int baseRadius)
                        radius = SkillRadius(player, baseRadius, gem.Stats, SkillsFor(player).IsSpell(gem.Effect), gem.Effect);
                    if (!PayMana(player, actor, out mana, out life)) return;
                    id = actor;
                }
                var skill = GemSkillEffects(player).FirstOrDefault(s => s.Id == id);
                if (skill.Name is null) return;
                int depth = fire.Event.Depth + 1;
                var lifetime = CombatLifetime;
                var session = ProjectilesFor(player);
                bool Current() => session.Active && CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime)
                    && !player.Closed && !player.LeavingArea && player.PendingTransfer is null && !player.Paused && player.PlayerLife > 0
                    && WornGrantedSkillOwner(player).StillWorn(grant, ItemsOf(player).EquipmentSnapshot());
                session.Enqueue(async () =>
                {
                    if (!Current()) return;
                    using var triggered = EnterTriggered(depth);
                    // How the client is told (TriggeredAreaDraw): which form of the action draws these skills is being
                    // found out in game, so every form is a setting.
                    string draw = TriggeredAreaDraw;
                    if (draw != "none")
                    {
                        bool asTriggered = draw is "triggered" or "target";
                        uint? target = draw is "target" or "instant-target" ? targetId : null;
                        ushort sequence;
                        sequence = player.Connection.LockstepClient ? NextLockstepSequence(player)
                            : asTriggered ? ++player.Twister.Sequence : (ushort)0xffff;
                        await SendWorldRecord(player, player.Id, InstanceProtocol.InstantAction(player.Id, id, aim.X, aim.Y,
                            target, sequence, player.Connection.LockstepClient ? (ushort)0x101 : (ushort)0, asTriggered),
                            $"0x01ce {skill.Name} triggered by its item at ({aim.X}, {aim.Y}), drawn as {draw}{(target is { } t ? $" on 0x{t:x}" : "")}");
                    }
                    await Strike();
                    if (mana > 0 || life > 0) await SendSkillCost(player, life, $"{skill.Name} cost");
                });
                // The later hits of a row with several, each on the session's own clock and queue.
                if (area.Hits > 1)
                    
                    {
                        if (!session.Active) return;
                        long start = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(AreaTime(player)));
                        for (int i = 1; i < area.Hits; i++)
                            session.Runtime.Schedule(start + ProjectileRuntime.Time(i * area.Every), () => session.Enqueue(async () =>
                            {
                                if (!Current()) return;
                                using var triggered = EnterTriggered(depth);
                                await Strike();
                            }));
                    }

                // One hit and its art. The client accepts the triggered action (and counts the skill's use) but shows
                // nothing for these skills from it (live 2026-11-22: 40 accepted, none seen), so the art is an effect
                // object at the spot, as a kill explosion's and a herald's are.
                async Task Strike()
                {
                    var at = aim;
                    if (area.Spread > 0)
                    {
                        var rng = AreaRng(player);
                        double angle = rng.NextDouble() * Math.Tau, far = Math.Sqrt(rng.NextDouble()) * area.Spread;
                        at = (aim.X + (int)Math.Round(Math.Cos(angle) * far), aim.Y + (int)Math.Round(Math.Sin(angle) * far));
                    }
                    if (TriggeredAreaArt && area.Art is { } art)
                        await PresentEffectArt(player, art, at, area.Scale, area.Seconds);
                    // from and to are the same spot, so the hit lands there whatever the skill's own targeting is.
                    await AreaSkillHit(player, id, skill, at, at, immediate: true,
                        radiusOverride: radius, triggeredCurrent: Current, baseExtra: baseExtra);
                }
            }));
    }

    /// <summary>Base damage a granted skill states in a form the hit plan does not read: added damage per character
    /// level (<c>minimum_added_fire_damage_per_level</c>: Blazing Glare, Caustic Retribution) and spell damage per
    /// point of the worn shield's quality (Shield Shatter). Null when the skill has neither.</summary>
    static readonly (uint LevelMin, uint LevelMax, uint QualityMin, uint QualityMax)[] BaseExtraStats =
        [.. Enum.GetValues<DamageType>().OrderBy(t => (int)t).Select(t => t.ToString().ToLowerInvariant()).Select(name =>
            (Stats.Key($"minimum_added_{name}_damage_per_level") ?? 0, Stats.Key($"maximum_added_{name}_damage_per_level") ?? 0,
             Stats.Key($"spell_minimum_base_{name}_damage_per_shield_quality") ?? 0, Stats.Key($"spell_maximum_base_{name}_damage_per_shield_quality") ?? 0))];

    public static DamagePacket? TriggeredBaseExtra(Player player, SupportedGem gem)
    {
        DamagePacket? extra = null;
        int level = Math.Max(1, (int)player.Pools.Level);
        int quality = ActiveOffHand(player) is { Type.Class: "Shield" } shield ? shield.Item.Quality : 0;
        foreach (var type in Enum.GetValues<DamageType>())
        {
            var keys = BaseExtraStats[(int)type];
            double min = (double)GolemStat(gem.Stats, keys.LevelMin) * level + (double)GolemStat(gem.Stats, keys.QualityMin) * quality;
            double max = (double)GolemStat(gem.Stats, keys.LevelMax) * level + (double)GolemStat(gem.Stats, keys.QualityMax) * quality;
            if (max > 0) extra = (extra ?? DamagePacket.Empty).Add(type, new(min, max));
        }
        return extra;
    }

    /// <summary>How an item-triggered skill that hits an area is carried out and shown: the art where it goes off (a
    /// MiscAnimated Id or an .ao path; null for none), the art's size and how long it stays, a radius in place of the
    /// skill's own, and for a skill of several hits their number, the seconds between them and how far from the spot
    /// each may land.</summary>
    public sealed record TriggeredArea(string? Art = null, float Scale = 1, double Seconds = 2, int? Radius = null,
        int Hits = 1, double Every = 0, int Spread = 0);

    sealed record TriggeredAreaTable(int Revision, Dictionary<string, TriggeredArea> Skills);
    static TriggeredAreaTable triggeredAreas = new(-1, []);
    static Dictionary<string, TriggeredArea> addedTriggeredAreas = [];

    /// <summary>The skills the "area" executor releases: world.db's <c>effect_skills</c> rows with how "area" (the
    /// art of most is a best guess until its row is checked against the game), and those a module or mod added
    /// with <see cref="AllowTriggeredAreaSkill"/>. A row wins over an added one: the table is the user's.</summary>
    public static TriggeredArea? TriggeredAreaOf(string effectId)
    {
        var table = Volatile.Read(ref triggeredAreas);
        int revision = GameData.Current.Revision;
        if (table.Revision != revision)
            lock (TriggeredSkillGate)
            {
                var skills = new Dictionary<string, TriggeredArea>(addedTriggeredAreas, StringComparer.Ordinal);
                foreach (var row in GameData.EffectSkills)
                    if (row.How == "area")
                        skills[row.Effect] = new(row.Art, (float)row.Scale, row.Seconds, row.Radius, row.Hits, row.Every, row.Spread);
                Volatile.Write(ref triggeredAreas, table = new(revision, skills));
            }
        return table.Skills.GetValueOrDefault(effectId);
    }

    /// <summary>Whether the "area" executor shows its art (the effect object). A switch for testing.</summary>
    public static volatile bool TriggeredAreaArt = true;

    /// <summary>How the client is told of an item-triggered area spell: "triggered" (the triggered form of the server's
    /// action, 0x1ce with its trigger bit), "instant" (the plain form, as a server-run gem use), "target" or
    /// "instant-target" (the same with the monster as the action's target), or "none".</summary>
    public static volatile string TriggeredAreaDraw = "triggered";

    public static void AllowTriggeredAreaSkill(string effectId, string? art = null) =>
        AllowTriggeredAreaSkill(effectId, new TriggeredArea(art));

    public static void AllowTriggeredAreaSkill(string effectId, TriggeredArea area)
    {
        lock (TriggeredSkillGate)
        {
            addedTriggeredAreas = new(addedTriggeredAreas) { [effectId] = area };
            Volatile.Write(ref triggeredAreas, new(-1, []));
        }
    }

    /// <summary>The .ao file of one of the client's MiscAnimated effects by its Id, or the text itself when it already
    /// is a path; null when the client has no such effect.</summary>
    public static string? EffectArtAo(string art)
    {
        if (art.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)) return art;
        foreach (var row in ClientTables.Table("MiscAnimated"))
            if (string.Equals(row.Text("Id"), art, StringComparison.OrdinalIgnoreCase)) return row.Text("AOFile");
        return null;
    }

    /// <summary>The Ids of the client's MiscAnimated effects that contain this text: for a tool that lets a person pick one.</summary>
    public static IReadOnlyList<string> EffectArtIds(string part, int most = 20)
    {
        var found = new List<string>();
        foreach (var row in ClientTables.Table("MiscAnimated"))
            if (row.Text("Id") is { } id && id.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(id);
                if (found.Count >= most) break;
            }
        return found;
    }

    /// <summary>Shows one of the client's effects at a spot for a while, as an effect object: a MiscAnimated Id or an
    /// .ao path. Removed by the projectile session's own scheduler, so nothing is left running for it.</summary>
    public static async Task<bool> PresentEffectArt(Player player, string art, (int X, int Y) at, float scale = 1, double seconds = 2)
    {
        if (EffectArtAo(art) is not { } ao) return false;
        var session = ProjectilesFor(player);
        uint id;
        
        {
            if (!session.Active || player.LeavingArea) return false;
            id = player.TakeObjectId();
            player.ProjectileObjects.Add(id);
        }
        var effect = new EffectObject(at.X, at.Y, ao) { Scale = scale };
        bool created = await SendWorldCreate(player, id,
            () => session.Active ? InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()) : null,
            $"0x02bb effect {art} at ({at.X}, {at.Y})", wake: true);
        
        {
            if (!created) { player.ProjectileObjects.Remove(id); return false; }
            if (!session.Active) return true;
            long until = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(AreaTime(player)))
                + ProjectileRuntime.Time(seconds);
            session.Runtime.Schedule(until, () => session.WorldRecords(id, () =>
            {
                player.ProjectileObjects.Remove(id);
                return [InstanceProtocol.RemoveObject(id)];
            }, remove: true));
        }
        return true;
    }
}
