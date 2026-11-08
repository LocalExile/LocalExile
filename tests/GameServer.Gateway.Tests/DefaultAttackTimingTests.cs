namespace GameServer.Gateway.Tests;

// When the default attack's contact plays: the class ACT's stance clip as the client plays it (a one-hand sword swings
// the two views of doublestrike_sword over a 0.45 s budget, stretched to the attack time before the contact).
public sealed class DefaultAttackTimingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"default-attack-timing-{Guid.NewGuid():N}");

    public DefaultAttackTimingTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "animation-contacts.json"), """
            { "Metadata/Characters/Dex/Dex.ao": {
                "animations": { "attack1_sword": [666, 299], "attack1_wand": [600, 300] },
                "defaultAttack": {
                  "OneHandSword": [
                    { "animation": "attack_sword_01a", "source": "doublestrike_sword", "index": 153, "begin": 0, "length": 365,
                      "contact": 299, "slow": [131, 218], "budget": 0.45, "weight": 1, "item": false, "pair": true, "position": null },
                    { "animation": "attack_sword_01b", "source": "doublestrike_sword", "index": 153, "begin": 365, "length": 768,
                      "contact": 534, "slow": null, "budget": 0.45, "weight": 1, "item": false, "pair": true, "position": null } ],
                  "Wand": [
                    { "animation": "attack1_wand", "source": "attack1_wand", "index": 31, "begin": 0, "length": 600,
                      "contact": 300, "slow": [150, 235], "budget": null, "weight": 1, "item": false, "pair": false, "position": [5.1, -82.0, -94.0] } ],
                  "Bow": [
                    { "animation": "attack1_bow", "source": "attack1_bow", "index": 2, "begin": 0, "length": 1066,
                      "contact": 399, "cancel": 600, "slow": [166, 397], "budget": null, "weight": 1, "item": true, "pair": false, "position": [-6.3, -59.8, -127.5] } ] } } }
            """);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_budgeted_sword_view_lands_its_contact_late_and_a_wand_clip_at_its_fraction()
    {
        var contacts = new AnimationContacts(_folder);
        const string ao = "Metadata/Characters/Dex/Dex.ao";
        var sword = new WeaponProfile(Shield: false, DualWield: false, Staff: false, Bow: false, DualClaws: false,
            OneHand: true, Melee: true, OneHandMelee: true, Sword: true);
        // The pair's later contact, as the client's order is not predicted: 299 ms at 365/0.45 of the clip's speed, plus
        // the 0.195 s the client stretches a 0.645 s attack by.
        Assert.True(contacts.TryDefaultAttackClip(ao, sword, 7, 0.645, out var clip));
        Assert.Equal("attack_sword_01a", clip.Animation);
        Assert.Equal(0.299 * 0.45 / 0.365 + 0.195, clip.ContactSeconds(0.645), 3);
        Assert.Equal(0.5636, contacts.DefaultAttackContactSeconds(ao, sword, 7, 0.645), 3);
        // One-hand maces use the sword stance too: the Marauder's club plays attack_sword_01a,
        // a 0..387 ms view of doublestrike_sword, rather than attack1_maul's contact fraction.
        var mace = sword with { Sword = false, Mace = true };
        Assert.Equal("OneHandSword", AnimationContacts.StanceFor(mace));
        Assert.Equal("OneHandSwordShield", AnimationContacts.StanceFor(mace with { Shield = true }));
        Assert.Equal("TwoHandMace", AnimationContacts.StanceFor(mace with { OneHand = false, TwoHandMelee = true }));
        Assert.Equal(contacts.DefaultAttackCutInSeconds(ao, sword, 7, 0.645),
            contacts.DefaultAttackCutInSeconds(ao, mace, 7, 0.645));
        // An attack shorter than the budget compresses the view: the contact at its fraction.
        Assert.Equal(0.4 * 299 / 365.0, clip.ContactSeconds(0.4), 3);
        // A clip without a budget stretches to the attack: a 600 ms wand clip's contact at 300 in a 0.667 s attack.
        var wand = new WeaponProfile(Shield: false, DualWield: false, Staff: false, Bow: false, DualClaws: false,
            OneHand: true, Wand: true);
        Assert.True(contacts.TryDefaultAttackClip(ao, wand, 7, 0.667, out var bolt));
        Assert.Equal("attack1_wand", bolt.Animation);
        Assert.Equal(0.3335, bolt.ContactSeconds(0.667), 3);
        // The client's own bolt leaves when its clip clock reaches the contact: the clip plays at its speed outside the
        // slow span (150-235), which takes up the attack's 67 ms over the clip, so 0.367 s; an attack no longer than the
        // clip compresses it evenly, and a budgeted view leaves at its contact.
        Assert.Equal(0.300 + 0.067, bolt.ReleaseSeconds(0.667), 4);
        Assert.Equal(0.5 * 0.5, bolt.ReleaseSeconds(0.5), 4);
        // At most 8 times its length: then the rest of the clip slows evenly over what is left.
        Assert.Equal(0.085 * 8 + (0.150 + 0.065) * (1.6 - 0.68) / 0.515, bolt.ReleaseSeconds(1.6), 4);
        Assert.Equal(clip.ContactSeconds(0.645), clip.ReleaseSeconds(0.645), 6);
        // A lockstep client fires on the frame tick its clip clock passes the release, counted from the start of the
        // frame its action began in: the wand's 0.367 s on frame 12, the bow's 0.268 s on 9 (33 ms frames), at least 1.
        Assert.Equal(10 + 12 * 0.033, Game.LockstepShotAt(10, bolt.ReleaseSeconds(0.667)), 6);
        Assert.Equal(10 + 9 * 0.033, Game.LockstepShotAt(10, 0.268), 6);
        Assert.Equal(10 + 11 * 0.033, Game.LockstepShotAt(10, 11 * 0.033), 6);
        Assert.Equal(10 + 0.033, Game.LockstepShotAt(10, 0), 6);
        Assert.NotNull(bolt.Point);
        // No lock_cancelling keyframe: a Move held during the swing runs at the contact.
        Assert.Equal(bolt.ContactSeconds(0.667), bolt.CutInSeconds(0.667), 6);
        Assert.Equal(clip.ContactSeconds(0.645), clip.CutInSeconds(0.645), 6);
        // The bow's clip stretches to the attack: the arrow leaves at the contact (399 of 1066 ms) and a held Move runs at
        // the lock_cancelling keyframe (600).
        var bow = new WeaponProfile(Shield: false, DualWield: false, Staff: false, Bow: true, DualClaws: false, TwoHand: true);
        Assert.True(contacts.TryDefaultAttackClip(ao, bow, 7, 0.714, out var shot));
        Assert.Equal(0.714 * 399 / 1066, shot.ContactSeconds(0.714), 4);
        Assert.Equal(0.714 * 600 / 1066, shot.CutInSeconds(0.714), 4);
        Assert.Equal(0.714 * 600 / 1066, contacts.DefaultAttackCutInSeconds(ao, bow, 7, 0.714), 4);
        // No pool for the stance (a staff): the attack1 keyframe's fraction, here the default without a clip.
        var staff = new WeaponProfile(Shield: false, DualWield: false, Staff: true, Bow: false, DualClaws: false, TwoHand: true, Melee: true);
        Assert.False(contacts.TryDefaultAttackClip(ao, staff, 7, 1.0, out _));
        Assert.Equal(AnimationContacts.DefaultAttackContact, contacts.DefaultAttackContactSeconds(ao, staff, 7, 1.0), 6);
    }
}
