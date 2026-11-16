namespace GameServer.Gateway.Tests;

public sealed class ProjectileAnimationDataTests
{
    [Fact]
    public void Monster_contacts_preserve_existing_releases_and_read_ordered_repeat_frames()
    {
        var skills = new SkillCatalog();
        var spitter = skills.MonsterContactPool("Metadata/Monsters/SandSpitters/SandSpitter1", "SpellProjectile");
        Assert.NotNull(spitter);
        Assert.Equal(new[] { "attack1", "attack2" }, spitter.Select(c => c.Animation));
        Assert.Equal(new int?[] { 375, 299 }, spitter.Select(c => c.ContactMs));
        Assert.All(spitter, c => { Assert.Equal(1000, c.LengthMs); Assert.Null(c.Contacts); Assert.NotNull(c.Point); });
        for (ushort sequence = 0; sequence < 32; sequence++)
            Assert.Contains(skills.MonsterContact("Metadata/Monsters/SandSpitters/SandSpitter1", "SpellProjectile", sequence), spitter);

        var hillock = skills.MonsterContact("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal", "Throw", 0);
        Assert.NotNull(hillock);
        Assert.Equal(("extract", 1833, (int?)458), (hillock.Animation, hillock.LengthMs, hillock.ContactMs));
        var iguana = Assert.Single(skills.MonsterContactPool("Metadata/Monsters/GemMonster/Iguana", "Throw")!);
        Assert.Equal(("projectile", 13u, 1533), (iguana.Animation, iguana.Index, iguana.LengthMs));
        Assert.NotNull(iguana.Contacts);
        Assert.Equal(new[] { 299, 433, 566, 699, 833 }, iguana.Contacts.Select(c => c.Ms));
        Assert.Equal((iguana.ContactMs!.Value, iguana.Point), iguana.Contacts[0]);
        Assert.All(iguana.Contacts, c => Assert.NotNull(c.Point));
    }

    [Fact]
    public void Projectile_clips_read_contact_times_views_groups_and_default_fallbacks()
    {
        var skills = new SkillCatalog();
        var spitter = skills.Clips("Metadata/Projectiles/SandspitterMortarProjectile")!;
        Assert.Equal(("proj", 1u, 1000, true, (int?)667),
            (spitter.Flight!.Name, spitter.Flight.Index, spitter.Flight.Ms, spitter.Flight.Once, spitter.Flight.Contact));
        Assert.False(spitter.Flight.Varies);
        Assert.Equal(("end", 0u, 666, true),
            (spitter.Impact!.Name, spitter.Impact.Index, spitter.Impact.Ms, spitter.Impact.Once));
        Assert.Null(spitter.Impact.Contact);
        var view = skills.Clips("Metadata/Projectiles/AbyssPinnacleSoulercoaster")!.Flight!;
        Assert.Equal(("animate_01", 0u, 830, (int?)753), (view.Name, view.Index, view.Ms, view.Contact));
        var group = skills.Clips("Metadata/Projectiles/ArchnemesisBombardierMortar")!.Flight!;
        Assert.Equal(("mortar_01", 1040, (int?)1011), (group.Name, group.Ms, group.Contact));
        Assert.True(group.Varies);
        var fallback = skills.Clips("Metadata/Projectiles/AbyssMonolithicSlugBouncingProjectile")!;
        Assert.Equal("idle", fallback.DefaultAnimation);
        Assert.Equal(("idle", 1000, (int?)1000), (fallback.Flight!.Name, fallback.Flight.Ms, fallback.Flight.Contact));
        Assert.Null(skills.Clips("Metadata/Projectiles/NonexistentBatch1Witness"));
    }
}
