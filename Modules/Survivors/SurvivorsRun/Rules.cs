using System.Collections.Concurrent;
using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
using ChatCommand = GameServer.Modding.ChatCommand;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

using GameServer.Modules.Survivors;

using static GameServer.Modules.Survivors.SurvivorsSkills;
using static GameServer.Modules.Survivors.SurvivorsRewards;

namespace GameServer.Modules.Survivors;

static partial class SurvivorsRunRules
{
    // The survivors mode (Agents/docs/game/survivors-mode.md): a Vampire Survivors-style run on the current area's own
    // monster packs. The run's own skills cast themselves at the nearest monster, so the player only moves.
    internal const int SurvivorsStepMs = 100;

    /// <summary>The ring wave cadence: a closing ring halfway through each 135 s boss cycle, as the reference does.</summary>
    internal const double SurvivorsRingIntervalSeconds = 135;

    /// <summary>The area's boss arrives this long after a stage starts: its death is what opens the next stage.</summary>
    internal const double SurvivorsBossDelaySeconds = 15;

    /// <summary>The areas a run moves through, one "level" every <see cref="SurvivorsRules.StageSeconds"/> (Act 1).</summary>
    internal static readonly string[] SurvivorsLevels =
        ["1_1_1", "1_1_2", "1_1_3", "1_1_5", "1_1_6", "1_1_9", "1_1_11_2"];

    /// <summary>A run that changed area: the run continues on the next connection.</summary>
    internal sealed record SurvivorsCarry(SurvivorsRun Run, int Stage, DateTime At, IReadOnlyCollection<uint>? ItemIds = null,
        string? Character = null);

    internal static readonly ConcurrentDictionary<uint, SurvivorsCarry> SurvivorsCarried = new();

