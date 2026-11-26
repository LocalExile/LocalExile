using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public sealed record RageTransfer(string Character, DateTime At, RageState.Carried State);
    public static readonly ConcurrentDictionary<uint, RageTransfer> CarriedRage = new();

    public static int RageStat(IReadOnlyDictionary<uint, int> stats, uint name) =>
        name is var key ? stats.GetValueOrDefault(key) : 0;

    // Called after raw character/utility inputs and before attack damage/panel calculations. Derived values are
    // authoritative: setting current_rage cannot create resources or bypass gain prevention.
    public static void WithRage(Player player, StatMap stats)
    {
        if (!CurrentGameWork) return;
        if (player.RageRetired || player.PlayerLife == 0) return;
        var inputs = RageStats.Inputs(player, name => RageStat(stats, name));
        // Stat projections must not advance Rage past an unprocessed projectile contact.
        var value = player.Rage.Read(ProjectileWorldTime(player), inputs);
        if (player.Rage.NextChangeAt() is double next) StatsChangeAt(next, () => ProjectileWorldTime(player));
        RageStats.ExpandItems(stats, value.Effective);
        foreach (var (name, amount) in RageStats.Display(value, inputs))
            if (name is var key) stats[key] = amount;
    }

    // Only accepted contacts call these, in the same Monsters critical section as life mutation, before any await.
    public static bool RageOnHitLocked(Player player, bool attack, bool melee, bool critical,
        IReadOnlyDictionary<uint, int>? own = null, Random? random = null, IReadOnlyDictionary<uint, int>? playerStats = null)
        => RageOnHitWithWeaponLocked(player, attack, melee, critical, null, own, random, playerStats);

    static bool RageOnHitWithWeaponLocked(Player player, bool attack, bool melee, bool critical, string? weaponClass,
        IReadOnlyDictionary<uint, int>? own = null, Random? random = null, IReadOnlyDictionary<uint, int>? playerStats = null,
        string? secondWeaponClass = null)
    {
        if (player.Character.Name is null || player.Closed || player.LeavingArea || player.RageRetired
            || player.Paused || player.PlayerLife == 0) return false;
        playerStats ??= CharacterPlayerStats(player);
        int Get(uint name) => (int)Math.Clamp((long)RageStat(playerStats, name)
            + (own is null ? 0 : RageStat(own, name)), int.MinValue, int.MaxValue);
        var inputs = RageStats.Inputs(Get);
        if (attack && (WeaponTypeDamage.FamilyOf(weaponClass) is "axe" or "sword"
                || WeaponTypeDamage.FamilyOf(secondWeaponClass) is "axe" or "sword")
            && Get(Stat.GainXRageOnHitWithAxesSwords) is int weaponRage and not 0)
            inputs = inputs with { GainOnAttackHit = ActorScale.Saturate((long)inputs.GainOnAttackHit + weaponRage) };
        return player.Rage.Hit(ProjectileWorldTime(player), inputs, attack, melee, critical, (random ?? AreaRng(player)).Next(100),
            soulGainPrevention: Get(Stat.HasSoulGainPrevention) != 0, nonDelayingRegenerationPerMinute: RageStats.ManaRegenerationPerMinute(player, Get));
    }

    internal static void RageOnMarkedStunLocked(Player player, int rage, double now)
    {
        if (player.RageRetired || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return;
        var stats = CharacterPlayerStats(player);
        int Get(uint name) => RageStat(stats, name);
        player.Rage.Gain(rage, ProjectileWorldTime(player), RageStats.Inputs(player, Get), Get(Stat.HasSoulGainPrevention) != 0);
    }

    public static void RageWhenHitLocked(Player player)
    {
        if (player.RageRetired || player.Closed || player.LeavingArea || player.Paused) return;
        // The lethal hit retires combat here, before PlayerDiedAtEpoch: the orb's stage icon must be queued for removal
        // first, or RetireOwnedCombat drops the runtime and the icon keeps the charges the player died with.
        if (player.PlayerLife == 0)
        {
            player.Rage.Clear();
            player.Rampage.Clear();
            ClearCycloneStages(player);
            ClearReaveStages(player);
            RetireBladeFlurry(player, player.ChannelGeneration);
            WinterOrbRetire(player);
            SnipeRetire(player);
            MirageWarriorsRetire(player);
            player.RetireOwnedCombat();
            return;
        }
        if (player.Character.Name is null) return;
        var stats = CharacterPlayerStats(player);
        player.Rage.EnemyHit(ProjectileWorldTime(player), RageStats.Inputs(player, name => RageStat(stats, name)),
            AreaRng(player).Next(100), soulGainPrevention: RageStat(stats, Stat.HasSoulGainPrevention) != 0);
    }

    public static void ClearRageOnDeath(Player player)
    {
        player.Rage.Clear();
    }

    // Successful Auth transfer moves count, loss fraction, activity age and shared interval exactly once. Loading time
    // is frozen by policy. The old connection is retired even when empty; it cannot mint a second carried balance.
    public static void CarryRage(Player player, uint ticket)
    {
        if (player.RageRetired || player.Closed) return;
        var stats = CharacterPlayerStats(player);
        if (player.PlayerLife == 0) player.Rage.Clear();
        var state = player.Rage.Carry(AreaTime(player), RageStats.Inputs(player, name => RageStat(stats, name)));
        player.Retire();
        foreach (var (old, entry) in CarriedRage)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedRage.TryRemove(old, out _);
        CarriedRage.TryAdd(ticket, new(CharacterName(player), DateTime.UtcNow, state));
    }

    public static void RestoreRage(Player player)
    {
        if (player.Closed || player.RageRetired || !CarriedRage.TryGetValue(player.Ticket, out var entry)
            || entry.Character != CharacterName(player)) return;
        if (!CarriedRage.TryRemove(new KeyValuePair<uint, RageTransfer>(player.Ticket, entry))) return;
        if (DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && player.PlayerLife > 0)
            player.Rage.Restore(entry.State, AreaTime(player));
    }

    // Executed inside the connection's send gate so a queued refresh cannot resurrect a dead/retired balance or buff.
    public static byte[]? RagePresentation(Player player, RagePrepared? prepared = null)
    {
        if (!CurrentGameWork) return null;
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired) return null;
        // Reconcile before all Rage reads/snapshots, even when the Rage value itself has not changed.
        var bytes = WithOwnAuraChanges(player, () => RageSnapshot(player, prepared));
        return bytes.Length == 0 ? null : bytes;
    }

    // Called only by RagePresentation under the send gate and state lock; no send or reconciliation recursion.
    public static byte[] RageSnapshot(Player player, RagePrepared? prepared = null)
    {
        if (!CurrentGameWork) return [];
        var stats = CharacterPlayerStats(player);
        var inputs = RageStats.Inputs(player, name => RageStat(stats, name));
        var value = player.Rage.Read(ProjectileWorldTime(player), inputs);
        bool prevention = RageStat(stats, Stat.HasSoulGainPrevention) != 0;
        // "when you reach Maximum Rage while a Unique Enemy is in your Presence" (Loyalty Tattoo of Akoya).
        if (value.Maximum > 0 && value.Current >= value.Maximum
            && (player.SentRage is not { } sent || sent.Maximum == 0 || sent.Current < sent.Maximum))
            LoyaltyMaxRageLocked(player, stats);
        if (player.SentRage == value && player.SentVaalPrevention == prevention) return [];
        var bytes = new List<byte>();
        player.RageBuff = (ushort)(value.Current > 0 ? 1 : 0);
        if (value.Current > 0) player.Area.Buffs.Set(player.Id, new(1060, player.Id, Stacks: (ushort)Math.Min(ushort.MaxValue, value.Current)));
        else player.Area.Buffs.Clear(player.Id, 1060, player.Id);
        bytes.AddRange(PlayerStatsOffFrames(player));
        // The skill list prepared before the gate for this very value, else built here (a value that moved meanwhile).
        var skills = prepared is { } p && p.Value == value && p.Inputs?.Matches(player, stats) == true
            ? p.Skills : PlayerSkills(player);
        bytes.AddRange(PlayerSkillsIfChanged(player, skills));
        player.SentRage = value;
        player.SentVaalPrevention = prevention;
        Console.WriteLine($"[gateway] {CharacterName(player)} Rage {value.Current}/{value.Maximum}, "
            + $"effective {value.Effective}, policy v{RageState.PolicyVersion} "
            + $"(t={AreaTime(player):0.000})");
        return [.. bytes];
    }

    /// <summary>A Rage value and the skill list built for it ahead of the send gate.</summary>
    public sealed record RagePrepared(RageState.Value Value, IReadOnlyList<GrantedSkill> Skills,
        SkillSnapshotInputs? Inputs = null);

    // A Rage value alone cannot identify a skill list: gems, equipment, conditional stats, pools, art and client mode also matter.
    public sealed record SkillSnapshotInputs(Dictionary<uint, int> Stats,
        InventoryItem[] Equipment, Dictionary<string, CharacterSaves.SavedCosmetic> Cosmetics,
        CharacterStats.Pools Pools, uint Life, bool LockstepClient)
    {
        public static SkillSnapshotInputs Capture(Player player, IReadOnlyDictionary<uint, int> stats) =>
            new(new Dictionary<uint, int>(stats), [.. ItemsOf(player).EquipmentSnapshot().Select(e => e.Item)],
                new(CharacterSaves.SkillCosmetics(CharacterName(player))), player.Pools, player.PlayerLife, player.Connection.LockstepClient);

        public bool Matches(Player player, IReadOnlyDictionary<uint, int> stats)
        {
            if (LockstepClient != player.Connection.LockstepClient || Pools != player.Pools || Life != player.PlayerLife || Stats.Count != stats.Count
                || Stats.Any(kv => !stats.TryGetValue(kv.Key, out int value) || value != kv.Value)) return false;
            var equipment = ItemsOf(player).EquipmentSnapshot().Select(e => e.Item).ToArray();
            if (equipment.Length != Equipment.Length) return false;
            for (int i = 0; i < equipment.Length; i++)
                if (equipment[i].Id != Equipment[i].Id || !SameSkillItem(equipment[i].Item, Equipment[i].Item, ItemsFor(player))) return false;
            var cosmetics = CharacterSaves.SkillCosmetics(CharacterName(player));
            return cosmetics.Count == Cosmetics.Count && Cosmetics.All(kv => cosmetics.TryGetValue(kv.Key, out var value) && value == kv.Value);
        }
    }

    // Gem XP rewrites an equipped host on every kill. XP within the same gem level changes neither its requirements
    // nor its skill records. Compare every other item field, socket colour and gem field normally.
    public static bool SameSkillItem(Item a, Item b, ItemCatalog catalog)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Sockets.Count != b.Sockets.Count || a with { Sockets = b.Sockets } != b) return false;
        for (int i = 0; i < a.Sockets.Count; i++)
        {
            var left = a.Sockets[i]; var right = b.Sockets[i];
            if (left.Colour != right.Colour) return false;
            if (ReferenceEquals(left.Gem, right.Gem)) continue;
            if (left.Gem is not { } first || right.Gem is not { } second
                || first with { GemExperience = second.GemExperience } != second
                || catalog.GemLevel(first) != catalog.GemLevel(second)) return false;
        }
        return true;
    }

    // The skill list is the expensive part of a Rage change (every gem's panel damage): build it before the send gate, so
    // the gate, which every hit and echo waits on, normally holds only comparison and packet assembly. Changed inputs
    // fall back to a current build rather than publishing the prepared list under the wrong owner state.
    public static Task SendRage(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired) return Task.CompletedTask;
        RagePrepared? prepared = null;
        // Neither the stats nor the skill list hold the combat lock for their whole calculation; only the read does.
        var stats = CharacterPlayerStats(player);
        RageState.Value? changed = null;
        if (CurrentGameWork && !player.Closed && !player.LeavingArea && !player.RageRetired)
        {
            var value = player.Rage.Read(AreaTime(player), RageStats.Inputs(player, name => RageStat(stats, name)));
            if (player.SentRage != value) changed = value;
        }
        // An unchanged value goes nowhere near the send gate: RagePresentation rebuilds the player's stats under it, and the
        // 10 checks a second held every hit, echo and move behind that work (live 2026-11-05: the input queue grew to 16 s,
        // the move echoes went out for clicks seconds old and put the player back where they had been).
        if (changed is null) return Task.CompletedTask;
        if (changed is { } next)
        {
            if (!CurrentGameWork) return Task.CompletedTask;
            var inputs = SkillSnapshotInputs.Capture(player, stats);
            var skills = PlayerSkills(player);
            // A change during the build cannot be cached under the earlier inputs.
            if (inputs.Matches(player, new Dictionary<uint, int>(CharacterPlayerStats(player)))) prepared = new(next, skills, inputs);
        }
        return Send(player, [], null, atGate: () => RagePresentation(player, prepared));
    }
}
