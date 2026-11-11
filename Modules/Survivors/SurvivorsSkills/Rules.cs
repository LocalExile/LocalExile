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

    /// <summary>How far a cast looks for targets: a pulse uses its own aura, a projectile the cast range.</summary>
    internal static double SurvivorsCastReach(bool herald, double radius) => herald ? radius : SurvivorsRules.Range;

    /// <summary>Where a pulse lands: a self-centred nova or Herald on the player, a ground-targeted area spell on the
    /// monster it was aimed at.</summary>
    internal static (int X, int Y) SurvivorsPulseCenter(bool selfCentered, (int X, int Y) player, (int X, int Y) target) =>
        selfCentered ? player : target;

    /// <summary>The struck targets that play a pulse's impact art: every one of them (the clip ends before the next
    /// pulse), so a pack does not look as if only the first few were hit.</summary>
    internal static int SurvivorsHeraldEffectCount(int struck) => Math.Max(struck, 0);

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

    /// <summary>
    /// One offer choice as the chat and /survivors status read it. A support names the skill it links to and a rank
    /// names the skill it raises, so the window's gem icons are never ambiguous about which skill they upgrade.
    /// </summary>
    internal static string SurvivorsChoiceLabel(SurvivorsRun run, SurvivorsChoice choice, string name,
        IReadOnlyList<SurvivorsSkill> catalog)
    {
        string target = choice.Target >= 0 && choice.Target < catalog.Count ? catalog[choice.Target].Name : "?";
        return choice.Kind switch
        {
            SurvivorsChoiceKind.NewSkill => $"New skill: {name}",
            SurvivorsChoiceKind.SkillRank => $"{name} -> rank {run.RankOf(choice.Index) + 1}",
            SurvivorsChoiceKind.Support => $"{name} -> {target}",
            SurvivorsChoiceKind.Blessing => $"{name} -> level {run.BlessingLevel((SurvivorsBlessing)choice.Index) + 1}",
            SurvivorsChoiceKind.Item => $"Unique: {name}",
            SurvivorsChoiceKind.Heal => "Healthstone: restores all life",
            _ => name,
        };
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
            // A placeholder row ("[UNUSED] Coiling Assault") or a playtest row is not a skill a player can be offered.
            if (baseType.Name.Contains("[UNUSED]", StringComparison.OrdinalIgnoreCase)) continue;
            if (baseType.Name.Contains("Playtest", StringComparison.OrdinalIgnoreCase)) continue;
            if (gem.IsSupport || gem.Variants.Count == 0) continue;
            var variant = gem.Variant(0);
            // Heralds have no flight: the run casts them as an aura pulse around the player. Any other gem with a
            // Projectiles row can fly here, even when its ActiveSkillTypes do not carry the Projectile flag (Storm Burst).
            bool herald = baseType.Name.StartsWith("Herald of", StringComparison.OrdinalIgnoreCase);
            // A melee attack gem (Elemental Hit, Cyclone, ...) carries a Projectiles row for its bow version and would
            // otherwise pass as a caster: the run is a caster's mode, so melee attacks stay out.
            bool melee = SkillsFor(conn).IsMelee(variant.Effect);
            var projectileRow = Skills.ProjectileFor(variant.Effect);
            bool projectile = projectileRow is not null && !melee;
            // A projectile gem with no Projectiles row has no flight to draw: falling back to a nova around the player
            // would show the wrong effect. It stays out until its row (or a fallback) is known.
            if (!herald && SkillsFor(conn).IsProjectile(variant.Effect) && projectileRow is null) continue;
            // An area gem without a flight and without a melee swing: its ActiveSkillTypes carry the Area flag, so the
            // run casts it as a nova pulse. It must also deal a hit: a warcry/curse/buff (Enduring Cry, Temporal
            // Chains) has no damage to pulse and too few supports to link.
            bool attack = SkillsFor(conn).IsAttack(variant.Effect);
            bool area = !herald && !attack && projectileRow is null
                && Supports.Of(variant.Effect)?.Types.Contains(7) == true
                && !SkillsFor(conn).HitDamage(variant.Effect, 1).IsZero;
            // The run has one presentation for a hit: a flight at the nearest monster or a pulse at the cast's
            // centre. A trap, mine, totem, brand, channel, travel or minion gem is not that; it stays out until the
            // run models the object or movement it places.
            if (!herald && !SurvivorsMechanicsSupported(conn, variant.Effect)) continue;
            if (!herald && !projectile && !area) continue;
            // The links-first offer needs the five supports a skill can take; a gem with fewer (Icefire) is left out.
            if (!herald && supports.Count(support => Linkable(variant.Effect, support)) < SurvivorsRules.MaxSupportsPerSkill)
                continue;
            // Two item rows can resolve to the same visible gem (a Vaal/alternate variant): one card is enough.
            if (catalog.Any(s => s.Name.Equals(baseType.Name, StringComparison.OrdinalIgnoreCase))) continue;
            string metadata = projectileRow?.Id ?? "Metadata/Projectiles/Fireball";
            catalog.Add(new SurvivorsSkill(path, baseType.Name, SurvivorsDamageType(baseType.Name), variant.Effect, variant.Effect,
                metadata, herald ? SurvivorsSkillKind.Herald : area ? SurvivorsSkillKind.Area : SurvivorsSkillKind.Projectile)
            {
                CastTimeMs = SurvivorsCastTimeMs(conn, variant.Effect),
                Attack = SkillsFor(conn).IsAttack(variant.Effect),
                SelfTargeted = SkillsFor(conn).IsSelfTargeted(variant.Effect),
                Nova = SkillsFor(conn).IsNova(variant.Effect),
            });
        }
        return catalog;
    }

    // ActiveSkillTypes the run's flight/pulse presentation does not model. Heralds are exempt: their aura and impact
    // art are run-specific already.
    static readonly string[] SurvivorsUnsupportedSkillTypes =
    [
        "Trap", "Trapped", "Mine", "RemoteMined", "Totem", "SummonsTotem", "Brand", "Channel", "Travel",
        "AppliesCurse", "Warcry", "Minion", "CreatesMinion", "Orb", "Hex", "Retaliation",
        "RequiresOffHandNotWeapon", "DestroysCorpse", "CreatesCorpse", "HasReservation", "Aura",
    ];

    /// <summary>Whether the run can present the gem's use: a flight at one monster or a pulse at the cast's centre.
    /// A trap, mine, totem, brand, channel, travel, minion or orb mechanic has no such presentation yet.</summary>
    internal static bool SurvivorsMechanicsSupported(ClientState conn, int effect)
    {
        foreach (string type in SurvivorsUnsupportedSkillTypes)
            if (SkillsFor(conn).HasType(effect, type)) return false;
        return true;
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

    /// <summary>
    /// The supports a level-up may offer: every normal support gem the data defines, not the original eight. The
    /// game's ActiveSkillType rules still decide each link (<see cref="SurvivorsCanSupport"/>); the catalogue keeps a
    /// skill only when at least five of these supports can link to it. Awakened and exceptional rows are duplicates of
    /// a normal support and stay out, as do the Royale/playtest rows.
    /// </summary>
    // Supports whose mechanic the run does not carry: traps, mines, totems, brands, triggers, curses, minions, item
    // drops, mana/life costs, repeat/intensity, stun/knockback/blind/flee/block, DOT/duration and the new conditional
    // attack mechanics. The catalogue would offer them by the game's type rules, but the cast pipeline reads none of
    // their numbers, so they are held back (the matrix fails if one stops moving a cast).
    static readonly HashSet<string> SurvivorsUnsupportedSupportIds = new(StringComparer.Ordinal)
    {
        "SupportInspiration", "SupportKnockback", "SupportStun", "SupportTrap", "SupportItemQuantity",
        "SupportItemRarity", "SupportSpellTotem", "SupportBlastchainMine", "SupportChanceToFlee", "SupportBlind",
        "SupportSpellEcho", "SupportPowerChargeOnCritical", "SupportHextouch", "SupportCastOnDeath",
        "SupportCastWhenDamageTaken", "SupportCastWhenStunned", "SupportBlockChanceReduction", "SupportHypothermia",
        "SupportVaalSoulHarvesting", "SupportLesserReducedManaCost", "SupportDecay", "SupportImmolate",
        "SupportArcaneSurge", "SupportSummonPhantasm", "SupportEnergyLeech", "SupportUnleash", "SupportHighImpactMine",
        "SupportArchmage", "SupportTrinity", "SupportLifetap", "SupportSpellblade", "SupportDevour", "SupportSacrifice",
        "SupportLivingLightning", "SupportFasterAttacks", "SupportAdditionalAccuracy", "SupportManaLeech",
        "SupportMoreDuration", "SupportLessDuration", "SupportLifeGainOnHit", "SupportDamageOnFullLife",
        "SupportCastOnCriticalStrike", "SupportChanceToBleed", "SupportMomentum", "SupportMirageArcher",
        "SupportImpale", "SupportPrismaticBurst", "SupportLocusMine", "SupportVolatility", "SupportRupture",
        "SupportWindburst",
    };

    internal static IReadOnlyList<SurvivorsSupportItem> SurvivorsSupportsFor(ClientState conn)
    {
        if (StateOf(conn).SurvivorsSupports is { Count: > 0 } cached) return cached;
        var result = new List<SurvivorsSupportItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in SurvivorsSupportPaths(conn))
        {
            if (ItemsFor(conn).Find(path) is not { } type || ItemsFor(conn).Gem(type) is not { IsSupport: true, Variants.Count: > 0 } gem
                || gem.IsAwakened || gem.IsExceptional) continue;
            var variant = gem.Variant(0);
            if (variant.Effect < 0 || !seen.Add(type.Name)) continue;
            string? id = SkillsFor(conn).EffectId(variant.Effect);
            if (id is null || SurvivorsUnsupportedSupportIds.Contains(id)) continue;
            // A support that adds a trap, mine, totem, brand, trigger, curse, minion or aura type places an object or
            // takes a route the run does not carry; its stats alone would pretend the link worked.
            if (SkillsFor(conn).AddedTypes(variant.Effect).Any(SurvivorsUnsupportedSkillTypes.Contains)) continue;
            result.Add(new(path, type.Name, SurvivorsSupportOf(id), variant.Effect, SurvivorsHeraldSupport(id)));
        }
        StateOf(conn).SurvivorsSupports = result;
        return result;
    }

    // The original eight keep their enum (the support matrix verifies the specific number each moves); every other
    // support is Other and is checked by its own gem stats.
    static SurvivorsSupport SurvivorsSupportOf(string? id) => id switch
    {
        "SupportAddedFireDamage" => SurvivorsSupport.AddedFire,
        "SupportAddedColdDamage" => SurvivorsSupport.AddedCold,
        "SupportAddedLightningDamage" => SurvivorsSupport.AddedLightning,
        "SupportFasterCasting" => SurvivorsSupport.FasterCasting,
        "SupportIncreasedAreaOfEffect" => SurvivorsSupport.Area,
        "SupportIncreasedCriticalStrikes" => SurvivorsSupport.Critical,
        "SupportFasterProjectiles" => SurvivorsSupport.FasterProjectiles,
        "SupportLifeLeech" => SurvivorsSupport.LifeLeech,
        _ => SurvivorsSupport.Other,
    };

    // What a Herald pulse can actually use. The real gem is a buff: the game's own type rules reject Added Damage,
    // Faster Casting, critical, leech, projectile, minion, trap/mine/totem and trigger supports, and the run's pulse is
    // a direct area hit, so DOT/ailment, duration, reservation, item and mana supports do nothing either. The five
    // below change the pulse's damage or radius through stats SurvivorsComputeCast reads.
    static bool SurvivorsHeraldSupport(string? id) => id is
        "SupportIncreasedAreaOfEffect" or "SupportConcentratedEffect" or "SupportBrutality" or "SupportVoidManipulation";

    /// <summary>The support gems the run may offer: normal SupportGem rows, without the Royale/playtest/placeholder
    /// duplicates.</summary>
    internal static IReadOnlyList<string> SurvivorsSupportPaths(ClientState conn) =>
        [.. ItemsFor(conn).All.Where(t => t.Supported
            && t.Id.Contains("SupportGem", StringComparison.Ordinal)
            && !t.Id.Contains("Royale", StringComparison.Ordinal)
            && !t.Name.Contains("[UNUSED]", StringComparison.OrdinalIgnoreCase)
            && !t.Name.Contains("Playtest", StringComparison.OrdinalIgnoreCase)
            && !t.Name.StartsWith("WIP", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id)];

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
        // Brutality cuts every non-physical type and Void Manipulation every non-chaos type: the real game allows the
        // link but the skill then deals nothing, which in a run means it stops casting. Never offer that combination.
        string? supportId = SkillsFor(conn).EffectId(supports[supportIndex].Effect);
        if (supportId is "SupportBrutality" && skill.Type != DamageType.Physical) return false;
        if (supportId is "SupportVoidManipulation" && skill.Type != DamageType.Chaos) return false;
        // The real Herald is a buff. Only the pulse supports that move the run's synthetic hit are offered; the rest
        // (added damage, cast speed, projectiles, minions, traps, DOT, duration, item and mana supports) do nothing.
        if (skill.Kind == SurvivorsSkillKind.Herald) return supports[supportIndex].Herald;
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
        // The Heralds are named after their lore, not their damage type; the name scan below would make Ash physical.
        "Herald of Ash" => DamageType.Fire,
        "Herald of Ice" => DamageType.Cold,
        "Herald of Thunder" => DamageType.Lightning,
        "Herald of Agony" => DamageType.Chaos,
        "Herald of Purity" => DamageType.Physical,
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
        // A self-centred pulse (a Herald, a nova) gathers to its own aura, not the projectile cast range: the old
        // 34-unit reach clipped a 48-unit aura. A ground-targeted area spell first finds its target within the cast
        // range, then gathers around that monster: its effect belongs where it lands, not at the player's feet.
        bool selfCentered = cast.Herald && (skill.Kind == SurvivorsSkillKind.Herald || skill.SelfTargeted || skill.Nova);
        double reach = cast.Herald && !selfCentered ? SurvivorsRules.Range : SurvivorsCastReach(cast.Herald, cast.HeraldRadius);
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
                if (distance > reach) continue;
                inReach.Add((id, distance, m.Life, at.X, at.Y));
            }
            if (inReach.Count == 0) return 0;
            if (cast.Herald)
            {
                (int X, int Y) target = here;
                if (!selfCentered)
                {
                    var nearest = inReach.OrderBy(t => t.Distance).First();
                    target = (nearest.X, nearest.Y);
                }
                origin = SurvivorsPulseCenter(selfCentered, here, target);
                if (!selfCentered)
                {
                    inReach.Clear();
                    foreach (var (id, m) in conn.Monsters)
                    {
                        if (m.Dead || m.Life == 0 || !m.Hostile) continue;
                        var at = m.Walk.Position(world);
                        double distance = Dist(origin, at);
                        if (distance > cast.HeraldRadius) continue;
                        inReach.Add((id, distance, m.Life, at.X, at.Y));
                    }
                    if (inReach.Count == 0) return 0;
                }
            }
            else origin = here;
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
            // Each herald's pulse plays the game's own hit effect at every struck target: Ash's fire explosion, Ice's
            // shatter, Thunder's bolt from above, Agony's poison splash, Purity's slam. The effect object is removed
            // when its clip ends, before the next pulse.
            var impact = SurvivorsHeraldImpactFor(conn, skill);
            int effectBudget = SurvivorsHeraldEffectCount(inReach.Count);
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
                if (impact is { } art && strikes++ < effectBudget)
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

    /// <summary>The client effect that marks an experience orb on the ground: the game's own charge orb (a small
    /// floating sphere), so it reads as something to pick up rather than a light show.</summary>
    internal const string SurvivorsOrbArt = "Metadata/Effects/Spells/Orbs/rig_charge.ao";

    /// <summary>The experience orb's size: the charge model is small, so it is scaled up slightly to be found.</summary>
    internal const float SurvivorsOrbScale = 1.5f;

    /// <summary>The flight visual a survivor projectile falls back to when its own Projectiles row has no
    /// projectile-clips.json entry (Burning Arrow, Lightning Arrow, Spectral Throw, Cold Snap): a plain arrow, so a
    /// missing clip never leaves the projectile invisible.</summary>
    internal const string SurvivorsDefaultFlightVisual = "Metadata/Effects/Spells/Base_Arrow_Attack/Default_Arrow.ao";

    /// <summary>The client effect that draws a death ground zone: a desecrated ground patch the player must leave.</summary>
    internal const string SurvivorsDeathZoneArt = "Metadata/Effects/Spells/Ground_Effects_V2/Desecrated/Desecration_01.ao";

    /// <summary>How many orbs can lie at once; beyond this the oldest orb carries the new experience, like the
    /// reference's merged gems.</summary>
    internal const int SurvivorsOrbCap = 200;

    /// <summary>Drops one monster's experience as an orb at the kill cell. The client object is a permanent effect;
    /// the run tick removes it when the player passes within <see cref="SurvivorsRules.OrbPickupRadius"/>.</summary>
    internal static async Task SurvivorsDropOrb(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, (int X, int Y) at, int experience)
    {
        if (experience <= 0 || StateOf(conn).Survivors is null) return;
        uint id;
        lock (conn.Monsters)
        {
            if (StateOf(conn).Survivors is null) return;
            var orbs = StateOf(conn).SurvivorsOrbs;
            if (orbs.Count >= SurvivorsOrbCap)
            {
                var oldest = orbs[0];
                orbs[0] = oldest with { Experience = oldest.Experience + experience };
                return;
            }
            id = conn.NextObjectId++;
            orbs.Add(new(id, at.X, at.Y, experience));
        }
        await Send(remote, stream, ciphers,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(at.X, at.Y, SurvivorsOrbArt) { Scale = SurvivorsOrbScale }.Blob()),
            $"0x02b9 survivors xp orb 0x{id:x} {experience} xp at ({at.X}, {at.Y})",
            still: () => StateOf(conn).Survivors is not null);
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null,
            still: () => StateOf(conn).Survivors is not null);
    }

    /// <summary>Collects every orb inside the pickup radius around the player and pays its experience.</summary>
    internal static async Task SurvivorsCollectOrbs(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsRun run)
    {
        if (StateOf(conn).Survivors is null || conn.PlayerLife == 0) return;
        var here = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        List<SurvivorsExperienceOrb> taken;
        lock (conn.Monsters)
        {
            var orbs = StateOf(conn).SurvivorsOrbs;
            taken = SurvivorsOrbsInReach(orbs, here, SurvivorsRules.OrbPickupRadius);
            foreach (var orb in taken) orbs.Remove(orb);
        }
        foreach (var orb in taken)
        {
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(orb.Id), null,
                still: () => StateOf(conn).Survivors is not null);
            run.RecordOrbExperience(orb.Experience);
        }
    }

    /// <summary>The orbs a player at <paramref name="here"/> can pick up: the pure part of the pickup, so the radius
    /// rule can be checked without a live area.</summary>
    internal static List<SurvivorsExperienceOrb> SurvivorsOrbsInReach(IReadOnlyList<SurvivorsExperienceOrb> orbs,
        (int X, int Y) here, double radius) =>
        [.. orbs.Where(orb => Dist(here, (orb.X, orb.Y)) <= radius)];

    /// <summary>Removes the run's orbs from the client and the state (run end or a fresh start).</summary>
    internal static async Task SurvivorsClearOrbs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        uint[] ids;
        lock (conn.Monsters)
        {
            ids = [.. StateOf(conn).SurvivorsOrbs.Select(orb => orb.Id)];
            StateOf(conn).SurvivorsOrbs.Clear();
        }
        foreach (uint id in ids)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
    }

    /// <summary>Removes the run's death ground zones from the client and the state (run end or a fresh start).</summary>
    internal static async Task SurvivorsClearGroundZones(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn)
    {
        uint[] ids;
        lock (conn.Monsters)
        {
            ids = [.. StateOf(conn).SurvivorsGroundZones.Select(zone => zone.Id)];
            StateOf(conn).SurvivorsGroundZones.Clear();
        }
        foreach (uint id in ids)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
    }

    /// <summary>Drops one death ground zone for a killed monster: 50/50 under the monster or under the player, drawn
    /// as a desecrated ground patch and drained by <see cref="SurvivorsAdvanceDeathZones"/> while the player stays.</summary>
    internal static async Task SurvivorsDropDeathZone(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, (int X, int Y) at)
    {
        if (StateOf(conn).Survivors is null) return;
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        // 50/50: the zone appears under the monster or under the player.
        bool underPlayer = Random.Shared.Next(2) == 0;
        int x = underPlayer ? px : at.X, y = underPlayer ? py : at.Y;
        uint id;
        lock (conn.Monsters)
        {
            if (StateOf(conn).Survivors is null) return;
            id = conn.NextObjectId++;
            StateOf(conn).SurvivorsGroundZones.Add(new(id, x, y, SurvivorsRules.DeathZoneRadius,
                conn.Clock.Elapsed.TotalSeconds + SurvivorsRules.DeathZoneDurationSeconds,
                SurvivorsRules.DeathZoneDamagePerSecond((int)conn.PlayerMaxLife)));
        }
        await Send(remote, stream, ciphers,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(x, y, SurvivorsDeathZoneArt).Blob()),
            $"0x02b9 survivors death zone 0x{id:x} at ({x}, {y})",
            still: () => StateOf(conn).Survivors is not null);
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null,
            still: () => StateOf(conn).Survivors is not null);
    }

    /// <summary>Removes a dead monster's body from the server and the client after
    /// <see cref="SurvivorsRules.CorpseLingerSeconds"/>, so a long run's corpses do not pile up. Runs every half second
    /// from the run tick; the corpse time is tracked per connection.</summary>
    internal static async Task SurvivorsSweepCorpses(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, HashSet<uint> horde)
    {
        if (StateOf(conn).Survivors is null) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        List<uint> remove = [];
        lock (conn.Monsters)
        {
            var corpses = StateOf(conn).SurvivorsCorpses;
            foreach (var id in corpses.Keys.ToArray())
                if (!conn.Monsters.ContainsKey(id)) corpses.Remove(id);
            foreach (var (id, monster) in conn.Monsters)
            {
                if (!monster.Dead && monster.Life > 0) { corpses.Remove(id); continue; }
                if (!corpses.TryGetValue(id, out double at)) corpses[id] = at = now;
                if (now - at >= SurvivorsRules.CorpseLingerSeconds) remove.Add(id);
            }
            foreach (uint id in remove)
            {
                conn.Monsters.Remove(id);
                corpses.Remove(id);
                horde.Remove(id);
            }
        }
        foreach (uint id in remove)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null,
                still: () => StateOf(conn).Survivors is not null);
    }

    /// <summary>Advances the death ground zones each run tick: removes the expired ones and drains life while the
    /// player stands inside one. The drain is <see cref="SurvivorsRules.DeathZoneDamagePercentPerSecond"/> of maximum
    /// life a second; standing still in a zone is fatal.</summary>
    internal static async Task SurvivorsAdvanceDeathZones(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, double step)
    {
        if (StateOf(conn).Survivors is null) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        var here = conn.Walk.Position(now);
        uint[] expired;
        int damagePerSecond = 0;
        lock (conn.Monsters)
        {
            var zones = StateOf(conn).SurvivorsGroundZones;
            expired = zones.Where(zone => zone.ExpiresAt <= now).Select(zone => zone.Id).ToArray();
            foreach (uint id in expired) zones.RemoveAll(zone => zone.Id == id);
            foreach (var zone in zones)
                if (Dist(here, (zone.X, zone.Y)) <= zone.Radius) { damagePerSecond = zone.DamagePerSecond; break; }
        }
        foreach (uint id in expired)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null,
                still: () => StateOf(conn).Survivors is not null);
        if (damagePerSecond <= 0 || step <= 0) return;
        int damage = (int)Math.Round(damagePerSecond * step);
        if (damage <= 0) return;
        uint life;
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0) return;
            life = conn.PlayerLife > (uint)damage ? conn.PlayerLife - (uint)damage : 0;
            conn.PlayerLife = life;
        }
        await Send(remote, stream, ciphers, PlayerLifeUpdate(conn), "survivors death zone drain",
            still: () => StateOf(conn).Survivors is not null);
    }

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
        var mergedKeys = SurvivorsKeyStats(merged);
        double baseSeconds = armedAttack ? weapon.AttackSeconds(mergedKeys) : skill.CastTimeMs / 1000.0;
        int castSpeed = armedAttack ? 0 : Get("base_cast_speed_+%") + Get("cast_speed_+%") + (spell ? Get("spell_cast_speed_+%") : 0);
        double interval = SurvivorsFormulas.CastInterval(baseSeconds, rank, castSpeed);
        var skillKeys = SurvivorsKeyStats(skillStats);
        var conversion = DamageStats.Conversion(mergedKeys, Stats, skill.Attack);
        List<(string Name, int Value)> supportMods = [];
        foreach (int index in run.SupportsOf(skillIndex))
            if (index >= 0 && index < supports.Count && supports[index].Effect >= 0)
                foreach (var (key, value) in SkillsFor(conn).EffectStats(supports[index].Effect, rank))
                    if (Stats.Name(key) is { Length: > 0 } name) supportMods.Add((name, value));
        if (skill.Kind == SurvivorsSkillKind.Herald)
        {
            // The real gem is a buff, so the run's pulse is synthetic; still resolve it through the same hit pipeline
            // so the pulse supports (area, damage more, elemental) change it exactly as the gem stats say.
            int baseDamage = SurvivorsRules.SkillDamage(roster.WeaponDamage, rank);
            var heraldRaw = DamagePacket.Of(skill.Type, baseDamage, baseDamage);
            heraldRaw = DamageStats.RawAdded(heraldRaw, Get, spell: true, effectiveness: 1.0);
            heraldRaw = SurvivorsGlobalAdded(heraldRaw, Get, 10_000);
            var pulse = PlayerHitDamage.Resolve(heraldRaw, Get, conversion, spell: true, area: true, projectile: false,
                supportModifiers: supportMods);
            if (pulse.IsZero) return null;
            double radius = SurvivorsFormulas.AreaRadius(SurvivorsHeraldRadius * SurvivorsRules.SkillRadius(rank),
                run.AreaIncreasedPercent + Get("skill_area_of_effect_+%") + Get("base_skill_area_of_effect_+%"));
            return new(interval, skill, rank, 0, null, null, null, false, true,
                (int)Math.Max(1, pulse.AverageTotal), radius);
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
        // An area gem has no flight: its cast is a pulse around the player with the gem's own area hit.
        if (skill.Kind == SurvivorsSkillKind.Area)
        {
            var pulseRaw = RawDamage();
            if (pulseRaw.IsZero) return null;
            var pulse = PlayerHitDamage.Resolve(pulseRaw, Get, conversion, spell, area: true, projectile: false,
                supportModifiers: supportMods);
            if (pulse.IsZero) return null;
            double pulseRadius = SurvivorsFormulas.AreaRadius(SurvivorsHeraldRadius * SurvivorsRules.SkillRadius(rank),
                run.AreaIncreasedPercent + Get("skill_area_of_effect_+%") + Get("base_skill_area_of_effect_+%"));
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
        var ailmentRows = AilmentPanelStats.Compute(new(hand, Spell: spell), raw, conversion, Get);
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
            Poison = MonsterPoisons.Settings.From(ailmentRows, hand, Get, local),
            Leech = LeechSources.From(Get, skill.Attack),
            SecondFormCriticalChance = secondForm,
        };
        // The client draws the projectile's own flight and impact clips (projectile-clips.json): an impact clip is the
        // skill's hit animation. Fireball and Kinetic Blast also carry the area effect their data names.
        // ThrownWeapon is Spectral Throw's client-predicted weapon: it has a flight clip but no server-side model, so
        // it is routed to the fallback below like a missing clip.
        if (definition.Metadata != "Metadata/Projectiles/ThrownWeapon"
            && MonsterProjectilePresentation(SkillsFor(conn).Clips(definition.Metadata),
                SurvivorsRules.Range / Math.Max(0.01, speed)) is { } presentation)
        {
            shot = shot with { Presentation = presentation };
            if (SurvivorsTrace)
                Console.WriteLine($"[gateway] survivors {skill.Name}: projectile {definition.Metadata} " +
                    $"flight clip {presentation.FlightClip?.ToString() ?? "default"}, impact clip {presentation.ImpactClip?.ToString() ?? "none"}");
        }
        else
        {
            // No projectile-clips.json entry for this metadata, or a client-predicted weapon type with no server model:
            // the server object would fly invisible. A plain arrow fallback keeps every projectile visible (Burning
            // Arrow, Lightning Arrow, Spectral Throw, Cold Snap and the like), so a missing clip is never a hidden hit.
            shot = shot with { FlightVisual = SurvivorsDefaultFlightVisual };
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
