using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public sealed record RageTransfer(string Character, DateTime At, RageState.Carried State);
    public static readonly ConcurrentDictionary<uint, RageTransfer> CarriedRage = new();

    public static int RageStat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    // Called after raw character/utility inputs and before attack damage/panel calculations. Derived values are
    // authoritative: setting current_rage cannot create resources or bypass gain prevention.
    public static void WithRage(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork) return;
            if (conn.RageRetired || conn.PlayerLife == 0) conn.Rage.Clear();
            if (conn.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(conn, state))
                state.Mana.Observe(RageStat(stats, ManaRecoveryRules.RagePer200));
            var inputs = RageStats.Inputs(name => RageStat(stats, name));
            // Stat projections must not advance Rage past an unprocessed projectile contact.
            var value = conn.Rage.Read(ProjectileWorldTime(conn), inputs);
            RageStats.ExpandItems(stats, value.Effective);
            foreach (var (name, amount) in RageStats.Display(value, inputs))
                if (Stats.Key(name) is uint key) stats[key] = amount;
        }
    }

    // Only accepted contacts call these, in the same Monsters critical section as life mutation, before any await.
    public static bool RageOnHitLocked(ClientState conn, bool attack, bool melee, bool critical,
        IReadOnlyDictionary<uint, int>? own = null, Random? random = null, IReadOnlyDictionary<uint, int>? player = null)
    {
        if (conn.Character is null || conn.Closed || conn.LeavingArea || conn.RageRetired
            || conn.Paused || conn.PlayerLife == 0) return false;
        player ??= CharacterPlayerStats(conn);
        int Get(string name) => (int)Math.Clamp((long)RageStat(player, name)
            + (own is null ? 0 : RageStat(own, name)), int.MinValue, int.MaxValue);
        var inputs = RageStats.Inputs(Get);
        return conn.Rage.Hit(ProjectileWorldTime(conn), inputs, attack, melee, critical, (random ?? Random.Shared).Next(100),
            soulGainPrevention: Get("has_soul_gain_prevention") != 0);
    }

    public static void RageWhenHitLocked(ClientState conn)
    {
        if (conn.RageRetired || conn.Closed || conn.LeavingArea || conn.Paused) return;
        // The lethal hit retires combat here, before PlayerDiedAtEpoch: the orb's stage icon must be queued for removal
        // first, or RetireOwnedCombat drops the runtime and the icon keeps the charges the player died with.
        if (conn.PlayerLife == 0) { conn.Rage.Clear(); conn.Rampage.Clear(); WinterOrbRetire(conn); conn.RetireOwnedCombat(); return; }
        if (conn.Character is null) return;
        var stats = CharacterPlayerStats(conn);
        conn.Rage.EnemyHit(ProjectileWorldTime(conn), RageStats.Inputs(name => RageStat(stats, name)),
            Random.Shared.Next(100), soulGainPrevention: RageStat(stats, "has_soul_gain_prevention") != 0);
    }

    public static void ClearRageOnDeath(ClientState conn)
    {
        lock (conn.Monsters) conn.Rage.Clear();
    }

    // Successful Auth transfer moves count, loss fraction, activity age and shared interval exactly once. Loading time
    // is frozen by policy. The old connection is retired even when empty; it cannot mint a second carried balance.
    public static void CarryRage(ClientState conn, uint ticket)
    {
        lock (conn.Monsters)
        {
            if (conn.RageRetired || conn.Closed) return;
            var stats = CharacterPlayerStats(conn);
            if (conn.PlayerLife == 0) conn.Rage.Clear();
            var state = conn.Rage.Carry(conn.Clock.Elapsed.TotalSeconds, RageStats.Inputs(name => RageStat(stats, name)));
            conn.Retire();
            foreach (var (old, entry) in CarriedRage)
                if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedRage.TryRemove(old, out _);
            CarriedRage.TryAdd(ticket, new(CharacterName(conn), DateTime.UtcNow, state));
        }
    }

    public static void RestoreRage(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.RageRetired || !CarriedRage.TryGetValue(conn.Ticket, out var entry)
                || entry.Character != CharacterName(conn)) return;
            if (!CarriedRage.TryRemove(new KeyValuePair<uint, RageTransfer>(conn.Ticket, entry))) return;
            if (DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && conn.PlayerLife > 0)
                conn.Rage.Restore(entry.State, conn.Clock.Elapsed.TotalSeconds);
        }
    }

    // Executed inside the connection's send gate so a queued refresh cannot resurrect a dead/retired balance or buff.
    public static byte[]? RagePresentation(ClientState conn, RagePrepared? prepared = null)
    {
        if (!CurrentGameWork) return null;
        using (conn.Performance.Lock(conn.Monsters, "rage"))
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
            // Reconcile before all Rage reads/snapshots, even when the Rage value itself has not changed.
            var bytes = WithOwnAuraChanges(conn, () => RageSnapshot(conn, prepared));
            return bytes.Length == 0 ? null : bytes;
        }
    }

    // Called only by RagePresentation under the send gate and state lock; no send or reconciliation recursion.
    public static byte[] RageSnapshot(ClientState conn, RagePrepared? prepared = null)
    {
        if (!CurrentGameWork) return [];
        var stats = CharacterPlayerStats(conn);
        var inputs = RageStats.Inputs(name => RageStat(stats, name));
        var value = conn.Rage.Read(ProjectileWorldTime(conn), inputs);
        bool prevention = RageStat(stats, "has_soul_gain_prevention") != 0;
        if (conn.SentRage == value && conn.SentVaalPrevention == prevention) return [];
        var bytes = new List<byte>();
        if (conn.RageBuff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, conn.RageBuff));
        conn.RageBuff = 0;
        if (value.Current > 0)
        {
            conn.RageBuff = conn.NextBuff++;
            bytes.AddRange(InstanceEntities.AddBuff(PlayerId, conn.RageBuff, 1060, PlayerId,
                stacks: (ushort)Math.Min(ushort.MaxValue, value.Current)));
        }
        bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
        // The skill list prepared before the gate for this very value, else built here (a value that moved meanwhile).
        var skills = prepared is { } p && p.Value == value && p.Inputs?.Matches(conn, stats) == true
            ? p.Skills : PlayerSkills(conn);
        bytes.AddRange(PlayerSkillsIfChanged(conn, skills));
        conn.SentRage = value;
        conn.SentVaalPrevention = prevention;
        Console.WriteLine($"[gateway] {CharacterName(conn)} Rage {value.Current}/{value.Maximum}, "
            + $"effective {value.Effective}, policy v{RageState.PolicyVersion} "
            + $"(t={conn.Clock.Elapsed.TotalSeconds:0.000})");
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
        public static SkillSnapshotInputs Capture(ClientState conn, Dictionary<uint, int> stats) =>
            new(new(stats), [.. ItemsOf(conn).EquipmentSnapshot().Select(e => e.Item)],
                new(CharacterSaves.SkillCosmetics(CharacterName(conn))), conn.Pools, conn.PlayerLife, conn.LockstepClient);

        public bool Matches(ClientState conn, Dictionary<uint, int> stats)
        {
            if (LockstepClient != conn.LockstepClient || Pools != conn.Pools || Life != conn.PlayerLife || Stats.Count != stats.Count
                || Stats.Any(kv => !stats.TryGetValue(kv.Key, out int value) || value != kv.Value)) return false;
            var equipment = ItemsOf(conn).EquipmentSnapshot().Select(e => e.Item).ToArray();
            if (equipment.Length != Equipment.Length) return false;
            for (int i = 0; i < equipment.Length; i++)
                if (equipment[i].Id != Equipment[i].Id || !SameSkillItem(equipment[i].Item, Equipment[i].Item, ItemsFor(conn))) return false;
            var cosmetics = CharacterSaves.SkillCosmetics(CharacterName(conn));
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
    public static Task SendRage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired) return Task.CompletedTask;
        RagePrepared? prepared = null;
        // Neither the stats nor the skill list hold the combat lock for their whole calculation; only the read does.
        var stats = CharacterPlayerStats(conn);
        RageState.Value? changed = null;
        lock (conn.Monsters)
            if (CurrentGameWork && !conn.Closed && !conn.LeavingArea && !conn.RageRetired)
            {
                var value = conn.Rage.Read(conn.Clock.Elapsed.TotalSeconds, RageStats.Inputs(name => RageStat(stats, name)));
                if (conn.SentRage != value) changed = value;
            }
        // An unchanged value goes nowhere near the send gate: RagePresentation rebuilds the player's stats under it, and the
        // 10 checks a second held every hit, echo and move behind that work (live 2026-11-05: the input queue grew to 16 s,
        // the move echoes went out for clicks seconds old and put the player back where they had been).
        if (changed is null) return Task.CompletedTask;
        if (changed is { } next)
        {
            if (!CurrentGameWork) return Task.CompletedTask;
            var inputs = SkillSnapshotInputs.Capture(conn, stats);
            var skills = PlayerSkills(conn);
            // A change during the build cannot be cached under the earlier inputs.
            if (inputs.Matches(conn, CharacterPlayerStats(conn))) prepared = new(next, skills, inputs);
        }
        return Send(remote, stream, ciphers, [], null, atGate: () => RagePresentation(conn, prepared));
    }

    public static async Task RunRage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerUpdatesState updates)
    {
        if (!PlayerUpdatesCurrent(conn, updates)) return;
        try
        {
            while (PlayerUpdatesCurrent(conn, updates))
            {
                await SendRage(remote, stream, ciphers, conn);
                await updates.Schedule.Wait(PlayerUpdateRole.Rage, 250, updates.Stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
}
