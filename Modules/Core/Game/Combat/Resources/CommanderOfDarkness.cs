/// <summary>Commander of Darkness (Necromancer ascendancy): the three values the node's stats carry, read from the
/// caster's raw passive and gear sources (CharacterStatSources) so the aura reconciliation can stamp them onto each
/// running aura (AuraCatalog.Aura's Commander* fields), which is how the allies and minions the aura reaches learn
/// them. Stats.dat rows 3773, 4817, 4482 (PassiveSkills 1514, values 3/30/30); a stat key is its row + 1.</summary>
static partial class Game
{
    /// <summary>The caster's Commander of Darkness values from its raw sources: the speed each of its auras grants,
    /// the elemental resistance and the damage for anyone its auras affect. 0 for a value the caster does not have
    /// (the node is not allocated, or the stat key is absent).</summary>
    internal static (int Speed, int Resistance, int Damage) CommanderOfDarkness(IReadOnlyDictionary<uint, int> sources)
    {
        return (sources.GetValueOrDefault(Stat.AurasGrantAttackAndCastSpeedPlusPctToYouAndYourAllies),
            sources.GetValueOrDefault(Stat.ElementalResistancesPlusPctForYouAndAlliesAffectedByYourAuras),
            sources.GetValueOrDefault(Stat.DamagePlusPctForYouAndAlliesAffectedByYourAuras));
    }
}
