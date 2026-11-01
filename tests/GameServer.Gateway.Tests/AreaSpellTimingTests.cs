namespace GameServer.Gateway.Tests;

// When an area spell's hits land: at the class's spell_aoe clip contact (the client plays it for the IceNova action's
// Animation.dat row 33, SpellAreaOfEffectCold), then as Ice Nova's ring reaches each victim.
public sealed class AreaSpellTimingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"area-spell-timing-{Guid.NewGuid():N}");

    public AreaSpellTimingTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "animation-contacts.json"), """
            { "Metadata/Characters/Int/Int.ao": { "animations": {
                "spell_projectile_wand": [1066, 599], "spell_aoe_wand": [766, 266] } },
              "Metadata/Characters/StrInt/StrInt.ao": { "animations": {
                "spell_projectile_unarmed": [900, 500], "spellaoe_unarmed": [833, 366] } },
              "Metadata/Characters/Dex/Dex.ao": { "animations": { "spell_projectile_unarmed": [1000, 400] } } }
            """);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void An_area_spell_lands_at_its_aoe_clip_contact()
    {
        var contacts = new AnimationContacts(_folder);
        Assert.Equal(266.0 / 766, contacts.SpellAreaRelease("Metadata/Characters/Int/Int.ao"), 6);
        Assert.Equal(366.0 / 833, contacts.SpellAreaRelease("Metadata/Characters/StrInt/StrInt.ao"), 6);
        // No area clip: the projectile release; no such rig: the default.
        Assert.Equal(0.4, contacts.SpellAreaRelease("Metadata/Characters/Dex/Dex.ao"), 6);
        Assert.Equal(AnimationContacts.DefaultSpellRelease, contacts.SpellAreaRelease("Metadata/None.ao"));
    }

    [Fact]
    public void The_ring_reaches_each_victim_in_proportion_to_its_distance()
    {
        Assert.Equal(0, IceNovaRing.Reach(0, 30, 0));
        Assert.Equal(0.1, IceNovaRing.Reach(15, 30, 0), 6);
        Assert.Equal(0.2, IceNovaRing.Reach(30, 30, 0), 6);
        Assert.Equal(0.25, IceNovaRing.Reach(30, 30, 3), 6);
        Assert.Equal(0.2, IceNovaRing.Reach(31, 30, 0), 6);
    }
}
