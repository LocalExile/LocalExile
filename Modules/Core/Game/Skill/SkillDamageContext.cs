using GameServer.Core.World;


/// <summary>Outgoing skill damage from the actual caster's stats, never an implicit owner's stats.</summary>
public readonly record struct SkillDamageContext(SkillHitKind Kind, IReadOnlyDictionary<uint, int> ActualCaster)
{
    public bool Suppressed => Kind == SkillHitKind.Spell
        && ActualCaster.GetValueOrDefault(StatKeys.SpellSkillsDealNoDamage) != 0
        || Get("keystone_ancestral_bond") != 0 || Get("deal_no_damage_yourself") != 0;

    int Get(string name) => Program.Stats.Key(name) is uint key ? ActualCaster.GetValueOrDefault(key) : 0;

    public DamagePacket Hit(DamagePacket packet) => Suppressed ? DamagePacket.Empty : DamageFactor == 1 ? packet : packet.Scale(DamageFactor);

    // Outgoing skill damage only. RF's independent self-burn must not pass through this context.
    // The caster's damage-type prohibitions (DamageStats.Prohibited) also stop a DoT of that type (PoB canDeal).
    public DamageOverTime OverTime(DamageOverTime damage) =>
        damage with
        {
            DamagePerSecond = Suppressed || Prohibits(damage.Type) ? 0 : damage.DamagePerSecond * DamageFactor,
        };

    bool Prohibits(DamageType type)
    {
        var caster = ActualCaster;
        int Get(string name) => Program.Stats.Key(name) is uint key ? caster.GetValueOrDefault(key) : 0;
        return DamageStats.Prohibited(type, Get, attack: Kind == SkillHitKind.Attack);
    }

    double DamageFactor => Game.GamblerFor(ActualCaster).DamageFactor;
}
