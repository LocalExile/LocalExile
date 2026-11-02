namespace GameServer.Gateway.Tests;

// A run cast plays a rig clip through 0x1b2 kind 0; every class/weapon stance has to resolve one, or the character
// stands still through the cast. The projectile impact clip is the skill's hit animation and comes from the same
// projectile-clips.json data the monsters use.
public sealed class SurvivorsAnimationTests
{
    static readonly string[] Classes =
    [
        "Metadata/Characters/Str/Str.ao", "Metadata/Characters/Int/Int.ao",
        "Metadata/Characters/StrDexInt/StrDexInt.ao", "Metadata/Characters/Dex/Dex.ao",
        "Metadata/Characters/StrDex/StrDex.ao", "Metadata/Characters/DexInt/DexInt.ao",
        "Metadata/Characters/StrInt/StrInt.ao",
    ];

    static readonly (string Name, WeaponProfile Weapon)[] Weapons =
    [
        ("unarmed", new WeaponProfile(false, false, false, false, false, Unarmed: true)),
        ("bow", new WeaponProfile(false, false, false, true, false)),
        ("wand", new WeaponProfile(false, false, false, false, false, Wand: true)),
        ("staff", new WeaponProfile(false, false, true, false, false)),
        ("sword", new WeaponProfile(false, false, false, false, false, Sword: true)),
        ("2h sword", new WeaponProfile(false, false, false, false, false, Sword: true, TwoHandMelee: true)),
        ("mace", new WeaponProfile(false, false, false, false, false, Mace: true)),
        ("dagger", new WeaponProfile(false, false, false, false, false, Dagger: true)),
        ("claw", new WeaponProfile(false, false, false, false, false, Claw: true)),
    ];

    [Fact]
    public void Every_class_and_weapon_resolves_a_cast_clip_and_the_projectile_impact()
    {
        var contacts = new AnimationContacts("data");
        foreach (string ao in Classes)
            foreach (var (weaponName, weapon) in Weapons)
                foreach (CastAnimationKind kind in Enum.GetValues<CastAnimationKind>())
                {
                    var clip = contacts.CastClip(ao, weapon, kind);
                    Assert.True(clip is not null, $"{ao} / {weaponName} / {kind}: no cast clip");
                    Assert.True(clip!.Value.Index != uint.MaxValue, $"{ao} / {weaponName} / {kind}: no rig index");
                    Assert.True(clip.Value.LengthMs > 0, $"{ao} / {weaponName} / {kind}: no clip length");
                }

        // The specific stance names in the class ACT, not a generic fallback.
        string bow = contacts.CastClip("Metadata/Characters/Dex/Dex.ao", Weapons[1].Weapon, CastAnimationKind.Attack)!.Value.Name;
        Assert.StartsWith("attack1_bow", bow, StringComparison.OrdinalIgnoreCase);
        string projectile = contacts.CastClip("Metadata/Characters/Dex/Dex.ao", Weapons[1].Weapon, CastAnimationKind.Projectile)!.Value.Name;
        Assert.StartsWith("spell_projectile_bow", projectile, StringComparison.OrdinalIgnoreCase);

        // Every projectile object's flight/impact clips: Fireball's explosion, Ice Spear's impact, Ethereal Knives' end.
        var skills = new SkillCatalog("data");
        foreach (string metadata in new[]
                 {
                     "Metadata/Projectiles/Fireball", "Metadata/Projectiles/IceSpear",
                     "Metadata/Projectiles/EtherealKnivesProjectile",
                 })
        {
            var presentation = Game.MonsterProjectilePresentation(skills.Clips(metadata), 0.5);
            Assert.NotNull(presentation);
            Assert.NotNull(presentation!.ImpactClip);
            Assert.True(presentation.ImpactLifetime > 0);
        }
    }
}
