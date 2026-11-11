using GameServer.Core.World;

// Frozen raw inputs, not a retained Game delegate. A later contact supplies only the recipient's web count/Blind.
public sealed record SpiderDamageSource(DamagePacket Raw, DamageConversion Conversion, IReadOnlyDictionary<uint, int> Stats,
    IReadOnlyDictionary<uint, int> Caster, bool Attack, bool Melee, bool Area, bool Projectile,
    IReadOnlyList<(string Name, int Value)> Modifiers, IReadOnlySet<int> Types, double Effectiveness,
    bool WieldingWand = false, bool Context = false, bool Secondary = false, bool Volatile = true,
    IReadOnlyDictionary<string, int>? Local = null, bool Bow = false, double IgniteFactor = 1, double PoisonFactor = 1);
