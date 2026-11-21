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

    // Reconcile before an await as well as at the send gate: a queued removal must lose its balance even if readded
    // before publication. Requirements/inactive equipment are refreshed by the normal character stat seam.
    public static void RefreshVaalSouls(Player player)
    {
        if (player.VaalSoulsRetired || player.Closed) return;
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
    }

    // Called with Life=0/Dead=true in the SAME Monsters section as the lethal mutation, before notification awaits.
    // Shared hit paths do not carry an object ID; resolve actual ownership here without accepting another recipient.
    public static void CollectVaalSoulAtDeathLocked(Player player, SpawnedMonster monster) =>
        CollectVaalSoulOnKill(player, player.Area.Monsters.FirstOrDefault(kv => ReferenceEquals(kv.Value, monster)).Key, monster);

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
        RefreshVaalSouls(player);
        var stats = CharacterPlayerStats(player);
        if (RageStat(stats, Stat.HasSoulGainPrevention) != 0)
        {
            Console.WriteLine($"[gateway] Vaal soul not gained from monster 0x{id:x}: soul gain prevention (t={VaalResourceTime(player):0.000})");
            return false;
        }
        if (!player.VaalSouls.Gain()) return false;
        // "Kills grant an additional Vaal Soul if you have Rampaged Recently".
        if (RageStat(stats, Stat.GainAnAdditionalVaalSoulOnKillIfHaveRampagedRecently) != 0
            && player.Rampage.Current.Active)
            player.VaalSouls.Gain();
        return true;
    }

    // Native UPDATE clamps against OLD capacity. Unknown rows and increases must seed first, then restore current.
    // Empty lists preserve native entries, so every removed row needs an explicit zero update.
    public static (VaalSoulSeed[] Seeds, VaalSoulUpdate[] Updates) VaalSoulChanges(Player player)
    {
        RefreshVaalSouls(player);
        var current = player.VaalSouls.Read().ToDictionary(b => b.ActiveSkillsRow);
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
        IReadOnlyList<ushort> gone)
    {
        var changes = VaalSoulChanges(player);
        return InstanceEntities.PlayerSkillsChange(player.Id, skills, gone, changes.Seeds, changes.Updates, CanDetonateMines(player));
    }

    public static byte[]? VaalSoulPresentation(Player player)
    {
        if (player.Closed || player.LeavingArea || player.VaalSoulsRetired) return null;
        var changes = VaalSoulChanges(player);
        return changes.Seeds.Length == 0 && changes.Updates.Length == 0 ? null
            : InstanceEntities.PlayerSkillsChange(player.Id, [], [], changes.Seeds, changes.Updates, CanDetonateMines(player));
    }

    public static Task SendVaalSouls(Player player) =>
        SendWorldRecord(player, player.Id, [], null, atGate: () => VaalSoulPresentation(player));

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
