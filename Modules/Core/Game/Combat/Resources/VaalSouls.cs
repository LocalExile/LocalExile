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
        return (int)Math.Clamp((long)playerStats.GetValueOrDefault(key) + own.GetValueOrDefault(key), int.MinValue, int.MaxValue);
    }

    public static void PublishVaalSoulInputs(Player player, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int> own, Dictionary<uint, int> stats)
    {
        foreach (uint key in VaalSoulCost.Inputs) stats[key] = VaalSoulInput(player, playerStats, own, key);
    }

    sealed class VaalRefreshCache
    {
        public long EquipmentMutations = -1;
        public int PassivesCount = -1;
        public int AscendancyCount = -1;
        public int Level = -1;
        public bool Alive;
        public bool HasVaalSources;
        public bool HardMode;
        public bool God;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, VaalRefreshCache> VaalRefreshCaches = new();

    // Reconcile before an await as well as at the send gate: a queued removal must lose its balance even if readded
    // before publication. Requirements/inactive equipment are refreshed by the normal character stat seam.
    public static void RefreshVaalSouls(Player player)
    {
        if (player.VaalSoulsRetired || player.Closed) return;
        var items = ItemsOf(player);
        long mutations = items.Mutations;
        int passivesCount = player.Character.Passives.Count;
        int ascCount = player.Character.MainAscendancy.Allocated.Count;
        int level = player.Pools.Level;
        bool alive = player.PlayerLife > 0;
        bool hardMode = LeagueFor(player).HardMode;
        bool god = player.God;
        var cache = VaalRefreshCaches.GetOrCreateValue(player);
        if (!cache.HasVaalSources && cache.EquipmentMutations == mutations && cache.PassivesCount == passivesCount
            && cache.AscendancyCount == ascCount && cache.Level == level && cache.Alive == alive
            && cache.HardMode == hardMode && cache.God == god && player.VaalSouls.Read().Length == 0)
            return;
        var playerStats = CharacterPlayerStats(player);
        var requirements = CurrentGemRequirements(player);
        var sources = new List<(uint, uint)>();
        foreach (var gem in GemSkillEffectsAt(player.Character.Name, requirements.Equipment, requirements.Inactive, LeagueFor(player),
            GrantedSkillsOf(player), WornGrantedSkillsOf(player, requirements.Equipment)))
        {
            if (SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is null) continue;
            var supported = SupportedSkillAt(player, gem.Id, requirements);
            if (!supported.RequirementsMet || SkillsFor(player).VaalBudget(gem.Effect, supported.Level) is not { } definition) continue;
            var own = supported.Stats;
            int required = VaalSoulCost.Required(definition.BaseCost,
                name => VaalSoulInput(player, playerStats, own, name));
            if (SkillsFor(player).IsAttack(gem.Effect)
                && VaalSoulInput(player, playerStats, own, Stat.VaalAttackRageCostInsteadOfSoulsPerUse) != 0)
                required = 0;
            sources.Add((definition.ActiveSkillsRow, VaalSoulCost.Capacity(required, definition.StoredUses)));
        }
        player.VaalSouls.Reconcile(sources);
        if (player.PlayerLife == 0) player.VaalSouls.ClearBalance();
        cache.EquipmentMutations = mutations;
        cache.PassivesCount = passivesCount;
        cache.AscendancyCount = ascCount;
        cache.Level = level;
        cache.Alive = alive;
        cache.HardMode = hardMode;
        cache.God = god;
        cache.HasVaalSources = sources.Count > 0;
    }

    // Called with Life=0/Dead=true in the SAME Monsters section as the lethal mutation, before notification awaits.
    // Shared hit paths do not carry an object ID; resolve actual ownership here without accepting another recipient.
    public static void CollectVaalSoulAtDeathLocked(Player player, SpawnedMonster monster)
    {
        if (monster.Life != 0 || !monster.Dead || monster.SoulKillProcessed) return;
        RefreshVaalSouls(player);
        if (player.VaalSouls.Read().Length == 0)
        {
            monster.SoulKillProcessed = true;
            return;
        }
        CollectVaalSoulOnKill(player, player.Area.Monsters.FirstOrDefault(kv => ReferenceEquals(kv.Value, monster)).Key, monster);
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
        if (stats is null || stats.GetValueOrDefault(Stat.HasSoulGainPrevention) != 0)
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

    // Same transfer policy as Rage: one successful ticket owns the balance; loading time is frozen, no save schema.
    public static void CarryVaalSouls(Player player, uint ticket)
    {
        if (player.VaalSoulsRetired || player.Closed) return;
        RefreshVaalSouls(player);
        var state = player.VaalSouls.Carry();
        player.VaalSoulsRetired = true;
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
        if (DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && player.PlayerLife > 0)
        {
            player.VaalSouls.Restore(entry.State);
            player.VaalPreventionUntil = VaalResourceTime(player) + entry.PreventionRemaining;
        }
    }
}
