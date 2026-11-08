namespace GameServer.Gateway.Tests;

// A gem melee attack's contact comes from the skill's own rig clip (Ground Slam's groundslam_maul: 633 of 1000 ms),
// not from the default attack's attack1 keyframe the class shares (the Str rig's attack1_maul: 599 of 866).
// AnimationContacts.SkillAttackContact resolves it by the skill's GrantedEffects Id without separators ("GroundSlam" →
// "groundslam"), and Game.GemAttackContactDelay calls it; a null answer keeps the attack1 keyframe, so the difference
// between those two fractions is the whole gap between the client's animation and the server's hit.
public sealed class SkillAnimationContactTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"skill-animation-contact-{Guid.NewGuid():N}");

    public SkillAnimationContactTests()
    {
        Directory.CreateDirectory(_folder);
        // The rigs as data/animation-contacts.json has them: the skill's own clips beside the default attack's, a staff
        // variant of the same skill, a rig whose only Ground Slam clip is the attack_<skill> form, and one with neither.
        File.WriteAllText(Path.Combine(_folder, "animation-contacts.json"), """
            { "Metadata/Characters/Str/Str.ao": { "animations": {
                "attack1_maul": [866, 599], "groundslam_maul": [1000, 633], "groundslam_staff": [1000, 700],
                "cleave_maul": [1133, 399] } },
              "Metadata/Characters/Int/Int.ao": { "animations": {
                "attack1_maul": [833, 566], "attack_groundslam_01": [2166, 1399] } },
              "Metadata/Characters/Shaper.ao": { "animations": { "attack1_maul": [833, 566] } } }
            """);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    const string Str = "Metadata/Characters/Str/Str.ao";
    static WeaponProfile Maul => new(Shield: false, DualWield: false, Staff: false, Bow: false, DualClaws: false,
        TwoHand: true, Melee: true, TwoHandMelee: true, Mace: true);
    static WeaponProfile Staff => new(Shield: false, DualWield: false, Staff: true, Bow: false, DualClaws: false,
        TwoHand: true, Melee: true, TwoHandMelee: true);

    [Fact]
    public void A_skill_clip_wins_over_the_default_attacks_attack1_keyframe_and_answers_null_without_one()
    {
        var contacts = new AnimationContacts(_folder);
        // The weapon's own variant of the skill's clip, and the keyframe it replaces on the same rig and weapon.
        var slam = contacts.SkillAttackContact(Str, Maul, "groundslam");
        Assert.True(slam.HasValue, "the Str rig's groundslam_maul should answer");
        Assert.Equal("groundslam_maul", slam.Value.Name);
        Assert.Equal(633.0 / 1000, slam.Value.Contact, 6);
        Assert.Equal(599.0 / 866, contacts.AttackContact(Str, Maul), 6);
        // The staff's own variant of the same skill, by the same name.
        var staff = contacts.SkillAttackContact(Str, Staff, "groundslam");
        Assert.True(staff.HasValue, "the Str rig's groundslam_staff should answer for a staff");
        Assert.Equal("groundslam_staff", staff.Value.Name);
        Assert.Equal(700.0 / 1000, staff.Value.Contact, 6);
        // Another skill resolves to its own clip too.
        var cleave = contacts.SkillAttackContact(Str, Maul, "cleave");
        Assert.True(cleave.HasValue, "the Str rig's cleave_maul should answer");
        Assert.Equal("cleave_maul", cleave.Value.Name);
        Assert.Equal(399.0 / 1133, cleave.Value.Contact, 6);
        // No clip named for the skill (Ice Crash has none on this rig): null, so the caller keeps the attack1 keyframe.
        Assert.Null(contacts.SkillAttackContact(Str, Maul, "icescrash"));
        Assert.Equal(0.692, contacts.AttackContact(Str, Maul), 3);
        // No skill to name, and a rig the file does not carry, both answer null rather than throw.
        Assert.Null(contacts.SkillAttackContact(Str, Maul, null));
        Assert.Null(contacts.SkillAttackContact("Metadata/None.ao", Maul, "groundslam"));
        // The newer attack_<skill> form is the last resort when it is the only clip the rig has for the skill.
        var newer = contacts.SkillAttackContact("Metadata/Characters/Int/Int.ao", Maul, "groundslam");
        Assert.True(newer.HasValue, "the Int rig's attack_groundslam_01 should answer");
        Assert.Equal("attack_groundslam_01", newer.Value.Name);
        Assert.Equal(1399.0 / 2166, newer.Value.Contact, 6);
        Assert.Null(contacts.SkillAttackContact("Metadata/Characters/Shaper.ao", Maul, "groundslam"));
    }
}
