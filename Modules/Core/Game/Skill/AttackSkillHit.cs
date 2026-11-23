using GameServer.Core.World;


/// <summary>The existing main-hand attack-gem hit before critical strikes and target mitigation.</summary>
public static class AttackSkillHit
{
    public static DamagePacket Resolve(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> character,
        IEnumerable<(uint, int)> levelConversionInputs, StatCatalog catalog, AttackAccuracyProfile accuracy, int effectiveness, bool melee = false,
        bool projectile = false)
    {
        // Damage modifiers and added attack damage keep the existing character-only inputs. Skill-owned damage
        // modifiers are not implemented in combat and must not become panel-only effects here.
        var Get = WeaponAttributeDamage.Read(WeaponAttributeDamage.Armed(weapon),
            name => name is var key ? character.GetValueOrDefault(key) : 0);
        var raw = SpellDamage.WithEffectiveness(SupportedAttackDamage.Raw(weapon, Get, accuracy), effectiveness);
        // Match ConversionFor exactly: player keys win; level-only skill keys fill gaps, without quality conversion.
        var conversionInputs = new Dictionary<uint, int>(character);
        foreach (var (key, value) in levelConversionInputs) conversionInputs.TryAdd(key, value);
        return MaximumAttackDamage.Apply(RageStats.ApplyAttack(PlayerHitDamage.Resolve(raw, SupportedAttackDamage.HitModifiers(weapon, Get),
            DamageStats.Conversion(conversionInputs, catalog, attack: true), spell: false,
            melee: melee, projectile: projectile, wieldingBow: weapon.Bow, wieldingWand: weapon.WieldingWand,
            additionalIncreased: SupportedAttackDamage.WeaponIncrease(weapon, Get),
            additional: WeaponTypeDamage.Of(weapon, Get)), Get), Get);
    }
}