    internal static int SurvivorsMaxAlive() =>
        int.TryParse(Environment.GetEnvironmentVariable("Game_SURVIVORS_MAX_ALIVE"), out int n)
            ? Math.Clamp(n, 10, 800) : SurvivorsRules.DefaultMaxAlive;

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
        // The rank-1 anchor is the starting tier pool, not the whole roster: the reference opens against its weakest
        // tiers, so the first cast kills in one or two hits and the tier ladder carries the rest.
        var start = SurvivorsRules.WavePool(0, varieties.Count)
            .Select(i => Monsters.BaseLife(varieties[i], level) ?? 0).Where(l => l > 0).OrderBy(l => l).ToList();
        int startLife = start.Count == 0 ? (lives.Count == 0 ? 20 : lives[lives.Count / 2]) : start[start.Count / 2];
        int topLife = lives.Count == 0 ? startLife : lives[^1];
        var ranged = varieties.Where(v => SurvivorsIsRanged(conn, v)).ToList();
        return new(varieties, bosses, level, Math.Max(1, startLife), Math.Max(1, topLife), Ranged: ranged);
    }

    /// <summary>Whether a variety's attack is a projectile: a bow/wand/mortar the player must dodge.</summary>
    internal static bool SurvivorsIsRanged(ClientState conn, MonsterCatalog.Variety variety)
    {
        var catalog = SkillsFor(conn);
        int effect = MavenNucleus.CombatEffect(catalog, variety);
        return MonsterAttackIsRanged(catalog, variety, effect);
    }

    /// <summary>The roster's ranged varieties (the subset whose attack is a projectile).</summary>
    internal static IReadOnlyList<MonsterCatalog.Variety> SurvivorsRangedPool(SurvivorsRoster roster) =>
        roster.Ranged is { Count: > 0 } ranged ? ranged : [];

    internal static async Task StartSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        int? only = null)
    {
        if (!Enabled(conn)) return;
        if (StateOf(conn).Survivors is not null)
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
        if (StateOf(conn).SurvivorsPausedByWindow && StateOf(conn).SurvivorsOffer is null)
        {
            StateOf(conn).SurvivorsPausedByWindow = false;
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
        var supports = SurvivorsSupportsFor(conn);
        roster = roster with { SkillScale = SurvivorsDamageScale(conn, catalog, roster) };
        var run = new SurvivorsRun(SurvivorsMaxAlive());
        run.Learn(only ?? 0);
        var ownership = BindRun(conn, run);
        await SurvivorsResetInventory(remote, stream, ciphers, conn);
        if (!ownership.Current) return;
        await SurvivorsGiveFlask(remote, stream, ciphers, conn);
        if (!ownership.Current) return;
        StateOf(conn).SurvivorsCatalog = catalog;
        StateOf(conn).SurvivorsSupports = supports;
        StateOf(conn).Stage = 0;
        await conn.Commands!.Reply(
            "--- Survivors ---:\n" +
            $"Survive {SurvivorsRules.VictorySeconds / 60:0} minutes. Your skills cast themselves at the nearest monster. " +
            $"Every {SurvivorsRules.StageSeconds / 60:0.#} minutes the run moves to the next area, but only after the " +
            $"area's lore boss falls.\n" +
            $"Start: {catalog[0].Name}. Each level opens the reward window with three upgrades (six skill slots, six " +
            "blessings, supports and uniques); /survivors stop ends the run. The run's skills and their cooldowns are " +
            "on your skill bar. /survivors curve prints the difficulty and power curve.");
        if (!ownership.Current) return;
        Console.WriteLine($"[gateway] {remote} survivors run started in {conn.Layout.Id} at level {roster.Level}: " +
            $"{roster.Varieties.Count} varieties, {roster.Bosses.Count} boss(es), {run.MaxAlive} alive at most, " +
            $"base damage {roster.WeaponDamage} (skill scale {roster.SkillScale:0.###}), {catalog.Count} skills, " +
            $"{SurvivorsBlessingsFor(conn).Count} blessings");
        // The run hides the loot already lying in the area: the client forgets the objects and the instance drops them.
        await SurvivorsClearGround(remote, stream, ciphers, conn);
        if (!ownership.Current) return;
        // The run's skills go out as Actor skills, which puts their gems and cooldowns on the skill bar.
        await SendSkills(remote, stream, ciphers, conn);
        if (!ownership.Current) return;
        await SurvivorsPublishHeralds(remote, stream, ciphers, conn, run, catalog);
        if (!ownership.Current) return;
        StartRunner(conn, token => RunSurvivors(remote, stream, ciphers, conn, run, roster, catalog, stage: 0, token));
    }

    /// <summary>Restores a run that changed area, on the next connection.</summary>
    internal static async Task ResumeSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!Enabled(conn) || StateOf(conn).Survivors is not null || conn.Character is null) return;
        if (!SurvivorsCarried.TryRemove(conn.Ticket, out var carry) || carry.Character != conn.Character) return;
        if (DateTime.UtcNow - carry.At > TimeSpan.FromMinutes(5)) return;
        if (SurvivorsRosterFor(conn) is not { Varieties.Count: > 0 } roster || SurvivorsCatalogFor(conn) is not { Count: > 0 } catalog)
        {
            Console.WriteLine($"[gateway] {remote} survivors run cannot resume in {conn.Layout.Id}: " +
                $"the area has no spawnable varieties");
            return;
        }
        roster = roster with { SkillScale = SurvivorsDamageScale(conn, catalog, roster) };
        var ownership = BindRun(conn, carry.Run);
        StateOf(conn).Stage = carry.Stage;
        StateOf(conn).SurvivorsCatalog = catalog;
        StateOf(conn).SurvivorsSupports = SurvivorsSupportsFor(conn);
        // The uniques the run equipped stay exempt from their requirements on the new connection.
        foreach (uint itemId in carry.ItemIds ?? []) StateOf(conn).SurvivorsItemIds.Add(itemId);
        // Admission built pools before this run's item ids were known; refresh the active equipment and native checks.
        lock (conn.Monsters)
        {
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "survivors restored equipment");
        if (!ownership.Current) return;
        Console.WriteLine($"[gateway] {remote} survivors run resumes in {conn.Layout.Id} (level {carry.Stage + 1}, " +
            $"{carry.Run.Kills} kills, run level {carry.Run.Level})");
        // The player object was created without the run's skills: publish them, and the bar with them.
        await SendSkills(remote, stream, ciphers, conn);
        if (!ownership.Current) return;
        await SendSkillBar(remote, stream, ciphers, conn);
        if (!ownership.Current) return;
        await SurvivorsPublishHeralds(remote, stream, ciphers, conn, carry.Run, catalog);
        if (!ownership.Current) return;
        // The new area's ground too: the run keeps the map clear.
        await SurvivorsClearGround(remote, stream, ciphers, conn);
        if (!ownership.Current) return;
        await conn.Commands!.Reply($"--- Level {carry.Stage + 1} ---\nThe run resumes: {SurvivorsClock(carry.Run.Elapsed)}, " +
            $"{carry.Run.Kills} kills.");
        if (!ownership.Current) return;
        StartRunner(conn, token => RunSurvivors(remote, stream, ciphers, conn, carry.Run, roster, catalog, carry.Stage, token));
    }

    /// <summary>Ends the run: no more spawns or casts, the run's skills off the Actor, and the result in chat. Safe when no
    /// run is active.</summary>
    internal static async Task EndSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
    {
        if (StateOf(conn).Survivors is not { } run) return;
        Release(conn, run);
        SurvivorsCarried.TryRemove(conn.Ticket, out _);
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
            if (StateOf(conn).Survivors is not null) await EndSurvivors(remote, stream, ciphers, conn, "stopped");
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
                "--- Survivors curve (14 -> 500 horde, +75% life, +180% damage, a boss a minute) ---:",
                " time   horde  monster life  monster damage  boss life  boss xp  rank damage",
            };
            for (int step = 0; step <= 7; step++)
            {
                double t = step * SurvivorsRules.VictorySeconds / 7;
                int stage = Math.Clamp(step, 0, 6);
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
            if (StateOf(conn).Survivors is not { } devRun || StateOf(conn).SurvivorsCatalog is not { } devCatalog)
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
            if (StateOf(conn).Survivors is not { } levelling) { await conn.Commands!.Reply("No survivors run is active."); return; }
            int before = levelling.Level;
            for (int i = 0; i < 4; i++) levelling.RecordKill(MonsterRarity.Unique);
            await conn.Commands!.Reply($"Run level {before} -> {levelling.Level} (dev).");
            return;
        }
        // Dev: learn the first Herald skill at once, so its aura pulse can be watched without hunting the offer list.
        if (what == "herald")
        {
            if (StateOf(conn).Survivors is not { } heraldRun) { await conn.Commands!.Reply("No survivors run is active."); return; }
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
            if (StateOf(conn).Survivors is not { } linked || StateOf(conn).SurvivorsSupports is not { } linkCatalog
                || StateOf(conn).SurvivorsCatalog is not { } linkSkills)
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
            int index = wanted.Length == 0 && StateOf(conn).Survivors is { } active
                ? active.Skills.Keys.OrderBy(k => k).DefaultIfEmpty(-1).First()
                : matrixCatalog.ToList().FindIndex(s => s.Name.ToLowerInvariant().Contains(wanted));
            if (index < 0 || index >= matrixCatalog.Count)
            {
                await conn.Commands!.Reply("Usage: /survivors matrix <gem>, e.g. /survivors matrix fireball.");
                return;
            }
            StateOf(conn).SurvivorsCatalog ??= matrixCatalog;
            StateOf(conn).SurvivorsSupports ??= SurvivorsSupportsFor(conn);
            var matrixSupports = StateOf(conn).SurvivorsSupports!;
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
            if (StateOf(conn).Survivors is null) { await conn.Commands!.Reply("No survivors run is active."); return; }
            await EndSurvivors(remote, stream, ciphers, conn, "stopped");
            return;
        }
        if (what == "pick")
        {
            if (StateOf(conn).Survivors is not { } picking)
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
        if (StateOf(conn).Survivors is not { } run)
        {
            await conn.Commands!.Reply("No survivors run is active. /survivors start begins one in this area.");
            return;
        }
        string offer = "";
        if (run.Offer.Count > 0 && StateOf(conn).SurvivorsOffer is { } shown)
        {
            var lines = new List<string>();
            foreach (var choice in run.Offer)
            {
                uint id = shown.FirstOrDefault(e => e.Value == choice).Key;
                string label = id != 0 && StateOf(conn).SurvivorsOfferNames?.GetValueOrDefault(id) is { } named
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

    internal static async Task RunSurvivors(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SurvivorsRun run, SurvivorsRoster roster, IReadOnlyList<SurvivorsSkill> catalog, int stage, CancellationToken token)
    {
        // Each stage is one area and one lore boss: the boss arrives 15 s in, and the way out opens only after it falls.
        double stageStart = stage * SurvivorsRules.StageSeconds;
        double last = conn.Clock.Elapsed.TotalSeconds, nextSpawn = 0,
            nextBoss = Math.Max(stageStart + SurvivorsBossDelaySeconds, run.Elapsed + 5),
            nextRing = Math.Max(stageStart + SurvivorsRingIntervalSeconds / 2, run.Elapsed + 10),
            nextRanged = Math.Max(stageStart + SurvivorsRules.RangedWaveIntervalSeconds, run.Elapsed + 5),
            nextReport = 10, nextSweep = 0;
        string bossName = roster.Bosses.Count > 0 ? roster.Bosses[0].Name : "";
        uint stageBoss = 0;
        bool bossSpawned = false, bossDefeated = false, bossWarned = false;
        int bossNumber = 0;
        // Every boss's id and the run experience its kill pays in total (the ordinary unique kill pays 50 of it).
        var bossBonuses = new Dictionary<uint, int>();
        bool carried = false;
        var horde = new HashSet<uint>();
        var nextFire = new Dictionary<int, double>();
        try
        {
            while (!token.IsCancellationRequested && !conn.Closed && !conn.LeavingArea && conn.PendingTransfer is null && ReferenceEquals(StateOf(conn).Survivors, run))
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                double step = now - last;
                last = now;
                if (conn.Paused || step <= 0)
                {
                    await Task.Delay(SurvivorsStepMs, token);
                    continue;
                }
                run.Advance(step);
                if (conn.PlayerLife == 0) { await EndSurvivors(remote, stream, ciphers, conn, "defeat"); break; }
                if (run.Elapsed >= SurvivorsRules.VictorySeconds) { await EndSurvivors(remote, stream, ciphers, conn, "victory"); break; }
                // Bosses pay their ladder experience when they fall; only the stage gate's death opens the way.
                foreach (var (id, total) in bossBonuses.ToList())
                {
                    bool dead;
                    lock (conn.Monsters) dead = !conn.Monsters.TryGetValue(id, out var boss) || boss.Dead || boss.Life == 0;
                    if (!dead) continue;
                    bossBonuses.Remove(id);
                    int bonus = total - SurvivorsRules.ExperienceForKill(MonsterRarity.Unique);
                    if (bonus > 0) run.RecordExperience(bonus);
                    if (id == stageBoss)
                    {
                        bossDefeated = true;
                        await conn.Commands!.Reply($"{bossName} falls. The way onward opens. +{total} run experience.");
                    }
                    else await conn.Commands!.Reply($"A guardian falls. +{total} run experience.");
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
                        var transfer = new PendingTransfer(nextArea, 0, false);
                        StateOf(conn).StageTransfer = transfer;
                        PendingRuns[conn] = new SurvivorsCarry(run, stage + 1, DateTime.UtcNow,
                            [.. StateOf(conn).SurvivorsItemIds], conn.Character);
                        carried = true;
                        string nextName = AreaTransitionCatalog.NameOf(nextArea) ?? SurvivorsLevels[stage + 1];
                        await conn.Commands!.Reply($"--- Level {stage + 2} ---\nMoving to {nextName}.");
                        await ChangeArea(remote, stream, ciphers, conn, transfer, "survivors level");
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
                // The stage gate boss arrives 15 s in; after it, a boss every minute keeps the pressure on.
                if (roster.Bosses.Count > 0 && run.Elapsed >= nextBoss)
                {
                    if (!bossSpawned)
                    {
                        stageBoss = await SpawnSurvivorsBoss(remote, stream, ciphers, conn, roster, run, stage, horde);
                        if (stageBoss != 0)
                        {
                            bossSpawned = true;
                            bossNumber = 1;
                            bossBonuses[stageBoss] = SurvivorsRules.BossExperience(stage);
                            nextBoss = run.Elapsed + SurvivorsRules.BossEverySeconds;
                        }
                        else nextBoss = run.Elapsed + 5;
                    }
                    else
                    {
                        uint id = await SpawnSurvivorsBoss(remote, stream, ciphers, conn, roster, run, stage, horde,
                            bossIndex: bossNumber);
                        if (id != 0)
                        {
                            bossBonuses[id] = SurvivorsRules.BossExperience(bossNumber);
                            bossNumber++;
                            nextBoss = run.Elapsed + SurvivorsRules.BossEverySeconds;
                        }
                        else nextBoss = run.Elapsed + 5;
                    }
                }
                // Halfway through the stage a ring of the moment's weakest closes in on the player.
                if (run.Elapsed >= nextRing)
                {
                    nextRing = run.Elapsed + SurvivorsRingIntervalSeconds;
                    await SpawnSurvivorsRing(remote, stream, ciphers, conn, run, roster, horde);
                }
                // Every twenty seconds a ring of archers/wands shoots from 22 cells: standing still is not safe.
                if (run.Elapsed >= nextRanged)
                {
                    nextRanged = run.Elapsed + SurvivorsRules.RangedWaveIntervalSeconds;
                    await SpawnSurvivorsRangedRing(remote, stream, ciphers, conn, run, roster, horde);
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
                await Task.Delay(SurvivorsStepMs, token);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while spawning or casting.
        }
        finally
        {
            if (!carried && ReferenceEquals(StateOf(conn).Survivors, run))
            {
                Release(conn, run);
                Console.WriteLine($"[gateway] {remote} survivors run ended early at {SurvivorsClock(run.Elapsed)} " +
                    $"with {run.Kills} kills (area change or closed connection)");
            }
        }
    }

    internal static async Task SpawnSurvivorsWave(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
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
        var ranged = SurvivorsRangedPool(roster);
        for (int i = 0; i < spawn; i++)
        {
            // Every third body is a ranged variety when the area has one: the player has to keep moving.
            var variety = ranged.Count > 0 && i % 3 == 2
                ? ranged[Random.Shared.Next(ranged.Count)]
                : roster.Varieties[pool[Random.Shared.Next(pool.Length)]];
            if (SurvivorsSpawnCell(conn, 30) is not { } cell) continue;
            horde.Add(await SpawnMonster(remote, stream, ciphers, conn, variety, roster.Level, SurvivorsRarity(run), cell.X, cell.Y,
                log: false, extraMods: SurvivorsMonsterMods(run.Elapsed)));
        }
    }

    /// <summary>
    /// A ring of the area's ranged varieties, every twenty run-seconds: the archers/wands/mortars that shoot at the
    /// player from 22 cells, so standing still means eating projectiles.
    /// </summary>
    internal static async Task SpawnSurvivorsRangedRing(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsRun run, SurvivorsRoster roster, HashSet<uint> horde)
    {
        var ranged = SurvivorsRangedPool(roster);
        if (ranged.Count == 0) return;
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        int count = SurvivorsRules.RangedWaveCount(SurvivorsRules.Progress(run.Elapsed));
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            var variety = ranged[Random.Shared.Next(ranged.Count)];
            double angle = i * Math.Tau / count;
            int x = (int)Math.Round(px + Math.Cos(angle) * 22), y = (int)Math.Round(py + Math.Sin(angle) * 22);
            if (conn.Layout.GridW > 0 && (x < 0 || y < 0 || x >= conn.Layout.GridW || y >= conn.Layout.GridH)) continue;
            if (!conn.Layout.WalkableFor(x, y, 0) || conn.Barrier.Contains((x, y))) continue;
            horde.Add(await SpawnMonster(remote, stream, ciphers, conn, variety, roster.Level, SurvivorsRarity(run), x, y,
                log: false, extraMods: SurvivorsMonsterMods(run.Elapsed)));
            spawned++;
        }
        Console.WriteLine($"[gateway] {remote} survivors ranged ring at {SurvivorsClock(run.Elapsed)}: {spawned} of {count} spawned");
        if (spawned > 0) await conn.Commands!.Reply("Archers close in!");
    }

    /// <summary>
    /// Halfway between bosses, a closing ring of the moment's weakest: 28 bodies at the start, 88 by the end. The
    /// reference mode's surround wave, which punishes standing still more than the trickle does.
    /// </summary>
    internal static async Task SpawnSurvivorsRing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
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

    internal static async Task<uint> SpawnSurvivorsBoss(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        SurvivorsRoster roster, SurvivorsRun run, int stage, HashSet<uint> horde, int bossIndex = 0)
    {
        if (roster.Bosses.Count == 0) return 0;
        var boss = roster.Bosses[bossIndex % roster.Bosses.Count];
        if (SurvivorsSpawnCell(conn, 24) is not { } cell)
        {
            Console.WriteLine($"[gateway] {remote} survivors: no cell for boss {boss.Name}");
            return 0;
        }
        uint id = await SpawnMonster(remote, stream, ciphers, conn, boss, roster.Level, MonsterRarity.Unique, cell.X, cell.Y,
            mods: SurvivorsBossMods(roster, boss, run, stage));
        horde.Add(id);
        string area = SurvivorsAreaName(conn.Layout.Id);
        await conn.Commands!.Reply(bossIndex == 0
            ? $"{boss.Name} guards {area}. Defeat it to open the way onward."
            : $"{boss.Name} rises in {area}.");
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
    internal static MonsterModSet SurvivorsBossMods(SurvivorsRoster roster, MonsterCatalog.Variety boss, SurvivorsRun run, int stage)
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
    internal static string SurvivorsAreaName(string id) =>
        AreaTransitionCatalog.HashOf(id) is { } hash ? AreaTransitionCatalog.NameOf(hash) ?? id : id;

    /// <summary>A spawned monster's mods for the run's clock: +75% life and +90% damage at 15:00, as the monster mods'
    /// own stats (MonsterModEffects).</summary>
    internal static MonsterModSet SurvivorsMonsterMods(double elapsed)
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

    internal static MonsterRarity SurvivorsRarity(SurvivorsRun run)    {
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

}
