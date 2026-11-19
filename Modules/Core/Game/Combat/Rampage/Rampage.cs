using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static GameServer.Modding.IModuleLifetime? CombatLifetime;
    public static bool CurrentGameRules => CombatLifetime is { IsActive: true }
        && Program.GameLogicOrNull is GameServer.Modules.Game.GameLogic logic
        && ReferenceEquals(logic.Lifetime, CombatLifetime);
    public static bool CurrentRampageWork => CurrentGameRules && (Program.ProjectileOutputGuard.Value?.Invoke() ?? true);
    public static readonly RampageWorld RetiredRampageWorld = new();

    public static void EnsureRampageWorker(Player conn, RampageWorld runtime)
    {
        // Called under Monsters, from current-runtime activation, not from a retained module callback.
        if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.RageRetired || !conn.PlayerSent
            || !conn.Connected
            || !runtime.Enabled && runtime.PendingRemoval.Count == 0 && runtime.PendingBuffRemoval.Count == 0) return;
        var owner = CombatLifetime!;
        var source = runtime.SourceOwner;
        if (!conn.OwnedCombatWorker.TryStart(conn, owner, source, runtime, out long generation)) return;
        // Reload can be requested from an old rules continuation; do not inherit its AsyncLocal output delegates.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = Task.Run(() => RunRampage(conn, runtime, owner, source, generation));
            return;
        }
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => RunRampage(conn, runtime, owner, source, generation));
    }

    static (uint Life, bool StageStarts) TakeOwnerDamageLocked(Player conn, SpawnedMonster m, int damage,
        bool? rampageSource = null, IReadOnlyDictionary<uint, int>? player = null)
    {
        var result = TakeDamageLocked(m, damage);
        if (m.Life > 0 && result.Life == 0)
        {
            CaptureMadnessExplosionLocked(conn, m);
            m.Poisons.RememberDeath(ProjectileWorldTime(conn));
            RampageOnKillLocked(conn, m, rampageSource, player);
        }
        return result;
    }

    // Smoke and Rampage apply the same Blind; overlapping sources do not multiply its penalty twice.
    public static int RampageBlindedValue(SpawnedMonster target, double now, int value, int groundValue) =>
        target.RampageBlindUntil > now ? Math.Min(groundValue, FlaskGrounds.Blinded(value)) : groundValue;

    static bool RampageActive(Player conn) =>
        !conn.Closed && !conn.LeavingArea && !conn.RageRetired && !conn.Paused;

    /// <summary>The two streak sources, read from one place: an explicit /set_stat value for either wins over the
    /// caller's snapshot and the passives/items, and one explicit key keeps the pair from rebuilding the sources.</summary>
    static (bool Melee, bool Kill) RampageSources(Player conn, IReadOnlyDictionary<uint, int>? player = null)
    {
        uint? meleeKey = Stats.Key(RampageSession.Source), killKey = Stats.Key(RampageSession.KillSource);
        var stats = player ?? CharacterStatSources(conn).Stats;
        return (meleeKey is uint m2 && stats.GetValueOrDefault(m2) != 0,
            killKey is uint k2 && stats.GetValueOrDefault(k2) != 0);
    }

    static bool RampageSource(Player conn, IReadOnlyDictionary<uint, int>? player = null)
    {
        var (melee, kill) = RampageSources(conn, player);
        return melee || kill;
    }

    // The caller may reuse this source decision for the same contact's lethal mutation, without a second stat read.
    static bool? RampageOnMeleeHitLocked(Player conn, SpawnedMonster target, bool melee,
        IReadOnlyDictionary<uint, int>? player = null)
    {
        if (!melee || !target.Hostile || target.Dead || target.Life == 0 || conn.PlayerLife == 0
            || !CurrentRampageWork || !RampageActive(conn)) return null;
        var (meleeGrants, killSource) = RampageSources(conn, player);
        bool enabled = meleeGrants || killSource;
        // Kills alone (player_gain_rampage_stacks) do not make ordinary melee hits grant stacks.
        double now = ProjectileWorldTime(conn);
        ReconcileRampage(conn, enabled, now);
        int movement = conn.Rampage.Current.MovementPercent;
        conn.Rampage.MeleeHit(now, enabled, true, true, meleeGrants, true);
        RampageThresholdEffects(conn);
        if (movement != conn.Rampage.Current.MovementPercent) conn.ApplyMovementSpeedAt(now);
        return enabled;
    }

    static void RampageOnKillLocked(Player conn, SpawnedMonster target, bool? source = null,
        IReadOnlyDictionary<uint, int>? player = null)
    {
        if (!target.Hostile || conn.PlayerLife == 0 || !CurrentRampageWork || !RampageActive(conn)) return;
        bool enabled = source ?? RampageSource(conn, player);
        double now = ProjectileWorldTime(conn);
        ReconcileRampage(conn, enabled, now);
        int movement = conn.Rampage.Current.MovementPercent;
        conn.Rampage.Kill(now, target, enabled, true, true, true);
        RampageThresholdEffects(conn);
        if (movement != conn.Rampage.Current.MovementPercent) conn.ApplyMovementSpeedAt(now);
    }

    /// <summary>The item modifiers that trigger on entering a new Rampage tier ("on Rampage threshold"): life
    /// recovery, physical damage immunity, phasing and dispelling ailments. Each crossing triggers them once.</summary>
    static void RampageThresholdEffects(Player conn)
    {
        int[] crossed = conn.Rampage.TakeThresholds();
        if (crossed.Length == 0) return;
        var sources = CharacterStatSources(conn).Stats;
        int Get(string name) => Stats.Key(name) is uint key
            ? sources.GetValueOrDefault(key) : 0;
        int heal = Get("recover_%_maximum_life_on_rampage_threshold");
        if (heal > 0 && conn.PlayerLife > 0)
            conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife,
                (long)conn.PlayerLife + (long)conn.PlayerMaxLife * heal / 100);
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (Get("gain_physical_damage_immunity_on_rampage_threshold_ms") is int immuneMs and > 0)
            conn.PhysicalImmunityUntil = Math.Max(conn.PhysicalImmunityUntil, now + immuneMs / 1000.0);
        if (Get("phasing_on_rampage_threshold_ms") is int phasingMs and > 0)
        {
            conn.PhasingUntil = Math.Max(conn.PhasingUntil, now + phasingMs / 1000.0);
            PresentStats(conn);
        }
        if (Get("dispel_status_ailments_on_rampage_threshold") != 0)
        {
            var before = conn.ElementalAilments.Snapshot(now);
            conn.ElementalAilments.Clear();
            conn.ApplyMovementSpeed();
            if (conn.Connected)
            {
                _ = SendPlayerAilmentBuffs(conn, before, conn.ElementalAilments.Snapshot(now));
            }
        }
    }

    // Contact accounting needs only this mechanic, not a full character/skill stat rebuild for every victim.
    static RampageState.Value ReconcileRampage(Player conn, bool enabled, double now)
    {
        var before = conn.Rampage.Current;
        var value = conn.Rampage.Observe(now, enabled, conn.PlayerLife > 0, !conn.Closed && !conn.RageRetired);
        if (before.MovementPercent != value.MovementPercent)
            conn.ApplyMovementSpeedAt(enabled && conn.PlayerLife > 0 && before.ExpiresAt is double end && now >= end ? end : now);
        if (enabled && conn.PlayerLife > 0 && !conn.Closed && !conn.LeavingArea && !conn.RageRetired)
        {
            var runtime = conn.RampageWorld;
            runtime.Enable();
            EnsureRampageWorker(conn, runtime);
        }
        else if (conn.OwnedCombatState is not null || conn.RetiredCombatObjects.Count != 0
            || conn.RetiredCombatBuffs.Count != 0)
        {
            var runtime = conn.RampageWorld;
            runtime.Disable();
            foreach (uint actor in runtime.PendingRemoval)
                if (conn.Monsters.Remove(actor, out var removed)) removed.Dead = true;
            foreach (var (actor, _) in runtime.Blinds)
                if (conn.Monsters.TryGetValue(actor, out var target)) target.RampageBlindUntil = 0;
            // Flush primitive cleanup once, then the worker releases its lease when idle.
            EnsureRampageWorker(conn, runtime);
        }
        return value;
    }

    static bool RampageEnabled(IReadOnlyDictionary<uint, int> stats) =>
        (Stats.Key(RampageSession.Source) is uint meleeKey && stats.GetValueOrDefault(meleeKey) != 0)
        || (Stats.Key(RampageSession.KillSource) is uint killKey && stats.GetValueOrDefault(killKey) != 0);

    // The streak as the stats allow it (PlayerStatsUpdated): cleared without a source, the movement speed and worker.
    static void ReconcileRampageSource(Player conn, IReadOnlyDictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            if (!CurrentRampageWork) return;
            double now = conn.LeavingArea && conn.RampageTransferAt is double frozen ? frozen : ProjectileWorldTime(conn);
            ReconcileRampage(conn, RampageEnabled(stats), now);
        }
    }

    static void WithRampage(Player conn, StatMap stats)
    {
        lock (conn.Monsters)
        {
            if (!CurrentRampageWork) return;
            if (Stat.MaxRampageStacks is var maximumStacks) stats[maximumStacks] = RampageState.MaximumStacks;
            if (!RampageEnabled(stats) || conn.PlayerLife == 0) return;
            // Pausing holds the game clock; it does not remove a streak.
            double now = conn.LeavingArea && conn.RampageTransferAt is double frozen ? frozen : ProjectileWorldTime(conn);
            var value = conn.Rampage.Current;
            if (value.ExpiresAt is not double expires || now >= expires) return;
            StatsChangeAt(expires, () => ProjectileWorldTime(conn));
            void Add(string name, int amount)
            {
                if (amount != 0 && Stats.Key(name) is uint key)
                    stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + amount, int.MinValue, int.MaxValue);
            }
            Add("damage_+%", value.DamagePercent);
            Add("movement_velocity_+%", value.MovementPercent);
            // "1% increased Rarity of Items found per X Rampage Kills": the streak's stack count.
            if (Stats.Key("item_found_rarity_+1%_per_X_rampage_stacks") is uint rarityPerStacks
                && stats.GetValueOrDefault(rarityPerStacks) is int perKills)
                Add("base_item_found_rarity_+%", RampageRarity(value.Stacks, perKills));
            // "X% increased Area of Effect per 25 Rampage Kills".
            if (Stat.AreaOfEffectPlusPctPer25RampageStacks is var areaPerStacks
                && stats.GetValueOrDefault(areaPerStacks) is int areaValue)
                Add("base_skill_area_of_effect_+%", RampageRarity(value.Stacks, 25) * areaValue);
            if (Stat.CurrentRampageStacks is var current) stats[current] = value.Stacks;
            if (Stat.MaxRampageStacks is var maximum) stats[maximum] = RampageState.MaximumStacks;
        }
    }

    public static int RampageMovement(Player conn)
    {
        lock (conn.Monsters) return conn.Rampage.Current.MovementPercent;
    }

    /// <summary>"1% increased Rarity of Items found per X Rampage Kills": the streak's whole groups of X kills.</summary>
    public static int RampageRarity(int stacks, int perKills) =>
        perKills > 0 ? Math.Max(0, stacks) / perKills : 0;

    // The 32 ms world loop offers a presentation every step; without this check each empty offer took the send
    // gate (about 29 a second even when nothing had changed).
    static bool RampagePresentationPending(Player conn)
    {
        lock (conn.Monsters) return conn.OwnedCombatState is RampageWorld && RampageUnsentLocked(conn);
    }

    // The streak differs from the one the client was sent. Under conn.Monsters.
    static bool RampageUnsentLocked(Player conn) =>
        CurrentRampageWork && !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.Rampage.Current != conn.SentRampage;

    /// <summary>What of a streak the full stat and skill snapshots depend on: they are sent again only when this
    /// changes. Damage and movement change every 20 stacks; rarity, area and Chain Hook's and Shield Charge's attack
    /// speed (every 10 stacks) only for a character that has the stat, at its own group size.</summary>
    public static (int Active, int Damage, int Movement, int Rarity, int Area, int AttackSpeed) RampageStatSignature(
        RampageState.Value value, int rarityPerKills, int areaPer25, int attackSpeedPer10) =>
        (value.Active ? 1 : 0, value.DamagePercent, value.MovementPercent, RampageRarity(value.Stacks, rarityPerKills),
            RampageRarity(value.Stacks, 25) * areaPer25, RampageRarity(value.Stacks, 10) * attackSpeedPer10);

    public static byte[]? RampagePresentation(Player conn)
    {
        lock (conn.Monsters)
        {
            if (!RampageUnsentLocked(conn)) return null;
            var stats = CharacterPlayerStats(conn);
            var value = conn.Rampage.Current;
            var bytes = new List<byte>();
            // The same buff instance takes the new count; replacing it every kill cost a remove and an add.
            if (value.Active)
            {
                if (conn.RampageBuff == 0) conn.RampageBuff = conn.NextBuff++;
                bytes.AddRange(InstanceEntities.AddBuff(PlayerId, conn.RampageBuff, RampageCatalog.BuffDefinitionRow,
                    PlayerId, u68: BitConverter.SingleToUInt32Bits((float)value.RemainingSeconds(ProjectileWorldTime(conn))),
                    stacks: (ushort)value.Stacks));
            }
            else if (conn.RampageBuff != 0)
            {
                bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, conn.RampageBuff));
                conn.RampageBuff = 0;
            }
            // The full stat and skill snapshots are ~39 KB together, so they follow only the changes that reach them
            // (RampageStatSignature). current_rampage_stacks changes every kill and is one small stat update (the buff
            // row reads its count from it).
            int Stat(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            var signature = RampageStatSignature(value, Stat("item_found_rarity_+1%_per_X_rampage_stacks"),
                Stat("area_of_effect_+%_per_25_rampage_stacks"),
                Stat("chain_hook_and_shield_charge_attack_speed_+%_per_10_rampage_stacks"));
            if (signature != conn.SentRampageStat)
            {
                conn.ApplyMovementSpeed();
                bytes.AddRange(PlayerStatsOffFrames(conn));
                bytes.AddRange(PlayerSkillsIfChanged(conn, PlayerSkills(conn)));
                conn.SentRampageStat = signature;
            }
            else if (global::Stat.CurrentRampageStacks is var current && value.Stacks != conn.SentRampageStacks)
            {
                bytes.AddRange(InstanceEntities.StatsUpdate(PlayerId, [(current, value.Stacks)]));
            }
            conn.SentRampageStacks = value.Stacks;
            conn.SentRampage = value;
            return bytes.Count == 0 ? null : [.. bytes];
        }
    }

    static async Task RunRampage(Player conn, RampageWorld runtime,
        GameServer.Modding.IModuleLifetime owner, GameServer.Modding.IModuleLifetime? source, long generation)
    {
        bool Current()
        {
            lock (conn.Monsters) return CurrentGameRules && !conn.Closed && !conn.LeavingArea && !conn.RageRetired
                && ReferenceEquals(AscendancyPolicies.SourceOwner(conn), source)
                && ReferenceEquals(conn.OwnedCombatState, runtime)
                && conn.OwnedCombatWorker.Current(owner, source, runtime, generation);
        }
        Program.ProjectileOutputGuard.Value = Current;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(owner.Stopping,
            source?.Stopping ?? CancellationToken.None);
        try
        {
            while (Current())
            {
                await AdvanceRampageEffects(conn);
                if (RampagePresentationPending(conn))
                    await Send(conn, [], null, atGate: () =>
                        Current() ? RampagePresentation(conn) : null);
                lock (conn.Monsters)
                    if (!runtime.Enabled && runtime.PendingRemoval.Count == 0 && runtime.PendingBuffRemoval.Count == 0
                        && runtime.Blinds.Count == 0) break;
                await Task.Delay(32, stopping.Token);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        catch (OperationCanceledException) when (!owner.IsActive || source is { IsActive: false }) { }
        catch (Exception e)
        {
            if (!Current()) return;
            Console.WriteLine($"[gateway] {conn.Remote} {conn.OwnedCombatWorker.Key} execution failed: {e}");
            lock (conn.Monsters) { conn.Closed = true; runtime.Disable(); }
        }
        finally
        {
            Program.ProjectileOutputGuard.Value = null;
            lock (conn.Monsters) conn.OwnedCombatWorker.Complete(owner, source, runtime, generation);
        }
    }
}
