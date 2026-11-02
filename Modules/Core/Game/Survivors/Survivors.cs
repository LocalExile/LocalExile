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

    /// <summary>The unique boss of the area arrives this often, in run seconds.</summary>
    internal const double SurvivorsBossIntervalSeconds = 135;

    /// <summary>A herald's aura reaches this far around the player, in cells, before its rank and area grow it.</summary>
    internal const double SurvivorsHeraldRadius = 12;

    /// <summary>The reward inventory the level-up window lists its skills in (the quest-reward window's own).</summary>
    internal const uint SurvivorsOfferInventory = 0x600;

    /// <summary>The areas a run moves through, one "level" every <see cref="SurvivorsRules.LevelSeconds"/>.</summary>
    internal static readonly string[] SurvivorsLevels =
        ["1_1_1", "1_1_2", "1_1_3", "1_1_5", "1_1_6", "1_1_9", "1_1_11_2"];

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

    /// <summary>The base cooldown of a run skill: its cast interval, which the client shows on the skill bar.</summary>
    internal static int SurvivorsSkillCooldownMs(int rank) =>
        (int)Math.Round(SurvivorsRules.SkillInterval(rank) * 1000);

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
                skills.Add(granted with { CooldownMs = (uint)SurvivorsSkillCooldownMs(rank) });
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
        ClientState conn, int skill, int rank, IReadOnlyList<(int X, int Y)> aims)
    {
        ushort id = SurvivorsSkillId(skill);
        if (id == 0) return;
        foreach (var aim in aims)
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, id, aim.X, aim.Y),
                $"0x01ce survivors skill 0x{id:x4} used at ({aim.X}, {aim.Y})");
        double interval = SurvivorsRules.SkillInterval(rank) / (conn.Survivors?.SkillCastMultiplier(skill) ?? 1);
        float total = SurvivorsSkillCooldownMs(rank) / 1000f;
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

    /// <summary>The monsters the current area can draw a horde from, weakest first, and the run skills' base damage.</summary>
    internal sealed record SurvivorsRoster(IReadOnlyList<MonsterCatalog.Variety> Varieties,
        IReadOnlyList<MonsterCatalog.Variety> Bosses, byte Level, int WeaponDamage);

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
        int median = lives.Count == 0 ? 20 : lives[lives.Count / 2];
        return new(varieties, bosses, level, Math.Max(1, median));
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
            // Heralds have no flight: the run casts them as an aura pulse around the player.
            bool herald = baseType.Name.StartsWith("Herald of", StringComparison.OrdinalIgnoreCase);
            if (!herald && ResolveProjectileDefinition(variant.Effect) is null) continue;
            string metadata = Skills.ProjectileFor(variant.Effect)?.Id ?? "Metadata/Projectiles/Fireball";
            catalog.Add(new SurvivorsSkill(path, baseType.Name, SurvivorsDamageType(baseType.Name), variant.Effect, variant.Effect,
                metadata, herald ? SurvivorsSkillKind.Herald : SurvivorsSkillKind.Projectile));
        }
        return catalog;
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
            .Select(c => new SurvivorsSupportItem(c.Path, c.Name, c.Support))];
    }

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

    internal static async Task StartSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
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
        var run = new SurvivorsRun(SurvivorsMaxAlive());        run.Learn(0);
        conn.Survivors = run;
        conn.SurvivorsCatalog = catalog;
        await conn.Commands!.Reply(
            "--- Survivors ---:\n" +
            $"Survive {SurvivorsRules.VictorySeconds / 60:0} minutes. Your skills cast themselves at the nearest monster; every " +
            $"{SurvivorsRules.LevelSeconds / 60:0} minutes the run moves to the next area.\n" +
            $"Start: {catalog[0].Name}. Each level opens the reward window with three upgrades; /survivors stop ends the run. " +
            "The run's skills and their cooldowns are on your skill bar.");
        Console.WriteLine($"[gateway] {remote} survivors run started in {conn.Layout.Id} at level {roster.Level}: " +
            $"{roster.Varieties.Count} varieties, {roster.Bosses.Count} boss(es), {run.MaxAlive} alive at most, " +
            $"base damage {roster.WeaponDamage}, {catalog.Count} skills, {SurvivorsBlessingsFor(conn).Count} blessings");
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
        conn.Survivors = carry.Run;
        conn.SurvivorsCatalog = catalog;
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
        double last = conn.Clock.Elapsed.TotalSeconds, nextSpawn = 0, nextBoss = SurvivorsBossIntervalSeconds,
            nextRing = SurvivorsBossIntervalSeconds / 2, nextReport = 10, nextSweep = 0;
        int bossesSpawned = 0, lastKills = run.Kills;
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
                if (run.Elapsed >= (stage + 1) * SurvivorsRules.LevelSeconds && stage + 1 < SurvivorsLevels.Length)
                {
                    if (AreaTransitionCatalog.HashOf(SurvivorsLevels[stage + 1]) is { } nextArea)
                    {
                        if (conn.Character is { } character)
                            SurvivorsCarried[character] = new SurvivorsCarry(run, stage + 1, DateTime.UtcNow, [.. conn.SurvivorsItemIds]);
                        carried = true;
                        await conn.Commands!.Reply($"--- Level {stage + 2} ---\nMoving to {SurvivorsLevels[stage + 1]}.");
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
                if (roster.Bosses.Count > 0 && run.Elapsed >= nextBoss)
                {
                    nextBoss += SurvivorsBossIntervalSeconds;
                    if (bossesSpawned < roster.Bosses.Count)
                        await SpawnSurvivorsBoss(remote, stream, ciphers, conn, roster, bossesSpawned++, horde);
                }
                // Halfway between bosses a ring of the moment's weakest closes in on the player.
                if (run.Elapsed >= nextRing)
                {
                    nextRing += SurvivorsBossIntervalSeconds;
                    await SpawnSurvivorsRing(remote, stream, ciphers, conn, run, roster, horde);
                }
                foreach (var (skill, rank) in run.Skills)
                {
                    if (skill < 0 || skill >= catalog.Count) continue;
                    double ready;
                    lock (conn.Monsters) nextFire.TryGetValue(skill, out ready);
                    if (now < ready) continue;
                    bool fired = await SurvivorsCast(remote, stream, ciphers, conn, run, roster, catalog[skill], rank, skill);
                    // An empty cast (nothing in reach) retries sooner than a full cooldown.
                    lock (conn.Monsters) nextFire[skill] = now + (fired
                        ? SurvivorsRules.SkillInterval(rank) / run.SkillCastMultiplier(skill) : 0.2);
                }
                // Leech heals for every kill since the last tick, whoever landed it.
                if (run.Kills > lastKills && run.LeechPerKill > 0)
                    await SurvivorsLeech(remote, stream, ciphers, conn, (run.Kills - lastKills) * run.LeechPerKill);
                lastKills = run.Kills;
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
                Console.WriteLine($"[gateway] {remote} survivors run ended early at {SurvivorsClock(run.Elapsed)} " +
                    $"with {run.Kills} kills (area change or closed connection)");
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
        int spawn = Math.Min(SurvivorsRules.MaxSpawnPerStep, missing);
        int unlocked = run.UnlockedCount(roster.Varieties.Count);
        int from = Math.Max(0, unlocked - 3);
        for (int i = 0; i < spawn; i++)
        {
            var variety = roster.Varieties[Random.Shared.Next(from, unlocked)];
            if (SurvivorsSpawnCell(conn, 30) is not { } cell) continue;
            horde.Add(await SpawnMonster(remote, stream, ciphers, conn, variety, roster.Level, SurvivorsRarity(run), cell.X, cell.Y,
                log: false, mods: SurvivorsMonsterMods(run.Elapsed)));
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
        var variety = roster.Varieties[0];
        int count = 28 + (int)(SurvivorsRules.Progress(run.Elapsed) * 60);
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            double angle = i * Math.Tau / count;
            int x = (int)Math.Round(px + Math.Cos(angle) * 26), y = (int)Math.Round(py + Math.Sin(angle) * 26);
            if (conn.Layout.GridW > 0 && (x < 0 || y < 0 || x >= conn.Layout.GridW || y >= conn.Layout.GridH)) continue;
            if (!conn.Layout.WalkableFor(x, y, 0) || conn.Barrier.Contains((x, y))) continue;
            horde.Add(await SpawnMonster(remote, stream, ciphers, conn, variety, roster.Level, MonsterRarity.Normal, x, y,
                log: false, mods: SurvivorsMonsterMods(run.Elapsed)));
            spawned++;
        }
        Console.WriteLine($"[gateway] {remote} survivors ring wave at {SurvivorsClock(run.Elapsed)}: {spawned} of {count} spawned");
        if (spawned > 0) await conn.Commands!.Reply("They're surrounding you!");
    }

    static async Task SpawnSurvivorsBoss(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SurvivorsRoster roster, int index, HashSet<uint> horde)
    {
        if (SurvivorsSpawnCell(conn, 24) is not { } cell)
        {
            Console.WriteLine($"[gateway] {remote} survivors: no cell for boss {roster.Bosses[index].Name}");
            return;
        }
        horde.Add(await SpawnMonster(remote, stream, ciphers, conn, roster.Bosses[index], roster.Level, MonsterRarity.Unique, cell.X, cell.Y));
        await conn.Commands!.Reply($"{roster.Bosses[index].Name} approaches.");
    }

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
    /// One run skill's cast: a real projectile object the client draws for each target in reach (one per rank's extra
    /// projectile), released from the player and resolved by the projectile runtime, so damage lands at the flight's own
    /// contact. Returns whether it fired; the run retries an empty cast sooner.
    /// </summary>
    internal static async Task<bool> SurvivorsCast(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SurvivorsRun run, SurvivorsRoster roster, SurvivorsSkill skill, int rank, int skillIndex)
    {
        (int X, int Y) origin;
        List<(int X, int Y)> aims = [];
        List<(uint Id, double Distance, uint Life, int X, int Y)> inReach = [];
        using (ProjectileMutation(conn))
        {
            double world = ProjectileWorldTime(conn);
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Paused) return false;
            var here = conn.Walk.Position(world);

            foreach (var (id, m) in conn.Monsters)
            {
                if (m.Dead || m.Life == 0 || !m.Hostile) continue;
                var at = m.Walk.Position(world);
                double distance = Dist(here, at);
                if (distance > SurvivorsRules.Range) continue;
                inReach.Add((id, distance, m.Life, at.X, at.Y));
            }
            if (inReach.Count == 0) return false;
            // One projectile per target: the nearest, or the toughest for a few named gems, up to the rank's count.
            var pick = SurvivorsAimsToughest(skill)
                ? inReach.OrderByDescending(t => t.Life).ThenBy(t => t.Distance)
                : inReach.OrderBy(t => t.Distance);
            aims = [.. pick.Take(SurvivorsRules.SkillProjectiles(rank)).Select(t => (t.X, t.Y))];
            origin = here;
        }
        // A herald is an aura pulse: every hostile around the player takes the hit, and the client plays the gem's cast.
        if (skill.Kind == SurvivorsSkillKind.Herald)
        {
            double radius = SurvivorsHeraldRadius * SurvivorsRules.SkillRadius(rank) * run.SkillAreaMultiplier(skillIndex);
            int heraldDamage = (int)(SurvivorsRules.SkillDamage(roster.WeaponDamage, rank) * run.SkillDamageMultiplier(skillIndex));
            int struck = 0;
            foreach (var (id, distance, _, _, _) in inReach)
            {
                if (distance > radius) continue;
                MonsterHit? hit = null;
                SpawnedMonster? target = null;
                lock (conn.Monsters)
                    if (conn.Monsters.TryGetValue(id, out target) && !target.Dead && target.Life > 0)
                        hit = ApplyMonsterHitLocked(conn, target, heraldDamage);
                if (hit is not { } applied) continue;
                struck++;
                await NotifyMonsterHit(remote, stream, ciphers, conn, id, target!, applied, heraldDamage, skill.Name, log: false, melee: true);
            }
            if (struck == 0) return false;
            ushort heraldId = SurvivorsSkillId(skillIndex);
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, heraldId, origin.X, origin.Y),
                $"0x01ce survivors herald 0x{heraldId:x4} {skill.Name} around ({origin.X}, {origin.Y})");
            await SendSurvivorsCooldown(remote, stream, ciphers, conn, skillIndex, rank, [origin]);
            return true;
        }
        if (ResolveProjectileDefinition(skill.Recipe) is not { } definition) return false;        int damage = (int)(SurvivorsRules.SkillDamage(roster.WeaponDamage, rank) * run.SkillDamageMultiplier(skillIndex));
        var packet = DamagePacket.Of(skill.Type, damage, damage);
        var shot = new ProjectileOffense(packet, Spell: true, Accuracy: int.MaxValue, CriticalChance: run.SkillCriticalChance(skillIndex),
            CriticalMultiplier: 200, Label: skill.Name)
        {
            // The client draws the shot: one instant action per target (0x1ce) runs the gem's own use there, so the
            // server's matching projectile is simulated but sends no object of its own.
            Effect = skill.Effect, Level = 1, ClientPredicted = false, HitPacket = true,
        };
        int baseSpeed = SkillsFor(conn).Projectile(definition.Metadata)?.Speed ?? 1000;
        double speed = ProjectileSpeed.CellsPerSecond(baseSpeed, _ => 0, _ => 0,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED")) * run.SkillProjectileSpeedMultiplier(skillIndex);
        if (!double.IsFinite(speed) || speed <= 0) return false;
        var profile = definition.Parameters.Resolve(_ => 0, speed, ArrowsFor(conn, skill.Effect));
        profile = profile with { Radius = profile.Radius * SurvivorsRules.SkillRadius(rank) * run.SkillAreaMultiplier(skillIndex) };
        var owner = new ProjectileActorKey(PlayerId, conn.ProjectileEpoch);
        var runtime = ProjectilesFor(remote, stream, ciphers, conn);
        long now = ProjectileRuntime.Time(conn.Clock.Elapsed.TotalSeconds);
        foreach (var aim in aims)
        {
            var use = new ProjectileUse(profile, owner, owner, 1, ProjectileSession.Vector(origin), ProjectileSession.Vector(aim))
            { Payload = shot, ReturnAnchor = owner };
            runtime.Schedule(now, () => true, () => use);
        }
        await SendSurvivorsCooldown(remote, stream, ciphers, conn, skillIndex, rank, aims);
        return true;
    }

    static async Task SurvivorsLeech(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int life)
    {
        uint after;
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0) return;
            after = (uint)Math.Min(conn.PlayerMaxLife, (long)conn.PlayerLife + life);
            if (after == conn.PlayerLife) return;
            conn.PlayerLife = after;
        }
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, after), null);
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
        var taken = run.Items;
        var worn = new HashSet<uint>();
        foreach (byte row in ItemModStats.EquipmentRows)
            foreach (var item in ItemsOf(conn).Items(row))
                if (item.Item.NameWords is [var word, ..]) worn.Add(word);
        var pool = new List<int>();
        for (int i = 0; i < all.Count; i++)
        {
            if (taken.Contains(i)) continue;
            if (UniquesFor(conn).Create(all[i].Name, Random.Shared) is { NameWords: [var own, ..] } && worn.Contains(own)) continue;
            pool.Add(i);
        }
        return pool;
    }

    internal static async Task SurvivorsOfferIfDue(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsRun run, IReadOnlyList<SurvivorsSkill> catalog)
    {
        if (!run.NeedsOffer) return;
        var blessings = SurvivorsBlessingsFor(conn);
        var supports = SurvivorsSupportsFor(conn);
        var offer = run.DealOffer(Random.Shared, catalog.Count, blessings.Count, supports.Count, SurvivorsItemPool(conn, run));
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
        conn.SurvivorsOfferItems = null;
        var items = ItemsOf(conn);
        await Send(remote, stream, ciphers,
            InstanceItems.InventoryChange(SurvivorsOfferInventory, ++conn.SurvivorsOfferRevision, offered, [], NpcShops.Manager),
            $"0x00bf survivors offer cleared after choosing {name}");
        var catalog = SurvivorsCatalogFor(conn);
        string target = chosen.Target >= 0 && chosen.Target < catalog.Count ? catalog[chosen.Target].Name : "the skill";
        // A unique item goes straight onto the character: the run's gear choice.
        bool equipped = unique is not null && await SurvivorsEquip(remote, stream, ciphers, conn, unique);
        await conn.Commands!.Reply(chosen.Kind switch
        {
            SurvivorsChoiceKind.Blessing => $"Took {name}, level {run.BlessingLevel((SurvivorsBlessing)chosen.Index)}.",
            SurvivorsChoiceKind.Support => $"Linked {name} to {target}.",
            SurvivorsChoiceKind.Item => equipped ? $"Equipped {name}." : $"Took {name}, but no equipment slot or inventory room was free.",
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
