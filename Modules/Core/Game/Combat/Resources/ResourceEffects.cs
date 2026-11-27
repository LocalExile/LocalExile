using System.Collections.Concurrent;
using GameServer.Core.World;
using GameServer.Modding;

/// <summary>Rule-owned resources, separate from the animated-actor runtime and its worker.</summary>
public sealed class ResourceEffectsState(IModuleLifetime? rules, IModuleLifetime? source, Player? player = null) : ITransferCombatState
{
    public IModuleLifetime? Rules { get; } = rules;
    public IModuleLifetime? Source { get; } = source;
    public long Generation;
    public readonly ManaRecoupState Mana = new();
    public readonly TimedBlind PlayerBlind = new();
    internal FlameTouchedState? FlameTouched;
    internal double MinionLifeLossCarry, MinionShieldLossCarry;
    public readonly Dictionary<uint, (SpawnedMonster Target, TimedBlind Blind)> Targets = [];
    readonly HashSet<(uint Actor, ushort Buff)> retired = [];
    public sealed record Snapshot(ManaRecoupState.Carried Mana, (double Seconds, int Less)[] Blind,
        PhysicalAegisState.Carried? Aegis, Game.CarriedSiphoning? Siphoning, Game.CarriedAvatar? Avatar = null,
        Game.CarriedHerEmbrace? HerEmbrace = null, Game.CarriedElusive? Elusive = null,
        Game.CarriedChargeDegeneration? ChargeDegeneration = null,
        Game.CarriedRecentHitLifeLoss? RecentHitLifeLoss = null, ReservationState.PausedCatCycle? Cat = null);
    public Snapshot? TransferCheckpoint;
    public bool Loading;
    public IReadOnlyCollection<(uint Actor, ushort Buff)> RetiredBuffs => retired;

