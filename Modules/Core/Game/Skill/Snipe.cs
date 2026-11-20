using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// Snipe (effect <c>Snipe</c>, ActiveSkill <c>channelled_snipe</c>, support half <c>ChannelledSnipeSupport</c>;
/// also granted at level 30 by Assailum via <c>GrantsHighLevelSnipeUnique__1</c> and
/// <c>GrantsHighLevelSnipeSupportUnique__1</c>): channelling charges bow stages up to <c>snipe_max_stacks</c>
/// (6 base + gem quality + Assailum's +2 + helm enchant's +1), displayed by BuffDefinitions 1494 (<c>snipe_stacks</c>,
/// ShowCount, BuffVisuals 1285 <c>snipe_stages</c>). On release, triggers linked eligible bow attack skills (up to
/// one per stage gained, in socket order) with <c>snipe_triggered_skill_damage_+%_final</c> (-40%) and per-stage
/// <c>snipe_triggered_skill_hit_damage_+%_final_per_stage</c> and
/// <c>snipe_triggered_skill_ailment_damage_+%_final_per_stage</c>; when no bow skill is linked, fires Snipe's own
/// default arrow (<c>Metadata/Projectiles/ChannelledSnipeDefaultProjectile</c>) with those per-stage multipliers.
/// </summary>
public static partial class Game
{
    public const string SnipeEffect = "Snipe";
    public const string SnipeSupportEffect = "ChannelledSnipeSupport";

    /// <summary>BuffDefinitions 1494 <c>snipe_stacks</c> (ShowCount, BuffVisuals 1285 <c>snipe_stages</c>).</summary>
    public const ushort SnipeStagesBuffRow = 1494;

    internal sealed class SnipeRuntime
    {
        public ushort SkillId;
        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem;
        public SupportedGem Source = null!;
        public int Action;
        public int ChannelGeneration;
        public int Epoch;
        public object? Rules;
        public int Stages;
        public bool Channelling;
        public (int X, int Y) Aim;
        public ushort PresentedBuff;
        public int PresentedStages = -1;
        public CancellationTokenSource? WaitCancel;
    }

    static readonly RuleTable<ClientState, SnipeRuntime> SnipeRuntimes = new();

    internal static bool IsSnipeEffect(string? effectId) => effectId == SnipeEffect;

    internal static bool IsSnipe(int effect) => IsSnipeEffect(Skills.EffectId(effect));

    internal static bool IsSnipeSupportEffect(string? effectId) => effectId == SnipeSupportEffect;

    /// <summary>Whether this connection is currently channelling Snipe on <paramref name="skillId"/>.</summary>
    internal static bool IsSnipeChannelling(ClientState conn, ushort skillId) =>
        SnipeRuntimes.TryGetValue(conn, out var state) && state.Channelling && state.SkillId == skillId
        && state.Epoch == conn.ProjectileEpoch && ReferenceEquals(state.Rules, CombatLifetime);

    /// <summary>Whether a linked bow attack supported by <c>ChannelledSnipeSupport</c> has a server executor.</summary>
    internal static bool SnipeTriggeredAttackExecutable(ClientState conn, ushort skillId, SupportedGem gem)
    {
        if (GolemStat(gem.Stats, "skill_triggered_by_snipe") == 0) return false;
        var catalog = SkillsFor(conn);
        if (!catalog.IsAttack(gem.Effect) || catalog.IsChannelled(gem.Effect)
            || PlacedKind(gem) is not (false, false) || GolemStat(gem.Stats, "is_totem") != 0
            || catalog.VaalBudget(gem.Effect, gem.Level) is not null)
            return false;
        string? effectId = catalog.EffectId(gem.Effect);
        if (ArrowRain(effectId)) return true;
        return UsesProjectileRuntime(gem.Effect) && ResolveProjectileDefinition(gem.Effect) is not null
            && SkillHitPlan.ForSkill(catalog, gem.Effect, gem.Level, gem.Quality).CanHit;
    }

