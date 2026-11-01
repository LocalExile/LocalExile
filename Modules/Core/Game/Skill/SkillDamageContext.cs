using GameServer.Core.World;


/// <summary>Outgoing skill damage from the actual caster's stats, never an implicit owner's stats.</summary>
readonly record struct SkillDamageContext(SkillHitKind Kind, IReadOnlyDictionary<uint, int> ActualCaster)
{
    public bool Suppressed => Kind == SkillHitKind.Spell
        && ActualCaster.GetValueOrDefault(StatKeys.SpellSkillsDealNoDamage) != 0;

    public DamagePacket Hit(DamagePacket packet) => Suppressed ? DamagePacket.Empty : DamageFactor == 1 ? packet : packet.Scale(DamageFactor);

    // This is outgoing spell damage only. RF's independent self-burn must not pass through this context.
    public DamageOverTime OverTime(DamageOverTime damage) =>
        damage with { DamagePerSecond = Suppressed ? 0 : damage.DamagePerSecond * DamageFactor };

    double DamageFactor => Game.GamblerFor(ActualCaster).DamageFactor;
}
