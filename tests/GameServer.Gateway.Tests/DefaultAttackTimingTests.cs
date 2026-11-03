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
                      "contact": 300, "slow": [150, 235], "budget": null, "weight": 1, "item": false, "pair": false, "position": [5.1, -82.0, -94.0] } ] } } }
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
        // An attack shorter than the budget compresses the view: the contact at its fraction.
        Assert.Equal(0.4 * 299 / 365.0, clip.ContactSeconds(0.4), 3);
        // A clip without a budget stretches to the attack: a 600 ms wand clip's contact at 300 in a 0.667 s attack.
        var wand = new WeaponProfile(Shield: false, DualWield: false, Staff: false, Bow: false, DualClaws: false,
            OneHand: true, Wand: true);
        Assert.True(contacts.TryDefaultAttackClip(ao, wand, 7, 0.667, out var bolt));
        Assert.Equal("attack1_wand", bolt.Animation);
        Assert.Equal(0.3335, bolt.ContactSeconds(0.667), 3);
        Assert.NotNull(bolt.Point);
        // No pool for the stance (a staff): the attack1 keyframe's fraction, here the default without a clip.
        var staff = new WeaponProfile(Shield: false, DualWield: false, Staff: true, Bow: false, DualClaws: false, TwoHand: true, Melee: true);
        Assert.False(contacts.TryDefaultAttackClip(ao, staff, 7, 1.0, out _));
        Assert.Equal(AnimationContacts.DefaultAttackContact, contacts.DefaultAttackContactSeconds(ao, staff, 7, 1.0), 6);
    }
}
