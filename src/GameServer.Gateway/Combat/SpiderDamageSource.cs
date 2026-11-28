using GameServer.Core.World;

// Frozen raw inputs. A later contact supplies the recipient's state before the hit.
public sealed record SpiderDamageSource(DamagePacket Raw, DamageConversion Conversion, IReadOnlyDictionary<uint, int> Stats,
    IReadOnlyDictionary<uint, int> Caster, bool Attack, bool Melee, bool Area, bool Projectile,
    IReadOnlyList<(uint Id, int Value)> Modifiers, IReadOnlySet<int> Types, double Effectiveness,
    bool WieldingWand = false, bool Context = false, bool Secondary = false, bool Volatile = true,
    IReadOnlyDictionary<uint, int>? Local = null, bool Bow = false, double IgniteFactor = 1, double PoisonFactor = 1,
    int WeaponIncreased = 0, HitIncreases WeaponTypedIncreases = default)
{
    public bool? Armed { get; init; }
    public bool Claw { get; init; }
    public bool OffHand { get; init; }
    public double HitIntensityMore { get; init; } = 1;
    public double ReleaseAilmentMore { get; init; } = 1;
    public long ProjectileAttackHitIncrease { get; init; }
    public long ExtraIncreased { get; init; }
    public bool HasTargetConditions { get; init; } = true;
    public double AddedDamageEffectiveness { get; init; } = 1;
}
