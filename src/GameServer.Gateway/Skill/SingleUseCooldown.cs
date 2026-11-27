/// <summary>Generic recovery for ordinary stored uses. The native record retains BaseMilliseconds and
/// consumes base-time units at RecoveryRate; it must not receive a pre-divided cooldown.</summary>
public readonly record struct SingleUseCooldown(int BaseMilliseconds, int RecoveryPercent, int Uses = 1, int? SharedGroup = null)
{
    public double RecoveryRate => Math.Max(0, 1 + RecoveryPercent / 100.0);

    static readonly uint[] Unsupported = [
        Stat.VirtualAddedCooldownCount, Stat.QuickDodgeAddedCooldownCount,
        Stat.ShockwaveSupportAddedCooldownCount, Stat.SupportDoubleNumberOfCooldownUses,
        Stat.SupportTripleNumberOfCooldownUses, Stat.SkillCooldownTimeOverrideMs, Stat.SkillCooldownIs4Seconds,
        Stat.VirtualCooldownModifierMs, Stat.BaseCooldownSpeedPlusPctFinal,
        Stat.ActiveSkillCooldownSpeedPlusPctFinalFromSkillSpecificStat,
    ];

    public static SingleUseCooldown? ForSkill(SkillCatalog skills, StatCatalog catalog, int effect, int level,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own)
        => ForSkill(skills, catalog, effect, level, player, own, additionalMilliseconds: 0);

    public static SingleUseCooldown? ForSkill(SkillCatalog skills, StatCatalog catalog, int effect, int level,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int> own, int additionalMilliseconds)
    {
        int native = skills.CooldownMs(effect, level);
        int ms = ActorScale.Saturate((long)native + additionalMilliseconds);
        long uses = skills.CooldownUses(effect, level);
        if (uses <= 0 && native <= 0 && ms > 0) uses = 1;
        // The hidden "Trigger level X Summon Spirit of ..." skills (the Ancestral Spirit options and the Loyalty
        // tattoos) are neither spells, attacks nor warcries, but their shared cooldown is still a plain stored use.
        bool warcry = skills.IsWarcry(effect);
        bool castable = skills.EffectId(effect) == "Unhinge" || skills.IsSpell(effect) || skills.IsAttack(effect) || warcry
            || skills.EffectId(effect)?.StartsWith("TriggerSummonedAncestor", StringComparison.Ordinal) == true;
        if (ms <= 0 || uses <= 0 || skills.CooldownPausesWhileActive(effect) || !castable)
            return null;
        int Get(IReadOnlyDictionary<uint, int> values, uint name) => name is var key ? values.GetValueOrDefault(key) : 0;
        if (Unsupported.Any(name => Get(player, name) != 0 || Get(own, name) != 0)) return null;
        // Echoes of Creation grants this base count to socketed warcries through an item support.
        int additionalUses = Get(own, Stat.BaseAddedCooldownCount);
        if (Get(player, Stat.BaseAddedCooldownCount) != 0 || !warcry && additionalUses != 0) return null;
        uses += additionalUses;
        // Second Wind adds a stored use only to a non-instant skill. Its recovery penalty still affects instant skills.
        if (!skills.IsInstant(effect)) uses += Math.Max(0, Get(own, Stat.SupportAddedCooldownCountIfNotInstant));
        if (uses is < 1 or > 255) return null;
        // The player's virtual total already includes its base input. Skill inputs are separate, including quality.
        long recovery = (long)Get(player, Stat.VirtualCooldownSpeedPlusPct)
            + Get(own, Stat.BaseCooldownSpeedPlusPct) + Get(own, Stat.VirtualCooldownSpeedPlusPct);
        // Warcry recovery is an increase for the Warcry type (PoB ModParser); the client adds it when is_warcry (0x142824b50).
        if (warcry)
            recovery += (long)Get(player, Stat.WarcryCooldownSpeedPlusPct) + Get(own, Stat.WarcryCooldownSpeedPlusPct);
        if (skills.IsMovement(effect))
            recovery += (long)Get(player, Stat.MovementSkillsCooldownSpeedPlusPct) + Get(own, Stat.MovementSkillsCooldownSpeedPlusPct);
        if (skills.HasType(effect, "Stance"))
            recovery += (long)Get(player, Stat.StanceSkillCooldownSpeedPlusPct) + Get(own, Stat.StanceSkillCooldownSpeedPlusPct);
        if (skills.EffectId(effect) is "GeneralsCry" or "GeneralsCryAltX" or "GeneralsCryAltY")
            recovery += (long)Get(player, Stat.GeneralsCryCooldownSpeedPlusPct) + Get(own, Stat.GeneralsCryCooldownSpeedPlusPct);
        if (skills.EffectId(effect) is { } id && id.StartsWith("Summon", StringComparison.Ordinal) && id.Contains("Golem", StringComparison.Ordinal))
            recovery += (long)Get(player, Stat.GolemSkillCooldownRecoveryPlusPct) + Get(own, Stat.GolemSkillCooldownRecoveryPlusPct);
        // Native construction adds 100 in a signed i32. Zero recovery stalls rather than reversing the clock.
        return new(ms, (int)Math.Clamp(recovery, -100, int.MaxValue - 100), (int)uses);
    }
}

