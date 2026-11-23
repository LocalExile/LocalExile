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

    /// <summary>Freeze the item decision as part of the lethal hit mutation, before hit notifications can yield.</summary>
    public static void CaptureUnhingedHitKillLocked(Player player, SpawnedMonster m, bool playerHit)
    {
        if (m.Life == 0)
            m.UnhingedHitKill = new(player, playerHit && UnhingedCorpseDestruction(player));
    }

    /// <summary>Consume the decision for this exact monster and owner. No current-state fallback is allowed.</summary>
    public static bool TakeUnhingedHitKillLocked(Player player, SpawnedMonster m)
    {
        var receipt = m.UnhingedHitKill;
        if (receipt is null || !ReferenceEquals(receipt.Owner, player)) return false;
        m.UnhingedHitKill = null;
        return receipt.DestroyCorpse;
    }

    public static Task PublishUnhingedCorpseDestruction(Player player, uint id, SpawnedMonster m, bool committed)
    {
        if (!committed) return Task.CompletedTask;
        if (!CurrentGameWork || player.Closed || player.LeavingArea || m.CorpseConsumed
            || !player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) return Task.CompletedTask;
        m.CorpseConsumed = true;
        // Item text: "Enemies Killed by your Hits are destroyed while Insane." The extraction supplies only the boolean,
        // so remove the accepted hit's corpse without inventing damage, another kill event, loot roll or experience award.
        // `committed` is the qualification captured at the lethal hit: later source/toggle changes cannot rewrite it.
        return SendWorldRemoval(player, id,
            $"0x02bb corpse of 0x{id:x} destroyed while unhinged", still: () =>
            {
                return CurrentGameWork && !player.Closed && !player.LeavingArea
                    && player.Area.Monsters.TryGetValue(id, out var current) && ReferenceEquals(current, m);
            });
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
