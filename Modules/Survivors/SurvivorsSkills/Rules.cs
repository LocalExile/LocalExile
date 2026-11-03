using System.Collections.Concurrent;
using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
using ChatCommand = GameServer.Modding.ChatCommand;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

using GameServer.Modules.Survivors;


namespace GameServer.Modules.Survivors;

static partial class SurvivorsSkills
{
    /// <summary>A herald's aura reaches this far around the player, in cells, before its rank and area grow it.</summary>
    internal const double SurvivorsHeraldRadius = 48;

    /// <summary>
    /// The hit effect each Herald gem plays where its pulse strikes: the game's own <c>.ao</c> for the herald's impact
    /// (Ash's fire explosion, Ice's shatter, Thunder's bolt from above, Agony's poison splash, Purity's slam) and the
    /// clip's length, after which the effect object is removed.
    /// </summary>
    internal sealed record SurvivorsHeraldImpact(string Ao, int Ms);

    internal static readonly IReadOnlyDictionary<string, SurvivorsHeraldImpact> SurvivorsHeraldImpacts =
        new Dictionary<string, SurvivorsHeraldImpact>(StringComparer.Ordinal)
        {
            ["HeraldOfAsh"] = new("Metadata/Effects/Spells/herald/fire/flameherald_explode.ao", 958),
            ["HeraldOfIce"] = new("Metadata/Effects/Spells/herald/ice/iceherald_explode.ao", 833),
            ["HeraldOfThunder"] = new("Metadata/Effects/Spells/herald/lightning/lightning_strike_herald.ao", 1625),
            ["HeraldOfAgony"] = new("Metadata/Effects/Spells/herald/agony/minion/mortarimpact.ao", 1000),
            ["HeraldOfPurity"] = new("Metadata/Effects/Spells/herald/light/slam_impact/slam_impact.ao", 1000),
        };

    internal static SurvivorsHeraldImpact? SurvivorsHeraldImpactFor(ClientState conn, SurvivorsSkill skill) =>
        SkillsFor(conn).EffectId(skill.Effect) is { } id && SurvivorsHeraldImpacts.TryGetValue(id, out var impact)
            ? impact : null;

    // The per-cast mechanic trace for the gem verification pass (issue #503): Game_SURVIVORS_TRACE=1 in the Gateway's
    // environment writes one line per cast with the numbers the cast actually used.
    internal static readonly bool SurvivorsTrace = Environment.GetEnvironmentVariable("Game_SURVIVORS_TRACE") == "1";

    /// <summary>
    /// The first skill id a run's skills use: above the gem band (FirstSkillId + 64), so no socketed gem ever takes one,
    /// and only ids that no basic action masks to (SkillCatalog.SkillIds).
    /// </summary>
    internal const ushort SurvivorsSkillIdBase = 0x0300;

    // The ids a run's skills may use, in the order the catalogue hands them out.
    internal static readonly Lazy<ushort[]> SurvivorsSkillIdPool =
        new(() => [.. Skills.SkillIds().Where(id => id >= SurvivorsSkillIdBase)]);

    /// <summary>The skill id of a catalogue entry. The catalogue follows the item data's order, so an entry keeps its id
    /// for the whole run, across area changes.</summary>
    internal static ushort SurvivorsSkillId(int index) =>
        index >= 0 && index < SurvivorsSkillIdPool.Value.Length ? SurvivorsSkillIdPool.Value[index] : (ushort)0;

    /// <summary>The base cooldown of a run skill: its gem's cast interval, which the client shows on the skill bar.</summary>
    internal static int SurvivorsSkillCooldownMs(SurvivorsSkill skill, int rank) =>
        (int)Math.Round(SurvivorsFormulas.CastInterval(skill.CastTimeMs / 1000.0, rank, 0) * 1000);

