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

    public static void EnsureRampageWorker(Player player, RampageWorld runtime)
    {
        // Called in the area's frame, from current-runtime activation, not from a retained module callback.
        if (!CurrentGameRules || player.Closed || player.LeavingArea || player.RageRetired || !player.Connection.PlayerSent
            || !player.Connection.Connected
            || !runtime.Enabled && runtime.PendingRemoval.Count == 0 && runtime.PendingBuffRemoval.Count == 0) return;
        var owner = CombatLifetime!;
        var source = runtime.SourceOwner;
        if (!player.OwnedCombatWorker.TryStart(player, owner, source, runtime, out long generation)) return;
        // Reload can be requested from an old rules continuation; do not inherit its AsyncLocal output delegates.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = AreaFrames.Start(() => RunRampage(player, runtime, owner, source, generation));
            return;
        }
        using (ExecutionContext.SuppressFlow())
            _ = AreaFrames.Start(() => RunRampage(player, runtime, owner, source, generation));
    }

    static (uint Life, bool StageStarts) TakeOwnerDamageLocked(Player player, SpawnedMonster m, int damage,
        bool? rampageSource = null, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        var result = TakeDamageLocked(m, damage);
        if (m.Life > 0 && result.Life == 0)
        {
            CaptureMadnessExplosionLocked(player, m);
            m.Poisons.RememberDeath(ProjectileWorldTime(player));
            RampageOnKillLocked(player, m, rampageSource, playerStats);
        }
        return result;
    }

    // Smoke and Rampage apply the same Blind; overlapping sources do not multiply its penalty twice.
    public static int RampageBlindedValue(SpawnedMonster target, double now, int value, int groundValue) =>
        target.RampageBlindUntil > now ? Math.Min(groundValue, FlaskGrounds.Blinded(value)) : groundValue;

    static bool RampageActive(Player player) =>
        !player.Closed && !player.LeavingArea && !player.RageRetired && !player.Paused;

    /// <summary>The two streak sources, read from one place: an explicit /set_stat value for either wins over the
    /// caller's snapshot and the passives/items, and one explicit key keeps the pair from rebuilding the sources.</summary>
    static (bool Melee, bool Kill) RampageSources(Player player, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        uint? meleeKey = (RampageSession.Source), killKey = (RampageSession.KillSource);
        int Get(uint key) => playerStats is not null ? playerStats.GetValueOrDefault(key) : PlayerStat(player, key);
        return (meleeKey is uint m2 && Get(m2) != 0, killKey is uint k2 && Get(k2) != 0);
    }

    static bool RampageSource(Player player, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        var (melee, kill) = RampageSources(player, playerStats);
        return melee || kill;
    }

    // The caller may reuse this source decision for the same contact's lethal mutation, without a second stat read.
    static bool? RampageOnMeleeHitLocked(Player player, SpawnedMonster target, bool melee,
        IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        if (!melee || !target.Hostile || target.Dead || target.Life == 0 || player.PlayerLife == 0
            || !CurrentRampageWork || !RampageActive(player)) return null;
        var (meleeGrants, killSource) = RampageSources(player, playerStats);
        bool enabled = meleeGrants || killSource;
        // Kills alone (player_gain_rampage_stacks) do not make ordinary melee hits grant stacks.
        double now = ProjectileWorldTime(player);
        ReconcileRampage(player, enabled, now);
        int movement = player.Rampage.Current.MovementPercent;
        player.Rampage.MeleeHit(now, enabled, true, true, meleeGrants, true);
        RampageThresholdEffects(player);
        if (movement != player.Rampage.Current.MovementPercent) player.ApplyMovementSpeedAt(now);
        return enabled;
    }

    static void RampageOnKillLocked(Player player, SpawnedMonster target, bool? source = null,
        IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        if (!target.Hostile || player.PlayerLife == 0 || !CurrentRampageWork || !RampageActive(player)) return;
        bool enabled = source ?? RampageSource(player, playerStats);
        double now = ProjectileWorldTime(player);
        ReconcileRampage(player, enabled, now);
        int movement = player.Rampage.Current.MovementPercent;
        player.Rampage.Kill(now, target, enabled, true, true, true);
        RampageThresholdEffects(player);
        if (movement != player.Rampage.Current.MovementPercent) player.ApplyMovementSpeedAt(now);
    }

    /// <summary>The item modifiers that trigger on entering a new Rampage tier ("on Rampage threshold"): life
    /// recovery, physical damage immunity, phasing and dispelling ailments. Each crossing triggers them once.</summary>
    static void RampageThresholdEffects(Player player)
    {
        int[] crossed = player.Rampage.TakeThresholds();
        if (crossed.Length == 0) return;
        int Get(uint key) => PlayerStat(player, key);
        int heal = Get(Stat.RecoverPctMaximumLifeOnRampageThreshold);
        if (heal > 0 && player.PlayerLife > 0)
            player.PlayerLife = (uint)Math.Min(player.PlayerMaxLife,
                (long)player.PlayerLife + (long)player.PlayerMaxLife * heal / 100);
        double now = AreaTime(player);
        if (Get(Stat.GainPhysicalDamageImmunityOnRampageThresholdMs) is int immuneMs and > 0)
            player.PhysicalImmunityUntil = Math.Max(player.PhysicalImmunityUntil, now + immuneMs / 1000.0);
        if (Get(Stat.PhasingOnRampageThresholdMs) is int phasingMs and > 0) GainPhasing(player, phasingMs / 1000.0);
        if (Get(Stat.DispelStatusAilmentsOnRampageThreshold) != 0)
        {
            var before = player.ElementalAilments.Snapshot(now);
            player.ElementalAilments.Clear();
            player.ApplyMovementSpeed();
            if (player.Connection.Connected)
            {
                _ = SendPlayerAilmentBuffs(player, before, player.ElementalAilments.Snapshot(now));
            }
        }
    }

    // Contact accounting needs only this mechanic, not a full character/skill stat rebuild for every victim.
    static RampageState.Value ReconcileRampage(Player player, bool enabled, double now)
    {
        var before = player.Rampage.Current;
        var value = player.Rampage.Observe(now, enabled, player.PlayerLife > 0, !player.Closed && !player.RageRetired);
        if (before.MovementPercent != value.MovementPercent)
            player.ApplyMovementSpeedAt(enabled && player.PlayerLife > 0 && before.ExpiresAt is double end && now >= end ? end : now);
        if (enabled && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea && !player.RageRetired)
        {
            var runtime = player.RampageWorld;
            runtime.Enable();
            EnsureRampageWorker(player, runtime);
        }
        else if (player.OwnedCombatState is not null || player.RetiredCombatObjects.Count != 0)
        {
            var runtime = player.RampageWorld;
            runtime.Disable();
            foreach (uint actor in runtime.PendingRemoval)
                if (player.Area.Monsters.Remove(actor, out var removed)) removed.Dead = true;
            foreach (var (actor, _) in runtime.Blinds)
                if (player.Area.Monsters.TryGetValue(actor, out var target)) target.RampageBlindUntil = 0;
            // Flush primitive cleanup once, then the worker releases its lease when idle.
            EnsureRampageWorker(player, runtime);
        }
        return value;
    }

    static bool RampageEnabled(IReadOnlyDictionary<uint, int> stats) =>
        (RampageSession.Source is var meleeKey && stats.GetValueOrDefault(meleeKey) != 0)
        || (RampageSession.KillSource is var killKey && stats.GetValueOrDefault(killKey) != 0);

    // The streak as the stats allow it (PlayerStatsUpdated): cleared without a source, the movement speed and worker.
    static void ReconcileRampageSource(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        if (!CurrentRampageWork) return;
        double now = player.LeavingArea && player.RampageTransferAt is double frozen ? frozen : ProjectileWorldTime(player);
        ReconcileRampage(player, RampageEnabled(stats), now);
    }

    static void WithRampage(Player player, StatMap stats)
    {
        if (!CurrentRampageWork) return;
        if (Stat.MaxRampageStacks is var maximumStacks) stats[maximumStacks] = RampageState.MaximumStacks;
        if (!RampageEnabled(stats) || player.PlayerLife == 0) return;
        // Pausing holds the game clock; it does not remove a streak.
        double now = player.LeavingArea && player.RampageTransferAt is double frozen ? frozen : ProjectileWorldTime(player);
        var value = player.Rampage.Current;
        if (value.ExpiresAt is not double expires || now >= expires) return;
        StatsChangeAt(expires, () => ProjectileWorldTime(player));
        void Add(uint name, int amount)
        {
            if (amount != 0 && name is var key)
                stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + amount, int.MinValue, int.MaxValue);
        }
        Add(Stat.DamagePlusPct, value.DamagePercent);
        Add(Stat.MovementVelocityPlusPct, value.MovementPercent);
        // "1% increased Rarity of Items found per X Rampage Kills": the streak's stack count.
        if (Stats.Key("item_found_rarity_+1%_per_X_rampage_stacks") is uint rarityPerStacks
            && stats.GetValueOrDefault(rarityPerStacks) is int perKills)
            Add(Stat.BaseItemFoundRarityPlusPct, RampageRarity(value.Stacks, perKills));
        // "X% increased Area of Effect per 25 Rampage Kills".
        if (Stat.AreaOfEffectPlusPctPer25RampageStacks is var areaPerStacks
            && stats.GetValueOrDefault(areaPerStacks) is int areaValue)
            Add(Stat.BaseSkillAreaOfEffectPlusPct, RampageRarity(value.Stacks, 25) * areaValue);
        if (Stat.CurrentRampageStacks is var current) stats[current] = value.Stacks;
        if (Stat.MaxRampageStacks is var maximum) stats[maximum] = RampageState.MaximumStacks;
    }

    public static int RampageMovement(Player player)
    {
        return player.Rampage.Current.MovementPercent;
    }

    /// <summary>"1% increased Rarity of Items found per X Rampage Kills": the streak's whole groups of X kills.</summary>
    public static int RampageRarity(int stacks, int perKills) =>
        perKills > 0 ? Math.Max(0, stacks) / perKills : 0;

    // The 32 ms world loop offers a presentation every step; without this check each empty offer took the send
    // gate (about 29 a second even when nothing had changed).
    static bool RampagePresentationPending(Player player)
    {
        return player.OwnedCombatState is RampageWorld && RampageUnsentLocked(player);
    }

    // The streak differs from the one the client was sent. Under conn.Monsters.
    static bool RampageUnsentLocked(Player player) =>
        CurrentRampageWork && !player.Closed && !player.LeavingArea && !player.RageRetired && player.Rampage.Current != player.SentRampage;

    /// <summary>What of a streak the full stat and skill snapshots depend on: they are sent again only when this
    /// changes. Damage and movement change every 20 stacks; rarity, area and Chain Hook's and Shield Charge's attack
    /// speed (every 10 stacks) only for a character that has the stat, at its own group size.</summary>
    public static (int Active, int Damage, int Movement, int Rarity, int Area, int AttackSpeed) RampageStatSignature(
        RampageState.Value value, int rarityPerKills, int areaPer25, int attackSpeedPer10) =>
        (value.Active ? 1 : 0, value.DamagePercent, value.MovementPercent, RampageRarity(value.Stacks, rarityPerKills),
            RampageRarity(value.Stacks, 25) * areaPer25, RampageRarity(value.Stacks, 10) * attackSpeedPer10);

    public static byte[]? RampagePresentation(Player player)
    {
        if (!RampageUnsentLocked(player)) return null;
        var stats = CharacterPlayerStats(player);
        var value = player.Rampage.Current;
        var bytes = new List<byte>();
        // The same buff instance takes the new count; replacing it every kill cost a remove and an add.
        if (value.Active)
            player.Area.Buffs.Set(player.Id, new(RampageCatalog.BuffDefinitionRow, player.Id,
                U68: Buff.Seconds(value.RemainingSeconds(ProjectileWorldTime(player))), Stacks: (ushort)value.Stacks, KeepInstance: true));
        else player.Area.Buffs.Clear(player.Id, RampageCatalog.BuffDefinitionRow, player.Id);
        player.RampageBuff = (ushort)(value.Active ? 1 : 0);
        // The full stat and skill snapshots are ~39 KB together, so they follow only the changes that reach them
        // (RampageStatSignature). current_rampage_stacks changes every kill and is one small stat update (the buff
        // row reads its count from it).
        int Stat(uint key) => stats.GetValueOrDefault(key);
        var signature = RampageStatSignature(value, Stat(global::Stat.ItemFoundRarityPlus1PctPerXRampageStacks),
            Stat(global::Stat.AreaOfEffectPlusPctPer25RampageStacks),
            Stat(global::Stat.ChainHookAndShieldChargeAttackSpeedPlusPctPer10RampageStacks));
        if (signature != player.SentRampageStat)
        {
            player.ApplyMovementSpeed();
            bytes.AddRange(PlayerStatsOffFrames(player));
            bytes.AddRange(PlayerSkillsIfChanged(player, PlayerSkills(player)));
            player.SentRampageStat = signature;
        }
        else if (global::Stat.CurrentRampageStacks is var current && value.Stacks != player.SentRampageStacks)
        {
            SetStats(player, player.Id, [(current, value.Stacks)]);
        }
        player.SentRampageStacks = value.Stacks;
        player.SentRampage = value;
        return bytes.Count == 0 ? null : [.. bytes];
    }

    static async Task RunRampage(Player player, RampageWorld runtime,
        GameServer.Modding.IModuleLifetime owner, GameServer.Modding.IModuleLifetime? source, long generation)
    {
        bool Current()
        {
            return CurrentGameRules && !player.Closed && !player.LeavingArea && !player.RageRetired
                && ReferenceEquals(AscendancyPolicies.SourceOwner(player), source)
                && ReferenceEquals(player.OwnedCombatState, runtime)
                && player.OwnedCombatWorker.Current(owner, source, runtime, generation);
        }
        Program.ProjectileOutputGuard.Value = Current;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(owner.Stopping,
            source?.Stopping ?? CancellationToken.None);
        try
        {
            while (Current())
            {
                await AdvanceRampageEffects(player);
                if (RampagePresentationPending(player))
                    await Send(player, [], null, atGate: () =>
                        Current() ? RampagePresentation(player) : null);
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
            Console.WriteLine($"[gateway] {player.Connection.Remote} {player.OwnedCombatWorker.Key} execution failed: {e}");
            player.Closed = true; runtime.Disable();
        }
        finally
        {
            Program.ProjectileOutputGuard.Value = null;
            player.OwnedCombatWorker.Complete(owner, source, runtime, generation);
        }
    }
}
