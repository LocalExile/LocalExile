namespace GameServer.Gateway.Tests;

// The attack1 contact keyframe the melee hit waits for (AnimationContacts.AttackContact): the weapon's clip, its shield or
// dual-wield variant, any attack1 of that weapon, else the default.
public class AnimationContactsTests : IDisposable
{
    private const string Ao = "Metadata/Characters/Str/Str.ao";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"anim-contacts-{Guid.NewGuid():N}");

    public AnimationContactsTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "animation-contacts.json"), """
            { "Metadata/Characters/Str/Str.ao": { "amd": "rig.amd", "animations": {
                "attack1_unarmed": [833, 333],
                "attack1_sword": [1000, 500],
                "attack1_sword_shield": [1000, 300],
                "attack1_claw_claw": [833, 299],
                "attack1_staff": [1000, 719],
                "attack1_dagger_alternate": [1000, 250]
            }, "positions": { "attack1_sword": [1.0, -70.0, -100.0], "attack1_staff": [0.0, -0.0, 0.0] } } }
            """);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private double Contact(WeaponProfile weapon) => new AnimationContacts(_folder).AttackContact(Ao, weapon);

    // The keyframe's position comes with the clip: -Y is in front of the model, -Z up; the vertical part in marker steps.
    [Fact]
    public void The_clips_contact_point_is_read_with_it()
    {
        var contacts = new AnimationContacts(_folder);
        var sword = contacts.AttackContactPoint(Ao, new WeaponProfile(false, false, false, false, false, Sword: true));
        Assert.NotNull(sword);
        Assert.Equal(70.0, sword!.Value.ForwardUnits, 3);
        Assert.Equal(100.0, sword.Value.UpUnits, 3);
        Assert.Equal(13, sword.Value.HeightSteps);
        Assert.Equal(70 / (250.0 / 23), sword.Value.ForwardCells, 6);
        Assert.Null(contacts.AttackContactPoint(Ao, new WeaponProfile(false, false, false, false, false, Unarmed: true)));
        Assert.Null(contacts.AttackContactPoint("absent.ao", new WeaponProfile(false, false, false, false, false, Sword: true)));
        // The rig's "0 -0 -0" placeholder for an event without a position is no release point (it would be the feet).
        Assert.Null(contacts.AttackContactPoint(Ao, new WeaponProfile(false, false, true, false, false, TwoHandMelee: true)));
    }

    [Fact]
    public void The_weapon_picks_its_attack1_clip()
    {
        Assert.Equal(333.0 / 833, Contact(new WeaponProfile(false, false, false, false, false, Unarmed: true)), 6);
        Assert.Equal(0.5, Contact(new WeaponProfile(false, false, false, false, false, Sword: true)), 6);
        Assert.Equal(0.719, Contact(new WeaponProfile(false, false, true, false, false, TwoHandMelee: true)), 6);
    }

}
