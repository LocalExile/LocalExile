using System.Collections.Concurrent;
using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
using ChatCommand = GameServer.Modding.ChatCommand;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    // The survivors mode (Agents/docs/game/survivors-mode.md): a Vampire Survivors-style run on the current area's own
    // monster packs. The run's own skills cast themselves at the nearest monster, so the player only moves.
    const int SurvivorsStepMs = 100;

    /// <summary>The ring wave cadence: a closing ring 67.5 s into every stage, one per 135 s cycle.</summary>
    internal const double SurvivorsRingIntervalSeconds = 135;

    /// <summary>The area's boss arrives this long after a stage starts: its death is what opens the next stage.</summary>
    internal const double SurvivorsBossDelaySeconds = 15;

    /// <summary>A herald's aura reaches this far around the player, in cells, before its rank and area grow it.</summary>
    internal const double SurvivorsHeraldRadius = 12;

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

    /// <summary>The reward inventory the level-up window lists its skills in (the quest-reward window's own).</summary>
    internal const uint SurvivorsOfferInventory = 0x600;

    /// <summary>The areas a run moves through, one "level" every <see cref="SurvivorsRules.StageSeconds"/>.</summary>
    internal static readonly string[] SurvivorsLevels =
        ["1_1_1", "1_1_2", "1_1_3", "1_1_5", "1_1_6", "1_1_9", "1_1_11_2"];

    // The per-cast mechanic trace for the gem verification pass (issue #503): Game_SURVIVORS_TRACE=1 in the Gateway's
    // environment writes one line per cast with the numbers the cast actually used.
    static readonly bool SurvivorsTrace = Environment.GetEnvironmentVariable("Game_SURVIVORS_TRACE") == "1";

    /// <summary>
    /// The first skill id a run's skills use: above the gem band (FirstSkillId + 64), so no socketed gem ever takes one,
    /// and only ids that no basic action masks to (SkillCatalog.SkillIds).
    /// </summary>
    internal const ushort SurvivorsSkillIdBase = 0x0300;

    // The ids a run's skills may use, in the order the catalogue hands them out.
    static readonly Lazy<ushort[]> SurvivorsSkillIdPool =
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
    static readonly Lazy<IReadOnlyDictionary<int, int>> HeraldBuffRows = new(() =>
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
    static IReadOnlyList<(uint, int)> SurvivorsSkillStats(ClientState conn, SurvivorsSkill skill, int rank)
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
        ushort id = SurvivorsSkillId(skill);
        if (id == 0) return;
        foreach (var aim in aims)
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, id, aim.X, aim.Y),
                $"0x01ce survivors skill 0x{id:x4} used at ({aim.X}, {aim.Y})");
        var definition = conn.SurvivorsCatalog is { } catalog && skill < catalog.Count ? catalog[skill] : null;
        float total = (definition is null ? 1000 : SurvivorsSkillCooldownMs(definition, rank)) / 1000f;
        float remaining = (float)Math.Clamp(interval, 0, total);
        await Send(remote, stream, ciphers,
            InstanceEntities.PlayerSingleUseCooldowns(PlayerId, [new SingleUseCooldownUpdate(id, remaining, total, 1f)]),
            $"0x02ba survivors cooldown 0x{id:x4} {remaining:0.00}/{total:0.00} s");
    }

    /// <summary>True when a skill id is one of the run's skills: a click on its bar slot must not become a walk.</summary>
    internal static bool SurvivorsOwnsSkill(ClientState conn, ushort id)
    {
        if (conn.SurvivorsCatalog is not { } catalog) return false;
        for (int index = 0; index < catalog.Count; index++)
            if (SurvivorsSkillId(index) == id) return true;
        return false;
    }

    /// <summary>The monsters the current area can draw a horde from, weakest first, and the run skills' base damage:
    /// <see cref="WeaponDamage"/> is the starting tier pool's median life (one rank-1 cast ≈ one starting hit), and
    /// <see cref="TopLife"/> is the roster's strongest variety, for reporting.</summary>
    internal sealed record SurvivorsRoster(IReadOnlyList<MonsterCatalog.Variety> Varieties,
        IReadOnlyList<MonsterCatalog.Variety> Bosses, byte Level, int WeaponDamage, int TopLife, double SkillScale = 1);

    /// <summary>A run that changed area: the run continues on the next connection.</summary>
    internal sealed record SurvivorsCarry(SurvivorsRun Run, int Stage, DateTime At, IReadOnlyCollection<uint>? ItemIds = null);

    static readonly ConcurrentDictionary<string, SurvivorsCarry> SurvivorsCarried = new(StringComparer.OrdinalIgnoreCase);

    internal static int SurvivorsMaxAlive() =>
        int.TryParse(Environment.GetEnvironmentVariable("Game_SURVIVORS_MAX_ALIVE"), out int n)
            ? Math.Clamp(n, 10, 400) : SurvivorsRules.DefaultMaxAlive;

    /// <summary>The area's packs, deduplicated and ordered by the life the client gives the variety at the area level.</summary>
    internal static SurvivorsRoster? SurvivorsRosterFor(ClientState conn)
    {
        if (Packs.For(conn.Layout.Id) is not { } area) return null;
        byte level = (byte)Math.Clamp(area.AreaLevel, 1, 100);
        IEnumerable<int> rows = area.Scattered.SelectMany(s => s.Pack.Entries.Select(e => e.Variety))
            .Concat(area.Named.Values.SelectMany(p => p.Entries.Select(e => e.Variety)))
            .Distinct();
        var varieties = rows.Select(r => Monsters.Find(r.ToString())).OfType<MonsterCatalog.Variety>()
            .Where(v => v.Supported)
            .GroupBy(v => v.Row).Select(g => g.First())
            .OrderBy(v => Monsters.BaseLife(v, level) ?? int.MaxValue).ThenBy(v => v.Row)
            .ToList();
        var bosses = area.Bosses.Select(r => Monsters.Find(r.ToString())).OfType<MonsterCatalog.Variety>()
            .Where(v => v.Supported).GroupBy(v => v.Row).Select(g => g.First()).ToList();
        var lives = varieties.Select(v => Monsters.BaseLife(v, level) ?? 0).Where(l => l > 0).OrderBy(l => l).ToList();
        // The rank-1 anchor is the starting three-tier pool, not the whole roster: the reference opens against its
        // weakest tiers, so the first cast kills in one or two hits and the tier ladder carries the rest.
        var start = SurvivorsRules.WavePool(0, varieties.Count)
            .Select(i => Monsters.BaseLife(varieties[i], level) ?? 0).Where(l => l > 0).OrderBy(l => l).ToList();
        int startLife = start.Count == 0 ? (lives.Count == 0 ? 20 : lives[lives.Count / 2]) : start[start.Count / 2];
        int topLife = lives.Count == 0 ? startLife : lives[^1];
        return new(varieties, bosses, level, Math.Max(1, startLife), Math.Max(1, topLife));
    }

    /// <summary>
    /// Every skill gem whose projectile metadata the client can draw and the projectile runtime can resolve, so the
    /// catalogue grows with the item data instead of a hand-written list. A gem with no projectile of its own (Arc, a
    /// nova, a curse) has no flight the client can draw here and stays out of the run.
    /// </summary>
    internal static IReadOnlyList<SurvivorsSkill> SurvivorsCatalogFor(ClientState conn)
    {
        var catalog = new List<SurvivorsSkill>();
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
            if (!herald && Skills.ProjectileFor(variant.Effect) is null) continue;
            string metadata = Skills.ProjectileFor(variant.Effect)?.Id ?? "Metadata/Projectiles/Fireball";
            catalog.Add(new SurvivorsSkill(path, baseType.Name, SurvivorsDamageType(baseType.Name), variant.Effect, variant.Effect,
                metadata, herald ? SurvivorsSkillKind.Herald : SurvivorsSkillKind.Projectile)
            {
                CastTimeMs = SurvivorsCastTimeMs(conn, variant.Effect),
                Attack = SkillsFor(conn).IsAttack(variant.Effect),
            });
        }
        return catalog;
    }

    // A gem's own cast time (GrantedEffects CastTime in EffectStats) is the run's interval base; an attack gem's
    // CastTime is its weapon swing time at 1.0 s here, since the run has no weapon to read a speed from.
    static int SurvivorsCastTimeMs(ClientState conn, int effect)
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

    static int SurvivorsGemEffect(ClientState conn, string path) =>
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
        if (conn.SurvivorsCatalog is not { } catalog || conn.SurvivorsSupports is not { } supports
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
    static IReadOnlyList<string> SurvivorsGemPaths(ClientState conn) =>
        [.. ItemsFor(conn).All.Where(t => t.Supported && t.Id.Contains("SkillGem", StringComparison.Ordinal)).Select(t => t.Id)];

    static DamageType SurvivorsDamageType(string name) => name switch
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

    /// <summary>The blessings a level-up may offer: support gems whose names describe the run-local effect.</summary>
    internal static IReadOnlyList<SurvivorsBlessingItem> SurvivorsBlessingsFor(ClientState conn)
    {
        (string Path, string Name, SurvivorsBlessing Blessing)[] candidates =
        [
            ("Metadata/Items/Gems/SupportGemAddedFireDamage", "Added Fire Damage Support", SurvivorsBlessing.Might),
            ("Metadata/Items/Gems/SupportGemFasterCast", "Faster Casting Support", SurvivorsBlessing.Haste),
            ("Metadata/Items/Gems/SupportGemIncreasedAreaOfEffect", "Increased Area of Effect Support", SurvivorsBlessing.Area),
            ("Metadata/Items/Gems/SupportGemIncreasedCriticalStrikes", "Increased Critical Strikes Support", SurvivorsBlessing.Critical),
            ("Metadata/Items/Gems/SupportGemLifeLeech", "Life Leech Support", SurvivorsBlessing.Leech),
            ("Metadata/Items/Gems/SupportGemAdditionalXP", "Enlighten Support", SurvivorsBlessing.Wisdom),
            ("Metadata/Items/Gems/SupportGemFortify", "Fortify Support", SurvivorsBlessing.Fortitude),
            ("Metadata/Items/Gems/SupportGemFasterProjectiles", "Faster Projectiles Support", SurvivorsBlessing.Swiftness),
        ];
        return [.. candidates.Where(c => ItemsFor(conn).Find(c.Path) is not null)
            .Select(c => new SurvivorsBlessingItem(c.Path, c.Name, c.Blessing))];
    }

    internal static async Task StartSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        int? only = null)
    {
        if (conn.Survivors is not null)
        {
            await conn.Commands!.Reply("A survivors run is already active. End it with /survivors stop.");
            return;
        }
        if (conn.PlayerLife == 0)
        {
            await conn.Commands!.Reply("You are dead. Resurrect first.");
            return;
        }
        // A previous run's open window may have left the clock stopped; a new run always begins unpaused.
        if (conn.SurvivorsPausedByWindow && conn.SurvivorsOffer is null)
        {
            conn.SurvivorsPausedByWindow = false;
            if (!conn.Clock.IsRunning) conn.Clock.Start();
        }
        if (SurvivorsRosterFor(conn) is not { } roster)
        {
            await conn.Commands!.Reply($"The area {conn.Layout.Id} has no monster packs to draw a horde from. Try an area with monsters, such as The Coast.");
            return;
        }
        if (roster.Varieties.Count == 0)
        {
            await conn.Commands!.Reply($"The area {conn.Layout.Id} has no supported monsters for a run.");
            return;
        }
        if (SurvivorsCatalogFor(conn) is not { Count: > 0 } catalog)
        {
            await conn.Commands!.Reply("No run skills are available: the item catalogue or the projectile definitions are missing.");
            return;
        }
        // Every run starts from an empty character: no carried, worn or flask item survives into a new run.
        await SurvivorsResetInventory(remote, stream, ciphers, conn);
        var supports = SurvivorsSupportsFor(conn);
        roster = roster with { SkillScale = SurvivorsDamageScale(conn, catalog, roster) };
        var run = new SurvivorsRun(SurvivorsMaxAlive());
        run.Learn(only ?? 0);
        conn.Survivors = run;
        conn.SurvivorsCatalog = catalog;
        conn.SurvivorsSupports = supports;
        await conn.Commands!.Reply(
            "--- Survivors ---:\n" +
            $"Survive {SurvivorsRules.VictorySeconds / 60:0} minutes. Your skills cast themselves at the nearest monster. " +
            $"Every {SurvivorsRules.StageSeconds / 60:0.#} minutes the run moves to the next area, but only after the " +
            $"area's lore boss falls.\n" +
            $"Start: {catalog[0].Name}. Each level opens the reward window with three upgrades (six skill slots, six " +
            "blessings, supports and uniques); /survivors stop ends the run. The run's skills and their cooldowns are " +
            "on your skill bar. /survivors curve prints the difficulty and power curve.");
        Console.WriteLine($"[gateway] {remote} survivors run started in {conn.Layout.Id} at level {roster.Level}: " +
            $"{roster.Varieties.Count} varieties, {roster.Bosses.Count} boss(es), {run.MaxAlive} alive at most, " +
            $"base damage {roster.WeaponDamage} (skill scale {roster.SkillScale:0.###}), {catalog.Count} skills, " +
            $"{SurvivorsBlessingsFor(conn).Count} blessings");
        // The run hides the loot already lying in the area: the client forgets the objects and the instance drops them.
        await SurvivorsClearGround(remote, stream, ciphers, conn);
        // The run's skills go out as Actor skills, which puts their gems and cooldowns on the skill bar.
        await SendSkills(remote, stream, ciphers, conn);
        await SurvivorsPublishHeralds(remote, stream, ciphers, conn, run, catalog);
        _ = RunSurvivors(remote, stream, ciphers, conn, run, roster, catalog, stage: 0);
    }

    /// <summary>Restores a run that changed area, on the next connection.</summary>
    internal static async Task ResumeSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.Survivors is not null || conn.Character is null) return;
        if (!SurvivorsCarried.TryRemove(conn.Character, out var carry)) return;
        if (DateTime.UtcNow - carry.At > TimeSpan.FromMinutes(5)) return;
        if (SurvivorsRosterFor(conn) is not { } roster || SurvivorsCatalogFor(conn) is not { Count: > 0 } catalog) return;
        roster = roster with { SkillScale = SurvivorsDamageScale(conn, catalog, roster) };
        conn.Survivors = carry.Run;
        conn.SurvivorsCatalog = catalog;
        conn.SurvivorsSupports = SurvivorsSupportsFor(conn);
        // The uniques the run equipped stay exempt from their requirements on the new connection.
        foreach (uint itemId in carry.ItemIds ?? []) conn.SurvivorsItemIds.Add(itemId);
        Console.WriteLine($"[gateway] {remote} survivors run resumes in {conn.Layout.Id} (level {carry.Stage + 1}, " +
            $"{carry.Run.Kills} kills, run level {carry.Run.Level})");
        // The player object was created without the run's skills: publish them, and the bar with them.
        await SendSkills(remote, stream, ciphers, conn);
        await SendSkillBar(remote, stream, ciphers, conn);
        await SurvivorsPublishHeralds(remote, stream, ciphers, conn, carry.Run, catalog);
        // The new area's ground too: the run keeps the map clear.
        await SurvivorsClearGround(remote, stream, ciphers, conn);
        await conn.Commands!.Reply($"--- Level {carry.Stage + 1} ---\nThe run resumes: {SurvivorsClock(carry.Run.Elapsed)}, " +
            $"{carry.Run.Kills} kills.");
        _ = RunSurvivors(remote, stream, ciphers, conn, carry.Run, roster, catalog, carry.Stage);
    }

    /// <summary>Ends the run: no more spawns or casts, the run's skills off the Actor, and the result in chat. Safe when no
    /// run is active.</summary>
    internal static async Task EndSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
    {
        if (conn.Survivors is not { } run) return;
        conn.Survivors = null;
        conn.SurvivorsOffer = null;
        conn.SurvivorsCatalog = null;
        conn.SurvivorsSupports = null;
        conn.SurvivorsOfferItems = null;
        conn.SurvivorsItemIds.Clear();
        if (conn.Character is { } character) SurvivorsCarried.TryRemove(character, out _);
        if (conn.SurvivorsPausedByWindow)
        {
            conn.SurvivorsPausedByWindow = false;
            if (!conn.Clock.IsRunning) conn.Clock.Start();
        }
        await SurvivorsClearHeralds(remote, stream, ciphers, conn);
        // Take the run's skills off the Actor and the bar: the client prunes the slots the ids no longer fill.
        if (conn.PlayerObjectCreated)
        {
            await SendSkills(remote, stream, ciphers, conn);
            await SendSkillBar(remote, stream, ciphers, conn);
        }
        string opening = why switch
        {
            "victory" => "You survived the run",
            "defeat" => "You died",
            "stopped" => "Run stopped",
            _ => "Run ended",
        };
        string text = $"--- Survivors ---:\n{opening} after {SurvivorsClock(run.Elapsed)} with {run.Kills} kills at run level {run.Level}.";
        if (run.PendingChoices > 0) text += $"\n{run.PendingChoices} upgrade(s) were left unspent.";
        if (conn.Commands is { } commands) await commands.Reply(text);
    }

    internal static string SurvivorsClock(double seconds) =>
        $"{(int)(seconds / 60):00}:{(int)(seconds % 60):00}";

    /// <summary>The /survivors command: start, stop, pick a choice without the window, or report the run.</summary>
    internal static async Task SurvivorsCommand(ChatCommand command)
    {
        var conn = ConnOf(command.Player);
        var (remote, stream, ciphers) = conn.Wire;
        var args = command.Args;
        string what = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
        if (what == "start") { await StartSurvivors(remote, stream, ciphers, conn); return; }
        // Dev: restart the run with one named gem only, so each gem and its supports can be verified in isolation.
        if (what == "test")
        {
            var known = SurvivorsCatalogFor(conn);
            string wanted = string.Join(' ', args.Skip(1)).Trim().ToLowerInvariant();
            int index = known.ToList().FindIndex(s => s.Name.ToLowerInvariant().Contains(wanted));
            if (wanted.Length == 0 || index < 0)
            {
                await conn.Commands!.Reply("Usage: /survivors test <gem name>, e.g. /survivors test fireball.");
                return;
            }
            if (conn.Survivors is not null) await EndSurvivors(remote, stream, ciphers, conn, "stopped");
            await StartSurvivors(remote, stream, ciphers, conn, index);
            await conn.Commands!.Reply($"Test run: {known[index].Name} only (dev).");
            return;
        }
        // Dev: print the reference's difficulty and power curve, for eyeballing a run's pacing without waiting 15 min.
        if (what == "curve")
        {
            var roster = SurvivorsRosterFor(conn);
            var lines = new List<string>
            {
                "--- Survivors curve (Azeroth Survivors: 14 -> 220 horde, +75% life, +90% damage, 6 bosses) ---:",
                " time   horde  monster life  monster damage  boss life  boss xp  rank damage",
            };
            for (int step = 0; step <= 7; step++)
            {
                double t = step * SurvivorsRules.VictorySeconds / 7;
                int stage = Math.Clamp(step, 0, SurvivorsLevels.Length - 1);
                string rank = step switch
                {
                    0 => "1 x1.00", <= 2 => "4 x1.90", <= 4 => "6 x2.50", _ => "8 x3.10",
                };
                lines.Add($"{SurvivorsClock(t)}  {SurvivorsRules.TargetAlive(t),5}  " +
                    $"x{SurvivorsRules.MonsterLifeScale(t):0.00}          x{SurvivorsRules.MonsterDamageScale(t):0.00}          " +
                    $"x{SurvivorsRules.BossLifeRatio(stage),5:0.00}  +{SurvivorsRules.BossExperience(stage),4}  {rank}");
            }
            if (roster is { } area)
                lines.Add($"Area {SurvivorsAreaName(conn.Layout.Id)}: tiers {area.Varieties.Count}, opening anchor " +
                    $"{area.WeaponDamage} life, top {area.TopLife} life, boss 0 {Math.Round(SurvivorsPoolTop(area, 0) * SurvivorsRules.BossLifeRatio(0))} life.");
            await conn.Commands!.Reply(string.Join('\n', lines));
            return;
        }
        // Dev: set every learned skill's rank, for the verification pass. (A "learn everything" command is not kept:
        // the client's bar holds only a few run skills, and 30 of them close the client.)
        if (what == "rank")
        {
            if (conn.Survivors is not { } devRun || conn.SurvivorsCatalog is not { } devCatalog)
            {
                await conn.Commands!.Reply("No survivors run is active.");
                return;
            }
            int rank = args.Count > 1 && int.TryParse(args[1], out int wantedRank)
                ? Math.Clamp(wantedRank, 1, SurvivorsRules.MaxRank) : SurvivorsRules.MaxRank;
            foreach (int index in devRun.Skills.Keys.ToList()) devRun.SetRank(index, rank);
            await SendSkills(remote, stream, ciphers, conn);
            await SendSkillBar(remote, stream, ciphers, conn);
            await conn.Commands!.Reply($"Every learned skill is at rank {rank} (dev).");
            return;
        }
        // Dev: bank a level without waiting for the horde to die, so the choice window can be tested at once.
        if (what == "level")
        {
            if (conn.Survivors is not { } levelling) { await conn.Commands!.Reply("No survivors run is active."); return; }
            int before = levelling.Level;
            for (int i = 0; i < 4; i++) levelling.RecordKill(MonsterRarity.Unique);
            await conn.Commands!.Reply($"Run level {before} -> {levelling.Level} (dev).");
            return;
        }
        // Dev: learn the first Herald skill at once, so its aura pulse can be watched without hunting the offer list.
        if (what == "herald")
        {
            if (conn.Survivors is not { } heraldRun) { await conn.Commands!.Reply("No survivors run is active."); return; }
            var heraldCatalog = SurvivorsCatalogFor(conn);
            int index = heraldCatalog.ToList().FindIndex(s => s.Kind == SurvivorsSkillKind.Herald);
            if (index < 0) { await conn.Commands!.Reply("No Herald skill gem in the catalogue."); return; }
            heraldRun.Learn(index);
            await conn.Commands!.Reply($"Learned {heraldCatalog[index].Name} (herald, dev).");
            await SendSkills(remote, stream, ciphers, conn);
            await SendSkillBar(remote, stream, ciphers, conn);
            await SurvivorsPublishHeralds(remote, stream, ciphers, conn, heraldRun, heraldCatalog);
            return;
        }
        // Dev: link a named support to the first learned skill, for the per-support verification pass.
        if (what == "link")
        {
            if (conn.Survivors is not { } linked || conn.SurvivorsSupports is not { } linkCatalog
                || conn.SurvivorsCatalog is not { } linkSkills)
            {
                await conn.Commands!.Reply("No survivors run is active.");
                return;
            }
            string wanted = string.Join(' ', args.Skip(1)).Trim().ToLowerInvariant();
            int support = linkCatalog.ToList().FindIndex(s => s.Name.ToLowerInvariant().Contains(wanted));
            int target = linked.Skills.Keys.OrderBy(k => k).DefaultIfEmpty(-1).First();
            if (wanted.Length == 0 || support < 0 || target < 0)
            {
                await conn.Commands!.Reply("Usage: /survivors link <support name>, e.g. /survivors link added cold.");
                return;
            }
            if (!SurvivorsCanSupport(conn, target, support))
            {
                await conn.Commands!.Reply(
                    $"{linkCatalog[support].Name} cannot support {linkSkills[target].Name} (the game's skill types).");
                return;
            }
            await conn.Commands!.Reply(linked.Link(target, support)
                ? $"Linked {linkCatalog[support].Name} to {linkSkills[target].Name} (dev)."
                : $"Could not link {linkCatalog[support].Name}: the skill already has five supports.");
            return;
        }
        // Dev: equip a named unique, for checking its mods on the character and on the run's casts.
        if (what == "unique")
        {
            string wanted = string.Join(' ', args.Skip(1)).Trim();
            if (wanted.Length == 0 || UniquesFor(conn).Create(wanted, Random.Shared) is not { } unique)
            {
                await conn.Commands!.Reply($"Usage: /survivors unique <name>, e.g. /survivors unique The Searing Touch.");
                return;
            }
            bool equipped = await SurvivorsEquip(remote, stream, ciphers, conn, unique);
            await conn.Commands!.Reply(equipped
                ? $"Equipped {wanted} (dev)." : $"{wanted} could not be equipped.");
            return;
        }
        // Dev: the full gem x support matrix through the same computation the live cast uses, so every support's
        // number can be checked without waiting for a level-up offer or a live cast.
        if (what == "matrix")
        {
            var matrixCatalog = SurvivorsCatalogFor(conn);
            string wanted = string.Join(' ', args.Skip(1)).Trim().ToLowerInvariant();
            int index = wanted.Length == 0 && conn.Survivors is { } active
                ? active.Skills.Keys.OrderBy(k => k).DefaultIfEmpty(-1).First()
                : matrixCatalog.ToList().FindIndex(s => s.Name.ToLowerInvariant().Contains(wanted));
            if (index < 0 || index >= matrixCatalog.Count)
            {
                await conn.Commands!.Reply("Usage: /survivors matrix <gem>, e.g. /survivors matrix fireball.");
                return;
            }
            conn.SurvivorsCatalog ??= matrixCatalog;
            conn.SurvivorsSupports ??= SurvivorsSupportsFor(conn);
            var matrixSupports = conn.SurvivorsSupports!;
            var roster = SurvivorsRosterFor(conn) ?? new SurvivorsRoster([], [], 1, 20, 40);
            var skill = matrixCatalog[index];
            SurvivorsRun Bare()
            {
                var run = new SurvivorsRun();
                run.Learn(index);
                return run;
            }
            if (SurvivorsComputeCast(conn, Bare(), skill, 1, index, roster) is not { } bare)
            {
                await conn.Commands!.Reply($"{skill.Name} has no cast to compare.");
                return;
            }
            string baseLine = bare.Herald
                ? $"base: interval {bare.Interval:0.00}, pulse {bare.HeraldDamage}, radius {bare.HeraldRadius:0.#}"
                : $"base: interval {bare.Interval:0.00}, radius {bare.Profile?.Radius:0.#}, " +
                  $"speed {bare.Profile?.Speed:0.#}, crit {bare.Shot?.CriticalChance / 100.0:0.##}%, " +
                  $"leech {bare.Shot?.Leech.Life.MinTotal:0.#}, hit {bare.Shot?.Hit}";
            await conn.Commands!.Reply($"--- Survivors matrix: {skill.Name} rank 1 ---\n{baseLine}");
            foreach (int support in Enumerable.Range(0, matrixSupports.Count))
            {
                if (!SurvivorsCanSupport(conn, index, support))
                {
                    await conn.Commands!.Reply($"{matrixSupports[support].Name}: cannot support {skill.Name} (skill types).");
                    continue;
                }
                var linkedRun = Bare();
                linkedRun.Link(index, support);
                if (SurvivorsComputeCast(conn, linkedRun, skill, 1, index, roster) is not { } with) continue;
                await conn.Commands!.Reply($"{matrixSupports[support].Name}: " +
                    $"interval {bare.Interval:0.00}->{with.Interval:0.00}, " +
                    $"radius {bare.Profile?.Radius:0.#}->{with.Profile?.Radius:0.#}, " +
                    $"speed {bare.Profile?.Speed:0.#}->{with.Profile?.Speed:0.#}, " +
                    $"crit {bare.Shot?.CriticalChance / 100.0:0.##}->{with.Shot?.CriticalChance / 100.0:0.##}%, " +
                    $"leech {bare.Shot?.Leech.Life.MinTotal:0.#}->{with.Shot?.Leech.Life.MinTotal:0.#}, " +
                    $"hit {bare.Shot?.Hit} -> {with.Shot?.Hit}");
            }
            return;
        }
        // Dev: sweep the whole unique catalogue and report what can be equipped and what its mods do.
        if (what == "uniques")
        {
            await SurvivorsUniqueReport(remote, stream, ciphers, conn);
            return;
        }
        if (what == "stop")
        {
            if (conn.Survivors is null) { await conn.Commands!.Reply("No survivors run is active."); return; }
            await EndSurvivors(remote, stream, ciphers, conn, "stopped");
            return;
        }
        if (what == "pick")
        {
            if (conn.Survivors is not { } picking)
            {
                await conn.Commands!.Reply("No survivors run is active. /survivors start begins one in this area.");
                return;
            }
            if (picking.Offer.Count == 0)
            {
                await conn.Commands!.Reply("No skill is waiting right now.");
                return;
            }
            int index = args.Count > 1 ? int.Parse(args[1]) : 0;
            if (index < 1 || index > picking.Offer.Count
                || !await ApplySurvivorsChoice(remote, stream, ciphers, conn, picking.Offer[index - 1]))
            {
                await conn.Commands!.Reply($"Pick a number from 1 to {picking.Offer.Count}.");
                return;
            }
            return;
        }
        if (conn.Survivors is not { } run)
        {
            await conn.Commands!.Reply("No survivors run is active. /survivors start begins one in this area.");
            return;
        }
        string offer = "";
        if (run.Offer.Count > 0 && conn.SurvivorsOffer is { } shown)
        {
            var lines = new List<string>();
            foreach (var choice in run.Offer)
            {
                uint id = shown.FirstOrDefault(e => e.Value == choice).Key;
                string label = id != 0 && conn.SurvivorsOfferNames?.GetValueOrDefault(id) is { } named
                    ? named : choice.Kind.ToString();
                lines.Add($"{lines.Count + 1}: {label} (0x{id:x8})");
            }
            offer = "\n" + string.Join("\n", lines);
        }
        await conn.Commands!.Reply(
            $"--- Survivors ---\n{SurvivorsClock(run.Elapsed)} of {SurvivorsRules.VictorySeconds / 60:0} minutes, " +
            $"{run.Kills} kills, run level {run.Level}, {run.Skills.Count} skill(s), {run.PendingChoices} choice(s) waiting, " +
            $"horde up to {run.TargetAlive} of {run.MaxAlive}." + offer);
    }

    static async Task RunSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SurvivorsRun run, SurvivorsRoster roster, IReadOnlyList<SurvivorsSkill> catalog, int stage)
    {
        // Each stage is one area and one lore boss: the boss arrives 15 s in, and the way out opens only after it falls.
        double stageStart = stage * SurvivorsRules.StageSeconds;
        double last = conn.Clock.Elapsed.TotalSeconds, nextSpawn = 0,
            nextBoss = Math.Max(stageStart + SurvivorsBossDelaySeconds, run.Elapsed + 5),
            nextRing = Math.Max(stageStart + SurvivorsRingIntervalSeconds / 2, run.Elapsed + 10),
            nextReport = 10, nextSweep = 0;
        string bossName = roster.Bosses.Count > 0 ? roster.Bosses[0].Name : "";
        uint stageBoss = 0;
        bool bossSpawned = false, bossDefeated = false, bossWarned = false;
        bool carried = false;
        var horde = new HashSet<uint>();
        var nextFire = new Dictionary<int, double>();
        try
        {
            while (!conn.Closed && !conn.LeavingArea && conn.PendingTransfer is null && ReferenceEquals(conn.Survivors, run))
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                double step = now - last;
                last = now;
                if (conn.Paused || step <= 0)
                {
                    await Task.Delay(SurvivorsStepMs);
                    continue;
                }
                run.Advance(step);
                if (conn.PlayerLife == 0) { await EndSurvivors(remote, stream, ciphers, conn, "defeat"); break; }
                if (run.Elapsed >= SurvivorsRules.VictorySeconds) { await EndSurvivors(remote, stream, ciphers, conn, "victory"); break; }
                // The stage boss is the gate: its death is the only thing that lets the run leave this area.
                if (stageBoss != 0 && !bossDefeated)
                {
                    bool dead;
                    lock (conn.Monsters) dead = !conn.Monsters.TryGetValue(stageBoss, out var boss) || boss.Dead || boss.Life == 0;
                    if (dead)
                    {
                        bossDefeated = true;
                        int bonus = SurvivorsRules.BossExperience(stage) - SurvivorsRules.ExperienceForKill(MonsterRarity.Unique);
                        if (bonus > 0) run.RecordExperience(bonus);
                        await conn.Commands!.Reply($"{bossName} falls. The way onward opens. " +
                            $"+{SurvivorsRules.BossExperience(stage)} run experience.");
                    }
                }
                if (run.Elapsed >= (stage + 1) * SurvivorsRules.StageSeconds && stage + 1 < SurvivorsLevels.Length)
                {
                    bool sealedByBoss = SurvivorsRules.WaySealed(roster.Bosses.Count, bossDefeated);
                    if (sealedByBoss)
                    {
                        if (!bossWarned)
                        {
                            bossWarned = true;
                            await conn.Commands!.Reply($"The way is sealed until {bossName} falls.");
                        }
                    }
                    else if (AreaTransitionCatalog.HashOf(SurvivorsLevels[stage + 1]) is { } nextArea)
                    {
                        if (conn.Character is { } character)
                            SurvivorsCarried[character] = new SurvivorsCarry(run, stage + 1, DateTime.UtcNow, [.. conn.SurvivorsItemIds]);
                        carried = true;
                        string nextName = AreaTransitionCatalog.NameOf(nextArea) ?? SurvivorsLevels[stage + 1];
                        await conn.Commands!.Reply($"--- Level {stage + 2} ---\nMoving to {nextName}.");
                        await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(nextArea, 0, false), "survivors level");
                        break;
                    }
                }
                if (now >= nextSpawn)
                {
                    nextSpawn = now + SurvivorsRules.SpawnIntervalSeconds;
                    await SpawnSurvivorsWave(remote, stream, ciphers, conn, run, roster, horde);
                }
                // The ground never shows loot during a run: anything dropped or placed is swept once a second.
                if (now >= nextSweep)
                {
                    nextSweep = now + 1;
                    await SurvivorsClearGround(remote, stream, ciphers, conn);
                }
                if (roster.Bosses.Count > 0 && !bossSpawned && run.Elapsed >= nextBoss)
                {
                    stageBoss = await SpawnSurvivorsBoss(remote, stream, ciphers, conn, roster, run, stage, horde);
                    if (stageBoss != 0) bossSpawned = true;
                    else nextBoss = run.Elapsed + 5;
                }
                // Halfway through the stage a ring of the moment's weakest closes in on the player.
                if (run.Elapsed >= nextRing)
                {
                    nextRing = stageStart + SurvivorsRingIntervalSeconds;
                    await SpawnSurvivorsRing(remote, stream, ciphers, conn, run, roster, horde);
                }
                foreach (var (skill, rank) in run.Skills)
                {
                    if (skill < 0 || skill >= catalog.Count) continue;
                    double ready;
                    lock (conn.Monsters) nextFire.TryGetValue(skill, out ready);
                    if (now < ready) continue;
                    double fired = await SurvivorsCast(remote, stream, ciphers, conn, run, roster, catalog[skill], rank, skill);
                    // The cast returns the interval from the gem's own cast time; an empty cast (nothing in reach)
                    // retries sooner than a full cooldown.
                    lock (conn.Monsters) nextFire[skill] = now + (fired > 0 ? fired : 0.2);
                }
                await SurvivorsOfferIfDue(remote, stream, ciphers, conn, run, catalog);
                if (run.Elapsed >= nextReport)
                {
                    nextReport = run.Elapsed + 10;
                    int alive;
                    lock (conn.Monsters) alive = horde.Count(id => conn.Monsters.GetValueOrDefault(id) is { Dead: false, Life: > 0 });
                    Console.WriteLine($"[gateway] {remote} survivors {SurvivorsClock(run.Elapsed)}: horde {alive}/{run.TargetAlive}, " +
                        $"{run.Kills} kills, run level {run.Level}, {run.Skills.Count} skills, {run.Blessings.Count} blessings, level {stage + 1}");
                }
                await Task.Delay(SurvivorsStepMs);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while spawning or casting.
        }
        finally
        {
            if (!carried && ReferenceEquals(conn.Survivors, run))
            {
                conn.Survivors = null;
                conn.SurvivorsOffer = null;
                conn.SurvivorsCatalog = null;
                conn.SurvivorsSupports = null;
                Console.WriteLine($"[gateway] {remote} survivors run ended early at {SurvivorsClock(run.Elapsed)} " +
                    $"with {run.Kills} kills (area change or closed connection)");
            }
            // A window opened by a run that was replaced in the same tick must not keep the next run's clock stopped.
            else if (conn.SurvivorsOffer is null && conn.SurvivorsPausedByWindow)
            {
                conn.SurvivorsPausedByWindow = false;
                if (!conn.Clock.IsRunning) conn.Clock.Start();
            }
        }
    }

    static async Task SpawnSurvivorsWave(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SurvivorsRun run, SurvivorsRoster roster, HashSet<uint> horde)
    {
        if (roster.Varieties.Count == 0) return;
        int alive;
        lock (conn.Monsters)
            alive = horde.Count(id => conn.Monsters.GetValueOrDefault(id) is { Dead: false, Life: > 0 });
        int missing = run.TargetAlive - alive;
        if (missing <= 0) return;
        int spawn = SurvivorsRules.SpawnRefill(missing);
        var pool = SurvivorsRules.WavePool(run.Elapsed, roster.Varieties.Count);
        if (pool.Length == 0) return;
        for (int i = 0; i < spawn; i++)
        {
            var variety = roster.Varieties[pool[Random.Shared.Next(pool.Length)]];
            if (SurvivorsSpawnCell(conn, 30) is not { } cell) continue;
            horde.Add(await SpawnMonster(remote, stream, ciphers, conn, variety, roster.Level, SurvivorsRarity(run), cell.X, cell.Y,
                log: false, extraMods: SurvivorsMonsterMods(run.Elapsed)));
        }
    }

    /// <summary>
    /// Halfway between bosses, a closing ring of the moment's weakest: 28 bodies at the start, 88 by the end. The
    /// reference mode's surround wave, which punishes standing still more than the trickle does.
    /// </summary>
    static async Task SpawnSurvivorsRing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SurvivorsRun run, SurvivorsRoster roster, HashSet<uint> horde)
    {
        if (roster.Varieties.Count == 0) return;
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        var pool = SurvivorsRules.WavePool(run.Elapsed, roster.Varieties.Count);
        var variety = roster.Varieties[pool.Length == 0 ? 0 : pool[0]];
        int count = 28 + (int)(SurvivorsRules.Progress(run.Elapsed) * 60);
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            double angle = i * Math.Tau / count;
            int x = (int)Math.Round(px + Math.Cos(angle) * 26), y = (int)Math.Round(py + Math.Sin(angle) * 26);
            if (conn.Layout.GridW > 0 && (x < 0 || y < 0 || x >= conn.Layout.GridW || y >= conn.Layout.GridH)) continue;
            if (!conn.Layout.WalkableFor(x, y, 0) || conn.Barrier.Contains((x, y))) continue;
            horde.Add(await SpawnMonster(remote, stream, ciphers, conn, variety, roster.Level, MonsterRarity.Normal, x, y,
                log: false, extraMods: SurvivorsMonsterMods(run.Elapsed)));
            spawned++;
        }
        Console.WriteLine($"[gateway] {remote} survivors ring wave at {SurvivorsClock(run.Elapsed)}: {spawned} of {count} spawned");
        if (spawned > 0) await conn.Commands!.Reply("They're surrounding you!");
    }

    static async Task<uint> SpawnSurvivorsBoss(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SurvivorsRoster roster, SurvivorsRun run, int stage, HashSet<uint> horde)
    {
        if (roster.Bosses.Count == 0) return 0;
        var boss = roster.Bosses[0];
        if (SurvivorsSpawnCell(conn, 24) is not { } cell)
        {
            Console.WriteLine($"[gateway] {remote} survivors: no cell for boss {boss.Name}");
            return 0;
        }
        uint id = await SpawnMonster(remote, stream, ciphers, conn, boss, roster.Level, MonsterRarity.Unique, cell.X, cell.Y,
            mods: SurvivorsBossMods(roster, boss, run, stage));
        horde.Add(id);
        string area = SurvivorsAreaName(conn.Layout.Id);
        await conn.Commands!.Reply($"{boss.Name} guards {area}. Defeat it to open the way onward.");
        return id;
    }

    /// <summary>The strongest variety in the tier pool the area is drawing from at <paramref name="stage"/>: the boss's
    /// ladder is measured against it (the reference's 700 HP boss over the 26 HP top of its 135 s pool).</summary>
    internal static int SurvivorsPoolTop(SurvivorsRoster roster, int stage)
    {
        if (roster.Varieties.Count == 0) return Math.Max(1, roster.TopLife);
        var pool = SurvivorsRules.WavePool(stage * SurvivorsRules.StageSeconds, roster.Varieties.Count);
        int top = 0;
        foreach (int index in pool)
            top = Math.Max(top, Monsters.BaseLife(roster.Varieties[index], roster.Level) ?? 0);
        return top > 0 ? top : Math.Max(1, roster.TopLife);
    }

    /// <summary>
    /// The lore boss's life and damage for the run's clock: the area's current tier pool top × the reference's boss
    /// ladder at <paramref name="stage"/> (26.92×, 25.71×, 49.41×, 31.82×, 50×, 47.62×, 60×) × the clock's life scale.
    /// The rolled unique mods are kept (their rarity life factor is solved out), so a boss is still the area's own unique.
    /// </summary>
    static MonsterModSet SurvivorsBossMods(SurvivorsRoster roster, MonsterCatalog.Variety boss, SurvivorsRun run, int stage)
    {
        var rolled = MonsterModRolls.Value.Roll(boss, roster.Level, MonsterRarity.Unique, Random.Shared, alone: true);
        int baseLife = Math.Max(1, Monsters.BaseLife(boss, roster.Level) ?? 100);
        double target = SurvivorsPoolTop(roster, stage) * SurvivorsRules.BossLifeRatio(stage)
            * SurvivorsRules.MonsterLifeScale(run.Elapsed);
        // MaxLife applies the rarity's own factor after maximum_life_+%, so solve the percentage for the target.
        double factor = (1 + rolled.Effects["monster_life_+%_final_from_rarity"] / 100.0)
            * (1 + rolled.Effects["monster_life_+%_final_from_rarity_table"] / 100.0);
        int lifePct = (int)Math.Round((target / (baseLife * Math.Max(0.01, factor)) - 1) * 100);
        lifePct = Math.Clamp(lifePct, -99, 1_000_000);
        int damagePct = (int)Math.Round((SurvivorsRules.MonsterDamageScale(run.Elapsed) - 1) * 100);
        return rolled with
        {
            Effects = rolled.Effects.Plus("maximum_life_+%", lifePct).Plus("damage_+%", damagePct),
        };
    }

    /// <summary>The client's own name for an area id, or the id when the atlas has none.</summary>
    static string SurvivorsAreaName(string id) =>
        AreaTransitionCatalog.HashOf(id) is { } hash ? AreaTransitionCatalog.NameOf(hash) ?? id : id;

    /// <summary>A spawned monster's mods for the run's clock: +75% life and +90% damage at 15:00, as the monster mods'
    /// own stats (MonsterModEffects).</summary>
    static MonsterModSet SurvivorsMonsterMods(double elapsed)
    {
        int life = (int)Math.Round((SurvivorsRules.MonsterLifeScale(elapsed) - 1) * 100);
        int damage = (int)Math.Round((SurvivorsRules.MonsterDamageScale(elapsed) - 1) * 100);
        if (life <= 0 && damage <= 0) return MonsterModSet.None;
        return MonsterModSet.None with
        {
            Effects = new MonsterModEffects(new Dictionary<string, int>
            {
                ["maximum_life_+%"] = life, ["damage_+%"] = damage,
            }),
        };
    }

    static MonsterRarity SurvivorsRarity(SurvivorsRun run)    {
        double progress = SurvivorsRules.Progress(run.Elapsed);
        int roll = Random.Shared.Next(100);
        if (progress > 0.5 && roll < 3) return MonsterRarity.Rare;
        if (progress > 0.2 && roll < 15) return MonsterRarity.Magic;
        return MonsterRarity.Normal;
    }

    /// <summary>A walkable cell on a ring around the player, or null when this area is too cramped for one.</summary>
    internal static (int X, int Y)? SurvivorsSpawnCell(ClientState conn, double radius)
    {
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            double angle = Random.Shared.NextDouble() * Math.Tau;
            double distance = radius + Random.Shared.NextDouble() * 8;
            int x = (int)Math.Round(px + Math.Cos(angle) * distance), y = (int)Math.Round(py + Math.Sin(angle) * distance);
            if (conn.Layout.GridW > 0 && (x < 0 || y < 0 || x >= conn.Layout.GridW || y >= conn.Layout.GridH)) continue;
            if (!conn.Layout.WalkableFor(x, y, 0) || conn.Barrier.Contains((x, y))) continue;
            return (x, y);
        }
        return null;
    }

    // Gems whose cast aims at the toughest target instead of the nearest one (Azeroth Survivors: pyroblast, aimed
    // shot, exorcism and starfire).
    static bool SurvivorsAimsToughest(SurvivorsSkill skill) =>
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
        // A herald is an aura pulse: every hostile around the player takes the hit, and the client plays the gem's cast.
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
                    if (conn.Monsters.TryGetValue(id, out target) && !target.Dead && target.Life > 0)
                        hit = ApplyMonsterHitLocked(conn, target, cast.HeraldDamage);
                if (hit is not { } applied) continue;
                struck++;
                await NotifyMonsterHit(remote, stream, ciphers, conn, id, target!, applied, cast.HeraldDamage, skill.Name, log: false, melee: true, hitCode: hitCode);
                if (impact is { } art && strikes++ < 3)
                    await SurvivorsHeraldEffect(remote, stream, ciphers, conn, tx, ty, art.Ao, art.Ms, skill.Name);
            }
            if (struck == 0) return 0;
            await SurvivorsPlayCastAnimation(remote, stream, ciphers, conn, skill, cast.Interval);
            ushort heraldId = SurvivorsSkillId(skillIndex);
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, heraldId, origin.X, origin.Y),
                $"0x01ce survivors herald 0x{heraldId:x4} {skill.Name} around ({origin.X}, {origin.Y})");
            await SendSurvivorsCooldown(remote, stream, ciphers, conn, skillIndex, rank, [origin], cast.Interval);
            return cast.Interval;
        }
        var profile = cast.Profile!;
        var shot = cast.Shot!;
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
        var owner = new ProjectileActorKey(PlayerId, conn.ProjectileEpoch);
        var runtime = ProjectilesFor(remote, stream, ciphers, conn);
        long now = ProjectileRuntime.Time(conn.Clock.Elapsed.TotalSeconds);
        foreach (var aim in aims)
        {
            var use = new ProjectileUse(profile, owner, owner, 1, ProjectileSession.Vector(origin), ProjectileSession.Vector(aim))
            { Payload = shot, ReturnAnchor = owner };
            runtime.Schedule(now, () => true, () => use);
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
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO")
            ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
        CastAnimationKind kind = SurvivorsCastKind(skill);
        if (Animations.CastClip(ao, WeaponProfileFor(conn, activeSet: true), kind) is not { } clip) return;
        float speed = interval > 0 && clip.LengthMs > 0
            ? (float)Math.Clamp(clip.LengthMs / 1000.0 / interval, 0.1, 10) : 1;
        await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(PlayerId, 0, clip.Index, speed),
            $"0x01b2 survivors cast {clip.Name} ({kind}) for {skill.Name}: index {clip.Index}, " +
            $"{clip.LengthMs} ms over {interval:0.00} s (speed {speed:0.##})");
    }

    /// <summary>
    /// A herald pulse's hit effect: a permanent effect object at the target cell playing the herald's own <c>.ao</c>
    /// (Ash's <c>flameherald_explode</c>, Ice's <c>iceherald_explode</c>, Thunder's <c>lightning_strike_herald</c>,
    /// Agony's <c>mortarimpact</c>, Purity's <c>slam_impact</c>), removed when its clip ends. The same art the real
    /// client plays for that herald's hit.
    /// </summary>
    static async Task SurvivorsHeraldEffect(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, int x, int y, string ao, int ms, string label)
    {
        int epoch = conn.ProjectileEpoch;
        uint id;
        lock (conn.Monsters) id = conn.NextObjectId++;
        await Send(remote, stream, ciphers,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(x, y, ao).Blob()),
            $"0x02b9 survivors herald {label} effect 0x{id:x} {ao} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        _ = Task.Run(async () =>
        {
            await Task.Delay(ms);
            if (conn.ProjectileActive(epoch))
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
        });
    }

    /// <summary>The stance clip family a run skill's cast plays: a herald buff, an attack swing, a spell projectile or
    /// a spell area cast. The gem's own ActiveSkillTypes decide between projectile and area, so Fireball (both) is a
    /// projectile cast, while Storm Burst (area without the projectile flag) is an area cast.</summary>
    internal static CastAnimationKind SurvivorsCastKind(SurvivorsSkill skill) =>
        skill.Kind == SurvivorsSkillKind.Herald ? CastAnimationKind.Buff
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
        var supports = conn.SurvivorsSupports ?? [];
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

        // PoB calcDamage: base + added × effectiveness, converted, then increased/more from the character, the gem, its
        // supports and the run's blessings. A spell's own base is already final; an attack uses the run's nominal
        // weapon, with added damage (the character's, a support's, the gem's) scaled like the rest of the run.
        int effectiveness = SkillsFor(conn).DamageEffectiveness(skill.Effect, rank);
        DamagePacket raw;
        if (spell)
        {
            // The gem's first-level row is its identity; the run's rank curve (the reference's +30% a rank) is applied
            // once, below, to the whole hit, so the power curve is the reference's and the gem stays itself.
            raw = SkillsFor(conn).HitDamage(skill.Effect, 1);
            raw = DamageStats.RawAdded(raw, Get, spell: true, effectiveness / 10_000.0);
            raw = SurvivorsGlobalAdded(raw, Get, effectiveness);
            raw = raw.Scale(roster.SkillScale);
        }
        else if (armedAttack)
        {
            // The equipped weapon's own hit (local damage, local added, attack added) at the gem's effectiveness.
            raw = SupportedAttackDamage.Raw(weapon, Get);
            raw = SurvivorsGlobalAdded(raw, Get, 10_000);
            raw = SpellDamage.WithEffectiveness(raw, effectiveness);
        }
        else
        {
            bool unarmed = Get("flask_throw_charges_used_per_projectile") != 0;
            var nominal = unarmed ? DamagePacket.Empty
                : DamagePacket.Of(DamageType.Physical, roster.WeaponDamage * 0.45, roster.WeaponDamage * 0.9);
            var added = DamageStats.RawAdded(DamagePacket.Empty, Get, spell: false);
            added = SurvivorsGlobalAdded(added, Get, 10_000);
            added = SpellDamage.WithEffectiveness(added, effectiveness);
            raw = SpellDamage.WithEffectiveness(nominal, effectiveness)
                .Add(added.Scale(roster.SkillScale));
        }
        // The reference's power curve: every run skill deals 30% more a rank above the first (3.1× at rank 8), on top
        // of the gem's own effectiveness, supports and the character's stats.
        raw = raw.Scale(SurvivorsRules.SkillDamageMultiplier(rank));
        if (raw.IsZero) return null;

        var mergedKeys = SurvivorsKeyStats(merged);
        var skillKeys = SurvivorsKeyStats(skillStats);
        var conversion = DamageStats.Conversion(mergedKeys, Stats, skill.Attack);
        List<(string Name, int Value)> supportMods = [];
        foreach (int index in run.SupportsOf(skillIndex))
            if (index >= 0 && index < supports.Count && supports[index].Effect >= 0)
                foreach (var (key, value) in SkillsFor(conn).EffectStats(supports[index].Effect, rank))
                    if (Stats.Name(key) is { Length: > 0 } name) supportMods.Add((name, value));
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
    static ProjectileSkillDefinition? SurvivorsDefinition(SurvivorsSkill skill)
    {
        if (ResolveProjectileDefinition(skill.Recipe) is { } definition) return definition;
        if (Skills.ProjectileFor(skill.Effect) is not { } projectile) return null;
        return new ProjectileSkillDefinition(projectile.Id, new ProjectileSkillParameters((get, speed, arrows) =>
            GatewayProjectileDefaults.Resolve(projectile.Id, get, speed, attack: false, arrows)));
    }

    /// <summary>A gem's own stats by name at a gem level (the run's rank is the gem level).</summary>
    static Dictionary<string, int> SurvivorsStatsByName(ClientState conn, int effect, int level)
    {
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        if (effect < 0) return stats;
        foreach (var (key, value) in SkillsFor(conn).EffectStats(effect, level))
            if (Stats.Name(key) is { Length: > 0 } name) stats[name] = stats.GetValueOrDefault(name) + value;
        return stats;
    }

    /// <summary>The character's final stats by name (passives, equipped uniques, flasks): the player side of Path of
    /// Building's damage formula, which a run skill now reads beside its own gem and supports.</summary>
    static Dictionary<string, int> SurvivorsPlayerStats(IReadOnlyDictionary<uint, int> player)
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
    static Dictionary<string, int> SurvivorsSkillAndSupportStats(ClientState conn, SurvivorsRun run, SurvivorsSkill skill,
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

    static Dictionary<uint, int> SurvivorsKeyStats(Dictionary<string, int> stats)
    {
        var keys = new Dictionary<uint, int>();
        foreach (var (name, value) in stats)
            if (Stats.Key(name) is uint key) keys[key] = keys.GetValueOrDefault(key) + value;
        return keys;
    }

    static string SurvivorsDamageSlug(DamageType type) => type switch
    {
        DamageType.Physical => "physical", DamageType.Lightning => "lightning", DamageType.Cold => "cold",
        DamageType.Fire => "fire", _ => "chaos",
    };

    /// <summary>
    /// The "global" added damage ranges (Added Cold/Lightning Support, a unique's "Adds X to Y Damage"): PoB's
    /// ModParser adds them to spells and attacks alike, scaled by the skill's damage effectiveness like any other
    /// added range.
    /// </summary>
    static DamagePacket SurvivorsGlobalAdded(DamagePacket raw, Func<string, int> get, int effectivenessPermyriad)
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
    static ProjectileProfile SurvivorsMechanics(SurvivorsSkill skill, Func<string, int> get, ProjectileProfile profile, bool spell)
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
    internal static async Task<bool> SurvivorsEquip(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, Item item)
    {
        try
        {
            return await SurvivorsEquipCore(remote, stream, ciphers, conn, item);
        }
        catch (Exception ex) when (ex is not IOException and not ObjectDisposedException)
        {
            // A broken equip must not leave the run paused with the choice already spent.
            Console.WriteLine($"[gateway] {remote} survivors equip failed for {item.TypeHash:x8}: {ex}");
            return false;
        }
    }

    static async Task<bool> SurvivorsEquipCore(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, Item item)
    {
        static Dictionary<uint, int> StatsNow(ClientState c) => PlayerStatsWithCleared(c).ToDictionary(s => s.Item1, s => s.Item2);
        var before = StatsNow(conn);
        if (ItemsFor(conn).ByHash(item.TypeHash) is not { } type) return false;
        var items = ItemsOf(conn);
        byte? slot = null;
        foreach (byte row in new byte[] { 1, 2, 3, 4, 5, 6, 7, 102, 8, 9, 10 })
            if (PlayerInventories.Accepts(row, type.Class) && items.Items(row).Count == 0) { slot = row; break; }
        if (slot is not { } equipped)
        {
            if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) return false;
            var inBag = items.Put(PlayerInventories.MainInventory,
                new InventoryItem(items.NewItemId(), cell.X, cell.Y, item));
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), [], [inBag]),
                $"0x00bf survivors unique {type.Name} into the inventory");
            return true;
        }
        var placed = items.Add(equipped, 0, 0, item);
        conn.SurvivorsItemIds.Add(placed.Id);
        await Send(remote, stream, ciphers,
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(equipped), items.NextRevision(equipped), [], [placed]),
            $"0x00bf survivors unique {type.Name} equipped in row {equipped}");
        if (PlayerInventories.ShowsOnModel(equipped)) await SendEquipment(remote, stream, ciphers, conn, items);
        await SendCharacterStats(remote, stream, ciphers, conn, $"survivors unique {type.Name}");
        // The item's mods must reach the character: the stat packet above is checked against the same build.
        var after = StatsNow(conn);
        var changed = after.Where(kv => before.GetValueOrDefault(kv.Key) != kv.Value)
            .Select(kv => $"{Stats.Name(kv.Key)} {before.GetValueOrDefault(kv.Key)}->{kv.Value}").Take(8).ToList();
        Console.WriteLine($"[gateway] {remote} survivors unique {type.Name}: {(changed.Count == 0 ? "no stat change" : string.Join(", ", changed))}");
        return true;
    }

    /// <summary>
    /// Dev: walk the whole unique catalogue and report what the server can actually make work: the base types it can
    /// create, the ones that fit an equipment row, the ones whose Mods.dat rows are all present, and the ones whose
    /// mods resolve to at least one global or local stat. A name in "No stat found" is a unique whose mechanic is not
    /// a plain stat (or a conditional one) and needs its own implementation.
    /// </summary>
    static async Task SurvivorsUniqueReport(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var uniques = UniquesFor(conn).All;
        var catalog = ItemsFor(conn);
        int creatable = 0, equipment = 0, withStats = 0, missingRows = 0, unsupported = 0;
        var empty = new List<string>();
        var missing = new List<string>();
        foreach (var definition in uniques)
        {
            if (UniquesFor(conn).Create(definition, Random.Shared) is not { } item) { unsupported++; continue; }
            creatable++;
            if (catalog.Find(definition.BaseTypeId) is not { } type
                || !ItemModStats.EquipmentRows.Any(row => PlayerInventories.Accepts(row, type.Class))) continue;
            equipment++;
            int absent = definition.Mods.Count(id => catalog.Mods.ById(id) is null)
                + (definition.Implicits?.Count(id => catalog.Mods.ById(id) is null) ?? 0);
            if (absent > 0)
            {
                missingRows++;
                if (missing.Count < 12) missing.Add($"{definition.Name} ({absent})");
            }
            var global = ItemModStats.Global([item], catalog.Mods, Stats);
            var local = ItemModStats.Local(item, catalog.Mods);
            if (global.Count > 0 || local.Count > 0) withStats++;
            else if (empty.Count < 12) empty.Add(definition.Name);
        }
        await conn.Commands!.Reply($"--- Uniques ---\n{uniques.Count} definitions: {creatable} create, {unsupported} " +
            $"unsupported base type(s), {equipment} fit an equipment row, {withStats} with a recognized stat, " +
            $"{missingRows} with a missing Mods.dat row.");
        if (missing.Count > 0) await conn.Commands!.Reply($"Missing Mods.dat rows: {string.Join(", ", missing)}");
        if (empty.Count > 0) await conn.Commands!.Reply($"No stat found: {string.Join(", ", empty)}");
        Console.WriteLine($"[gateway] {remote} survivors uniques: {uniques.Count} definitions, {creatable} create, " +
            $"{equipment} equipment, {withStats} with stats, {missingRows} missing rows, {unsupported} unsupported");
    }

    /// <summary>A new run starts from an empty character: every carried, worn and flask item is removed and the client
    /// is told row by row (0xbf), so nothing from an earlier run carries in.</summary>
    static async Task SurvivorsResetInventory(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var items = ItemsOf(conn);
        var rows = Enumerable.Range(0, 16).Select(i => (byte)i).Append((byte)102).Append(PlayerInventories.Cursor).Distinct();
        foreach (byte row in rows)
        {
            var ids = items.Items(row).Select(i => i.Id).ToList();
            if (ids.Count == 0) continue;
            foreach (uint id in ids) items.Remove(row, id);
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), ids, []),
                $"0x00bf survivors reset row {row}: {ids.Count} item(s) removed");
        }
        conn.SurvivorsItemIds.Clear();
    }

    /// <summary>Puts a learned Herald's aura buff on the player (S→C 0x1c1), so the client draws it and its icon; the
    /// instances are removed when the run ends.</summary>
    static async Task SurvivorsPublishHeralds(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsRun run, IReadOnlyList<SurvivorsSkill> catalog)
    {
        foreach (var (index, _) in run.Skills)
        {
            if (index < 0 || index >= catalog.Count || catalog[index].Kind != SurvivorsSkillKind.Herald) continue;
            if (!conn.SurvivorsHeraldSkills.Add(index)) continue;
            if (!HeraldBuffRows.Value.TryGetValue(catalog[index].Effect, out int row) || row is < 0 or > 0xffff) continue;
            ushort skillId = SurvivorsSkillId(index);
            ushort aura = conn.NextBuff++;
            conn.SurvivorsHeraldBuffs.Add(aura);
            await Send(remote, stream, ciphers,
                InstanceEntities.AddBuff(PlayerId, aura, (ushort)row, PlayerId, skillId, auraSource: true, displayState: 0),
                $"0x01c1 survivors herald aura {catalog[index].Name} (BuffDefinitions row {row})");
            if (SkillsFor(conn).ActionHash(catalog[index].Effect) is ushort action)
            {
                ushort icon = conn.NextBuff++;
                conn.SurvivorsHeraldBuffs.Add(icon);
                await Send(remote, stream, ciphers,
                    InstanceEntities.AddBuff(PlayerId, icon, (ushort)row, PlayerId, skillId, activeSkill: (skillId, action)),
                    $"0x01c1 survivors herald icon {catalog[index].Name}");
            }
        }
    }

    /// <summary>Removes every herald aura buff the run put on the player.</summary>
    static async Task SurvivorsClearHeralds(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        foreach (ushort buff in conn.SurvivorsHeraldBuffs)
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, buff), $"0x01c2 survivors herald buff {buff}");
        conn.SurvivorsHeraldBuffs.Clear();
        conn.SurvivorsHeraldSkills.Clear();
    }

    /// <summary>Removes every item on the ground of the area for this run: the client forgets the object and the
    /// instance's store drops it, so loot neither shows nor comes back on the next connection.</summary>
    static async Task SurvivorsClearGround(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        foreach (var (id, _) in conn.Instance.Ground.All())
        {
            conn.Instance.Ground.Take(id);
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id),
                $"0x02bb survivors hide world item 0x{id:x}");
        }
    }

    /// <summary>The unique items a level-up may still offer: the catalogue's definitions the run has not taken and the
    /// character is not already wearing (a worn unique is matched by its name word).</summary>
    internal static IReadOnlyList<int> SurvivorsItemPool(ClientState conn, SurvivorsRun run)
    {
        var all = UniquesFor(conn).All;
        var catalog = ItemsFor(conn);
        var taken = run.Items;
        var worn = new HashSet<uint>();
        foreach (byte row in ItemModStats.EquipmentRows)
            foreach (var item in ItemsOf(conn).Items(row))
                if (item.Item.NameWords is [var word, ..]) worn.Add(word);
        var pool = new List<int>();
        for (int i = 0; i < all.Count; i++)
        {
            if (taken.Contains(i)) continue;
            // Only an item that fits an equipment row can work in a run: a jewel or flask has nowhere to go in a
            // character whose inventory is emptied at the start, and offering it would waste the reward.
            if (catalog.Find(all[i].BaseTypeId) is not { } type
                || !ItemModStats.EquipmentRows.Any(row => PlayerInventories.Accepts(row, type.Class))) continue;
            if (UniquesFor(conn).NameWord(all[i]) is not { } own || worn.Contains(own)) continue;
            pool.Add(i);
        }
        return pool;
    }

    internal static async Task SurvivorsOfferIfDue(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsRun run, IReadOnlyList<SurvivorsSkill> catalog)
    {
        // A run that was stopped or replaced while this call was in flight must not deal an offer into the next run's
        // state (it would stop the clock for a window the next run cannot close).
        if (!ReferenceEquals(conn.Survivors, run) || !run.NeedsOffer) return;
        var blessings = SurvivorsBlessingsFor(conn);
        var supports = SurvivorsSupportsFor(conn);
        var offer = run.DealOffer(Random.Shared, catalog.Count, blessings.Count, supports.Count,
            SurvivorsItemPool(conn, run), SurvivorsLinkRule(conn));
        if (offer.Count == 0) return;
        var items = ItemsOf(conn);
        var uniques = UniquesFor(conn).All;
        var shown = new Dictionary<uint, SurvivorsChoice>();
        var names = new Dictionary<uint, string>();
        var offeredItems = new Dictionary<uint, Item>();
        var listed = new List<InventoryItem>();
        byte cell = 0;
        foreach (var choice in offer)
        {
            // A unique item is created now, with its rolled mods, so the pick equips exactly what the window showed.
            Item? created = null;
            string? path = null;
            string name;
            if (choice.Kind == SurvivorsChoiceKind.Item)
            {
                if (choice.Index < 0 || choice.Index >= uniques.Count) continue;
                name = uniques[choice.Index].Name;
                if (UniquesFor(conn).Create(name, Random.Shared) is not { } unique) continue;
                // A run's unique is worn whatever the character's level: the level requirement is a byte we send.
                created = unique with { RequiredLevel = 0 };
            }
            else if (choice.Kind == SurvivorsChoiceKind.Heal)
            {
                // The reference's Healthstone: shown as a life flask, restores all life when picked.
                var flask = ItemsFor(conn).All.FirstOrDefault(t => t.Supported && t.Class == "LifeFlask");
                if (flask is null) continue;
                path = flask.Id;
                name = "Healthstone";
                created = ItemCatalog.Create(flask);
            }
            else
            {
                path = choice.Kind switch
                {
                    SurvivorsChoiceKind.Blessing => choice.Index >= 0 && choice.Index < blessings.Count ? blessings[choice.Index].Path : null,
                    SurvivorsChoiceKind.Support => choice.Index >= 0 && choice.Index < supports.Count ? supports[choice.Index].Path : null,
                    _ => choice.Index >= 0 && choice.Index < catalog.Count ? catalog[choice.Index].Path : null,
                };
                name = choice.Kind switch
                {
                    SurvivorsChoiceKind.Blessing => blessings[choice.Index].Name,
                    SurvivorsChoiceKind.Support => supports[choice.Index].Name,
                    _ => catalog[choice.Index].Name,
                };
                if (path is null || ItemsFor(conn).Find(path) is not { } baseType) continue;
                created = ItemCatalog.Create(baseType);
            }
            int width = ItemsFor(conn).ByHash(created.TypeHash)?.Width ?? 1;
            var item = new InventoryItem(items.NewItemId(), cell, 0, created);
            // A wide item (a two-cell unique) takes two cells: the next choice starts after it, or the icons overlap.
            cell = (byte)Math.Min(11, cell + Math.Max(1, width));
            listed.Add(item);
            shown[item.Id] = choice;
            names[item.Id] = name;
            if (choice.Kind == SurvivorsChoiceKind.Item) offeredItems[item.Id] = created;
        }
        if (listed.Count == 0) return;
        conn.SurvivorsOffer = shown;
        conn.SurvivorsOfferNames = names;
        conn.SurvivorsOfferList = listed;
        conn.SurvivorsOfferItems = offeredItems.Count > 0 ? offeredItems : null;
        // The choice pauses the run: the clock stops, so monsters, spawns, casts and recovery wait for the pick.
        lock (conn.Monsters)
            if (conn.Clock.IsRunning) { conn.Clock.Stop(); conn.SurvivorsPausedByWindow = true; }
        // 0xc0 registers the reward inventory with its contents: 0xbf for an id the client does not know is fatal
        // ("Got an inventory set change for an inventory that isn't in the set").
        await Send(remote, stream, ciphers,
            InstanceInventories.AddInventory(SurvivorsOfferInventory, NpcShops.Tab, NpcShops.Manager, listed),
            $"0x00c0 survivors level {run.Level}: {listed.Count} choice(s) offered");
        await Send(remote, stream, ciphers, InstanceNpcs.OpenQuestReward(SurvivorsOfferInventory, 0),
            "0x0087 survivors level-up window");
    }

    /// <summary>A click in the level-up window (C→S 0x88/0x8a). False when the id is not one of its choices.</summary>
    internal static async Task<bool> ChooseSurvivorsSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint itemId)
    {
        if (conn.SurvivorsOffer is not { } shown) return false;
        if (!shown.TryGetValue(itemId, out var choice))
        {
            Console.WriteLine($"[gateway] {remote} survivors offer click 0x{itemId:x8} is not one of " +
                $"{string.Join(", ", shown.Keys.Select(k => $"0x{k:x8}"))}");
            return false;
        }
        return await ApplySurvivorsChoice(remote, stream, ciphers, conn, choice);
    }

    /// <summary>Applies one choice (the window's click or `/survivors pick`), closes the window and deals the next one.</summary>
    internal static async Task<bool> ApplySurvivorsChoice(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsChoice choice)
    {
        if (conn.Survivors is not { } run) { conn.SurvivorsOffer = null; return false; }
        int index = 0;
        for (int i = 0; i < run.Offer.Count; i++) if (run.Offer[i] == choice) { index = i + 1; break; }
        if (index == 0 || !run.TryChoose(index, out var chosen)) return false;
        string name = "a skill";
        Item? unique = null;
        if (conn.SurvivorsOffer is { } offeredNow)
            foreach (var (id, c) in offeredNow)
                if (c == chosen)
                {
                    if (conn.SurvivorsOfferNames?.GetValueOrDefault(id) is { } found) name = found;
                    if (conn.SurvivorsOfferItems?.GetValueOrDefault(id) is { } shownUnique) unique = shownUnique;
                    break;
                }
        var offered = conn.SurvivorsOffer?.Keys.ToList() ?? [];
        conn.SurvivorsOffer = null;
        conn.SurvivorsOfferNames = null;
        conn.SurvivorsOfferList = null;
        conn.SurvivorsOfferItems = null;
        var items = ItemsOf(conn);
        await Send(remote, stream, ciphers,
            InstanceItems.InventoryChange(SurvivorsOfferInventory, ++conn.SurvivorsOfferRevision, offered, [], NpcShops.Manager),
            $"0x00bf survivors offer cleared after choosing {name}");
        var catalog = SurvivorsCatalogFor(conn);
        string target = chosen.Target >= 0 && chosen.Target < catalog.Count ? catalog[chosen.Target].Name : "the skill";
        // A unique item goes straight onto the character: the run's gear choice.
        bool equipped = unique is not null && await SurvivorsEquip(remote, stream, ciphers, conn, unique);
        if (chosen.Kind == SurvivorsChoiceKind.Heal)
        {
            lock (conn.Monsters) conn.PlayerLife = conn.PlayerMaxLife;
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife),
                "survivors healthstone restores life");
        }
        await conn.Commands!.Reply(chosen.Kind switch
        {
            SurvivorsChoiceKind.Blessing => $"Took {name}, level {run.BlessingLevel((SurvivorsBlessing)chosen.Index)}.",
            SurvivorsChoiceKind.Support => $"Linked {name} to {target}.",
            SurvivorsChoiceKind.Item => equipped ? $"Equipped {name}." : $"Took {name}, but no equipment slot or inventory room was free.",
            SurvivorsChoiceKind.Heal => "The Healthstone restores all your life.",
            _ => $"Learned {name}, rank {run.RankOf(chosen.Index)}.",
        });
        // A new skill or rank changes the Actor's skills and the bar: the client draws the new gem and its cooldown.
        await SendSkills(remote, stream, ciphers, conn);
        await SurvivorsPublishHeralds(remote, stream, ciphers, conn, run, catalog);
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 survivors offer closed");
        await SurvivorsOfferIfDue(remote, stream, ciphers, conn, run, catalog);
        // Nothing left to choose: the run resumes where it paused.
        lock (conn.Monsters)
            if (conn.SurvivorsPausedByWindow && !run.NeedsOffer)
            {
                conn.SurvivorsPausedByWindow = false;
                conn.Clock.Start();
            }
        return true;
    }
}