    // The BuffDefinitions row of each Herald effect (the aura the client draws around the player), read once: the
    // effect's ActiveSkills row names the buff (<c>herald_of_ash</c> and the like).
    internal static readonly Lazy<IReadOnlyDictionary<int, int>> HeraldBuffRows = new(() =>
    {
        var result = new Dictionary<int, int>();
        string folder = Path.Combine("data", "tables");
        if (!File.Exists(Path.Combine(folder, "GrantedEffects.json")) || !File.Exists(Path.Combine(folder, "ActiveSkills.json"))
            || !File.Exists(Path.Combine(folder, "BuffDefinitions.json"))) return result;
        using var effects = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "GrantedEffects.json")));
        using var actives = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "ActiveSkills.json")));
        using var buffs = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "BuffDefinitions.json")));
        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < buffs.RootElement.GetArrayLength(); i++)
            if (buffs.RootElement[i].TryGetProperty("Id", out var id) && id.GetString() is { } name) byId[name] = i;
        for (int row = 0; row < effects.RootElement.GetArrayLength(); row++)
        {
            if (!effects.RootElement[row].TryGetProperty("ActiveSkill", out var active) || active.ValueKind != JsonValueKind.Number) continue;
            int at = active.GetInt32();
            if (at < 0 || at >= actives.RootElement.GetArrayLength()) continue;
            if (actives.RootElement[at].TryGetProperty("Id", out var skill) && skill.GetString() is { } effectId
                && byId.TryGetValue(effectId, out int buff)) result[row] = buff;
        }
        return result;
    });

    /// <summary>
    /// The run's learned skills as Actor skills, so the client puts their gems on the skill bar and draws their cooldown.
    /// Each is the real gem's effect at the run skill's rank, with one stored use and its cast interval as the cooldown.
    /// </summary>
    internal static IReadOnlyList<GrantedSkill> SurvivorsGrantedSkills(ClientState conn, SurvivorsRun run,
        IReadOnlyList<SurvivorsSkill> catalog)
    {
        var skills = new List<GrantedSkill>();
        foreach (var (index, rank) in run.Skills.OrderBy(s => s.Key))
        {
            if (index < 0 || index >= catalog.Count) continue;
            ushort id = SurvivorsSkillId(index);
            if (id == 0) continue;
            var stats = SurvivorsSkillStats(conn, catalog[index], rank);
            if (SkillsFor(conn).FindByRow(id, catalog[index].Effect, rank, stats) is { } granted)
                skills.Add(granted with { CooldownMs = (uint)SurvivorsSkillCooldownMs(catalog[index], rank) });
        }
        return skills;
    }

    // A run skill's stats: the gem's own at its rank. The client creates the bar's cooldown record only with a stored use
    // and the record's duration (0x141eea6a0), and the run pays no mana.
    internal static IReadOnlyList<(uint, int)> SurvivorsSkillStats(ClientState conn, SurvivorsSkill skill, int rank)
    {
        var stats = SkillsFor(conn).EffectStats(skill.Effect, rank).ToDictionary(s => s.Item1, s => s.Item2);
        foreach (var name in new[] { "base_cooldown_count", "virtual_total_cooldown_count" })
            if (Stats.Key(name) is uint key) stats[key] = 1;
        foreach (var name in new[] { "base_mana_cost", "mana_cost" })
            if (Stats.Key(name) is uint key) stats[key] = 0;
        return [.. stats.Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>The skill bar's cooldown for one run skill: one server instant action per target runs the gem's own use
    /// there (the client draws its projectile and starts the bar's cooldown, live 2026-10-21), then the actor's cooldown
    /// trailer sets the exact time to the next cast.</summary>
    internal static async Task SendSurvivorsCooldown(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, int skill, int rank, IReadOnlyList<(int X, int Y)> aims, double interval)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership) return;
        ushort id = SurvivorsSkillId(skill);
        if (id == 0) return;
        foreach (var aim in aims)
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, id, aim.X, aim.Y),
                $"0x01ce survivors skill 0x{id:x4} used at ({aim.X}, {aim.Y})", still: () => ownership.Current);
        if (!ownership.Current) return;
        var definition = StateOf(conn).SurvivorsCatalog is { } catalog && skill < catalog.Count ? catalog[skill] : null;
        float total = (definition is null ? 1000 : SurvivorsSkillCooldownMs(definition, rank)) / 1000f;
        float remaining = (float)Math.Clamp(interval, 0, total);
        await Send(remote, stream, ciphers,
            InstanceEntities.PlayerSingleUseCooldowns(PlayerId, [new SingleUseCooldownUpdate(id, remaining, total, 1f)]),
            $"0x02ba survivors cooldown 0x{id:x4} {remaining:0.00}/{total:0.00} s", still: () => ownership.Current);
    }

    /// <summary>True when a skill id is one of the run's skills: a click on its bar slot must not become a walk.</summary>
    internal static bool SurvivorsOwnsSkill(ClientState conn, ushort id)
    {
        if (StateOf(conn).SurvivorsCatalog is not { } catalog) return false;
        for (int index = 0; index < catalog.Count; index++)
            if (SurvivorsSkillId(index) == id) return true;
        return false;
    }

    /// <summary>The monsters the current area can draw a horde from, weakest first, and the run skills' base damage:
    /// <see cref="WeaponDamage"/> is the starting tier pool's median life (one rank-1 cast ≈ one starting hit), and
    /// <see cref="TopLife"/> is the roster's strongest variety, for reporting. <see cref="Ranged"/> is the subset whose
    /// attack is a projectile (a bow, a wand, a mortar): the rings the player has to dodge.</summary>
    internal sealed record SurvivorsRoster(IReadOnlyList<MonsterCatalog.Variety> Varieties,
        IReadOnlyList<MonsterCatalog.Variety> Bosses, byte Level, int WeaponDamage, int TopLife, double SkillScale = 1,
        IReadOnlyList<MonsterCatalog.Variety>? Ranged = null);

    /// <summary>
    /// Every skill gem the client can draw: a projectile gem flies its own Projectiles row, a Herald pulses around
    /// the player, and an area gem (a nova, Firestorm and the like: an area ActiveSkillType with no flight) pulses
    /// around the player with its own area damage. A gem with neither flight nor area (Arc, a curse) stays out, and so
    /// does a gem that cannot link at least five of the run's supports: the links-first offer needs them.
    /// </summary>
    internal static IReadOnlyList<SurvivorsSkill> SurvivorsCatalogFor(ClientState conn)
    {
        var catalog = new List<SurvivorsSkill>();
        var supports = SurvivorsSupportsFor(conn);
        bool Linkable(int effect, SurvivorsSupportItem support)
        {
            if (Supports.Of(effect) is not { } target) return false;
            if (Supports.Of(support.Effect) is not { Support: true } sup) return false;
            return GemSupports.Compatible(sup, target.Types.ToHashSet(), target.MinionTypes, weapon: null);
        }
        foreach (string path in SurvivorsGemPaths(conn))
        {
            if (ItemsFor(conn).Find(path) is not { } baseType || ItemsFor(conn).Gem(baseType) is not { } gem) continue;
            // A placeholder row ("[UNUSED] Coiling Assault") is not a skill a player can be offered.
            if (baseType.Name.Contains("[UNUSED]", StringComparison.OrdinalIgnoreCase)) continue;
            if (gem.IsSupport || gem.Variants.Count == 0) continue;
            var variant = gem.Variant(0);
            // Heralds have no flight: the run casts them as an aura pulse around the player. Any other gem with a
            // Projectiles row can fly here, even when its ActiveSkillTypes do not carry the Projectile flag (Storm Burst).
            bool herald = baseType.Name.StartsWith("Herald of", StringComparison.OrdinalIgnoreCase);
            // An area gem without a flight and without a melee swing: its ActiveSkillTypes carry the Area flag, so the
            // run casts it as a nova pulse. It must also deal a hit: a warcry/curse/buff (Enduring Cry, Temporal
            // Chains) has no damage to pulse and too few supports to link. Melee area skills (Cyclone, Ground Slam,
            // Cleave) are attacks and stay out too.
            bool attack = SkillsFor(conn).IsAttack(variant.Effect);
            bool area = !herald && !attack && Skills.ProjectileFor(variant.Effect) is null
                && Supports.Of(variant.Effect)?.Types.Contains(7) == true
                && !SkillsFor(conn).HitDamage(variant.Effect, 1).IsZero;
            if (!herald && !area && Skills.ProjectileFor(variant.Effect) is null) continue;
            // The links-first offer needs the five supports a skill can take; a gem with fewer (Icefire) is left out.
            if (!herald && supports.Count(support => Linkable(variant.Effect, support)) < SurvivorsRules.MaxSupportsPerSkill)
                continue;
            string metadata = Skills.ProjectileFor(variant.Effect)?.Id ?? "Metadata/Projectiles/Fireball";
            catalog.Add(new SurvivorsSkill(path, baseType.Name, SurvivorsDamageType(baseType.Name), variant.Effect, variant.Effect,
                metadata, herald ? SurvivorsSkillKind.Herald : area ? SurvivorsSkillKind.Area : SurvivorsSkillKind.Projectile)
            {
                CastTimeMs = SurvivorsCastTimeMs(conn, variant.Effect),
                Attack = SkillsFor(conn).IsAttack(variant.Effect),
            });
        }
        return catalog;
    }

    // A gem's own cast time (GrantedEffects CastTime in EffectStats) is the run's interval base; an attack gem's
    // CastTime is its weapon swing time at 1.0 s here, since the run has no weapon to read a speed from.
    internal static int SurvivorsCastTimeMs(ClientState conn, int effect)
    {
        foreach (var (key, value) in SkillsFor(conn).EffectStats(effect, 1))
            if (Stats.Name(key) == "base_spell_cast_time_ms" && value > 0) return Math.Clamp(value, 200, 3000);
        return 1000;
    }

    /// <summary>
    /// The mode's damage anchor: the catalogue's median rank-1 spell hit lands for the area's median monster life.
    /// Each gem keeps its own base damage, its own gem-level curve (rank = gem level) and its own supports, so the run
    /// stays playable in every area without flattening the gems into one number.
    /// </summary>
    internal static double SurvivorsDamageScale(ClientState conn, IReadOnlyList<SurvivorsSkill> catalog, SurvivorsRoster roster)
    {
        List<double> hits = [.. catalog.Where(s => s.Kind == SurvivorsSkillKind.Projectile && !s.Attack)
            .Select(s => SkillsFor(conn).HitDamage(s.Effect, 1).AverageTotal).Where(h => h > 0).Order()];
        double median = hits.Count == 0 ? 0 : hits[hits.Count / 2];
        return median > 0 ? roster.WeaponDamage / median : 1;
    }

    /// <summary>The supports a level-up may offer: eight support gems, at least five linkable to every skill.</summary>
    internal static IReadOnlyList<SurvivorsSupportItem> SurvivorsSupportsFor(ClientState conn)
    {
        (string Path, string Name, SurvivorsSupport Support)[] candidates =
        [
            ("Metadata/Items/Gems/SupportGemAddedFireDamage", "Added Fire Damage Support", SurvivorsSupport.AddedFire),
            ("Metadata/Items/Gems/SupportGemAddedColdDamage", "Added Cold Damage Support", SurvivorsSupport.AddedCold),
            ("Metadata/Items/Gems/SupportGemAddedLightningDamage", "Added Lightning Damage Support", SurvivorsSupport.AddedLightning),
            ("Metadata/Items/Gems/SupportGemFasterCast", "Faster Casting Support", SurvivorsSupport.FasterCasting),
            ("Metadata/Items/Gems/SupportGemIncreasedAreaOfEffect", "Increased Area of Effect Support", SurvivorsSupport.Area),
            ("Metadata/Items/Gems/SupportGemIncreasedCriticalStrikes", "Increased Critical Strikes Support", SurvivorsSupport.Critical),
            ("Metadata/Items/Gems/SupportGemFasterProjectiles", "Faster Projectiles Support", SurvivorsSupport.FasterProjectiles),
            ("Metadata/Items/Gems/SupportGemLifeLeech", "Life Leech Support", SurvivorsSupport.LifeLeech),
        ];
        return [.. candidates.Where(c => ItemsFor(conn).Find(c.Path) is not null)
            .Select(c => new SurvivorsSupportItem(c.Path, c.Name, c.Support, SurvivorsGemEffect(conn, c.Path)))];
    }

    internal static int SurvivorsGemEffect(ClientState conn, string path) =>
        ItemsFor(conn).Find(path) is { } type && ItemsFor(conn).Gem(type) is { Variants.Count: > 0 } gem
            ? gem.Variant(0).Effect : -1;

    /// <summary>
    /// Whether a support can support a run skill, by the same ActiveSkillType rules the game's own gem links use
    /// (<see cref="GemSupports.Compatible"/>). A herald takes none: the real gem is a buff (its damage is the buff's
    /// burn/shatter, not a hit), so these damage supports have nothing to modify; the run's aura pulse is the mode's
    /// own effect, not the gem's hit.
    /// </summary>
    internal static bool SurvivorsCanSupport(ClientState conn, int skillIndex, int supportIndex)
    {
        if (StateOf(conn).SurvivorsCatalog is not { } catalog || StateOf(conn).SurvivorsSupports is not { } supports
            || skillIndex < 0 || skillIndex >= catalog.Count || supportIndex < 0 || supportIndex >= supports.Count)
            return false;
        var skill = catalog[skillIndex];
        if (skill.Kind == SurvivorsSkillKind.Herald) return false;
        if (Supports.Of(skill.Effect) is not { } target) return false;
        if (Supports.Of(supports[supportIndex].Effect) is not { Support: true } support) return false;
        return GemSupports.Compatible(support, target.Types.ToHashSet(), target.MinionTypes, weapon: null);
    }

    /// <summary>The offer pairs a support with a learned skill only when the game's type rules allow the link.</summary>
    internal static Func<int, int, bool> SurvivorsLinkRule(ClientState conn) => (skill, support) =>
        SurvivorsCanSupport(conn, skill, support);

    // BaseItemTypes rows for skill gems, read once; a table without the key yields an empty catalogue.
    internal static IReadOnlyList<string> SurvivorsGemPaths(ClientState conn) =>
        [.. ItemsFor(conn).All.Where(t => t.Supported && t.Id.Contains("SkillGem", StringComparison.Ordinal)).Select(t => t.Id)];

    internal static DamageType SurvivorsDamageType(string name) => name switch
    {
        var n when n.Contains("Fire", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Flame", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Burn", StringComparison.OrdinalIgnoreCase) => DamageType.Fire,
        var n when n.Contains("Cold", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Ice", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Frost", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Freez", StringComparison.OrdinalIgnoreCase) => DamageType.Cold,
        var n when n.Contains("Lightning", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Spark", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Arc", StringComparison.OrdinalIgnoreCase) => DamageType.Lightning,
        var n when n.Contains("Chaos", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Poison", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Toxic", StringComparison.OrdinalIgnoreCase) => DamageType.Chaos,
        _ => DamageType.Physical,
    };

    // Gems whose cast aims at the toughest target instead of the nearest one (Azeroth Survivors: pyroblast, aimed
    // shot, exorcism and starfire).
    internal static bool SurvivorsAimsToughest(SurvivorsSkill skill) =>
        skill.Name.Contains("Pyroblast", StringComparison.OrdinalIgnoreCase)
        || skill.Name.Contains("Aimed Shot", StringComparison.OrdinalIgnoreCase)
        || skill.Name.Contains("Exorcism", StringComparison.OrdinalIgnoreCase)
        || skill.Name.Contains("Starfire", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One run skill's cast with the gem's own numbers: base damage, damage effectiveness, conversion, critical strike
    /// chance, cast time, projectile count, pierce/chain/fork/split, area, duration and ailment chances all come from the
    /// same game-data rows Path of Building reads, and each linked support contributes its gem's real stats. The hit is
    /// resolved by the Gateway's PoB damage port (<see cref="PlayerHitDamage"/>) and the flight by the shared projectile
    /// runtime, so the run follows the gem rather than one flat "base damage" number.
    /// Returns the interval to the next cast, 0 when nothing fired.
    /// </summary>
    internal static async Task<double> SurvivorsCast(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SurvivorsRun run, SurvivorsRoster roster, SurvivorsSkill skill, int rank, int skillIndex)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership || !ReferenceEquals(ownership.Run, run)) return 0;
        if (SurvivorsComputeCast(conn, run, skill, rank, skillIndex, roster) is not { } cast) return 0;
        (int X, int Y) origin;
        List<(int X, int Y)> aims = [];
        List<(uint Id, double Distance, uint Life, int X, int Y)> inReach = [];
        using (ProjectileMutation(conn))
        {
            double world = ProjectileWorldTime(conn);
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Paused) return 0;
            var here = conn.Walk.Position(world);

            foreach (var (id, m) in conn.Monsters)
            {
                if (m.Dead || m.Life == 0 || !m.Hostile) continue;
                var at = m.Walk.Position(world);
                double distance = Dist(here, at);
                if (distance > SurvivorsRules.Range) continue;
                inReach.Add((id, distance, m.Life, at.X, at.Y));
            }
            if (inReach.Count == 0) return 0;
            origin = here;
        }
        // A pulse skill (a herald's aura or an area gem's nova): every hostile around the player takes the hit, and the
        // client plays the gem's cast.
        if (cast.Herald)
        {
            int struck = 0;
            // The 0x1af code selects the elemental hit particles; the herald's pulse is its gem's damage type.
            byte hitCode = skill.Type switch
            {
                DamageType.Cold => 1, DamageType.Fire => 2, DamageType.Lightning => 3, DamageType.Chaos => 4, _ => 0,
            };
            // Each herald's pulse plays the game's own hit effect at up to three struck targets (the wave still damages
            // every target): Ash's fire explosion, Ice's shatter, Thunder's bolt from above, Agony's poison splash,
            // Purity's slam. The effect object is removed when its clip ends.
            var impact = SurvivorsHeraldImpactFor(conn, skill);
            int strikes = 0;
            foreach (var (id, distance, _, tx, ty) in inReach)
            {
                if (distance > cast.HeraldRadius) continue;
                MonsterHit? hit = null;
                SpawnedMonster? target = null;
                lock (conn.Monsters)
                    if (ownership.Current && conn.Monsters.TryGetValue(id, out target) && !target.Dead && target.Life > 0)
                        hit = ApplyMonsterHitLocked(conn, target, cast.HeraldDamage);
                if (hit is not { } applied) continue;
                struck++;
                await NotifyMonsterHit(remote, stream, ciphers, conn, id, target!, applied, cast.HeraldDamage, skill.Name, log: false, melee: true, hitCode: hitCode);
                if (!ownership.Current) return 0;
                if (impact is { } art && strikes++ < 3)
                    await SurvivorsHeraldEffect(remote, stream, ciphers, conn, tx, ty, art.Ao, art.Ms, skill.Name);
            }
            if (struck == 0) return 0;
            await SurvivorsPlayCastAnimation(remote, stream, ciphers, conn, skill, cast.Interval);
            if (!ownership.Current) return 0;
            ushort pulseId = SurvivorsSkillId(skillIndex);
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, pulseId, origin.X, origin.Y),
                $"0x01ce survivors pulse 0x{pulseId:x4} {skill.Name} around ({origin.X}, {origin.Y})", still: () => ownership.Current);
            if (!ownership.Current) return 0;
            await SendSurvivorsCooldown(remote, stream, ciphers, conn, skillIndex, rank, [origin], cast.Interval);
            return cast.Interval;
        }
        var profile = cast.Profile!;
        var shot = cast.Shot! with { Owner = ownership, Valid = () => ownership.Current };
        // The pick is the nearest target, or the toughest for a few named gems, up to the gem's own projectile count.
        var pick = cast.Toughest
            ? inReach.OrderByDescending(t => t.Life).ThenBy(t => t.Distance)
            : inReach.OrderBy(t => t.Distance);
        aims = [.. pick.Take(Math.Min(cast.Count, inReach.Count)).Select(t => (t.X, t.Y))];
        if (SurvivorsTrace)
            Console.WriteLine($"[gateway] {remote} survivors cast {skill.Name} rank {rank}: {aims.Count} aim(s) of {cast.Count}, " +
                $"pierce {profile.Pierce}, chain {profile.Chain}, fork {profile.Fork}, radius {profile.Radius:0.#}, " +
                $"speed {profile.Speed:0.#}, hit {shot.Hit}, crit {shot.CriticalChance / 100.0:0.##}%, " +
                $"leech {shot.Leech.Life.MinTotal:0.#}, interval {cast.Interval:0.00} s");
        await SurvivorsPlayCastAnimation(remote, stream, ciphers, conn, skill, cast.Interval);
        if (!ownership.Current) return 0;
        var owner = new ProjectileActorKey(PlayerId, conn.ProjectileEpoch);
        var runtime = ProjectilesFor(remote, stream, ciphers, conn);
        long now = ProjectileRuntime.Time(conn.Clock.Elapsed.TotalSeconds);
        foreach (var aim in aims)
        {
            var use = new ProjectileUse(profile, owner, owner, 1, ProjectileSession.Vector(origin), ProjectileSession.Vector(aim))
            { Payload = shot, ReturnAnchor = owner };
            runtime.Schedule(now, () => ownership.Current, () => use);
        }
        await SendSurvivorsCooldown(remote, stream, ciphers, conn, skillIndex, rank, aims, cast.Interval);
        return cast.Interval;
    }

    /// <summary>
    /// Plays a run cast's character animation with 0x1b2 kind 0: a rig clip change, not the 0x1d1 Animation.dat route,
    /// which cancels the player's action and locks input for its duration (11-skills.md "Player hit recovery"). The
    /// clip is the class/weapon stance's own (spell_projectile_wand, attack1_bow, ...), stretched over the cast
    /// interval, so the character visibly casts while the run keeps moving the player.
    /// </summary>
    internal static async Task SurvivorsPlayCastAnimation(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsSkill skill, double interval)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership) return;
        // A cast clip replaces the rig's walk clip while it plays, which reads as the character strafing sideways.
        // Play it only while the player stands still: the skill's own projectile, pulse and hit art still show.
        bool moving;
        lock (conn.Monsters) moving = conn.Walk.Moving(conn.Clock.Elapsed.TotalSeconds);
        if (moving) return;
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO")
            ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
        CastAnimationKind kind = SurvivorsCastKind(skill);
        if (Animations.CastClip(ao, WeaponProfileFor(conn, activeSet: true), kind) is not { } clip) return;
        float speed = interval > 0 && clip.LengthMs > 0
            ? (float)Math.Clamp(clip.LengthMs / 1000.0 / interval, 0.1, 10) : 1;
        await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(PlayerId, 0, clip.Index, speed),
            $"0x01b2 survivors cast {clip.Name} ({kind}) for {skill.Name}: index {clip.Index}, " +
            $"{clip.LengthMs} ms over {interval:0.00} s (speed {speed:0.##})", still: () => ownership.Current);
    }

    /// <summary>
    /// A herald pulse's hit effect: a permanent effect object at the target cell playing the herald's own <c>.ao</c>
    /// (Ash's <c>flameherald_explode</c>, Ice's <c>iceherald_explode</c>, Thunder's <c>lightning_strike_herald</c>,
    /// Agony's <c>mortarimpact</c>, Purity's <c>slam_impact</c>), removed when its clip ends. The same art the real
    /// client plays for that herald's hit.
    /// </summary>
    internal static async Task SurvivorsHeraldEffect(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, int x, int y, string ao, int ms, string label)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership) return;
        int epoch = conn.ProjectileEpoch;
        uint id;
        lock (conn.Monsters)
        {
            if (!ownership.Current) return;
            id = conn.NextObjectId++;
            StateOf(conn).Effects.Add(id);
        }
        await Send(remote, stream, ciphers,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(x, y, ao).Blob()),
            $"0x02b9 survivors herald {label} effect 0x{id:x} {ao} at ({x}, {y})", still: () => ownership.Current);
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null, still: () => ownership.Current);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ms, ownership.Token); }
            catch (OperationCanceledException) when (ownership.Token.IsCancellationRequested) { }
            if (conn.ProjectileActive(epoch))
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
            lock (conn.Monsters) StateOf(conn).Effects.Remove(id);
        });
    }

    /// <summary>The stance clip family a run skill's cast plays: a herald buff, an area spell's nova, an attack swing or
    /// a spell projectile. The gem's own ActiveSkillTypes decide between projectile and area, so Fireball (both) is a
    /// projectile cast, while Storm Burst (area without the projectile flag) is an area cast.</summary>
    internal static CastAnimationKind SurvivorsCastKind(SurvivorsSkill skill) =>
        skill.Kind == SurvivorsSkillKind.Herald ? CastAnimationKind.Buff
        : skill.Kind == SurvivorsSkillKind.Area ? CastAnimationKind.Area
        : skill.Attack ? CastAnimationKind.Attack
        : Supports.Of(skill.Effect)?.Types.Contains(2) == true ? CastAnimationKind.Projectile
        : Supports.Of(skill.Effect)?.Types.Contains(7) == true ? CastAnimationKind.Area
        : CastAnimationKind.Projectile;

    /// <summary>One cast's computed numbers, before the live part (positions, aims, packets) needs a monster.</summary>
    internal sealed record SurvivorsComputedCast(double Interval, SurvivorsSkill Skill, int Rank, int Count,
        ProjectileSkillDefinition? Definition, ProjectileProfile? Profile, ProjectileOffense? Shot, bool Toughest,
        bool Herald, int HeraldDamage, double HeraldRadius, DamagePacket? Raw = null);

    /// <summary>
    /// The pure part of a run cast: the gem's stats, its supports' stats, the character's final stats, the interval, the
    /// projectile profile and the resolved hit. It reads no position and sends nothing, so the support matrix can be
    /// checked for every gem and support without a live area.
    /// </summary>
    internal static SurvivorsComputedCast? SurvivorsComputeCast(ClientState conn, SurvivorsRun run, SurvivorsSkill skill,
        int rank, int skillIndex, SurvivorsRoster roster)
    {
        var supports = StateOf(conn).SurvivorsSupports ?? [];
        var own = SurvivorsStatsByName(conn, skill.Effect, rank);
        var skillStats = SurvivorsSkillAndSupportStats(conn, run, skill, skillIndex, rank, supports, own);
        var playerKeys = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        var playerStats = SurvivorsPlayerStats(playerKeys);
        // The panel's skill_area_of_effect_+% already counts the player's base area (PanelStats); keeping the base key as
        // well would count it twice in SkillArea.Increased.
        playerStats.Remove(SkillArea.BaseIncreased);
        var merged = new Dictionary<string, int>(playerStats, StringComparer.Ordinal);
        foreach (var (name, value) in skillStats) merged[name] = merged.GetValueOrDefault(name) + value;
        int Get(string name) => merged.GetValueOrDefault(name);
        bool spell = !skill.Attack;
        // An attack gem swings the weapon the character actually wears (a unique weapon's local damage, attack speed and
        // type now matter); with no weapon it falls back to the mode's nominal weapon and the gem's cast time.
        var weapon = WeaponFor(conn);
        bool armedAttack = skill.Attack && weapon.LocalSources is not null;
        double baseSeconds = armedAttack ? weapon.AttackSeconds(playerKeys) : skill.CastTimeMs / 1000.0;
        int castSpeed = armedAttack ? 0 : Get("base_cast_speed_+%") + Get("cast_speed_+%") + (spell ? Get("spell_cast_speed_+%") : 0);
        double interval = SurvivorsFormulas.CastInterval(baseSeconds, rank, castSpeed);
        if (skill.Kind == SurvivorsSkillKind.Herald)
        {
            double radius = SurvivorsFormulas.AreaRadius(SurvivorsHeraldRadius * SurvivorsRules.SkillRadius(rank),
                run.AreaIncreasedPercent + playerStats.GetValueOrDefault("skill_area_of_effect_+%"));
            int damage = (int)Math.Round(SurvivorsRules.SkillDamage(roster.WeaponDamage, rank) * run.DamageMultiplier
                * (1 + playerStats.GetValueOrDefault("damage_+%") / 100.0));
            return new(interval, skill, rank, 0, null, null, null, false, true, damage, radius);
        }
        // The gem's own hit at rank 1 (a spell's base, or the equipped/nominal weapon for an attack), scaled by the
        // run's rank curve. Shared by the area pulse and the projectile path.
        DamagePacket RawDamage()
        {
            int effectiveness = SkillsFor(conn).DamageEffectiveness(skill.Effect, rank);
            DamagePacket own;
            if (spell)
            {
                // The gem's first-level row is its identity; the run's rank curve (the reference's +30% a rank) is applied
                // once, below, to the whole hit, so the power curve is the reference's and the gem stays itself.
                own = SkillsFor(conn).HitDamage(skill.Effect, 1);
                own = DamageStats.RawAdded(own, Get, spell: true, effectiveness / 10_000.0);
                own = SurvivorsGlobalAdded(own, Get, effectiveness);
                own = own.Scale(roster.SkillScale);
            }
            else if (armedAttack)
            {
                // The equipped weapon's own hit (local damage, local added, attack added) at the gem's effectiveness.
                own = SupportedAttackDamage.Raw(weapon, Get);
                own = SurvivorsGlobalAdded(own, Get, 10_000);
                own = SpellDamage.WithEffectiveness(own, effectiveness);
            }
            else
            {
                bool unarmed = Get("flask_throw_charges_used_per_projectile") != 0;
                var nominal = unarmed ? DamagePacket.Empty
                    : DamagePacket.Of(DamageType.Physical, roster.WeaponDamage * 0.45, roster.WeaponDamage * 0.9);
                var added = DamageStats.RawAdded(DamagePacket.Empty, Get, spell: false);
                added = SurvivorsGlobalAdded(added, Get, 10_000);
                added = SpellDamage.WithEffectiveness(added, effectiveness);
                own = SpellDamage.WithEffectiveness(nominal, effectiveness)
                    .Add(added.Scale(roster.SkillScale));
            }
            // The reference's power curve: every run skill deals 30% more a rank above the first (3.1× at rank 8), on top
            // of the gem's own effectiveness, supports and the character's stats.
            return own.Scale(SurvivorsRules.SkillDamageMultiplier(rank));
        }
        var mergedKeys = SurvivorsKeyStats(merged);
        var skillKeys = SurvivorsKeyStats(skillStats);
        var conversion = DamageStats.Conversion(mergedKeys, Stats, skill.Attack);
        List<(string Name, int Value)> supportMods = [];
        foreach (int index in run.SupportsOf(skillIndex))
            if (index >= 0 && index < supports.Count && supports[index].Effect >= 0)
                foreach (var (key, value) in SkillsFor(conn).EffectStats(supports[index].Effect, rank))
                    if (Stats.Name(key) is { Length: > 0 } name) supportMods.Add((name, value));
        // An area gem has no flight: its cast is a pulse around the player with the gem's own area hit.
        if (skill.Kind == SurvivorsSkillKind.Area)
        {
            var pulseRaw = RawDamage();
            if (pulseRaw.IsZero) return null;
            var pulse = PlayerHitDamage.Resolve(pulseRaw, Get, conversion, spell, area: true, projectile: false,
                supportModifiers: supportMods);
            if (pulse.IsZero) return null;
            double pulseRadius = SurvivorsFormulas.AreaRadius(SurvivorsHeraldRadius * SurvivorsRules.SkillRadius(rank),
                run.AreaIncreasedPercent + playerStats.GetValueOrDefault("skill_area_of_effect_+%"));
            return new(interval, skill, rank, 0, null, null, null, false, true,
                (int)Math.Max(1, pulse.AverageTotal), pulseRadius);
        }
        if (SurvivorsDefinition(skill) is not { } definition) return null;
        int baseSpeed = SkillsFor(conn).Projectile(definition.Metadata)?.Speed ?? 1000;
        // Riptide's Projectiles row is a 10-unit placeholder: a projectile that slow cannot cross a cell.
        if (baseSpeed < 100) baseSpeed = 800;
        double speed = ProjectileSpeed.CellsPerSecond(baseSpeed, Get, Get,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        if (!double.IsFinite(speed) || speed <= 0) return null;
        var profile = definition.Parameters.Resolve(Get, speed, ArrowsFor(conn, skill.Effect));
        profile = SurvivorsMechanics(skill, Get, profile, spell);
        int count = Math.Clamp(profile.Count, 1, 32);
        // PoB's calcRadius: increased area grows the radius by its square root, never linearly.
        profile = profile with
        {
            Radius = profile.Radius * Math.Sqrt(SkillArea.Modifier(Get, spell)),
            Count = 1,
        };
        var raw = RawDamage();
        if (raw.IsZero) return null;
        var hitDamage = PlayerHitDamage.Resolve(raw, Get, conversion, spell, area: false, projectile: true,
            supportModifiers: supportMods);
        var areaHit = PlayerHitDamage.Resolve(raw, Get, conversion, spell, area: true, projectile: true,
            supportModifiers: supportMods);
        if (hitDamage.IsZero && areaHit.IsZero) return null;

        // The character's penetration, resistance modifiers and leech join the run's hit exactly as they join a gem's.
        var local = spell ? null : weapon.LocalSources;
        int baseCrit = spell ? SkillsFor(conn).SpellCritChance(skill.Effect, rank) ?? 0
            : armedAttack ? WeaponProfileFor(conn, activeSet: true).Critical : 500;
        int crit = spell
            ? SpellCriticalStrike.Chance(baseCrit, _ => 0, Get)
            : AddedBaseCriticalStrike.Chance(baseCrit, Get("critical_strike_chance_+%"), spell: false, _ => 0, Get);
        int? secondForm = null;
        if (spell && Get("ice_spear_second_form_critical_strike_chance_+%") is > 0 and var secondCrit)
            secondForm = AddedBaseCriticalStrike.Chance(baseCrit,
                Get("critical_strike_chance_+%") + secondCrit, spell: true, _ => 0, Get);
        int multiplier = spell
            ? (int)Math.Clamp((long)conn.Pools.SpellCriticalStrikeMultiplier
                + skillStats.GetValueOrDefault("base_critical_strike_multiplier_+"), int.MinValue, int.MaxValue)
            : AttackCriticalMultiplier(conn, playerKeys, skill.Effect, skillKeys);
        string hand = spell ? "spell" : "main_hand";
        var ailmentRows = AilmentPanelStats.Compute(new(hand, Spell: spell, Projectile: true), raw, conversion, Get);
        var shot = new ProjectileOffense(hitDamage, spell, Accuracy: spell ? int.MaxValue : conn.Pools.Accuracy,
            CriticalChance: crit, CriticalMultiplier: multiplier, Label: skill.Name)
        {
            AreaHit = areaHit,
            // The survivor projectile is the server's own (ClientPredicted=false), so it carries its flight and impact
            // clips; the client's instant action only starts the bar's cooldown and the cast animation.
            Effect = skill.Effect, Level = rank, ClientPredicted = false, HitPacket = true,
            SkillStats = skillKeys,
            Penetration = Penetration.Of(playerKeys, Stats, attack: !spell, skillKeys, local),
            ResistanceMods = HitResistMods(playerKeys, skillKeys),
            AccuracyProfile = spell ? null : AttackAccuracyProfile.From(conn.Pools, playerKeys, Stats, skillKeys, local),
            AlwaysHits = Get("global_always_hit") != 0,
            Ignite = MonsterIgnites.Settings.From(ailmentRows, hand, Get),
            Leech = LeechSources.From(Get, skill.Attack),
            SecondFormCriticalChance = secondForm,
        };
        // The client draws the projectile's own flight and impact clips (projectile-clips.json): an impact clip is the
        // skill's hit animation. Fireball and Kinetic Blast also carry the area effect their data names.
        if (MonsterProjectilePresentation(SkillsFor(conn).Clips(definition.Metadata),
                SurvivorsRules.Range / Math.Max(0.01, speed)) is { } presentation)
        {
            shot = shot with { Presentation = presentation };
            if (SurvivorsTrace)
                Console.WriteLine($"[gateway] survivors {skill.Name}: projectile {definition.Metadata} " +
                    $"flight clip {presentation.FlightClip?.ToString() ?? "default"}, impact clip {presentation.ImpactClip?.ToString() ?? "none"}");
        }
        if (GatewayProjectileSkillProfiles.IsFireball(definition.Metadata))
        {
            string ao = Art.AnimatedInPlaceOf(DefaultProjectileImpactAo, 0);
            shot = shot with { Presentation = (shot.Presentation ?? new ProjectilePresentation()) with
            {
                EffectLifetime = ProjectileRuntime.Time(1.5),
                EventEffect = value => value.Kind == ProjectileEventKind.Area ? ao : null,
            } };
        }
        else if (Skills.EffectId(skill.Effect) is { } effectId && GatewayProjectileSkillProfiles.IsKineticBlast(effectId))
            shot = shot with { AreaVisual = Art.AnimatedInPlaceOf(KineticBlastExplosionAo, 0),
                Presentation = (shot.Presentation ?? new ProjectilePresentation()) with
                { EffectLifetime = ProjectileRuntime.Time(.35) } };
        return new(interval, skill, rank, count, definition, profile, shot, SurvivorsAimsToughest(skill), false, 0, 0, raw);
    }

    /// <summary>
    /// A run skill's executable projectile: the Gateway's own definition when the skill has one, else the generic
    /// profile of its Projectiles row. Storm Burst has a flight row but no Projectile skill type, so the shared
    /// resolver refuses it; the run can still draw and simulate its flight.
    /// </summary>
    internal static ProjectileSkillDefinition? SurvivorsDefinition(SurvivorsSkill skill)
    {
        if (ResolveProjectileDefinition(skill.Recipe) is { } definition) return definition;
        if (Skills.ProjectileFor(skill.Effect) is not { } projectile) return null;
        return new ProjectileSkillDefinition(projectile.Id, new ProjectileSkillParameters((get, speed, arrows) =>
            GatewayProjectileDefaults.Resolve(projectile.Id, get, speed, attack: false, arrows)));
    }

    /// <summary>A gem's own stats by name at a gem level (the run's rank is the gem level).</summary>
    internal static Dictionary<string, int> SurvivorsStatsByName(ClientState conn, int effect, int level)
    {
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        if (effect < 0) return stats;
        foreach (var (key, value) in SkillsFor(conn).EffectStats(effect, level))
            if (Stats.Name(key) is { Length: > 0 } name) stats[name] = stats.GetValueOrDefault(name) + value;
        return stats;
    }

    /// <summary>The character's final stats by name (passives, equipped uniques, flasks): the player side of Path of
    /// Building's damage formula, which a run skill now reads beside its own gem and supports.</summary>
    internal static Dictionary<string, int> SurvivorsPlayerStats(IReadOnlyDictionary<uint, int> player)
    {
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in player)
            if (Stats.Name(key) is { Length: > 0 } name) stats[name] = stats.GetValueOrDefault(name) + value;
        return stats;
    }

    /// <summary>
    /// The skill side of a run cast: its own gem, each linked support at the skill's rank, and the run's blessings as
    /// PoB-style increased modifiers. The character's own final stats are layered over this in
    /// <see cref="SurvivorsCast"/>, so the profile resolver, the damage resolver and the ailment code read one
    /// dictionary and a support cannot mean one thing to the flight and another to the hit.
    /// </summary>
    internal static Dictionary<string, int> SurvivorsSkillAndSupportStats(ClientState conn, SurvivorsRun run, SurvivorsSkill skill,
        int skillIndex, int rank, IReadOnlyList<SurvivorsSupportItem> supports, Dictionary<string, int> own)
    {
        var stats = new Dictionary<string, int>(own, StringComparer.Ordinal);
        foreach (int index in run.SupportsOf(skillIndex))
        {
            if (index < 0 || index >= supports.Count || supports[index].Effect < 0) continue;
            foreach (var (key, value) in SkillsFor(conn).EffectStats(supports[index].Effect, rank))
                if (Stats.Name(key) is { Length: > 0 } name) stats[name] = stats.GetValueOrDefault(name) + value;
        }
        void Add(string name, int value) { if (value != 0) stats[name] = stats.GetValueOrDefault(name) + value; }
        Add("damage_+%", run.DamageIncreasedPercent);
        Add("cast_speed_+%", run.CastSpeedIncreasedPercent);
        Add("base_skill_area_of_effect_+%", run.AreaIncreasedPercent);
        Add("additional_base_critical_strike_chance", run.CriticalChanceAddedPermyriad);
        Add("life_leech_from_any_damage_permyriad", run.LeechPermyriad);
        Add("base_projectile_speed_+%", run.ProjectileSpeedIncreasedPercent);
        // Gems whose signature mechanic lives in a stat the generic profile does not read.
        if (stats.GetValueOrDefault("lightning_arrow_maximum_number_of_extra_targets") is > 0 and var extraTargets)
            stats["number_of_chains"] = Math.Max(stats.GetValueOrDefault("number_of_chains"), extraTargets);
        if (stats.GetValueOrDefault("vaal_lightning_arrow_number_of_redirects") is > 0 and var redirects)
            stats["number_of_chains"] = Math.Max(stats.GetValueOrDefault("number_of_chains"), redirects);
        if (stats.GetValueOrDefault("base_number_of_projectiles_in_spiral_nova") is > 0 and var spiral)
            stats["base_number_of_projectiles"] = Math.Min(12, Math.Max(1, spiral / 8));
        return stats;
    }

    internal static Dictionary<uint, int> SurvivorsKeyStats(Dictionary<string, int> stats)
    {
        var keys = new Dictionary<uint, int>();
        foreach (var (name, value) in stats)
            if (Stats.Key(name) is uint key) keys[key] = keys.GetValueOrDefault(key) + value;
        return keys;
    }

    internal static string SurvivorsDamageSlug(DamageType type) => type switch
    {
        DamageType.Physical => "physical", DamageType.Lightning => "lightning", DamageType.Cold => "cold",
        DamageType.Fire => "fire", _ => "chaos",
    };

    /// <summary>
    /// The "global" added damage ranges (Added Cold/Lightning Support, a unique's "Adds X to Y Damage"): PoB's
    /// ModParser adds them to spells and attacks alike, scaled by the skill's damage effectiveness like any other
    /// added range.
    /// </summary>
    internal static DamagePacket SurvivorsGlobalAdded(DamagePacket raw, Func<string, int> get, int effectivenessPermyriad)
    {
        double effectiveness = Math.Max(0, effectivenessPermyriad / 10_000.0);
        foreach (var type in new[] { DamageType.Physical, DamageType.Lightning, DamageType.Cold, DamageType.Fire, DamageType.Chaos })
        {
            string slug = SurvivorsDamageSlug(type);
            var added = new DamageRange(get($"global_minimum_added_{slug}_damage"), get($"global_maximum_added_{slug}_damage"));
            if (!added.IsZero) raw = raw.Add(type, added.Scale(effectiveness));
        }
        return raw;
    }

    /// <summary>
    /// The gem mechanics the shared profile rules do not carry: Ice Spear's second form, Ball Lightning's repeated
    /// area hits along its flight and Storm Burst's jumping orbs. Everything else (Fireball's explosion, Kinetic
    /// Blast's clusters, Spark's hit window, Tornado Shot's arrows) is part of the resolved profile already.
    /// </summary>
    internal static ProjectileProfile SurvivorsMechanics(SurvivorsSkill skill, Func<string, int> get, ProjectileProfile profile, bool spell)
    {
        string? effect = Skills.EffectId(skill.Effect);
        if (effect == "IceSpear")
        {
            double formSpeed = 1 + get("ice_spear_second_form_projectile_speed_+%_final") / 100.0;
            profile = profile with { FormChange = new(profile.Range * 0.35, Math.Max(1, formSpeed), -1) };
        }
        if (effect is "BallLightning")
        {
            int radius = SkillArea.Radius(SkillArea.BaseRadius(effect, get), get, spell);
            profile = profile with { Pulse = new("ball-lightning", radius, ProjectileRuntime.Time(0.2)) };
        }
        if (effect is "StormBurst")
        {
            int radius = SkillArea.Radius(SkillArea.BaseRadius(effect, get), get, spell);
            int ms = Math.Clamp(get("display_storm_burst_jump_time_ms"), 100, 2000);
            profile = profile with { Pulse = new("storm-burst", radius, ProjectileRuntime.Time(ms / 1000.0)) };
        }
        // A gem whose own duration is the projectile's life (Spark, Vaal Spark, Storm Burst, Static Tether).
        if (effect is ("Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark" or "StormBurst" or "StaticTether")
            && get("base_skill_effect_duration") is > 0 and var duration)
            profile = profile with { Lifetime = ProjectileRuntime.Time(Math.Clamp(duration / 1000.0, 0.4, 10)) };
        // Spark's signature wander, the client's own seeded legs (SparkLegs): the server flies the same path it draws.
        if (effect is ("Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark"))
        {
            double life = get("base_skill_effect_duration") is > 0 and var ms ? ms / 1000.0 : 2.0;
            int randomDuration = get("spark_random_duration_%");
            int sparkIndex = 0;
            profile = profile with { Legs = _ => new SparkLegs(0, sparkIndex++, PlayerId, life, randomDuration, profile.Speed) };
        }
        return profile;
    }
    /// <summary>
    /// Tells the player the three choices of a level-up through the client's own reward window: the items are real gems
    /// (skills) or support gems (blessings) in inventory 0x600, and a click comes back as C→S 0x88 with the item's id.
    /// </summary>
    /// <summary>
    /// Equips a run's unique item: the first free equipment row its class fits, else the main inventory. The client gets
    /// the inventory change and, for a visible slot, the equipment update; the run also waives the item's requirements
    /// while it lasts (a low-level character can wear a unique it would not normally meet).
    /// </summary>
    /// <summary>Puts a learned Herald's aura buff on the player (S→C 0x1c1), so the client draws it and its icon; the
    /// instances are removed when the run ends.</summary>
    internal static async Task SurvivorsPublishHeralds(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsRun run, IReadOnlyList<SurvivorsSkill> catalog)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership || !ReferenceEquals(ownership.Run, run)) return;
        foreach (var (index, _) in run.Skills)
        {
            if (!ownership.Current) return;
            if (index < 0 || index >= catalog.Count || catalog[index].Kind != SurvivorsSkillKind.Herald) continue;
            if (!StateOf(conn).SurvivorsHeraldSkills.Add(index)) continue;
            if (!HeraldBuffRows.Value.TryGetValue(catalog[index].Effect, out int row) || row is < 0 or > 0xffff) continue;
            ushort skillId = SurvivorsSkillId(index);
            ushort aura = conn.NextBuff++;
            StateOf(conn).SurvivorsHeraldBuffs.Add(aura);
            await Send(remote, stream, ciphers,
                InstanceEntities.AddBuff(PlayerId, aura, (ushort)row, PlayerId, skillId, auraSource: true, displayState: 0),
                $"0x01c1 survivors herald aura {catalog[index].Name} (BuffDefinitions row {row})", still: () => ownership.Current);
            if (!ownership.Current) return;
            if (SkillsFor(conn).ActionHash(catalog[index].Effect) is ushort action)
            {
                ushort icon = conn.NextBuff++;
                StateOf(conn).SurvivorsHeraldBuffs.Add(icon);
                await Send(remote, stream, ciphers,
                    InstanceEntities.AddBuff(PlayerId, icon, (ushort)row, PlayerId, skillId, activeSkill: (skillId, action)),
                    $"0x01c1 survivors herald icon {catalog[index].Name}", still: () => ownership.Current);
            }
        }
    }

    /// <summary>Removes every herald aura buff the run put on the player.</summary>
    internal static async Task SurvivorsClearHeralds(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        ushort[] buffs;
        lock (conn.Monsters)
        {
            buffs = [.. StateOf(conn).SurvivorsHeraldBuffs];
            StateOf(conn).SurvivorsHeraldBuffs.Clear();
            StateOf(conn).SurvivorsHeraldSkills.Clear();
        }
        foreach (ushort buff in buffs)
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, buff), $"0x01c2 survivors herald buff {buff}");
    }

}
