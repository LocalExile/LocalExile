namespace GameServer.Gateway.Tests;

public class SkillArtTests
{
    // Rows shaped as the extracted tables: Fireball's projectile and explosion variations, Righteous Fire's rings.
    static SkillArt Fixture()
    {
        string dir = Directory.CreateTempSubdirectory("skillart").FullName, tables = Path.Combine(dir, "tables");
        Directory.CreateDirectory(tables);
        File.WriteAllText(Path.Combine(tables, "Projectiles.json"),
            """[{"Id":"Metadata/Projectiles/Fireball"},{"Id":"Metadata/Projectiles/StygianFireball"}]""");
        File.WriteAllText(Path.Combine(tables, "ProjectilesArtVariations.json"),
            """[{"Id":"fireball","Variant":0,"Projectiles":[0]},{"Id":"fireball","Variant":22,"Projectiles":[1]}]""");
        File.WriteAllText(Path.Combine(tables, "MiscAnimated.json"), """
            [{"AOFile":"Metadata/Effects/Spells/fireball/explode_new2.ao","__col4":0},
             {"AOFile":"Metadata/Effects/Microtransactions/Spells/fireball/stygian/aoe.ao","__col4":0},
             {"AOFile":"Metadata/Effects/Spells/ri_fire/rifire.ao","__col4":400},
             {"AOFile":"Metadata/Effects/Spells/ri_fire/rifire.ao","__col4":0},
             {"AOFile":"Metadata/Effects/Spells/ri_fire_white/rifire_white.ao","__col4":200}]
            """);
        File.WriteAllText(Path.Combine(tables, "MiscAnimatedArtVariations.json"), """
            [{"Id":"fireball_standard","Variant":0,"Animation":[0]},{"Id":"fireball_standard","Variant":22,"Animation":[1]},
             {"Id":"righteous_fire_ring","Variant":0,"Animation":[2]},{"Id":"righteous_fire_ring","Variant":2,"Animation":[4]}]
            """);
        File.WriteAllText(Path.Combine(tables, "BuffVisualsArtVariations.json"), "[]");
        return new SkillArt(dir);
    }

    [Fact]
    public void A_variation_swaps_the_projectile_and_its_explosion()
    {
        var art = Fixture();
        Assert.Equal("Metadata/Projectiles/StygianFireball", art.Projectile("Metadata/Projectiles/Fireball", 22));
        Assert.Equal("Metadata/Effects/Microtransactions/Spells/fireball/stygian/aoe.ao",
            art.AnimatedInPlaceOf("Metadata/Effects/Spells/fireball/explode_new2.ao", 22));
        // No row for the variation (or none worn): the default.
        Assert.Equal("Metadata/Projectiles/Fireball", art.Projectile("Metadata/Projectiles/Fireball", 5));
        Assert.Equal("Metadata/Projectiles/Fireball", art.Projectile("Metadata/Projectiles/Fireball", 0));
        Assert.Equal("Metadata/Effects/Spells/fireball/explode_new2.ao", art.AnimatedInPlaceOf("Metadata/Effects/Spells/fireball/explode_new2.ao", 5));
    }

}
