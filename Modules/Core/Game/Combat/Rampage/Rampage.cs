using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static GameServer.Modding.IModuleLifetime? CombatLifetime;
    internal static bool CurrentGameRules => CombatLifetime is { IsActive: true }
        && Program.GameLogicOrNull is GameServer.Modules.Game.GameLogic logic
        && ReferenceEquals(logic.Lifetime, CombatLifetime);
    internal static bool CurrentRampageWork => CurrentGameRules && (Program.ProjectileOutputGuard.Value?.Invoke() ?? true);
    internal static readonly RampageWorld RetiredRampageWorld = new();

    internal static void EnsureRampageWorker(ClientState conn, RampageWorld runtime)
    {
        // Called under Monsters, from current-runtime activation, not from a retained module callback.
        if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.RageRetired || !conn.PlayerSent
            || conn.Wire.Stream is null || conn.Wire.Ciphers is null
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

    static (uint Life, bool StageStarts) TakeOwnerDamageLocked(ClientState conn, SpawnedMonster m, int damage,
        bool? rampageSource = null, IReadOnlyDictionary<uint, int>? player = null)
    {
        var result = TakeDamageLocked(m, damage);
        if (m.Life > 0 && result.Life == 0)
        {
            CaptureMadnessExplosionLocked(conn, m);
            RampageOnKillLocked(conn, m, rampageSource, player);
        }
        return result;
    }

    // Smoke and Rampage apply the same Blind; overlapping sources do not multiply its penalty twice.
    internal static int RampageBlindedValue(SpawnedMonster target, double now, int value, int groundValue) =>
        target.RampageBlindUntil > now ? Math.Min(groundValue, FlaskGrounds.Blinded(value)) : groundValue;

    static bool RampageActive(ClientState conn) =>
        !conn.Closed && !conn.LeavingArea && !conn.RageRetired && !conn.Paused;

    /// <summary>The two streak sources, read from one place: an explicit /set_stat value for either wins over the
    /// caller's snapshot and the passives/items, and one explicit key keeps the pair from rebuilding the sources.</summary>
    static (bool Melee, bool Kill) RampageSources(ClientState conn, IReadOnlyDictionary<uint, int>? player = null)
    {
        uint? meleeKey = Stats.Key(RampageSession.Source), killKey = Stats.Key(RampageSession.KillSource);
        bool Has(uint? key) => key is uint k && conn.PlayerStats.ContainsKey(k);
        if (Has(meleeKey) || Has(killKey))
            return (meleeKey is uint m && conn.PlayerStats.GetValueOrDefault(m) != 0,
                killKey is uint k && conn.PlayerStats.GetValueOrDefault(k) != 0);
        var stats = player ?? CharacterStatSources(conn).Stats;
        return (meleeKey is uint m2 && stats.GetValueOrDefault(m2) != 0,
            killKey is uint k2 && stats.GetValueOrDefault(k2) != 0);
    }

    static bool RampageSource(ClientState conn, IReadOnlyDictionary<uint, int>? player = null)
    {
        var (melee, kill) = RampageSources(conn, player);
        return melee || kill;
    }

    // The caller may reuse this source decision for the same contact's lethal mutation, without a second stat read.
    static bool? RampageOnMeleeHitLocked(ClientState conn, SpawnedMonster target, bool melee,
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

    static void RampageOnKillLocked(ClientState conn, SpawnedMonster target, bool? source = null,
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
    static void RampageThresholdEffects(ClientState conn)
    {
        int[] crossed = conn.Rampage.TakeThresholds();
        if (crossed.Length == 0) return;
        var sources = CharacterStatSources(conn).Stats;
        int Get(string name) => Stats.Key(name) is uint key
            ? conn.PlayerStats.TryGetValue(key, out int dev) ? dev : sources.GetValueOrDefault(key) : 0;
        int heal = Get("recover_%_maximum_life_on_rampage_threshold");
        if (heal > 0 && conn.PlayerLife > 0)
            conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife,
                (long)conn.PlayerLife + (long)conn.PlayerMaxLife * heal / 100);
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (Get("gain_physical_damage_immunity_on_rampage_threshold_ms") is int immuneMs and > 0)
            conn.PhysicalImmunityUntil = Math.Max(conn.PhysicalImmunityUntil, now + immuneMs / 1000.0);
        if (Get("phasing_on_rampage_threshold_ms") is int phasingMs and > 0)
            conn.PhasingUntil = Math.Max(conn.PhasingUntil, now + phasingMs / 1000.0);
        if (Get("dispel_status_ailments_on_rampage_threshold") != 0)
        {
            var before = conn.ElementalAilments.Snapshot(now);
            conn.ElementalAilments.Clear();
            conn.ApplyMovementSpeed();
            if (conn.Wire.Stream is not null && conn.Wire.Ciphers is not null)
            {
                var (remote, stream, ciphers) = conn.Wire;
                _ = SendPlayerAilmentBuffs(remote, stream, ciphers, before, conn.ElementalAilments.Snapshot(now));
            }
        }
    }

    // Contact accounting needs only this mechanic, not a full character/skill stat rebuild for every victim.
    static RampageState.Value ReconcileRampage(ClientState conn, bool enabled, double now)
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

    static void WithRampage(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            if (!CurrentRampageWork) return;
            bool enabled = (Stats.Key(RampageSession.Source) is uint meleeKey && stats.GetValueOrDefault(meleeKey) != 0)
                || (Stats.Key(RampageSession.KillSource) is uint killKey && stats.GetValueOrDefault(killKey) != 0);
            // Pausing holds the game clock; it does not remove a streak.
            double now = conn.LeavingArea && conn.RampageTransferAt is double frozen ? frozen : ProjectileWorldTime(conn);
            var value = ReconcileRampage(conn, enabled, now);
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
            if (Stats.Key("area_of_effect_+%_per_25_rampage_stacks") is uint areaPerStacks
                && stats.GetValueOrDefault(areaPerStacks) is int areaValue)
                Add("base_skill_area_of_effect_+%", RampageRarity(value.Stacks, 25) * areaValue);
            if (Stats.Key("current_rampage_stacks") is uint current) stats[current] = value.Stacks;
            if (Stats.Key("max_rampage_stacks") is uint maximum) stats[maximum] = RampageState.MaximumStacks;
        }
    }

    internal static int RampageMovement(ClientState conn)
    {
        lock (conn.Monsters) return conn.Rampage.Current.MovementPercent;
    }

    /// <summary>"1% increased Rarity of Items found per X Rampage Kills": the streak's whole groups of X kills.</summary>
    internal static int RampageRarity(int stacks, int perKills) =>
        perKills > 0 ? Math.Max(0, stacks) / perKills : 0;

    internal static byte[]? RampagePresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentRampageWork || conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
            CharacterPlayerStats(conn);
            var value = conn.Rampage.Current;
            if (value == conn.SentRampage) return null;
            var bytes = new List<byte>();
            if (conn.RampageBuff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, conn.RampageBuff));
            conn.RampageBuff = 0;
            if (value.Active)
            {
                conn.RampageBuff = conn.NextBuff++;
                bytes.AddRange(InstanceEntities.AddBuff(PlayerId, conn.RampageBuff, RampageCatalog.BuffDefinitionRow,
                    PlayerId, u68: BitConverter.SingleToUInt32Bits((float)value.RemainingSeconds(ProjectileWorldTime(conn))),
                    stacks: (ushort)value.Stacks));
            }
            conn.ApplyMovementSpeed();
            bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
            bytes.AddRange(PlayerSkillsIfChanged(conn, PlayerSkills(conn)));
            conn.SentRampage = value;
            return [.. bytes];
        }
    }

    static async Task RunRampage(ClientState conn, RampageWorld runtime,
        GameServer.Modding.IModuleLifetime owner, GameServer.Modding.IModuleLifetime? source, long generation)
    {
        var (remote, stream, ciphers) = conn.Wire;
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
                await AdvanceRampageEffects(remote, stream, ciphers, conn);
                await Send(remote, stream, ciphers, [], null, atGate: () =>
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
            Console.WriteLine($"[gateway] {remote} {conn.OwnedCombatWorker.Key} execution failed: {e}");
            lock (conn.Monsters) { conn.Closed = true; runtime.Disable(); }
        }
        finally
        {
            Program.ProjectileOutputGuard.Value = null;
            lock (conn.Monsters) conn.OwnedCombatWorker.Complete(owner, source, runtime, generation);
        }
    }
}