    public TimedBlind TargetBlind(uint id, SpawnedMonster target)
    {
        if (Targets.TryGetValue(id, out var old) && ReferenceEquals(old.Target, target)) return old.Blind;
        if (old.Blind is { Buff: > 0 } blind) retired.Add((id, blind.Buff));
        var value = new TimedBlind(); Targets[id] = (target, value); return value;
    }
    public Snapshot Capture(double at) => new(Mana.Capture(at), PlayerBlind.Capture(at),
        player?.PhysicalAegis.Capture(at), player is null ? null : Game.CaptureSiphoning(player, at),
        player is null ? null : Game.CaptureAvatarOfFire(player, at),
        player is null ? null : Game.CaptureHerEmbrace(player, at),
        player is null ? null : Game.CaptureElusive(player, at),
        player is null ? null : Game.CaptureChargeDegeneration(player, at),
        player is null ? null : Game.CaptureRecentHitLifeLoss(player, at),
        player is null ? null : Game.CaptureCatCycle(player, at));
    public void CollectRetiredTargets(Player player)
    {
        foreach (var (id, value) in Targets.ToArray())
            if (!ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), value.Target))
            {
                if (value.Blind.Buff != 0) retired.Add((id, value.Blind.Buff));
                Targets.Remove(id);
            }
        player.RetiredResourceBuffs.UnionWith(retired); retired.Clear();
    }
    public void Freeze(double at)
    {
        TransferCheckpoint = Capture(at);
        if (player is not null) Game.PauseRecentHitLifeLoss(player, at);
    }
    public void Resume(double at)
    {
        // A refused transfer resumes the live draft, discarding loading earnings rather than paying catch-up.
        if (player is not null && TransferCheckpoint?.Siphoning is { } siphoning)
            Game.RestoreSiphoning(player, siphoning, at);
        if (player is not null && TransferCheckpoint?.Avatar is { } avatar)
            Game.RestoreAvatarOfFire(player, avatar, at);
        if (player is not null && TransferCheckpoint?.HerEmbrace is { } embrace)
            Game.RestoreHerEmbrace(player, embrace, at);
        if (player is not null && TransferCheckpoint?.Elusive is { } elusive)
            Game.RestoreElusive(player, elusive, at);
        if (player is not null && TransferCheckpoint?.ChargeDegeneration is { } degeneration)
            Game.RestoreChargeDegeneration(player, degeneration, at);
        if (player is not null) Game.RestoreRecentHitLifeLoss(player, TransferCheckpoint?.RecentHitLifeLoss, at);
        if (player is not null) Game.RestoreCatCycle(player, TransferCheckpoint?.Cat);
        Mana.Take(at); Mana.DiscardRecoveryFraction(); PlayerBlind.Read(at); TransferCheckpoint = null;
    }
    public void Restore(Snapshot value, double at)
    {
        Mana.Restore(value.Mana, at); PlayerBlind.Restore(value.Blind, at);
        if (player is not null && value.Aegis is { } aegis) player.PhysicalAegis.Restore(aegis, this, at);
        if (player is not null && value.Siphoning is { } siphoning) Game.RestoreSiphoning(player, siphoning, at);
        if (player is not null && value.Avatar is { } avatar) Game.RestoreAvatarOfFire(player, avatar, at);
        if (player is not null && value.HerEmbrace is { } embrace) Game.RestoreHerEmbrace(player, embrace, at);
        if (player is not null && value.Elusive is { } elusive) Game.RestoreElusive(player, elusive, at);
        if (player is not null && value.ChargeDegeneration is { } degeneration) Game.RestoreChargeDegeneration(player, degeneration, at);
        if (player is not null) Game.RestoreRecentHitLifeLoss(player, value.RecentHitLifeLoss, at);
        if (player is not null) Game.RestoreCatCycle(player, value.Cat);
    }
    public void Clear()
    {
        MinionLifeLossCarry = MinionShieldLossCarry = 0;
        if (player is not null)
        {
            Game.RetireAvatarOfFire(player);
            Game.RetireHerEmbrace(player);
            Game.RetireFlameTouched(player, this);
            Game.RetireRecentHitLifeLoss(player);
            Game.RetireElusive(player);
            if (Game.ChargeSessions.TryGetValue(player, out var charges)) charges.Degeneration.Clear(Game.ResourceEffectsTime(player));
            player.RetiredPhysicalAegisBuffs.UnionWith(player.PhysicalAegis.Retire());
            if (player.Unhinged.Retire() is ushort unhingedBuff and not 0)
                player.RetiredUnhingedBuffs.Add(unhingedBuff);
        }
        if (PlayerBlind.Buff != 0 && player is not null) retired.Add((player.Id, PlayerBlind.Buff));
        foreach (var (id, value) in Targets)
            if (value.Blind.Buff != 0) retired.Add((id, value.Blind.Buff));
        // Accepted flask records contain only Gateway data. Game retirement hands them to the next writer;
        // explicit/source/death/connection retirement keeps the existing copied-effect cleanup policy.
        if (player is not null && (Rules is not { IsActive: false } || Source is { IsActive: false }
            || player.Closed || player.RageRetired))
            foreach (var effect in player.FlaskEffects.End(e => e.CopyManaToLife))
                if (effect.Buff != 0) retired.Add((player.Id, effect.Buff));
        Mana.Clear(); PlayerBlind.Clear(); Targets.Clear(); TransferCheckpoint = null;
    }
}

public static partial class Game
{
    static readonly ResourceEffectsState RetiredResourceEffects = new(null, null);
    public static ResourceEffectsState ResourceEffectsOf(Player player)
    {
        if (!CurrentGameRules || player.Closed || player.RageRetired || player.PlayerLife == 0) return RetiredResourceEffects;
        var owner = CombatLifetime!;
        var source = AscendancyPolicies.SourceOwner(player);
        if (player.ResourceEffectsState is ResourceEffectsState state && ReferenceEquals(state.Rules, owner)
            && ReferenceEquals(state.Source, source) && player.ResourceEffectsLease.Current(owner, source, state, state.Generation))
            return state;
        player.RetireResourceEffects();
        state = new(owner, source, player); player.ResourceEffectsState = state;
        if (player.ResourceEffectsLease.Bind(player, owner, source, state, out long generation))
        { state.Generation = generation; return state; }
        player.RetireResourceEffects(); return RetiredResourceEffects;
    }
    public static bool ResourceEffectsCurrent(Player player, ResourceEffectsState state) => CurrentGameRules
        && !player.Closed && !player.RageRetired && ReferenceEquals(player.ResourceEffectsState, state)
        && ReferenceEquals(state.Source, AscendancyPolicies.SourceOwner(player)) && state.Rules is { IsActive: true } owner
        && player.ResourceEffectsLease.Current(owner, state.Source, state, state.Generation);

