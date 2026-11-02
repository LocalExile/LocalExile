using GameServer.Core.Protocol.Instance;
using League = GameServer.Core.Accounts.League;

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

    [Fact]
    public void Every_run_skill_maps_to_a_cast_clip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-survivors-anim-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "SurvivorsAnimation", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "SurvivorsAnimation", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var catalog = Game.SurvivorsCatalogFor(conn);
            var contacts = new AnimationContacts("data");
            string ao = Game.SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
            var unarmed = new WeaponProfile(false, false, false, false, false, Unarmed: true);
            Assert.True(catalog.Count >= 25, $"only {catalog.Count} run skills");
            foreach (var skill in catalog)
            {
                var kind = Game.SurvivorsCastKind(skill);
                Assert.True(contacts.CastClip(ao, unarmed, kind) is not null,
                    $"{skill.Name}: {kind} has no clip on {ao}");
            }
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