    /// <summary>Maximum Snipe stages from the gem/Assailum (<c>snipe_max_stacks</c> base 6, +quality, +2 Assailum, +1 enchant).</summary>
    public static int SnipeMaxStages(ClientState conn, ushort skillId)
    {
        var stats = SupportedStats(conn, skillId);
        int max = Stats.Key("snipe_max_stacks") is uint key ? stats.GetValueOrDefault(key) : 0;
        return Math.Clamp(max > 0 ? max : 6, 1, 32);
    }

    /// <summary>Per-stage hit and ailment damage multipliers for Snipe at <paramref name="stages"/>.</summary>
    public static (double HitFactor, double AilmentFactor) SnipeStageMultipliers(IReadOnlyDictionary<uint, int> snipeStats,
        int stages, IReadOnlyDictionary<uint, int>? linkedStats = null)
    {
        int clamped = Math.Max(0, stages);
        int hitPerStage = GolemStat(snipeStats, "snipe_triggered_skill_hit_damage_+%_final_per_stage");
        int ailmentPerStage = GolemStat(snipeStats, "snipe_triggered_skill_ailment_damage_+%_final_per_stage");
        double baseLess = linkedStats is not null
            ? Math.Max(0, 1 + GolemStat(linkedStats, "snipe_triggered_skill_damage_+%_final") / 100.0) : 1;
        return (baseLess * Math.Max(0, 1 + (double)clamped * hitPerStage / 100.0),
            baseLess * Math.Max(0, 1 + (double)clamped * ailmentPerStage / 100.0));
    }

