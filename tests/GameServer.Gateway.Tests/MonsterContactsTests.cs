using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A monster projectile skill's release from the data the client itself plays (data/monster-contacts.json and the 3.29.3b
// tables): the Sand Spitter mortar's clips are its ACT's SpellProjectile pool (MonsterMortar.Animation → Animation.dat
// "SpellProjectile"; attack1, attack2), the native selector picks attack1 for a monster action's 0xffff sequence, and that
// clip's keyframe is 375 of 1000 ms at 45.9 world units behind the body (which faces away from its target) and 71.6 up.
public class MonsterContactsTests
{
    const string Spitter = "Metadata/Monsters/SandSpitters/SandSpitter1";

    static ClientState Conn() => new(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1);

    [Fact]
    public void The_spitter_mortar_resolves_its_animation_key_minimum_range_and_clip()
    {
        TestSaves.Ensure();
        var catalog = Program.SkillsFor(Conn());
        var projectile = catalog.MonsterProjectile(617);
        Assert.NotNull(projectile);
        Assert.Equal("SpellProjectile", projectile!.AnimationKey);
        Assert.Equal(50, projectile.MinimumRange);

        var clip = catalog.MonsterContact(Spitter, "SpellProjectile", Program.MonsterActionSequence);
        Assert.NotNull(clip);
        Assert.Equal(("attack1", 0u, 1000, 375), (clip!.Animation, clip.Index, clip.LengthMs, clip.ContactMs));
        Assert.Equal(0.375, clip.ContactFraction!.Value, 6);
        Assert.NotNull(clip.Point);
        Assert.Equal(-45.89, clip.Point!.Value.ForwardUnits, 1);
        Assert.Equal(71.56, clip.Point.Value.UpUnits, 1);
        Assert.Equal(9, clip.Point.Value.HeightSteps);
        Assert.Null(catalog.MonsterContact(Spitter, "NoSuchKey", Program.MonsterActionSequence));
        Assert.Null(catalog.MonsterContact("Metadata/Monsters/NoSuchMonster", "Melee", Program.MonsterActionSequence));
    }

    [Fact]
    public void The_spitters_release_comes_from_its_clip_and_its_stat_set()
    {
        TestSaves.Ensure();
        var conn = Conn();
        var variety = Program.Monsters.Find(Spitter);
        Assert.NotNull(variety);
        Assert.Equal(1.0, variety!.ModelScale, 6);
        var projectile = Program.SkillsFor(conn).MonsterProjectile(617)!;
        var release = Program.MonsterReleaseFor(conn, variety, 617, 1, projectile);
        Assert.NotNull(release);
        Assert.Equal("attack1", release!.Clip);
        Assert.Equal(0.375, release.Fraction, 6);
        Assert.True(release.FacesAway, "action_should_face_away_from_target");
        Assert.NotNull(release.Point);
        Assert.Equal(-4.2, release.Point!.Value.ForwardCells, 1);
        Assert.Equal(9, release.Point.Value.HeightSteps);
    }
}
