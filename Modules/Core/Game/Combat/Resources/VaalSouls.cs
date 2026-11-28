using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public sealed record SoulTransfer(string Character, DateTime At, VaalSoulState.Carried State, double PreventionRemaining);
    public static readonly ConcurrentDictionary<uint, SoulTransfer> CarriedVaalSouls = new();

    // Only the proved native getter inputs are projected. Explicit development values win, including zero.
    public static int VaalSoulInput(Player player, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int> own, uint key)
    {
        // The selector describes this skill, not the caster. Only an explicit developer replacement is global.
        if (key == VaalSoulCost.Aura) return own.GetValueOrDefault(key);
        long value = (long)playerStats.GetValueOrDefault(key) + own.GetValueOrDefault(key);
        if (key is VaalSoulCost.AuraMultiplier or VaalSoulCost.NonAuraMultiplier)
            value += (long)playerStats.GetValueOrDefault(Stat.VaalSkillSoulRequirementPlusPct)
                + own.GetValueOrDefault(Stat.VaalSkillSoulRequirementPlusPct);
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    public static void PublishVaalSoulInputs(Player player, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int> own, Dictionary<uint, int> stats)
    {
        foreach (uint key in VaalSoulCost.Inputs) stats[key] = VaalSoulInput(player, playerStats, own, key);
    }

    readonly record struct VaalSourceInputs(PlayerInventories Items, long ItemChanges, LeagueRuntime League, int Class,
        (int Level, int Strength, int Dexterity, int Intelligence) Attributes,
        bool Alive, bool God, List<ushort> Passives, int PassivesCount,
        AscendancyProgress Ascendancy, int DescendancyCount, int ClusterCount, int TattooRevision,
        IReadOnlyDictionary<ushort, ushort> Masteries, long ExecutableRevision, long ExemptionsRevision);

    sealed class VaalRefreshCache
    {
        public VaalSourceInputs? Inputs;
        public IReadOnlyDictionary<uint, int>? FrameStats;
        public bool HasCandidates;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, VaalRefreshCache> VaalRefreshCaches = new();

    // Reconcile before an await as well as at the send gate: a queued removal must lose its balance even if readded
    // before publication. Requirements/inactive equipment are refreshed by the normal character stat seam.
    public static void RefreshVaalSouls(Player player) => RefreshVaalSouls(player, onlyWhenChanged: false);

    static void RefreshVaalSouls(Player player, bool onlyWhenChanged)
    {
        if (player.VaalSoulsRetired || player.Closed) return;
        var items = ItemsOf(player);
        var character = player.Character;
        var league = LeagueFor(player);
        var stats = FrameStatsOf.TryGetValue(player, out var frame) && frame.ByFrame ? frame.Graph?.Values : null;
        // Gem experience beneath a level boundary is unsaved and does not change its soul source.
        // The gem level-up path refreshes immediately when an unsaved change crosses that boundary.
        var pools = player.Pools;
        var inputs = new VaalSourceInputs(items, items.Changes, league, character.ClassRow,
            (pools.Level, pools.Strength, pools.Dexterity, pools.Intelligence), player.PlayerLife > 0, player.God,
            character.Passives, character.Passives.Count, character.MainAscendancy,
            character.DescendancyNodes.Count, character.ClusterPassives.Count, character.TattooRevision,
            character.MasteryEffects, GameEvents.GrantedSkillExecutable.Revision, GameEvents.RequirementExemptions.Revision);
        var cache = VaalRefreshCaches.GetOrCreateValue(player);
        if (cache.Inputs == inputs && (!cache.HasCandidates && !player.VaalSouls.HasSources
            || onlyWhenChanged && ReferenceEquals(cache.FrameStats, stats))) return;
        var playerStats = CharacterPlayerStats(player);
        var requirements = CurrentGemRequirements(player);
        var skills = SkillsFor(player);
        var sources = new List<(uint, uint)>();
        bool hasCandidates = false;
        foreach (var gem in PlayerSkillSourcesAt(character.Name, requirements.Equipment, requirements.Inactive, league,
            GrantedSkillsOf(player), WornGrantedSkillsOf(player, requirements.Equipment)))
        {
            if (skills.VaalBudget(gem.Effect, gem.Level) is null) continue;
            hasCandidates = true;
            if (!gem.Active) continue;
            var supported = SupportedSkillAt(player, gem.Id, requirements);
            if (!supported.RequirementsMet || skills.VaalBudget(gem.Effect, supported.Level) is not { } definition) continue;
            var own = supported.Stats;
            int required = VaalSoulCost.Required(definition.BaseCost,
                name => VaalSoulInput(player, playerStats, own, name));
            if (skills.IsAttack(gem.Effect)
                && VaalSoulInput(player, playerStats, own, Stat.VaalAttackRageCostInsteadOfSoulsPerUse) != 0)
                required = 0;
            sources.Add((definition.ActiveSkillsRow, VaalSoulCost.Capacity(required, definition.StoredUses)));
        }
        player.VaalSouls.Reconcile(sources);
        cache.Inputs = inputs;
        cache.FrameStats = stats;
        cache.HasCandidates = hasCandidates;
    }

    // Called with Life=0/Dead=true in the SAME Monsters section as the lethal mutation, before notification awaits.
    // Shared hit paths do not carry an object ID; resolve actual ownership here without accepting another recipient.
    public static void CollectVaalSoulAtDeathLocked(Player player, SpawnedMonster monster)
    {
        if (monster.Life != 0 || !monster.Dead || monster.SoulKillProcessed) return;
        RefreshVaalSouls(player, onlyWhenChanged: true);
        if (!player.VaalSouls.CanGain)
        {
            monster.SoulKillProcessed = true;
            return;
        }
        var monsters = player.Area.Monsters;
        uint id = monster.ObjectId;
        if (!monsters.TryGetValue(id, out var found) || !ReferenceEquals(found, monster))
            id = monsters.FirstOrDefault(kv => ReferenceEquals(kv.Value, monster)).Key;
        CollectVaalSoulOnKill(player, id, monster);
    }

    // Consume ineligible deaths too. Notifications may only publish current state; they never call this method.
    public static bool CollectVaalSoulOnKill(Player player, uint id, SpawnedMonster monster)
    {
        if (monster.Life != 0 || !monster.Dead || monster.SoulKillProcessed) return false;
        monster.SoulKillProcessed = true;
        if (!player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(monster, current)) return false;
        if (!monster.Hostile || monster.Variety is null || player.Character.Name is null || player.Closed
            || player.LeavingArea || player.VaalSoulsRetired || player.RageRetired || player.Paused
            || player.PlayerLife == 0 || InTown(player)) return false;
        double now = AreaTime(player);
        if (Dist(monster.Walk.Position(now), player.Walk.Position(now)) > ExperienceRange) return false;
        // The sources are reconciled by the equipment and skill seams (RefreshVaalSouls there); a kill only adds one
        // soul to what they set. Reading the whole player stat snapshot and every supported Vaal gem here cost 2-3 ms
        // per corpse in a pack (measured 2026-11-18), and the prevention it read is this connection's own deadline
        // (WithVaalPrevention publishes has_soul_gain_prevention from it). Vaal Immortal Call holds soul gain for
        // its duration (the buff's cannot_gain_souls); its state is a cheap table read, not a stat build.
        bool held = VaalImmortalCallActive(player, now);
        var stats = held || player.VaalPreventionUntil > VaalResourceTime(player) ? null : CharacterPlayerStats(player);
        if (stats is null || SoulGainPreventedByStats(stats))
        {
            Console.WriteLine($"[gateway] Vaal soul not gained from monster 0x{id:x}: " +
                $"{(held ? "Vaal Immortal Call" : "soul gain prevention")} (t={VaalResourceTime(player):0.000})");
            return false;
        }
        if (!player.VaalSouls.Gain()) return false;
        // These are two separate sources of additional souls on an eligible kill.
        if (stats.GetValueOrDefault(Stat.ChanceToGainVaalSoulOnKillPct) is int chance and > 0
            && GameServer.Core.World.RandomRolls.Chance(chance, 100, AreaRng(player)))
            player.VaalSouls.Gain();
        if (player.Rampage.Current.Active
            && stats.GetValueOrDefault(Stat.GainAnAdditionalVaalSoulOnKillIfHaveRampagedRecently) != 0)
            player.VaalSouls.Gain();
        return true;
    }

    static bool SoulGainPreventedByStats(IReadOnlyDictionary<uint, int> stats) =>
        stats.GetValueOrDefault(Stat.HasSoulGainPrevention) != 0
        || stats.GetValueOrDefault(Stat.CannotGainSouls) != 0 || stats.GetValueOrDefault(Stat.VirtualCannotGainSouls) != 0;

    sealed class HitSoulCooldown { public double NextAt = double.NegativeInfinity; }
    static readonly RuleTable<Player, HitSoulCooldown> HitSoulCooldowns = new();

    static void HarvestVaalSoulOnHitLocked(Player player, int intervalMs, double now,
        IReadOnlyDictionary<uint, int> playerStats)
    {
        if (player.Closed || player.LeavingArea || player.VaalSoulsRetired || player.Paused || player.PlayerLife == 0
            || InTown(player) || player.VaalPreventionUntil > VaalResourceTime(player)
            || VaalImmortalCallActive(player, now) || SoulGainPreventedByStats(playerStats)
            || playerStats.GetValueOrDefault(Stat.PreventVaalSoulsOnHit) != 0) return;

        var cooldown = HitSoulCooldowns.GetOrCreateValue(player);
        if (now < cooldown.NextAt || !player.VaalSouls.Gain()) return;
        cooldown.NextAt = now + intervalMs / 1000.0;
        QueueVaalSoulPresentationLocked(player, "Vaal souls from a supported attack");
    }

    static void QueueVaalSoulPresentationLocked(Player player, string label)
    {
        var queue = player.Minions.OutputQueue;
        if (!queue.Any(o => o.Kind == PlayerMinions.OutputKind.Vaal))
            queue.Add(new([], label, Kind: PlayerMinions.OutputKind.Vaal));
    }

    // Native UPDATE clamps against OLD capacity. Unknown rows and increases must seed first, then restore current.
    // Empty lists preserve native entries, so every removed row needs an explicit zero update.
    public static (VaalSoulSeed[] Seeds, VaalSoulUpdate[] Updates) VaalSoulChanges(Player player, bool reconcile = true)
    {
        if (reconcile) RefreshVaalSouls(player);
        var budgets = player.VaalSouls.Read();
        if (budgets.Length == 0 && player.SentVaalSouls.Count == 0) return ([], []);
        var current = budgets.ToDictionary(b => b.ActiveSkillsRow);
        var seeds = new List<VaalSoulSeed>();
        var updates = new List<VaalSoulUpdate>();
        foreach (var budget in current.Values.OrderBy(b => b.ActiveSkillsRow))
        {
            bool known = player.SentVaalSouls.TryGetValue(budget.ActiveSkillsRow, out var old);
            if (known && old == budget) continue;
            if (!known || budget.Capacity > old.Capacity) seeds.Add(new(budget.ActiveSkillsRow, budget.Capacity));
            updates.Add(new(budget.ActiveSkillsRow, budget.Current, budget.Capacity));
        }
        foreach (uint row in player.SentVaalSouls.Keys.Except(current.Keys).Order()) updates.Add(new(row, 0, 0));
        player.SentVaalSouls.Clear();
        foreach (var (row, budget) in current) player.SentVaalSouls.Add(row, budget);
        return ([.. seeds], [.. updates]);
    }

    public static byte[] PlayerSkillsWithVaalSouls(Player player, IReadOnlyList<GrantedSkill> skills,
        IReadOnlyList<ushort> gone) =>
        [.. PlayerSkillsBatchesWithVaalSouls(player, skills, gone).SelectMany(p => p)];

    public static byte[][] PlayerSkillsBatchesWithVaalSouls(Player player, IReadOnlyList<GrantedSkill> skills,
        IReadOnlyList<ushort> gone)
    {
        var changes = VaalSoulChanges(player);
        return InstanceEntities.PlayerSkillsChangeBatches(player.Id, skills, gone, changes.Seeds, changes.Updates, CanDetonateMines(player));
    }

    public static byte[]? VaalSoulPresentation(Player player)
    {
        if (player.Closed || player.LeavingArea || player.VaalSoulsRetired) return null;
        // Nothing a client can see has changed: no source walk, no stat snapshot, no packet. This runs after every
        // kill (MonsterKilledBy, a projectile's commit), and a soul only moves on some of them.
        if (!player.VaalSouls.Unpresented) return null;
        // A gained or spent soul does not change the sources, so the presentation does not reconcile them.
        var changes = VaalSoulChanges(player, reconcile: false);
        player.VaalSouls.Presented();
        return changes.Seeds.Length == 0 && changes.Updates.Length == 0 ? null
            : InstanceEntities.PlayerSkillsChange(player.Id, [], [], changes.Seeds, changes.Updates, CanDetonateMines(player));
    }

    /// <summary>Whether a presentation would send anything. A cheap pre-check for the per-kill callers: an unchanged
    /// balance must not take the send gate.</summary>
    public static bool VaalSoulsUnpresented(Player player) =>
        !player.Closed && !player.LeavingArea && !player.VaalSoulsRetired && player.VaalSouls.Unpresented;

    public static Task SendVaalSouls(Player player) =>
        VaalSoulsUnpresented(player)
            ? SendWorldRecord(player, player.Id, [], null, atGate: () => VaalSoulPresentation(player))
            : Task.CompletedTask;

    // Souls belong to the instance they were gained in (poewiki "Vaal skill"): leaving keeps the character's balance on
    // the instance it leaves; entering restores what it kept there, or starts at zero (town, hideout, a new instance).
    static readonly Store<InstanceState, ConcurrentDictionary<string, VaalSoulState.Carried>> InstanceVaalSouls = new();
    static ConcurrentDictionary<string, VaalSoulState.Carried> InstanceVaalSoulsOf(InstanceState instance) =>
        InstanceVaalSouls.GetValue(instance, _ => new ConcurrentDictionary<string, VaalSoulState.Carried>(StringComparer.OrdinalIgnoreCase));
    static uint SoulTotal(VaalSoulState.Carried state) => state.Budgets.Aggregate(0u, (n, b) => n + b.Current);

    // Same transfer policy as Rage: one successful ticket owns the prevention; loading time is frozen, no save schema.
    public static void CarryVaalSouls(Player player, uint ticket)
    {
        if (player.VaalSoulsRetired || player.Closed) return;
        RefreshVaalSouls(player);
        var state = player.VaalSouls.Carry();
        player.VaalSoulsRetired = true;
        uint total = SoulTotal(state);
        if (player.Instance is { } left)
        {
            if (total > 0) InstanceVaalSoulsOf(left)[CharacterName(player)] = state;
            else if (InstanceVaalSouls.TryGetValue(left, out var stored)) stored.TryRemove(CharacterName(player), out _);
        }
        if (GameServer.Core.Diagnostics.ServerTrace.Skills)
            Console.WriteLine($"[gateway] Vaal souls kept on the instance left (area 0x{player.Layout.Hash:x}): {total}");
        foreach (var (old, entry) in CarriedVaalSouls)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedVaalSouls.TryRemove(old, out _);
        double prevention = Math.Max(0, player.VaalPreventionUntil - VaalResourceTime(player));
        player.VaalUse = null; player.VaalPreventionUntil = 0;
        CarriedVaalSouls.TryAdd(ticket, new(CharacterName(player), DateTime.UtcNow, state, prevention));
    }

    public static void RestoreVaalSouls(Player player)
    {
        if (player.Closed || player.VaalSoulsRetired || !CarriedVaalSouls.TryGetValue(player.Ticket, out var entry)
            || entry.Character != CharacterName(player)) return;
        if (!CarriedVaalSouls.TryRemove(new KeyValuePair<uint, SoulTransfer>(player.Ticket, entry))) return;
        if (DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5))
        {
            var kept = player.Instance is { } here && InstanceVaalSouls.TryGetValue(here, out var byCharacter)
                && byCharacter.TryRemove(CharacterName(player), out var stored)
                ? stored : null;
            if (kept is not null) player.VaalSouls.Restore(kept);
            if (GameServer.Core.Diagnostics.ServerTrace.Skills)
                Console.WriteLine($"[gateway] Vaal souls on entering area 0x{player.Layout.Hash:x}: " +
                (kept is null ? "new instance, 0" : $"restored {SoulTotal(kept)}") + $" (had {SoulTotal(entry.State)} on leaving)");
            if (player.PlayerLife > 0) player.VaalPreventionUntil = VaalResourceTime(player) + entry.PreventionRemaining;
        }
    }
}
