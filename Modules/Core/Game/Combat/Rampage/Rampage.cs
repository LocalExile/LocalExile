using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static GameServer.Modding.IModuleLifetime? CombatLifetime;
    internal static bool CurrentGameRules => CombatLifetime is { IsActive: true }
        && Program.GameLogicOrNull is GameServer.Modules.Game.GameLogic;
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

    static (uint Life, bool StageStarts) TakeOwnerDamageLocked(ClientState conn, SpawnedMonster m, int damage)
    {
        var result = TakeDamageLocked(m, damage);
        if (m.Life > 0 && result.Life == 0) RampageOnKillLocked(conn, m);
        return result;
    }

    // Smoke and Rampage apply the same Blind; overlapping sources do not multiply its penalty twice.
    internal static int RampageBlindedValue(SpawnedMonster target, double now, int value, int groundValue) =>
        target.RampageBlindUntil > now ? Math.Min(groundValue, FlaskGrounds.Blinded(value)) : groundValue;

    static bool RampageActive(ClientState conn) =>
        !conn.Closed && !conn.LeavingArea && !conn.RageRetired && !conn.Paused;

    static bool RampageSource(ClientState conn)
    {
        var sources = CharacterStatSources(conn).Stats;
        return Stats.Key(RampageSession.Source) is uint key
            && conn.PlayerStats.GetValueOrDefault(key, sources.GetValueOrDefault(key)) != 0;
    }

    static void RampageOnMeleeHitLocked(ClientState conn, SpawnedMonster target, bool melee)
    {
        if (!CurrentRampageWork || conn.Paused || target.Dead || target.Life == 0) return;
        CharacterPlayerStats(conn);
        int movement = conn.Rampage.Current.MovementPercent;
        conn.Rampage.MeleeHit(ProjectileWorldTime(conn), RampageSource(conn), conn.PlayerLife > 0,
            RampageActive(conn), melee, target.Hostile);
        if (movement != conn.Rampage.Current.MovementPercent) conn.ApplyMovementSpeedAt(ProjectileWorldTime(conn));
    }

    static void RampageOnKillLocked(ClientState conn, SpawnedMonster target)
    {
        if (!CurrentRampageWork || conn.Paused) return;
        CharacterPlayerStats(conn);
        int movement = conn.Rampage.Current.MovementPercent;
        conn.Rampage.Kill(ProjectileWorldTime(conn), target, RampageSource(conn), conn.PlayerLife > 0,
            RampageActive(conn), target.Hostile);
        if (movement != conn.Rampage.Current.MovementPercent) conn.ApplyMovementSpeedAt(ProjectileWorldTime(conn));
    }

    static void WithRampage(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            if (!CurrentRampageWork) return;
            bool enabled = Stats.Key(RampageSession.Source) is uint source && stats.GetValueOrDefault(source) != 0;
            // Pausing holds the game clock; it does not remove a streak.
            var before = conn.Rampage.Current;
            double now = conn.LeavingArea && conn.RampageTransferAt is double frozen ? frozen : ProjectileWorldTime(conn);
            var value = conn.Rampage.Observe(now, enabled, conn.PlayerLife > 0,
                !conn.Closed && !conn.RageRetired);
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
            void Add(string name, int amount)
            {
                if (amount != 0 && Stats.Key(name) is uint key)
                    stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + amount, int.MinValue, int.MaxValue);
            }
            Add("damage_+%", value.DamagePercent);
            Add("movement_velocity_+%", value.MovementPercent);
            if (Stats.Key("current_rampage_stacks") is uint current) stats[current] = value.Stacks;
            if (Stats.Key("max_rampage_stacks") is uint maximum) stats[maximum] = RampageState.MaximumStacks;
        }
    }

    internal static int RampageMovement(ClientState conn)
    {
        lock (conn.Monsters) return conn.Rampage.Current.MovementPercent;
    }

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
            bytes.AddRange(InstanceEntities.PlayerSkillsChange(PlayerId, PlayerSkills(conn), []));
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
