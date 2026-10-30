using GameServer.Core.World;

/// <summary>The existing main-hand attack-gem hit before critical strikes and target mitigation.</summary>
static class AttackSkillHit
{
    public static DamagePacket Resolve(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> character,
        IEnumerable<(uint, int)> levelConversionInputs, StatCatalog catalog, int effectiveness, bool melee = false)
    {
        // Damage modifiers and added attack damage keep the existing character-only inputs. Skill-owned damage
        // modifiers are not implemented in combat and must not become panel-only effects here.
        int Get(string name) => catalog.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        var hit = DamageStats.Added(weapon.HitDamage(character), Get, spell: false, melee,
            wieldingWand: weapon.WieldingWand);
        // Match ConversionFor exactly: player keys win; level-only skill keys fill gaps, without quality conversion.
        var conversionInputs = new Dictionary<uint, int>(character);
        foreach (var (key, value) in levelConversionInputs) conversionInputs.TryAdd(key, value);
        return RageStats.ApplyAttack(SpellDamage.WithEffectiveness(hit, effectiveness)
            .Convert(DamageStats.Conversion(conversionInputs, catalog, attack: true)), Get);
    }
}