    /// <summary>Wakes a sleeping Snipe channel wait immediately when the player releases the button.</summary>
    internal static void SnipeWakeChannel(ClientState conn)
    {
        CancellationTokenSource? cts = null;
        lock (conn.Monsters)
        {
            if (SnipeRuntimes.TryGetValue(conn, out var state))
            {
                cts = state.WaitCancel;
                state.WaitCancel = null;
            }
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Updates the release aim while channelling Snipe (C→S 0x197 / 0x198 / lockstep 0x192).</summary>
    internal static void SnipeAimedAt(ClientState conn, (int X, int Y) aim)
    {
        lock (conn.Monsters)
            if (SnipeRuntimes.TryGetValue(conn, out var state) && state.Channelling
                && state.Epoch == conn.ProjectileEpoch && ReferenceEquals(state.Rules, CombatLifetime))
                state.Aim = aim;
    }

    /// <summary>Token that cancels immediately when the player releases Snipe so release fires without delay.</summary>
    internal static CancellationToken SnipeWaitToken(ClientState conn, int generation, int action)
    {
        lock (conn.Monsters)
        {
            if (!SnipeRuntimes.TryGetValue(conn, out var state) || !state.Channelling
                || state.ChannelGeneration != generation || state.Action != action)
                return CancellationToken.None;
            state.WaitCancel?.Dispose();
            state.WaitCancel = new CancellationTokenSource();
            return state.WaitCancel.Token;
        }
    }

    internal static async Task SnipeStart(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, int action, (int X, int Y) aim)
    {
        ushort staleBuff = 0;
        var source = SupportedSkill(conn, skillId);
        int max = SnipeMaxStages(conn, skillId);
        lock (conn.Monsters)
        {
            if (SnipeRuntimes.TryGetValue(conn, out var old))
            {
                staleBuff = old.PresentedBuff;
                old.PresentedBuff = 0;
                old.Channelling = false;
                try { old.WaitCancel?.Cancel(); old.WaitCancel?.Dispose(); } catch (ObjectDisposedException) { }
                old.WaitCancel = null;
            }
            var runtime = new SnipeRuntime
            {
                SkillId = skillId,
                Gem = gem,
                Source = source,
                Action = action,
                ChannelGeneration = conn.ChannelGeneration,
                Epoch = conn.ProjectileEpoch,
                Rules = CombatLifetime,
                Stages = Math.Clamp(1, 1, max),
                Channelling = true,
                Aim = aim,
                PresentedBuff = staleBuff,
                PresentedStages = -1,
            };
            SnipeRuntimes.AddOrUpdate(conn, runtime);
        }
        await SnipePresentStages(remote, stream, ciphers, conn);
    }

    /// <summary>Advances Snipe by one stage on each paid channel tick, capped at <see cref="SnipeMaxStages"/>.</summary>
    internal static int SnipeStageStep(ClientState conn, int generation, int action)
    {
        int max;
        ushort skillId;
        lock (conn.Monsters)
        {
            if (!SnipeRuntimes.TryGetValue(conn, out var runtime) || !runtime.Channelling
                || runtime.ChannelGeneration != generation || runtime.Action != action)
                return 0;
            skillId = runtime.SkillId;
        }
        max = SnipeMaxStages(conn, skillId);
        lock (conn.Monsters)
        {
            if (!SnipeRuntimes.TryGetValue(conn, out var runtime) || !runtime.Channelling
                || runtime.ChannelGeneration != generation || runtime.Action != action)
                return 0;
            if (runtime.Stages < max) runtime.Stages++;
            return runtime.Stages;
        }
    }

    internal static async Task SnipePresentStages(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn)
    {
        byte[] packet;
        int stages;
        lock (conn.Monsters)
        {
            if (!SnipeRuntimes.TryGetValue(conn, out var runtime) || !runtime.Channelling
                || runtime.Stages <= 0 || runtime.PresentedStages == runtime.Stages) return;
            stages = runtime.Stages;
            ushort instance = runtime.PresentedBuff;
            if (instance == 0) instance = runtime.PresentedBuff = conn.NextBuff++;
            packet = InstanceEntities.AddBuff(PlayerId, instance, SnipeStagesBuffRow, PlayerId, runtime.SkillId,
                stacks: (ushort)stages);
            runtime.PresentedStages = stages;
        }
        await Send(remote, stream, ciphers, packet, $"snipe {stages} stages");
    }

    /// <summary>
    /// Death, area change or rules retirement: drops the Snipe state without firing and queues its stage buff
    /// for removal on respawn/reload.
    /// </summary>
    internal static void SnipeRetire(ClientState conn)
    {
        CancellationTokenSource? cts = null;
        lock (conn.Monsters)
        {
            if (!SnipeRuntimes.TryGetValue(conn, out var runtime)) return;
            if (runtime.PresentedBuff != 0) conn.RetiredCombatBuffs.Add((PlayerId, runtime.PresentedBuff));
            runtime.PresentedBuff = 0;
            runtime.PresentedStages = -1;
            runtime.Channelling = false;
            cts = runtime.WaitCancel;
            runtime.WaitCancel = null;
            SnipeRuntimes.Remove(conn);
        }
        try { cts?.Cancel(); cts?.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Finds eligible bow attack skills supported by this Snipe source (same item and link group for a socketed
    /// Snipe gem; any socket in the granting item for Assailum's worn-granted Snipe), ordered by socket.
    /// </summary>
    public static List<(ushort Id, SupportedGem Gem, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Effect)>
        SnipeLinkedBowSkills(ClientState conn, SupportedGem snipe)
    {
        var result = new List<(ushort, SupportedGem, (ushort, int, string, int, int, uint))>();
        uint host = snipe.WornGrant is { } grant ? grant.Host : snipe.Host;
        if (host == 0) return result;
        var effects = GemSkillEffects(conn).ToDictionary(g => g.Id);
        foreach (var source in PlayerSkillSourcesLocked(conn)
            .Where(s => s.Active && s.SourceGem is not null && s.Identity.Host == host)
            .OrderBy(s => s.Identity.Socket))
        {
            if (source.Effect == snipe.Effect) continue;
            var linked = SupportedSkill(conn, source.Id);
            if (snipe.WornGrant is null && (snipe.Group < 0 || linked.Group != snipe.Group)) continue;
            if (!SnipeTriggeredAttackExecutable(conn, source.Id, linked)) continue;
            if (!linked.Supports.Any(s => IsSnipeSupportEffect(s.Effect.Id))) continue;
            if (GemSkillInstanceDisabled(conn, linked.Effect, source.Id, triggered: true) is not null) continue;
            if (effects.TryGetValue(source.Id, out var gemEffect))
                result.Add((source.Id, linked, gemEffect));
        }
        return result;
    }

    /// <summary>
    /// Called when Snipe's channel ends. Removes the stage buff and, if released normally while alive with a bow
    /// equipped, fires linked bow skills (up to one per stage) or Snipe's own default arrow with per-stage damage
    /// and ailment multipliers.
    /// </summary>
    internal static async Task SnipeRelease(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, int generation, int action)
    {
        SnipeRuntime? runtime = null;
        ushort buff = 0;
        bool shouldFire = false;
        CancellationTokenSource? cts = null;
        lock (conn.Monsters)
        {
            if (!SnipeRuntimes.TryGetValue(conn, out var current)
                || current.ChannelGeneration != generation || current.Action != action) return;
            runtime = current;
            buff = current.PresentedBuff;
            current.PresentedBuff = 0;
            current.PresentedStages = -1;
            current.Channelling = false;
            cts = current.WaitCancel;
            current.WaitCancel = null;
            SnipeRuntimes.Remove(conn);
            shouldFire = CurrentGameRules && ReferenceEquals(current.Rules, CombatLifetime)
                && current.Epoch == conn.ProjectileEpoch && !conn.Closed && !conn.LeavingArea
                && !conn.RageRetired && conn.PlayerLife > 0 && !InTown(conn) && current.Stages > 0;
        }
        try { cts?.Dispose(); } catch (ObjectDisposedException) { }
        if (buff != 0 && !conn.Closed && !conn.LeavingArea)
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, buff), "snipe stages cleared");
        if (!shouldFire || runtime is null) return;
        await ReleaseSnipeStages(remote, stream, ciphers, conn, runtime.SkillId, runtime.Gem, runtime.Stages, runtime.Aim);
    }

    /// <summary>
    /// Executes Snipe's release payload for <paramref name="stages"/> aimed at <paramref name="aim"/>. Exposed
    /// directly so tests and live releases share one deterministic code path.
    /// </summary>
    public static async Task<int> ReleaseSnipeStages(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) snipeGem, int stages, (int X, int Y) aim)
    {
        if (stages <= 0 || !WeaponProfileFor(conn, activeSet: true).Bow) return 0;
        var snipeSupported = SupportedSkill(conn, skillId);
        if (snipeSupported.Effect != snipeGem.Effect) return 0;
        List<(ushort Id, SupportedGem Gem, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Effect)> linked;
        lock (conn.Monsters)
        {
            if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return 0;
            linked = SnipeLinkedBowSkills(conn, snipeSupported);
        }

        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        int fired = 0;

        if (linked.Count > 0)
        {
            int toTrigger = Math.Min(stages, linked.Count);
            uint totalLifeCost = 0;
            bool paidAnyCost = false;
            using (ProjectileMutation(conn))
            {
                if (!session.Active || conn.PlayerLife == 0) return 0;
                double now = ProjectileWorldTime(conn);
                var triggers = LinkedTriggersOf(conn);
                for (int i = 0; i < toTrigger; i++)
                {
                    var (linkedId, linkedGem, linkedEffect) = linked[i];
                    if (now < triggers.ReadyAt.GetValueOrDefault(linkedGem.Effect)) continue;
                    if (GemSkillInstanceDisabled(conn, linkedGem.Effect, linkedId, triggered: true) is not null) continue;
                    if (!PayMana(conn, linkedId, out uint mana, out uint life)) continue;
                    if (mana > 0 || life > 0) { paidAnyCost = true; totalLifeCost += life; }

                    // ChannelledSnipeSupport's native 50 ms cooldown prevents the same skill firing twice in one release.
                    var support = linkedGem.Supports.FirstOrDefault(s => IsSnipeSupportEffect(s.Effect.Id));
                    int cooldownMs = Math.Max(SkillsFor(conn).CooldownMs(linkedGem.Effect, linkedGem.Level),
                        support is null ? 50 : SkillsFor(conn).CooldownMs(support.Gem.Effect, support.Gem.Level));
                    triggers.ReadyAt[linkedGem.Effect] = now + cooldownMs / 1000d;

                    var (hitFactor, ailmentFactor) = SnipeStageMultipliers(snipeSupported.Stats, stages, linkedGem.Stats);
                    var critical = CaptureSkillCriticalRoll(conn, linkedId);
                    string? effectId = SkillsFor(conn).EffectId(linkedGem.Effect);
                    if (ArrowRain(effectId))
                    {
                        double podFactor = Math.Max(0, 1 + GolemStat(linkedGem.Stats, "snipe_triggered_skill_damage_+%_final") / 100.0);
                        ReleaseArrowRain(session, linkedId, linkedEffect, aim, critical, 0,
                            transform: shot => ScaleSnipeOffense(shot, hitFactor, ailmentFactor), podDamageFactor: podFactor);
                        fired++;
                    }
                    else if (ResolveProjectileDefinition(linkedGem.Effect) is not null)
                    {
                        var use = CaptureGemProjectile(conn, session, linkedId, linkedEffect, aim, critical);
                        var scaled = ScaleSnipeOffense((ProjectileOffense)use.Payload!, hitFactor, ailmentFactor);
                        session.ReleaseNow(use with { Payload = scaled });
                        fired++;
                    }
                }
            }
            if (paidAnyCost)
                await SendSkillCost(remote, stream, ciphers, conn, totalLifeCost, $"snipe release cost ({fired} skill(s))");
            Console.WriteLine($"[gateway] {remote} snipe released at {stages} stage(s): triggered {fired}/{toTrigger} linked bow skill(s) toward ({aim.X}, {aim.Y})");
            return fired;
        }

        // Unlinked Snipe (or Assailum with no socketed bow skill): fire Snipe's own default arrow with per-stage multipliers.
        using (ProjectileMutation(conn))
        {
            if (!session.Active || conn.PlayerLife == 0 || ResolveProjectileDefinition(snipeGem.Effect) is null) return 0;
            var (hitFactor, ailmentFactor) = SnipeStageMultipliers(snipeSupported.Stats, stages);
            var critical = CaptureSkillCriticalRoll(conn, skillId);
            var use = CaptureGemProjectile(conn, session, skillId, snipeGem, aim, critical);
            var scaled = ScaleSnipeOffense((ProjectileOffense)use.Payload!, hitFactor, ailmentFactor);
            session.ReleaseNow(use with { Payload = scaled });
            fired = 1;
        }
        Console.WriteLine($"[gateway] {remote} snipe released at {stages} stage(s): fired default Snipe arrow toward ({aim.X}, {aim.Y})");
        return fired;
    }

    static ProjectileOffense ScaleSnipeOffense(ProjectileOffense shot, double hitFactor, double ailmentFactor) => shot with
    {
        ClientPredicted = false,
        Hit = shot.Hit.Scale(hitFactor),
        AreaHit = shot.AreaHit?.Scale(hitFactor),
        HitProfileMore = shot.HitProfileMore * hitFactor,
        AilmentProfileMore = shot.AilmentProfileMore * ailmentFactor,
        Ignite = ScaleSnipeIgnite(shot.Ignite, ailmentFactor),
        Bleed = ScaleSnipeIgnite(shot.Bleed, ailmentFactor),
        Poison = ScaleSnipePoison(shot.Poison, ailmentFactor),
    };

    static MonsterIgnites.Settings ScaleSnipeIgnite(MonsterIgnites.Settings value, double factor) => value with
    {
        Min = value.Min * factor,
        Max = value.Max * factor,
        CriticalMin = value.CriticalMin * factor,
        CriticalMax = value.CriticalMax * factor,
    };

    static MonsterPoisons.Settings ScaleSnipePoison(MonsterPoisons.Settings value, double factor) => value with
    {
        Min = value.Min * factor,
        Max = value.Max * factor,
        CriticalMin = value.CriticalMin * factor,
        CriticalMax = value.CriticalMax * factor,
    };
}
