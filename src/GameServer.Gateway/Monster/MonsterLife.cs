/// <summary>Life information an absolute, whole-number pool cannot recover on the next area entry.</summary>
public sealed record SavedMonsterLife(uint Maximum, (uint Life, uint Maximum)? Fraction, (uint Maximum, uint Scale)? Supplied);

public sealed partial class SpawnedMonster
{
    uint? baseLife;
    // An encounter-supplied maximum already includes its starting modifiers.
    const uint ScaleBase = 100_000;
    (uint Maximum, uint Scale)? suppliedLife;
    (uint Life, uint Maximum)? lifeFraction;

    /// <summary>Use the variety's base life or the final maximum supplied at spawn. The instance keeps absolute
    /// remaining life when a change in party size gives a restored monster a different maximum.</summary>
    public void InitializeLife(uint? varietyBase, SavedMonsterLife? kept)
    {
        baseLife = varietyBase;
        if (varietyBase is null)
        {
            uint scale = Effects.MaxLife(ScaleBase);
            suppliedLife = kept?.Supplied is { } basis && SuppliedMaximum(basis, scale) == MaxLife
                ? basis : (MaxLife, scale);
        }
        if (kept?.Maximum == MaxLife) lifeFraction = kept.Fraction;
    }

    /// <summary>Apply the current modifiers from the original life basis, under the Monsters lock.</summary>
    public void RefreshLife(MonsterModEffects before, MonsterModEffects after)
    {
        uint basis = baseLife ?? ScaleBase;
        uint previous = before.MaxLife(basis), next = after.MaxLife(basis);
        if (previous == next) return;
        if (baseLife is null) suppliedLife ??= (MaxLife, previous);
        uint maximum = MaximumFrom(next);
        if (maximum == MaxLife) return;
        var fraction = CurrentFraction();
        MaxLife = maximum;
        SetLifeFromPool(fraction.Life, fraction.Maximum);
    }

    /// <summary>Set the remaining share of an encounter's pool in the monster's current maximum, under the Monsters lock.</summary>
    public void SetLifeFromPool(uint remaining, uint maximum)
    {
        lifeFraction = (remaining, maximum);
        Life = Project((remaining, maximum), MaxLife);
    }

    /// <summary>Absolute remaining life with the modifiers kept by the instance, before any change of party.</summary>
    public (uint Life, SavedMonsterLife? Scaling) LifeForSave()
    {
        if (lifeFraction is null && suppliedLife is null) return (Life, null);
        uint maximum = TemporaryStats.Count == 0 ? MaxLife : MaximumFrom(Curses.Effects(EncounterEffects).MaxLife(baseLife ?? ScaleBase));
        var fraction = CurrentFraction();
        uint life = Project(fraction, maximum);
        bool exact = (ulong)fraction.Life * maximum == (ulong)life * fraction.Maximum;
        return (life, exact && suppliedLife is null ? null : new(maximum, exact ? null : fraction, suppliedLife));
    }

    uint MaximumFrom(uint value) => suppliedLife is { } basis ? SuppliedMaximum(basis, value) : value;

    static uint SuppliedMaximum((uint Maximum, uint Scale) basis, uint scale) =>
        (uint)Math.Clamp((ulong)basis.Maximum * scale / basis.Scale, 1, uint.MaxValue);

    // Preserve the unrounded fraction until a hit or recovery changes the displayed life.
    (uint Life, uint Maximum) CurrentFraction() =>
        lifeFraction is { } fraction && Project(fraction, MaxLife) == Life ? fraction : (Life, MaxLife);

    static uint Project((uint Life, uint Maximum) fraction, uint maximum) =>
        (uint)(((ulong)fraction.Life * maximum + fraction.Maximum - 1) / fraction.Maximum);
}