    public static double ResourceEffectsTime(Player player)
    {
        if (player.ResourceEffectsState is ResourceEffectsState { Loading: true }) return 0;
        return player.LeavingArea && player.RampageTransferAt is double at ? at
            : player.ProjectileRuntimeState is { Active: true } runtime ? runtime.NowTicks / 120000d : AreaTime(player);
    }
    public static void ManaSpentLocked(Player player, uint amount, IReadOnlyDictionary<uint, int> playerStats)
    {
        if (amount == 0 || player.LeavingArea || player.PlayerLife == 0 || !CurrentGameRules) return;
        player.RecentEvents.RecordAmount(StatConditions.SpentMana, ResourceEffectsTime(player), amount);
        SigilManaSpent(player, amount, AreaTime(player));
        var state = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, state)) return;
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        long gain = state.Mana.Spent(amount, Get(ManaRecoveryRules.RagePer200));
        if (gain > 0) player.Rage.Gain(gain, ResourceEffectsTime(player), RageStats.Inputs(player, Get), Get(Stat.HasSoulGainPrevention) != 0);
        var (frenzy, power) = state.Mana.SpentForCharges(amount, Get(Stat.GainXFrenzyChargesAfterSpending200Mana),
            Get(Stat.LocalDisplayGainPowerChargeOnSpendingMana) > 0 ? 1 : 0);
        if (frenzy > 0) ChargesOf(player).Enqueue(new ChargeEvent.Grant(ChargeType.Frenzy, frenzy, "mana spent"));
        if (power > 0) ChargesOf(player).Enqueue(new ChargeEvent.Grant(ChargeType.Power, power, "mana spent"));
    }
    public static void SettleFullManaLocked(Player player, double at)
    {
        if (player.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(player, state)
            && player.PlayerMana >= UnreservedMana(player))
        { state.Mana.Take(at); state.Mana.DiscardRecoveryFraction(); }
    }
    static void RecordUsedSkillLocked(Player player, double now, int effect = -1, bool activated = true)
    {
        if (MapSkillActionSpeedPerUse(player) != 0)
        {
            player.RecentEvents.RecordAmount(StatConditions.UsedSkill, now);
            if (!player.Connection.LockstepClient || !player.Frames.Running)
            {
                player.ApplyMovementSpeedAt(now);
                if (player.Frames is { Running: true, Area: { } area })
                    AfterSeconds(player, RecentEvents.WindowSeconds, () =>
                    {
                        if (!player.LeavingArea && ReferenceEquals(player.Frames.Area, area))
                            player.ApplyMovementSpeedAt(AreaTime(player));
                    });
            }
        }
        else player.RecentEvents.Record(StatConditions.UsedSkill, now);
        var raw = StatSourceValues(player);
        if (activated && effect >= 0) BlindOnElementalSkillUseLocked(player, effect, now, raw);
        ApplySiphoningSkillUseLocked(player, now, raw);
        long loss = activated ? Math.Max(0, raw.GetValueOrDefault(Stat.LoseXManaWhenYouUseSkill)) : 0;
        if (activated && raw.GetValueOrDefault(Stat.LosePctOfManaWhenYouUseAnAttackSkill) is > 0 and var percent
            && (effect < 0 || SkillsFor(player).IsAttack(effect)))
            loss += (long)player.Pools.Mana * Math.Min(percent, 100) / 100;
        if (loss > 0 && player.PlayerMana != 0)
        {
            uint before = player.PlayerMana, maximum = player.Pools.Mana;
            uint after = before - (uint)Math.Min(before, loss);
            player.PlayerMana = after;
            if (maximum > 0 && (before >= maximum && after < maximum
                || (long)before * 2 > maximum && (long)after * 2 <= maximum))
            {
                EnsureConditionalItemPools(player);
                player.AuraStatsDirty = true;
            }
            player.Frames.Area?.Life.Resend(player);
        }
        int chance = raw.GetValueOrDefault(Stat.Recover10PctOfMaximumManaOnSkillUsePct);
        if (chance <= 0) return;

        uint cap = (uint)UnreservedMana(player);
        if (player.PlayerMana < cap && RandomRolls.Chance(chance, 100, AreaRng(player)))
            player.PlayerMana = (uint)Math.Min(cap, (ulong)player.PlayerMana + (ulong)player.Pools.Mana / 10);
    }

    public static void DiscardResourceLoadingRecovery(Player player, double now)
    {
        if (player.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(player, state))
        { state.Mana.Take(now); state.Mana.DiscardRecoveryFraction(); }
    }
    public static bool CompleteResourceRecoveryLocked(Player player, double from, double now,
        IReadOnlyDictionary<uint, int> stats, double earnedMana, uint manaBefore, uint manaCap, ref double manaCarry)
    {
        if (player.WitchbaneImmunityUntil is > 0 and var expiry && now >= expiry)
        {
            player.WitchbaneImmunityUntil = 0;
            if (HasMapHexes(MapModsOf(player))) RecomputePoolsAndSpeed(player);
            MarkPlayerStats(player, PlayerStatStep.SelfVulnerability);
        }
        AdvanceWardLocked(player, now);
        AdvancePlayerAilmentsLocked(player, from, now, stats);
        if (player.PlayerLife > 0) AdvanceSiphoningDamageLocked(player, now, stats);
        if (player.PlayerLife > 0) AdvanceHerEmbraceDamageLocked(player, now, stats);
        if (player.PlayerLife > 0) AdvanceFlameTouchedDamage(player, now, stats);
        if (player.PlayerLife > 0) AdvanceUtilityFlaskChaosDamageLocked(player, now, stats);
        if (player.PlayerLife > 0) AdvanceChargeDegenerationLocked(player, now, stats);
        if (player.PlayerLife > 0) AdvanceRecentHitLifeLossLocked(player, now);
        if (player.PlayerLife > 0) AdvanceMinionPoolLossLocked(player, from, now, stats);
        if (player.PlayerLife == 0)
        {
            manaCarry = 0; LeechOf(player).Clear(now); FortificationOf(player).Clear();
            player.EnergyShield.Empty(); ClearWardBreakOnDeath(player); return false;
        }
        var state = ResourceEffectsOf(player);
        double recoup = ResourceEffectsCurrent(player, state) ? state.Mana.Take(now) : 0;
        // Advance from live PlayerMana so Chaos DoT Mana-before-Life drain applied above is preserved.
        player.PlayerMana = RecoveryPool.Advance(player.PlayerMana, manaCap, earnedMana + recoup, ref manaCarry);
        if (player.PlayerMana >= manaCap && ResourceEffectsCurrent(player, state)) state.Mana.DiscardRecoveryFraction();
        return true;
    }

    static void AdvanceMinionPoolLossLocked(Player player, double from, double now, IReadOnlyDictionary<uint, int> stats)
    {
        int shieldRate = Math.Max(0, stats.GetValueOrDefault(Stat.LifeAndEnergyShieldDegenerationPermyriadPerMinutePerMinion));
        long lifeRate = shieldRate + (long)Math.Max(0, stats.GetValueOrDefault(Stat.LifeDegenerationPermyriadPerMinutePerMinion));
        if (lifeRate == 0)
        {
            if (player.ResourceEffectsState is ResourceEffectsState inactive)
                inactive.MinionLifeLossCarry = inactive.MinionShieldLossCarry = 0;
            return;
        }
        if (now <= from || InTown(player) || player.Immune || stats.GetValueOrDefault(Stat.BaseCannotBeDamaged) > 0
            || HardcoreProgressBlocked(player) || InGracePeriod(player)) return;
        var state = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, state)) return;
        int count = player.Minions.All.Count(minion => minion.Until > now && !minion.Monster.Dead && minion.Monster.Life > 0
            && ReferenceEquals(player.Area.Monsters.GetValueOrDefault(minion.Id), minion.Monster));
        if (count == 0)
        {
            state.MinionLifeLossCarry = state.MinionShieldLossCarry = 0;
            return;
        }
        double time = (now - from) * count / 600_000;
        uint life = RecoveryPool.Advance(player.PlayerLife, player.PlayerMaxLife,
            -time * lifeRate * player.PlayerMaxLife, ref state.MinionLifeLossCarry);
        player.PlayerLife = VaalImmortalCallLife(player, life, now);
        player.EnergyShield.Lose(time * shieldRate * player.EnergyShield.Maximum, ref state.MinionShieldLossCarry);
        if (player.PlayerLife != 0) return;
        player.InvalidateIncomingHits();
        RecordHardcoreDeath(player);
    }

    sealed record ResourceTransfer(string Character, DateTime At, IModuleLifetime Rules, IModuleLifetime? Source,
        ResourceEffectsState.Snapshot State);
    static readonly ConcurrentDictionary<uint, ResourceTransfer> CarriedResourceEffects = new();
    public static void CarryResourceEffects(Player player, uint ticket)
    {
        if (player.PlayerLife == 0 || player.Closed || player.RageRetired
            || player.ResourceEffectsState is not ResourceEffectsState state || !ResourceEffectsCurrent(player, state)) return;
        foreach (var (key, entry) in CarriedResourceEffects)
            if (!entry.Rules.IsActive || entry.Source is { IsActive: false }
                || DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedResourceEffects.TryRemove(key, out _);
        var checkpoint = state.TransferCheckpoint ?? state.Capture(ResourceEffectsTime(player));
        CarriedResourceEffects.TryAdd(ticket, new(CharacterName(player), DateTime.UtcNow, state.Rules!, state.Source, checkpoint));
        state.Clear();
    }
    public static void RestoreResourceEffects(Player player)
    {
        if (!CarriedResourceEffects.TryGetValue(player.Ticket, out var entry) || entry.Character != CharacterName(player)) return;
        if (!CarriedResourceEffects.TryRemove(new KeyValuePair<uint, ResourceTransfer>(player.Ticket, entry))) return;
        if (player.PlayerLife == 0 || !ReferenceEquals(entry.Rules, CombatLifetime) || !entry.Rules.IsActive
            || !ReferenceEquals(entry.Source, AscendancyPolicies.SourceOwner(player)) || entry.Source is { IsActive: false }
            || DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) return;
        var state = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, state)) return;
        // Initial player publication reads restored state at zero; loading cannot consume its remaining duration.
        state.Restore(entry.State, 0); state.Loading = true;
    }
    static void PrimeCatTransfer(Player player)
    {
        if (!CarriedResourceEffects.TryGetValue(player.Ticket, out var entry)
            || entry.Character != CharacterName(player) || entry.State.Cat is not { } cat
            || !ReferenceEquals(entry.Rules, CombatLifetime) || !entry.Rules.IsActive
            || !ReferenceEquals(entry.Source, AscendancyPolicies.SourceOwner(player))
            || entry.Source is { IsActive: false } || DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) return;
        // Saved aura restoration can build pools before SpawnPlayer restores the full resource snapshot.
        player.Reservations.PausedCat = cat;
        player.Reservations.CatTransferLoading = true;
    }

    public static void ActivateResourceEffects(Player player, double at)
    {
        if (player.ResourceEffectsState is ResourceEffectsState { Loading: true } state && ResourceEffectsCurrent(player, state))
        {
            state.Restore(state.Capture(0), at);
            state.Loading = false;
            player.Reservations.CatTransferLoading = false;
        }
    }
}
