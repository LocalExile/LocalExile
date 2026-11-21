using System.Collections.Concurrent;
using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
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

    /// <summary>The area's own champion arrives this long after a stage starts; the gate boss comes at the stage's end.</summary>
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
    internal static SurvivorsRoster? SurvivorsRosterFor(Player player)
    {
        if (Packs.For(player.Layout.Id) is not { } area) return null;
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
        var ranged = varieties.Where(v => SurvivorsIsRanged(player, v)).ToList();
        return new(varieties, bosses, level, Math.Max(1, startLife), Math.Max(1, topLife), Ranged: ranged);
    }

    /// <summary>Whether a variety's attack is a projectile: a bow/wand/mortar the player must dodge.</summary>
    internal static bool SurvivorsIsRanged(Player player, MonsterCatalog.Variety variety)
    {
        var catalog = SkillsFor(player);
        int effect = MavenNucleus.CombatEffect(catalog, variety);
        return MonsterAttackIsRanged(catalog, variety, effect);
    }

    /// <summary>The roster's ranged varieties (the subset whose attack is a projectile).</summary>
    internal static IReadOnlyList<MonsterCatalog.Variety> SurvivorsRangedPool(SurvivorsRoster roster) =>
        roster.Ranged is { Count: > 0 } ranged ? ranged : [];

    internal static async Task StartSurvivors(Player player,
        int? only = null)
    {
        if (!Enabled(player)) return;
        if (StateOf(player).Survivors is not null)
        {
            await player.Commands!.Reply("A survivors run is already active. End it with /survivors stop.");
            return;
        }
        if (player.PlayerLife == 0)
        {
            await player.Commands!.Reply("You are dead. Resurrect first.");
            return;
        }
        // A previous run's open window may have left the clock stopped; a new run always begins unpaused.
        if (StateOf(player).SurvivorsPausedByWindow && StateOf(player).SurvivorsOffer is null)
        {
            StateOf(player).SurvivorsPausedByWindow = false;
            player.PauseHeld = false;
            if (!player.Clock.IsRunning) player.Clock.Start();
            await Send(player, InstancePause.State(false),
                "0x01d3 resumed (a previous window was closed)");
        }
        if (SurvivorsRosterFor(player) is not { } roster)
        {
            await player.Commands!.Reply($"The area {player.Layout.Id} has no monster packs to draw a horde from. Try an area with monsters, such as The Coast.");
            return;
        }
        if (roster.Varieties.Count == 0)
        {
            await player.Commands!.Reply($"The area {player.Layout.Id} has no supported monsters for a run.");
            return;
        }
        if (SurvivorsCatalogFor(player) is not { Count: > 0 } catalog)
        {
            await player.Commands!.Reply("No run skills are available: the item catalogue or the projectile definitions are missing.");
            return;
        }
        // Every run starts from an empty character with no skill: the opening window offers the first one, and each
        // following stage teaches the next, so the player chooses the build instead of receiving a fixed gem.
        var supports = SurvivorsSupportsFor(player);
        roster = roster with { SkillScale = SurvivorsDamageScale(player, catalog, roster) };
        var run = new SurvivorsRun(SurvivorsMaxAlive());
        if (only is int pick) run.Learn(pick);
        else run.RecordExperience(SurvivorsRules.LevelRequirement(1));
        var ownership = BindRun(player, run);
        await SurvivorsResetInventory(player);
        if (!ownership.Current) return;
        await SurvivorsGiveFlask(player);
        if (!ownership.Current) return;
        StateOf(player).SurvivorsCatalog = catalog;
        StateOf(player).SurvivorsSupports = supports;
        StateOf(player).Stage = 0;
        // A run has no entry protection: the horde (and later the gate boss) must start pressing at once.
        if (InGracePeriod(player)) await EndGracePeriod(player, action: true);
        await player.Commands!.Reply(
            "--- Survivors ---:\n" +
            $"Survive seven stages. Your skills cast themselves at the nearest monster. " +
            $"Every {SurvivorsRules.StageSeconds / 60:0.#} minutes the stage's gate boss rises: kill it to move on, " +
            $"and the seventh gate boss wins the run.\n" +
            (only is int chosen
                ? $"Start: {catalog[chosen].Name}. "
                : "Choose your first skill in the opening window; the next skill is offered once the current one has " +
                  "all its supports. ") +
            "Each level opens the reward window (six choices, says which skill each upgrade is for, and always lists " +
            "the supports a skill is missing); /survivors stop ends the run. /survivors curve prints the difficulty and " +
            "power curve.");
        if (!ownership.Current) return;
        Console.WriteLine($"[gateway] {player.Connection.Remote} survivors run started in {player.Layout.Id} at level {roster.Level}: " +
            $"{roster.Varieties.Count} varieties, {roster.Bosses.Count} boss(es), {run.MaxAlive} alive at most, " +
            $"base damage {roster.WeaponDamage} (skill scale {roster.SkillScale:0.###}), {catalog.Count} skills, " +
            $"{SurvivorsBlessingsFor(player).Count} blessings");
        // The run hides the loot already lying in the area: the client forgets the objects and the instance drops them.
        await SurvivorsClearGround(player);
        if (!ownership.Current) return;
        // The run's skills go out as Actor skills, which puts their gems and cooldowns on the skill bar.
        await SendSkills(player);
        if (!ownership.Current) return;
        await SurvivorsPublishHeralds(player, run, catalog);
        if (!ownership.Current) return;
        // Deal the opening window before the runner starts: the clock stops here, so the first spawn waits for the
        // pick and monsters are already frozen at the very start (the runner would otherwise spawn a wave first).
        await SurvivorsOfferIfDue(player, run, catalog);
        if (!ownership.Current) return;
        StartRunner(player, token => RunSurvivors(player, run, roster, catalog, stage: 0, token));
    }

    /// <summary>Restores a run that changed area, on the next connection.</summary>
    internal static async Task ResumeSurvivors(Player player)
    {
        if (!Enabled(player) || StateOf(player).Survivors is not null || player.Character.Name is null) return;
        if (!SurvivorsCarried.TryRemove(player.Ticket, out var carry) || carry.Character != player.Character.Name) return;
        if (DateTime.UtcNow - carry.At > TimeSpan.FromMinutes(5)) return;
        if (SurvivorsRosterFor(player) is not { Varieties.Count: > 0 } roster || SurvivorsCatalogFor(player) is not { Count: > 0 } catalog)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} survivors run cannot resume in {player.Layout.Id}: " +
                $"the area has no spawnable varieties");
            return;
        }
        roster = roster with { SkillScale = SurvivorsDamageScale(player, catalog, roster) };
        var ownership = BindRun(player, carry.Run);
        StateOf(player).Stage = carry.Stage;
        StateOf(player).SurvivorsCatalog = catalog;
        StateOf(player).SurvivorsSupports = SurvivorsSupportsFor(player);
        // The uniques the run equipped stay exempt from their requirements on the new connection.
        foreach (uint itemId in carry.ItemIds ?? []) StateOf(player).SurvivorsItemIds.Add(itemId);
        // Admission built pools before this run's item ids were known; refresh the active equipment and native checks.
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        await SendCharacterStats(player, PlayerStatChange.Build);
        if (!ownership.Current) return;
        Console.WriteLine($"[gateway] {player.Connection.Remote} survivors run resumes in {player.Layout.Id} (level {carry.Stage + 1}, " +
            $"{carry.Run.Kills} kills, run level {carry.Run.Level})");
        // The player object was created without the run's skills: publish them, and the bar with them.
        await SendSkills(player);
        if (!ownership.Current) return;
        await SendSkillBar(player);
        if (!ownership.Current) return;
        await SurvivorsPublishHeralds(player, carry.Run, catalog);
        if (!ownership.Current) return;
        // The new area's ground too: the run keeps the map clear.
        await SurvivorsClearGround(player);
        if (!ownership.Current) return;
        await player.Commands!.Reply($"--- Level {carry.Stage + 1} ---\nThe run resumes: {SurvivorsClock(carry.Run.Elapsed)}, " +
            $"{carry.Run.Kills} kills.");
        if (!ownership.Current) return;
        StartRunner(player, token => RunSurvivors(player, carry.Run, roster, catalog, carry.Stage, token));
    }

    /// <summary>Ends the run: no more spawns or casts, the run's skills off the Actor, and the result in chat. Safe when no
    /// run is active.</summary>
    internal static async Task EndSurvivors(Player player, string why)
    {
        if (StateOf(player).Survivors is not { } run) return;
        await SurvivorsClearOrbs(player);
        await SurvivorsClearGroundZones(player);
        Release(player, run);
        SurvivorsCarried.TryRemove(player.Ticket, out _);
        await SurvivorsClearHeralds(player);
        // Take the run's skills off the Actor and the bar: the client prunes the slots the ids no longer fill.
        if (player.Connection.PlayerObjectCreated)
        {
            await SendSkills(player);
            await SendSkillBar(player);
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
        if (player.Commands is { } commands) await commands.Reply(text);
    }

    internal static string SurvivorsClock(double seconds) =>
        $"{(int)(seconds / 60):00}:{(int)(seconds % 60):00}";

    /// <summary>The /survivors command: start, stop, pick a choice without the window, or report the run.</summary>
    internal static async Task SurvivorsCommand(Player player, string[] args)
    {
        string what = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        if (what == "start") { await StartSurvivors(player); return; }
        // Dev: restart the run with one named gem only, so each gem and its supports can be verified in isolation.
        if (what == "test")
        {
            var known = SurvivorsCatalogFor(player);
            string wanted = string.Join(' ', args.Skip(1)).Trim().ToLowerInvariant();
            int index = known.ToList().FindIndex(s => s.Name.ToLowerInvariant().Contains(wanted));
            if (wanted.Length == 0 || index < 0)
            {
                await player.Commands!.Reply("Usage: /survivors test <gem name>, e.g. /survivors test fireball.");
                return;
            }
            if (StateOf(player).Survivors is not null) await EndSurvivors(player, "stopped");
            await StartSurvivors(player, index);
            await player.Commands!.Reply($"Test run: {known[index].Name} only (dev).");
            return;
        }
        // Dev: print the reference's difficulty and power curve, for eyeballing a run's pacing without waiting 15 min.
        if (what == "curve")
        {
            var roster = SurvivorsRosterFor(player);
            var lines = new List<string>
            {
                "--- Survivors curve (14 -> 220 horde, x6 life, x3 damage, DPS threat, a boss a minute) ---:",
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
                    $"x{SurvivorsRules.MonsterLifeScale(t) * SurvivorsRules.EnemyLifeMultiplier:0.00}          " +
                    $"x{SurvivorsRules.MonsterDamageScale(t) * SurvivorsRules.EnemyDamageMultiplier:0.00}          " +
                    $"x{SurvivorsRules.BossLifeRatio(stage),5:0.00}  +{SurvivorsRules.BossExperience(stage),4}  {rank}");
            }
            if (roster is { } area)
                lines.Add($"Area {SurvivorsAreaName(player.Layout.Id)}: tiers {area.Varieties.Count}, opening anchor " +
                    $"{area.WeaponDamage} life, top {area.TopLife} life, boss 0 {Math.Round(SurvivorsPoolTop(area, 0) * SurvivorsRules.BossLifeRatio(0))} life.");
            await player.Commands!.Reply(string.Join('\n', lines));
            return;
        }
        // Dev: set every learned skill's rank, for the verification pass. (A "learn everything" command is not kept:
        // the client's bar holds only a few run skills, and 30 of them close the client.)
        if (what == "rank")
        {
            if (StateOf(player).Survivors is not { } devRun || StateOf(player).SurvivorsCatalog is not { } devCatalog)
            {
                await player.Commands!.Reply("No survivors run is active.");
                return;
            }
            int rank = args.Length > 1 && int.TryParse(args[1], out int wantedRank)
                ? Math.Clamp(wantedRank, 1, SurvivorsRules.MaxRank) : SurvivorsRules.MaxRank;
            foreach (int index in devRun.Skills.Keys.ToList()) devRun.SetRank(index, rank);
            await SendSkills(player);
            await SendSkillBar(player);
            await player.Commands!.Reply($"Every learned skill is at rank {rank} (dev).");
            return;
        }
        // Dev: bank a level without waiting for the horde to die, so the choice window can be tested at once.
        if (what == "level")
        {
            if (StateOf(player).Survivors is not { } levelling) { await player.Commands!.Reply("No survivors run is active."); return; }
            int before = levelling.Level;
            for (int i = 0; i < 4; i++)
            {
                levelling.RecordKill(MonsterRarity.Unique);
                levelling.RecordExperience(SurvivorsRules.LevelRequirement(levelling.Level));
            }
            await player.Commands!.Reply($"Run level {before} -> {levelling.Level} (dev).");
            return;
        }
        // Dev: learn the first Herald skill at once, so its aura pulse can be watched without hunting the offer list.
        if (what == "herald")
        {
            if (StateOf(player).Survivors is not { } heraldRun) { await player.Commands!.Reply("No survivors run is active."); return; }
            var heraldCatalog = SurvivorsCatalogFor(player);
            int index = heraldCatalog.ToList().FindIndex(s => s.Kind == SurvivorsSkillKind.Herald);
            if (index < 0) { await player.Commands!.Reply("No Herald skill gem in the catalogue."); return; }
            heraldRun.Learn(index);
            await player.Commands!.Reply($"Learned {heraldCatalog[index].Name} (herald, dev).");
            await SendSkills(player);
            await SendSkillBar(player);
            await SurvivorsPublishHeralds(player, heraldRun, heraldCatalog);
            return;
        }
        // Dev: link a named support to the first learned skill, for the per-support verification pass.
        if (what == "link")
        {
            if (StateOf(player).Survivors is not { } linked || StateOf(player).SurvivorsSupports is not { } linkCatalog
                || StateOf(player).SurvivorsCatalog is not { } linkSkills)
            {
                await player.Commands!.Reply("No survivors run is active.");
                return;
            }
            string wanted = string.Join(' ', args.Skip(1)).Trim().ToLowerInvariant();
            int support = linkCatalog.ToList().FindIndex(s => s.Name.ToLowerInvariant().Contains(wanted));
            int target = linked.Skills.Keys.OrderBy(k => k).DefaultIfEmpty(-1).First();
            if (wanted.Length == 0 || support < 0 || target < 0)
            {
                await player.Commands!.Reply("Usage: /survivors link <support name>, e.g. /survivors link added cold.");
                return;
            }
            if (!SurvivorsCanSupport(player, target, support))
            {
                await player.Commands!.Reply(
                    $"{linkCatalog[support].Name} cannot support {linkSkills[target].Name} (the game's skill types).");
                return;
            }
            await player.Commands!.Reply(linked.Link(target, support)
                ? $"Linked {linkCatalog[support].Name} to {linkSkills[target].Name} (dev)."
                : $"Could not link {linkCatalog[support].Name}: the skill already has five supports.");
            return;
        }
        // Dev: equip a named unique, for checking its mods on the character and on the run's casts.
        if (what == "unique")
        {
            string wanted = string.Join(' ', args.Skip(1)).Trim();
            if (wanted.Length == 0 || UniquesFor(player).Create(wanted, AreaRng(player)) is not { } unique)
            {
                await player.Commands!.Reply($"Usage: /survivors unique <name>, e.g. /survivors unique The Searing Touch.");
                return;
            }
            bool equipped = await SurvivorsEquip(player, unique);
            await player.Commands!.Reply(equipped
                ? $"Equipped {wanted} (dev)." : $"{wanted} could not be equipped.");
            return;
        }
        // Dev: the full gem x support matrix through the same computation the live cast uses, so every support's
        // number can be checked without waiting for a level-up offer or a live cast.
        if (what == "matrix")
        {
            var matrixCatalog = SurvivorsCatalogFor(player);
            string wanted = string.Join(' ', args.Skip(1)).Trim().ToLowerInvariant();
            int index = wanted.Length == 0 && StateOf(player).Survivors is { } active
                ? active.Skills.Keys.OrderBy(k => k).DefaultIfEmpty(-1).First()
                : matrixCatalog.ToList().FindIndex(s => s.Name.ToLowerInvariant().Contains(wanted));
            if (index < 0 || index >= matrixCatalog.Count)
            {
                await player.Commands!.Reply("Usage: /survivors matrix <gem>, e.g. /survivors matrix fireball.");
                return;
            }
            StateOf(player).SurvivorsCatalog ??= matrixCatalog;
            StateOf(player).SurvivorsSupports ??= SurvivorsSupportsFor(player);
            var matrixSupports = StateOf(player).SurvivorsSupports!;
            var roster = SurvivorsRosterFor(player) ?? new SurvivorsRoster([], [], 1, 20, 40);
            var skill = matrixCatalog[index];
            SurvivorsRun Bare()
            {
                var run = new SurvivorsRun();
                run.Learn(index);
                return run;
            }
            if (SurvivorsComputeCast(player, Bare(), skill, 1, index, roster) is not { } bare)
            {
                await player.Commands!.Reply($"{skill.Name} has no cast to compare.");
                return;
            }
            string baseLine = bare.Herald
                ? $"base: interval {bare.Interval:0.00}, pulse {bare.HeraldDamage}, radius {bare.HeraldRadius:0.#}"
                : $"base: interval {bare.Interval:0.00}, radius {bare.Profile?.Radius:0.#}, " +
                  $"speed {bare.Profile?.Speed:0.#}, crit {bare.Shot?.CriticalChance / 100.0:0.##}%, " +
                  $"leech {bare.Shot?.Leech.Life.MinTotal:0.#}, hit {bare.Shot?.Hit}";
            await player.Commands!.Reply($"--- Survivors matrix: {skill.Name} rank 1 ---\n{baseLine}");
            foreach (int support in Enumerable.Range(0, matrixSupports.Count))
            {
                if (!SurvivorsCanSupport(player, index, support))
                {
                    await player.Commands!.Reply($"{matrixSupports[support].Name}: cannot support {skill.Name} (skill types).");
                    continue;
                }
                var linkedRun = Bare();
                linkedRun.Link(index, support);
                if (SurvivorsComputeCast(player, linkedRun, skill, 1, index, roster) is not { } with) continue;
                await player.Commands!.Reply($"{matrixSupports[support].Name}: " +
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
            await SurvivorsUniqueReport(player);
            return;
        }
        if (what == "stop")
        {
            if (StateOf(player).Survivors is null) { await player.Commands!.Reply("No survivors run is active."); return; }
            await EndSurvivors(player, "stopped");
            return;
        }
        if (what == "pick")
        {
            if (StateOf(player).Survivors is not { } picking)
            {
                await player.Commands!.Reply("No survivors run is active. /survivors start begins one in this area.");
                return;
            }
            if (picking.Offer.Count == 0)
            {
                await player.Commands!.Reply("No skill is waiting right now.");
                return;
            }
            int index = args.Length > 1 ? int.Parse(args[1]) : 0;
            if (index < 1 || index > picking.Offer.Count
                || !await ApplySurvivorsChoice(player, picking.Offer[index - 1]))
            {
                await player.Commands!.Reply($"Pick a number from 1 to {picking.Offer.Count}.");
                return;
            }
            return;
        }
        if (StateOf(player).Survivors is not { } run)
        {
            await player.Commands!.Reply("No survivors run is active. /survivors start begins one in this area.");
            return;
        }
        string offer = "";
        if (run.Offer.Count > 0 && StateOf(player).SurvivorsOffer is { } shown)
        {
            var lines = new List<string>();
            var catalog = StateOf(player).SurvivorsCatalog ?? SurvivorsCatalogFor(player);
            foreach (var choice in run.Offer)
            {
                uint id = shown.FirstOrDefault(e => e.Value == choice).Key;
                string name = id != 0 && StateOf(player).SurvivorsOfferNames?.GetValueOrDefault(id) is { } named
                    ? named : choice.Kind.ToString();
                lines.Add($"{lines.Count + 1}: {SurvivorsChoiceLabel(run, choice, name, catalog)} (0x{id:x8})");
            }
            offer = "\n" + string.Join("\n", lines);
        }
        await player.Commands!.Reply(
            $"--- Survivors ---\n{SurvivorsClock(run.Elapsed)}, stage {StateOf(player).Stage + 1}/{SurvivorsLevels.Length}, " +
            $"{run.Kills} kills, run level {run.Level}, {run.Skills.Count} skill(s), {run.PendingChoices} choice(s) waiting, " +
            $"horde up to {run.TargetAlive} of {run.MaxAlive}." + offer);
    }

    internal static async Task RunSurvivors(Player player,
        SurvivorsRun run, SurvivorsRoster roster, IReadOnlyList<SurvivorsSkill> catalog, int stage, CancellationToken token)
    {
        // Each stage is one area: two minutes of horde (with its champions), then a gate boss whose death opens the
        // way onward. The stage's clock starts when the run enters it, not on the run's global clock: the gate fight
        // itself takes time, so a global boundary would make the next stage's gate spawn on arrival.
        double stageStart = run.Elapsed;
        double last = AreaTime(player), nextSpawn = 0,
            nextBoss = Math.Max(stageStart + SurvivorsBossDelaySeconds, run.Elapsed + 5),
            nextRing = Math.Max(stageStart + SurvivorsRingIntervalSeconds / 2, run.Elapsed + 10),
            nextRanged = Math.Max(stageStart + SurvivorsRules.RangedWaveIntervalSeconds, run.Elapsed + 5),
            nextReport = 10, nextSweep = 0, nextCorpseSweep = 0;
        string bossName = roster.Bosses.Count > 0 ? roster.Bosses[0].Name : "";
        uint stageBoss = 0;
        bool bossSpawned = false, gateSpawned = false, gateDefeated = false;
        SurvivorsBossFight? gateFight = null;
        int bossNumber = 0;
        // Every boss's id and the run experience its kill pays in total (the ordinary unique kill pays 50 of it).
        var bossBonuses = new Dictionary<uint, int>();
        bool carried = false;
        var horde = new HashSet<uint>();
        var nextFire = new Dictionary<int, double>();
        try
        {
            while (!token.IsCancellationRequested && !player.Closed && !player.LeavingArea && player.PendingTransfer is null && ReferenceEquals(StateOf(player).Survivors, run))
            {
                double now = AreaTime(player);
                double step = now - last;
                last = now;
                if (player.Paused || step <= 0)
                {
                    await Task.Delay(SurvivorsStepMs, token);
                    continue;
                }
                run.Advance(step);
                if (InGracePeriod(player)) await EndGracePeriod(player, action: true);
                if (player.PlayerLife == 0) { await EndSurvivors(player, "defeat"); break; }
                await SurvivorsCollectOrbs(player, run);
                // Death ground zones: expire the old ones and drain life while the player stands inside one.
                await SurvivorsAdvanceDeathZones(player, step);
                // Dead monsters are removed after a short linger: a long run's corpses otherwise pile up and glitch.
                if (now >= nextCorpseSweep)
                {
                    nextCorpseSweep = now + 0.5;
                    await SurvivorsSweepCorpses(player, horde);
                }
                // The stage's own champions pay their ladder experience as an orb when they fall.
                foreach (var (id, total) in bossBonuses.ToList())
                {
                    bool dead;
                    (int X, int Y) at;
                    dead = !player.Area.Monsters.TryGetValue(id, out var boss) || boss.Dead || boss.Life == 0;
                    at = player.Area.Monsters.TryGetValue(id, out var fallen) ? fallen.Walk.Position(now) : player.Walk.Position(now);
                    if (!dead) continue;
                    bossBonuses.Remove(id);
                    int bonus = total - SurvivorsRules.ExperienceForKill(MonsterRarity.Unique);
                    if (bonus > 0) await SurvivorsDropOrb(player, at, bonus);
                    await player.Commands!.Reply(id == stageBoss
                        ? $"{bossName} falls. +{total} run experience."
                        : id == gateFight?.BossId
                            ? $"The gate boss falls. +{total} run experience."
                            : $"A guardian falls. +{total} run experience.");
                }
                // The two-minute stage ends with its gate boss; only its death opens the way onward or wins the run.
                if (!gateSpawned && run.Elapsed - stageStart >= SurvivorsRules.StageSeconds)
                {
                    gateSpawned = true;
                    gateFight = await SpawnSurvivorsGateBoss(player, roster, run, stage, horde, token);
                    if (gateFight is not null) bossBonuses[gateFight.BossId] = SurvivorsRules.BossExperience(stage);
                }
                // No cell for it: do not seal the run behind a boss that never arrived; the way opens instead.
                bool gateCleared = gateSpawned && (gateFight is null || gateFight.Defeated);
                // A reward window still holds the client's 0x600 inventory set: changing area while it is open makes the
                // client crash on the set change ("Got an inventory set change for an inventory that isn't in the set").
                // The clock is paused for the window, so waiting here costs the run nothing.
                if (gateCleared && !gateDefeated && StateOf(player).SurvivorsOffer is null)
                {
                    gateDefeated = true;
                    if (stage + 1 >= SurvivorsLevels.Length)
                    {
                        await EndSurvivors(player, "victory");
                        break;
                    }
                    if (AreaTransitionCatalog.HashOf(SurvivorsLevels[stage + 1]) is { } nextArea)
                    {
                        var transfer = new PendingTransfer(nextArea, 0, false);
                        StateOf(player).StageTransfer = transfer;
                        PendingRuns[player] = new SurvivorsCarry(run, stage + 1, DateTime.UtcNow,
                            [.. StateOf(player).SurvivorsItemIds], player.Character.Name);
                        carried = true;
                        string nextName = AreaTransitionCatalog.NameOf(nextArea) ?? SurvivorsLevels[stage + 1];
                        await player.Commands!.Reply($"--- Level {stage + 2} ---\nMoving to {nextName}.");
                        await ChangeArea(player, transfer, "survivors level");
                        break;
                    }
                }
                if (!gateSpawned && now >= nextSpawn)
                {
                    nextSpawn = now + SurvivorsRules.SpawnIntervalSeconds;
                    await SpawnSurvivorsWave(player, run, roster, horde);
                }
                // The ground never shows loot during a run: anything dropped or placed is swept once a second.
                if (now >= nextSweep)
                {
                    nextSweep = now + 1;
                    await SurvivorsClearGround(player);
                }
                // The stage's champions arrive 15 s in and every minute after; the gate phase stops new hordes and rings.
                if (!gateSpawned && roster.Bosses.Count > 0 && run.Elapsed >= nextBoss)
                {
                    if (!bossSpawned)
                    {
                        stageBoss = await SpawnSurvivorsBoss(player, roster, run, stage, horde);
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
                        uint id = await SpawnSurvivorsBoss(player, roster, run, stage, horde,
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
                if (!gateSpawned && run.Elapsed >= nextRing)
                {
                    nextRing = run.Elapsed + SurvivorsRingIntervalSeconds;
                    await SpawnSurvivorsRing(player, run, roster, horde);
                }
                // Every twenty seconds a ring of archers/wands shoots from 22 cells: standing still is not safe.
                if (!gateSpawned && run.Elapsed >= nextRanged)
                {
                    nextRanged = run.Elapsed + SurvivorsRules.RangedWaveIntervalSeconds;
                    await SpawnSurvivorsRangedRing(player, run, roster, horde);
                }
                foreach (var (skill, rank) in run.Skills)
                {
                    if (skill < 0 || skill >= catalog.Count) continue;
                    double ready;
                    nextFire.TryGetValue(skill, out ready);
                    if (now < ready) continue;
                    double fired = await SurvivorsCast(player, run, roster, catalog[skill], rank, skill);
                    // The cast returns the interval from the gem's own cast time; an empty cast (nothing in reach)
                    // retries sooner than a full cooldown.
                    nextFire[skill] = now + (fired > 0 ? fired : 0.2);
                }
                await SurvivorsOfferIfDue(player, run, catalog);
                if (run.Elapsed >= nextReport)
                {
                    nextReport = run.Elapsed + 10;
                    int alive;
                    alive = horde.Count(id => player.Area.Monsters.GetValueOrDefault(id) is { Dead: false, Life: > 0 });
                    Console.WriteLine($"[gateway] {player.Connection.Remote} survivors {SurvivorsClock(run.Elapsed)}: horde {alive}/{run.TargetAlive}, " +
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
            if (!carried && ReferenceEquals(StateOf(player).Survivors, run))
            {
                Release(player, run);
                Console.WriteLine($"[gateway] {player.Connection.Remote} survivors run ended early at {SurvivorsClock(run.Elapsed)} " +
                    $"with {run.Kills} kills (area change or closed connection)");
            }
        }
    }

    internal static async Task SpawnSurvivorsWave(Player player,
        SurvivorsRun run, SurvivorsRoster roster, HashSet<uint> horde)
    {
        if (roster.Varieties.Count == 0) return;
        int alive;
        alive = horde.Count(id => player.Area.Monsters.GetValueOrDefault(id) is { Dead: false, Life: > 0 });
        int missing = run.TargetAlive - alive;
        if (missing <= 0) return;
        int spawn = SurvivorsRules.SpawnRefill(missing);
        var pool = SurvivorsRules.WavePool(run.Elapsed, roster.Varieties.Count);
        if (pool.Length == 0) return;
        var ranged = SurvivorsRangedPool(roster);
        for (int i = 0; i < spawn; i++)
        {
            // Every third body is a ranged variety when the area has one: the player has to keep moving.
            bool isRanged = ranged.Count > 0 && i % 3 == 2;
            var variety = isRanged
                ? ranged[AreaRng(player).Next(ranged.Count)]
                : roster.Varieties[pool[AreaRng(player).Next(pool.Length)]];
            if (SurvivorsSpawnCell(player, 60) is not { } cell) continue;
            horde.Add(await SpawnMonster(player, variety, roster.Level, SurvivorsRarity(run), cell.X, cell.Y,
                log: false, extraMods: SurvivorsMonsterMods(run, isRanged)));
        }
    }

    /// <summary>
    /// A ring of the area's ranged varieties, every twenty run-seconds: the archers/wands/mortars that shoot at the
    /// player from 22 cells, so standing still means eating projectiles.
    /// </summary>
    internal static async Task SpawnSurvivorsRangedRing(Player player, SurvivorsRun run, SurvivorsRoster roster, HashSet<uint> horde)
    {
        var ranged = SurvivorsRangedPool(roster);
        if (ranged.Count == 0) return;
        var (px, py) = player.Walk.Position(AreaTime(player));
        int count = SurvivorsRules.RangedWaveCount(SurvivorsRules.Progress(run.Elapsed));
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            var variety = ranged[AreaRng(player).Next(ranged.Count)];
            double angle = i * Math.Tau / count;
            int x = (int)Math.Round(px + Math.Cos(angle) * 44), y = (int)Math.Round(py + Math.Sin(angle) * 44);
            if (player.Layout.GridW > 0 && (x < 0 || y < 0 || x >= player.Layout.GridW || y >= player.Layout.GridH)) continue;
            if (!player.Layout.WalkableFor(x, y, 0) || player.Barrier.Contains((x, y))) continue;
            horde.Add(await SpawnMonster(player, variety, roster.Level, SurvivorsRarity(run), x, y,
                log: false, extraMods: SurvivorsMonsterMods(run, ranged: true)));
            spawned++;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} survivors ranged ring at {SurvivorsClock(run.Elapsed)}: {spawned} of {count} spawned");
        if (spawned > 0) await player.Commands!.Reply("Archers close in!");
    }

    /// <summary>
    /// Halfway between bosses, a closing ring of the moment's weakest: 28 bodies at the start, 88 by the end. The
    /// reference mode's surround wave, which punishes standing still more than the trickle does.
    /// </summary>
    internal static async Task SpawnSurvivorsRing(Player player,
        SurvivorsRun run, SurvivorsRoster roster, HashSet<uint> horde)
    {
        if (roster.Varieties.Count == 0) return;
        var (px, py) = player.Walk.Position(AreaTime(player));
        var pool = SurvivorsRules.WavePool(run.Elapsed, roster.Varieties.Count);
        var variety = roster.Varieties[pool.Length == 0 ? 0 : pool[0]];
        int count = 28 + (int)(SurvivorsRules.Progress(run.Elapsed) * 60);
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            double angle = i * Math.Tau / count;
            int x = (int)Math.Round(px + Math.Cos(angle) * 52), y = (int)Math.Round(py + Math.Sin(angle) * 52);
            if (player.Layout.GridW > 0 && (x < 0 || y < 0 || x >= player.Layout.GridW || y >= player.Layout.GridH)) continue;
            if (!player.Layout.WalkableFor(x, y, 0) || player.Barrier.Contains((x, y))) continue;
            horde.Add(await SpawnMonster(player, variety, roster.Level, MonsterRarity.Normal, x, y,
                log: false, extraMods: SurvivorsMonsterMods(run)));
            spawned++;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} survivors ring wave at {SurvivorsClock(run.Elapsed)}: {spawned} of {count} spawned");
        if (spawned > 0) await player.Commands!.Reply("They're surrounding you!");
    }

    internal static async Task<uint> SpawnSurvivorsBoss(Player player,
        SurvivorsRoster roster, SurvivorsRun run, int stage, HashSet<uint> horde, int bossIndex = 0)
    {
        if (roster.Bosses.Count == 0) return 0;
        var boss = roster.Bosses[bossIndex % roster.Bosses.Count];
        if (SurvivorsSpawnCell(player, 24) is not { } cell)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} survivors: no cell for boss {boss.Name}");
            return 0;
        }
        uint id = await SpawnMonster(player, boss, roster.Level, MonsterRarity.Unique, cell.X, cell.Y,
            mods: SurvivorsBossMods(roster, boss, run, stage));
        horde.Add(id);
        string area = SurvivorsAreaName(player.Layout.Id);
        await player.Commands!.Reply(bossIndex == 0
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
        var rolled = MonsterModRolls.Roll(boss, roster.Level, MonsterRarity.Unique, Random.Shared, alone: true);
        int baseLife = Math.Max(1, Monsters.BaseLife(boss, roster.Level) ?? 100);
        double target = SurvivorsPoolTop(roster, stage) * SurvivorsRules.BossLifeRatio(stage)
            * SurvivorsRules.MonsterLifeScale(run.Elapsed) * SurvivorsRules.BossLifeMultiplier;
        // MaxLife applies the rarity's own factor after maximum_life_+%, so solve the percentage for the target.
        double factor = (1 + rolled.Effects[Stat.MonsterLifePlusPctFinalFromRarity] / 100.0)
            * (1 + rolled.Effects[Stat.MonsterLifePlusPctFinalFromRarityTable] / 100.0);
        int lifePct = (int)Math.Round((target / (baseLife * Math.Max(0.01, factor)) - 1) * 100);
        lifePct = Math.Clamp(lifePct, -99, 1_000_000);
        int damagePct = (int)Math.Round((SurvivorsRules.MonsterDamageScale(run.Elapsed) - 1) * 100);
        return rolled with
        {
            Effects = rolled.Effects.Plus(Stat.MaximumLifePlusPct, lifePct).Plus(Stat.DamagePlusPct, damagePct),
        };
    }

    /// <summary>The client's own name for an area id, or the id when the atlas has none.</summary>
    internal static string SurvivorsAreaName(string id) =>
        AreaTransitionCatalog.HashOf(id) is { } hash ? AreaTransitionCatalog.NameOf(hash) ?? id : id;

    /// <summary>A spawned monster's mods for the run's clock: the reference's life/damage curves scaled by the
    /// playtest enemy-life (8x) and enemy-damage (4x) multipliers, then the DPS threat from the player's sustained
    /// kill rate on top of the life, so a steamrolling build faces a horde that outlives its damage. A ranged monster's
    /// damage is further cut by <see cref="SurvivorsRules.RangedDamageMultiplier"/>: a volley from 22 cells otherwise
    /// kills on its own.</summary>
    internal static MonsterModSet SurvivorsMonsterMods(SurvivorsRun run, bool ranged = false)
    {
        double threat = SurvivorsRules.ThreatMultiplier(run.ThreatKillsPerSecond);
        double damageMultiplier = SurvivorsRules.EnemyDamageMultiplier
            * (ranged ? SurvivorsRules.RangedDamageMultiplier : 1);
        int life = (int)Math.Round((SurvivorsRules.MonsterLifeScale(run.Elapsed) * SurvivorsRules.EnemyLifeMultiplier * threat - 1) * 100);
        int damage = (int)Math.Round((SurvivorsRules.MonsterDamageScale(run.Elapsed) * damageMultiplier - 1) * 100);
        if (life <= 0 && damage <= 0) return MonsterModSet.None;
        return MonsterModSet.None with
        {
            Effects = new MonsterModEffects(new Dictionary<uint, int>
            {
                [Stat.MaximumLifePlusPct] = life, [Stat.DamagePlusPct] = damage,
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
    internal static (int X, int Y)? SurvivorsSpawnCell(Player player, double radius)
    {
        var (px, py) = player.Walk.Position(AreaTime(player));
        // The wave rings are far from the player, so a cramped area needs more attempts to find a walkable cell.
        for (int attempt = 0; attempt < 24; attempt++)
        {
            double angle = AreaRng(player).NextDouble() * Math.Tau;
            double distance = radius + AreaRng(player).NextDouble() * 8;
            int x = (int)Math.Round(px + Math.Cos(angle) * distance), y = (int)Math.Round(py + Math.Sin(angle) * distance);
            if (player.Layout.GridW > 0 && (x < 0 || y < 0 || x >= player.Layout.GridW || y >= player.Layout.GridH)) continue;
            if (!player.Layout.WalkableFor(x, y, 0) || player.Barrier.Contains((x, y))) continue;
            return (x, y);
        }
        return null;
    }

}
