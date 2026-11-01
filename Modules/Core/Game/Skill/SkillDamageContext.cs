using GameServer.Core.World;


/// <summary>Outgoing skill damage from the actual caster's stats, never an implicit owner's stats.</summary>
readonly record struct SkillDamageContext(SkillHitKind Kind, IReadOnlyDictionary<uint, int> ActualCaster)
{
    public bool Suppressed => Kind == SkillHitKind.Spell
        && ActualCaster.GetValueOrDefault(StatKeys.SpellSkillsDealNoDamage) != 0;

    public DamagePacket Hit(DamagePacket packet) => Suppressed ? DamagePacket.Empty : DamageFactor == 1 ? packet : packet.Scale(DamageFactor);

    // This is outgoing spell damage only. RF's independent self-burn must not pass through this context.
    // The caster's damage-type prohibitions (DamageStats.Prohibited) also stop a DoT of that type (PoB canDeal).
    public DamageOverTime OverTime(DamageOverTime damage) =>
        damage with { DamagePerSecond = Suppressed || Prohibits(damage.Type) ? 0 : damage.DamagePerSecond * DamageFactor };

    bool Prohibits(DamageType type)
    {
        var caster = ActualCaster;
        return DamageStats.Prohibited(type, name => Program.Stats.Key(name) is uint key ? caster.GetValueOrDefault(key) : 0);
    }

    double DamageFactor => Game.GamblerFor(ActualCaster).DamageFactor;
}
