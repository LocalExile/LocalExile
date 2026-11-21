/// <summary>Generic recovery for ordinary stored uses. The native record retains BaseMilliseconds and
/// consumes base-time units at RecoveryRate; it must not receive a pre-divided cooldown.</summary>
public readonly record struct SingleUseCooldown(int BaseMilliseconds, int RecoveryPercent, int Uses = 1, int? SharedGroup = null)
{
    public double RecoveryRate => Math.Max(0, 1 + RecoveryPercent / 100.0);

    static readonly uint[] Unsupported = [
        Stat.BaseAddedCooldownCount, Stat.VirtualAddedCooldownCount, Stat.QuickDodgeAddedCooldownCount,
        Stat.ShockwaveSupportAddedCooldownCount, Stat.SupportDoubleNumberOfCooldownUses,
        Stat.SupportTripleNumberOfCooldownUses, Stat.SkillCooldownTimeOverrideMs, Stat.SkillCooldownIs4Seconds,
        Stat.VirtualCooldownModifierMs, Stat.BaseCooldownSpeedPlusPctFinal,
        Stat.ActiveSkillCooldownSpeedPlusPctFinalFromSkillSpecificStat,
    ];

    public static SingleUseCooldown? ForSkill(SkillCatalog skills, StatCatalog catalog, int effect, int level,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
    {
        int ms = skills.CooldownMs(effect, level);
        // The hidden "Trigger level X Summon Spirit of ..." skills (the Ancestral Spirit options and the Loyalty
        // tattoos) are neither spells, attacks nor warcries, but their shared cooldown is still a plain stored use.
        bool castable = skills.EffectId(effect) == "Unhinge" || skills.IsSpell(effect) || skills.IsAttack(effect) || skills.IsWarcry(effect)
            || skills.EffectId(effect)?.StartsWith("TriggerSummonedAncestor", StringComparison.Ordinal) == true;
        if (ms <= 0 || skills.CooldownUses(effect, level) <= 0 || skills.CooldownPausesWhileActive(effect) || !castable)
            return null;
        int Get(IReadOnlyDictionary<uint, int> values, uint name) => name is var key ? values.GetValueOrDefault(key) : 0;
        if (Unsupported.Any(name => Get(player, name) != 0 || Get(own, name) != 0)) return null;
        int uses = skills.CooldownUses(effect, level);
        // Second Wind adds a stored use only to a non-instant skill. Its recovery penalty still affects instant skills.
        if (!skills.IsInstant(effect)) uses += Math.Max(0, Get(own, Stat.SupportAddedCooldownCountIfNotInstant));
        if (uses is < 1 or > 255) return null;
        // The player's virtual total already includes its base input. Skill inputs are separate, including quality.
        long recovery = (long)Get(player, Stat.VirtualCooldownSpeedPlusPct)
            + Get(own, Stat.BaseCooldownSpeedPlusPct) + Get(own, Stat.VirtualCooldownSpeedPlusPct);
        // Warcry recovery is an increase for the Warcry type (PoB ModParser); the client adds it when is_warcry (0x142824b50).
        if (skills.IsWarcry(effect))
            recovery += (long)Get(player, Stat.WarcryCooldownSpeedPlusPct) + Get(own, Stat.WarcryCooldownSpeedPlusPct);
        if (skills.EffectId(effect) is { } id && id.StartsWith("Summon", StringComparison.Ordinal) && id.Contains("Golem", StringComparison.Ordinal))
            recovery += (long)Get(player, Stat.GolemCooldownRecoveryPlusPct) + Get(own, Stat.GolemCooldownRecoveryPlusPct);
        // Native construction adds 100 in a signed i32. Zero recovery stalls rather than reversing the clock.
        return new(ms, (int)Math.Clamp(recovery, -100, int.MaxValue - 100), uses);
    }
}

/// <summary>Elapsed game-time budget, used in the area's frame. Rate changes settle the old rate
/// first, preserving progress through equipment/stat refreshes and pause.</summary>
public sealed class SingleUseCooldownState(double at, double rate, double remainingBaseMilliseconds = 0,
    int totalBaseMilliseconds = 0)
{
    public double RemainingBaseMilliseconds { get; private set; } = remainingBaseMilliseconds;
    public int TotalBaseMilliseconds { get; private set; } = totalBaseMilliseconds;
    public double Rate { get; private set; } = rate;
    public int UseVersion { get; private set; }
    public ushort NativeSkillId { get; private set; }
    double? nativeRate;
    bool nativeAcknowledged, nativeDirty;
    double spentRate;
    double lastAt = at;
    public int Uses { get; private set; } = 1;
    public int SpentUses { get; private set; } = remainingBaseMilliseconds > 0 ? 1 : 0;
    public bool FullyRecovered => SpentUses == 0;

    // A capacity change never erases recharge debt (including when a support is removed then re-linked).
    public void Capacity(int uses) => Uses = Math.Max(1, uses);

    // Equipping a new member of a cooling group cannot grant a fresh pool. Keep the existing recovery fraction.
    public void InheritDebt(SingleUseCooldownState source, double now, SingleUseCooldown plan)
    {
        source.Refresh(now, source.Rate);
        if (source.FullyRecovered) return;
        Capacity(plan.Uses);
        SpentUses = Math.Min(Uses, source.SpentUses);
        TotalBaseMilliseconds = plan.BaseMilliseconds;
        RemainingBaseMilliseconds = source.TotalBaseMilliseconds > 0
            ? source.RemainingBaseMilliseconds / source.TotalBaseMilliseconds * plan.BaseMilliseconds : plan.BaseMilliseconds;
        lastAt = now;
        spentRate = Rate = plan.RecoveryRate;
    }

    public void Refresh(double now, double rate)
    {
        double recovered = Math.Max(0, now - lastAt) * 1000 * Rate;
        while (SpentUses > 0 && recovered + 1e-6 >= RemainingBaseMilliseconds)
        {
            recovered = Math.Max(0, recovered - RemainingBaseMilliseconds);
            SpentUses--;
            RemainingBaseMilliseconds = SpentUses > 0 ? TotalBaseMilliseconds : 0;
        }
        if (SpentUses > 0) RemainingBaseMilliseconds = Math.Max(0, RemainingBaseMilliseconds - recovered);
        if (FullyRecovered && !nativeDirty && nativeRate == Rate) nativeRate = null; // Known aligned completion.
        lastAt = Math.Max(lastAt, now);
        double next = Math.Max(0, rate);
        if (!FullyRecovered && UseVersion > 0 && next != Rate) nativeDirty = true;
        Rate = next;
    }

    public bool Ready => SpentUses < Uses;
    public double RemainingSeconds => Ready ? 0 : Rate > 0 ? RemainingBaseMilliseconds / (1000 * Rate) : double.PositiveInfinity;
    public bool TryUse(double now, SingleUseCooldown plan)
    {
        Refresh(now, plan.RecoveryRate);
        Capacity(plan.Uses);
        if (!Ready) return false;
        if (FullyRecovered) RemainingBaseMilliseconds = plan.BaseMilliseconds;
        SpentUses++;
        TotalBaseMilliseconds = plan.BaseMilliseconds;
        spentRate = Rate;
        nativeRate = null; // No native entry may be fabricated before the original acknowledgement reaches its gate.
        nativeAcknowledged = nativeDirty = false;
        NativeSkillId = 0;
        UseVersion++;
        return true;
    }

    public void ConfirmNativeUse(int version, ushort skillId, bool synchronizeProgress)
    {
        if (version != UseVersion || nativeAcknowledged) return;
        nativeAcknowledged = true;
        nativeRate = spentRate;
        NativeSkillId = skillId;
        nativeDirty |= synchronizeProgress || Uses > 1;
    }
    public bool NeedsNativeRefresh => nativeAcknowledged && nativeDirty;
    public void NativeRefreshed()
    {
        nativeRate = FullyRecovered ? null : Rate;
        nativeDirty = false;
    }
}