/// <summary>Elapsed game-time budget, used in the area's frame. Rate changes settle the old rate
/// first, preserving progress through equipment/stat refreshes and pause.</summary>
public sealed class SingleUseCooldownState(double at, double rate, double remainingBaseMilliseconds = 0,
    int totalBaseMilliseconds = 0)
{
    public double RemainingBaseMilliseconds { get; private set; } = remainingBaseMilliseconds;
    public int TotalBaseMilliseconds { get; private set; } = totalBaseMilliseconds;
    /// <summary>The entry does not recover: a guard's buff holds it at full and the bar publishes it with a zero rate
    /// (the official client's frozen entry, measured 2026-11-18). Held also stops the state's own clock, so the cooldown
    /// starts only when the buff ends (Resume).</summary>
    public bool Held { get; private set; }
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
        double next = Math.Max(0, rate);
        if (Held)
        {
            // The buff is running: the entry keeps its full remaining time and the bar shows it with a zero rate.
            if (UseVersion > 0 && next != Rate) nativeDirty = true;
            Rate = next;
            lastAt = Math.Max(lastAt, now);
            return;
        }
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
        if (!FullyRecovered && UseVersion > 0 && next != Rate) nativeDirty = true;
        Rate = next;
    }

    /// <summary>Holds the entry at full while its skill is active: the official client shows that as the skill's own
    /// cooldown at a zero recovery rate (measured 3.00 / 3.00 / rate 0), and this client takes a zero rate on an entry
    /// it did not just predict (the press prediction is replaced by this entry).</summary>
    public void Hold(int baseMilliseconds)
    {
        Held = true;
        TotalBaseMilliseconds = Math.Max(1, baseMilliseconds);
        RemainingBaseMilliseconds = TotalBaseMilliseconds;
        nativeDirty = true;
    }

    /// <summary>Starts the skill's own cooldown after the effect ends: the held entry becomes the plan's base and
    /// counts down at the character's recovery rate.</summary>
    public void Resume(double now, int baseMilliseconds)
    {
        Held = false;
        lastAt = Math.Max(lastAt, now);
        TotalBaseMilliseconds = Math.Max(1, baseMilliseconds);
        RemainingBaseMilliseconds = TotalBaseMilliseconds;
        nativeDirty = true;
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
        Held = false;
        TotalBaseMilliseconds = plan.BaseMilliseconds;
        spentRate = Rate;
        nativeRate = null; // No native entry may be fabricated before the original acknowledgement reaches its gate.
        nativeAcknowledged = nativeDirty = false;
        NativeSkillId = 0;
        UseVersion++;
        return true;
    }

    /// <summary>Adjusts the reservation of a single use that creates one entity per stored charge.</summary>
    public bool CompleteReservedUse(double now, int version, int count)
    {
        if (UseVersion != version || count < 0) return false;
        Refresh(now, Rate);
        int change = count - 1;
        if (change > Math.Max(0, Uses - SpentUses)) return false;
        SpentUses = Math.Max(0, SpentUses + change);
        if (SpentUses == 0) RemainingBaseMilliseconds = 0;
        if (change > 0 && RemainingBaseMilliseconds == 0) RemainingBaseMilliseconds = TotalBaseMilliseconds;
        if (change != 0 && nativeAcknowledged) nativeDirty = true;
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

    /// <summary>Marks a skill whose use triggered an instant cooldown reset/prevention (0x50cd/0x52e2) so the next
    /// <c>PlayerSingleUseCooldowns</c> update clears the client's predicted cooldown sweep.</summary>
    public void SynchronizeNativeReset(ushort skillId, int baseMilliseconds)
    {
        TotalBaseMilliseconds = Math.Max(1, baseMilliseconds);
        NativeSkillId = skillId;
        nativeAcknowledged = true;
        nativeDirty = true;
    }
    public bool NeedsNativeRefresh => nativeAcknowledged && nativeDirty;
    public void NativeRefreshed()
    {
        nativeRate = FullyRecovered ? null : Rate;
        nativeDirty = false;
    }
}
