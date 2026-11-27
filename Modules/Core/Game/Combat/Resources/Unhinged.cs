using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public const string UnhingeEffect = "Unhinge";
    public const string UnhingeGrant = "GrantsUnhingeUnique__1";
    public const uint UnhingedCriticalFinal = Stat.CriticalStrikeChancePlusPctFinalWhileUnhinged;
    public const uint SaneDamageFinal = Stat.PhysicalAndChaosDamageTakenPlusPctFinalWhileNotUnhinged;
    public const uint SaneHitRecovery = Stat.RegeneratePctLifeOver1SecondWhenHitWhileNotUnhinged;
    public const uint UnhingedCorpseDestroy = Stat.EnemiesExplodeOnKillWhileUnhinged;
    public const ushort UnhingedBuffRow = 1723;

    /// <summary>
    /// Resolve the live equipped grant and reconcile source ownership. Unhinge itself starts sane; toggling changes only
    /// this connection-owned state. A changed owner, host/mod/GEPL identity, inactive item, mode refusal or death retires
    /// the toggle and queues its native buff for removal.
    /// </summary>
    static UnhingedState UnhingedAt(Player player, double now)
    {
        var state = player.Unhinged;
        var owner = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, owner))
        {
            RetireUnhinged(player);
            return state;
        }
        var equipment = ItemsOf(player).EquipmentSnapshot();
        var grant = AllWornGrantedSkillsOf(player, equipment)
            .Select(g => (WornGrantedSkills.Grant?)g)
            .FirstOrDefault(g => g is { } candidate
                && candidate.EffectId == UnhingeEffect && candidate.ModId == UnhingeGrant
                && candidate.EffectsPerLevelRow == 69315 && !candidate.Support
                && !IsInactive(player, candidate.Host)
                && LeagueFor(player).Rules.AllowsGemEffect(candidate.EffectId));
        if (grant is not { } matched)
        {
            RetireUnhinged(player);
            return state;
        }
        if (!ReferenceEquals(state.Owner, owner) || state.Source != matched)
        {
            if (state.Retire() is ushort old and not 0) player.RetiredUnhingedBuffs.Add(old);
            state.Install(owner, matched);
        }
        return state;
    }

    static void RetireUnhinged(Player player)
    {
        if (player.Unhinged.Retire() is ushort buff and not 0) player.RetiredUnhingedBuffs.Add(buff);
    }

    public static bool Unhinged(Player player)
    {
        return UnhingedAt(player, ResourceEffectsTime(player)).Active;
    }

    /// <summary>The exact raw sane Damage Taken final for one component. It has no effect on elemental damage.</summary>
    public static int UnhingedPhysicalChaosTakenPct(DamageType type, Func<uint, int> get) =>
        type is DamageType.Physical or DamageType.Chaos ? get(SaneDamageFinal) : 0;

    /// <summary>Apply the sane physical/chaos final once, after ordinary mitigation/increased taken.</summary>
    public static int ApplyUnhingedTaken(int damage, DamageType type, Func<uint, int> get) =>
        (int)Math.Clamp((long)Math.Max(0, damage) * Math.Max(0L, 100L + UnhingedPhysicalChaosTakenPct(type, get)) / 100,
            0, int.MaxValue);

    /// <summary>
    /// Accepted hit contact while sane starts or refreshes one non-stacking one-second regeneration contribution. This
    /// runs after block/avoidance/range admission, so a fully absorbed or blocked hit still recovers and a miss does not.
    /// </summary>
    public static void UnhingedHitRecoveryLocked(Player player, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (player.PlayerLife == 0) return;
        var state = UnhingedAt(player, now);
        if (!state.Exists || state.Enabled) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        int percent = Get(SaneHitRecovery);
        if (percent <= 0) return;
        double perSecond = player.PlayerMaxLife * (double)percent / 100
            * PanelStats.LifeRegenerationMultiplier(Get);
        if (perSecond > 0) state.HitRecovery.Begin(now, 1, perSecond);
    }

    public static bool UnhingedCorpseDestruction(Player player)
    {
        if (!Unhinged(player)) return false;
        var stats = CharacterPlayerStats(player);
        return UnhingedCorpseDestroy is var key && stats.GetValueOrDefault(key) != 0;
    }

    static bool HitKillDestroysCorpseLocked(Player player, SpawnedMonster m, bool directPlayerHit,
        IReadOnlyDictionary<uint, int>? playerStats)
    {
        if (!m.Hostile || m.Effects[Stat.CorpseCannotBeDestroyed] != 0 || IsSpectreCorpse(m)
            || (MapModsOf(player)?.Get(Stat.MapCorpseCannotBeDestroyed) ?? 0) != 0)
            return false;
        var stats = playerStats ?? PlayerStatValues(player);
        if (stats.GetValueOrDefault(Stat.EnemiesExplodeOnKill) != 0) return true;
        if (directPlayerHit && UnhingedCorpseDestruction(player)) return true;
        if (m.Ignites.IgnitedAt(ProjectileWorldTime(player)) || m.Ignites.DiedBurning)
        {
            if (stats.GetValueOrDefault(Stat.IgnitedEnemiesExplodeOnKill) != 0) return true;
            if (stats.GetValueOrDefault(Stat.BaseChanceToDestroyCorpseOnKillPctVsIgnited) is > 0 and var ignitedChance
                && RandomRolls.Chance(ignitedChance, 100, AreaRng(player)))
                return true;
        }
        if (stats.GetValueOrDefault(Stat.DestroyCorpsesOnKillPctChance) is > 0 and var destroyChance
            && RandomRolls.Chance(destroyChance, 100, AreaRng(player)))
            return true;
        return false;
    }

    /// <summary>Freeze the hit-kill corpse-destruction decision as part of the lethal hit mutation, before hit
    /// notifications can yield. Covers Axe Mastery / Kitava's Feast (<c>enemies_explode_on_kill</c>), Unhinged,
    /// Pyre / Cremator (<c>ignited_enemies_explode_on_kill</c>), and chance-based corpse destruction on hit kills.</summary>
    public static void CaptureUnhingedHitKillLocked(Player player, SpawnedMonster m, bool playerHit) =>
        CaptureHitCorpseDestructionLocked(player, m, playerHit, playerStats: null);

    internal static void CaptureHitCorpseDestructionLocked(Player player, SpawnedMonster m, bool directPlayerHit,
        IReadOnlyDictionary<uint, int>? playerStats)
    {
        if (m.Life == 0)
            m.UnhingedHitKill = new(player, HitKillDestroysCorpseLocked(player, m, directPlayerHit, playerStats));
    }

    /// <summary>Consume the decision for this exact monster and owner. No current-state fallback is allowed.</summary>
    public static bool TakeUnhingedHitKillLocked(Player player, SpawnedMonster m)
    {
        var receipt = m.UnhingedHitKill;
        if (receipt is null || !ReferenceEquals(receipt.Owner, player)) return false;
        m.UnhingedHitKill = null;
        return receipt.DestroyCorpse;
    }

    /// <summary>Marks a hit-killed monster's corpse destroyed immediately in the kill commit so death skills (such as
    /// Porcupine spines), corpse consumption, warcries, Raise Zombie, Detonate Dead, and re-entry snapshots never see
    /// the corpse. Returns true when the caller should send <c>0x02bd</c> removal (false if already consumed or
    /// shattered natively).</summary>
    internal static bool DestroyHitKillCorpseLocked(Player player, uint id, SpawnedMonster m)
    {
        if (m.CorpseConsumed) return false;
        m.CorpseConsumed = true;
        player.Area.Monsters.Remove(id);
        return !m.Ailments.Shattered;
    }

    public static Task PublishUnhingedCorpseDestruction(Player player, uint id, SpawnedMonster m, bool committed)
    {
        if (!committed) return Task.CompletedTask;
        if (!CurrentGameWork || player.Closed || player.LeavingArea || m.CorpseConsumed
            || !player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) return Task.CompletedTask;
        m.CorpseConsumed = true;
        player.Area.Monsters.Remove(id);
        if (m.Ailments.Shattered) return Task.CompletedTask;
        return SendWorldRemoval(player, id,
            $"0x02bd corpse of 0x{id:x} destroyed on hit kill", still: () =>
                CurrentGameWork && !player.Closed && !player.LeavingArea);
    }

    public static async Task<bool> ToggleUnhinged(Player player, ushort skillId, int effect, Func<bool> current)
    {
        bool toggled = false;
        await SendWorldRecord(player, player.Id, [], "0x01c1 player buff unhinged toggled", atGate: () =>
        {
            if (!current()) return null;
            var actor = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
            if (actor.Effect != effect || SkillEffectId(player, skillId) != UnhingeEffect) return null;
            double now = ResourceEffectsTime(player);
            var state = UnhingedAt(player, now);
            if (!state.Exists || state.Source is not { } source || IsInactive(player, source.Host)) return null;
            bool enabled = state.Toggle();
            toggled = true;
            if (enabled) player.Area.Buffs.Set(player.Id, new(UnhingedBuffRow, player.Id));
            else player.Area.Buffs.Clear(player.Id, UnhingedBuffRow, player.Id);
            return [];
        });
        return toggled;
    }

    public static Task SendUnhinged(Player player) =>
        SendWorldRecords(player, player.Id, () => UnhingedPresentationRecords(player));

    public static byte[]? UnhingedPresentation(Player player) =>
        UnhingedPresentationRecords(player) is { } records ? [.. records.SelectMany(p => p)] : null;

    static byte[][]? UnhingedPresentationRecords(Player player)
    {
        player.RetiredUnhingedBuffs.Clear();
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired) return null;
        var state = UnhingedAt(player, ResourceEffectsTime(player));
        if (state.Exists && state.Enabled) player.Area.Buffs.Keep(player.Id, new(UnhingedBuffRow, player.Id));
        else player.Area.Buffs.Clear(player.Id, UnhingedBuffRow, player.Id);
        return null;
    }
}
