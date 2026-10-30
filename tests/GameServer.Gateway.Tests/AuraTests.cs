namespace GameServer.Gateway.Tests;

public class AuraTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static AuraCatalog.Aura Aura(SkillCatalog skills, string effect, ushort buffRow, int flat, int percent) =>
        new(0x0103, skills.EffectRow(effect), effect, effect, 1, buffRow, flat, percent);

    [Fact]
    public void Simple_aura_reservations_come_from_GrantedEffectsPerLevel()
    {
        var skills = new SkillCatalog(Data());

        Assert.Equal((0, 5000), skills.Reservation("PurityOfElements", 1));
        Assert.Equal((0, 3500), skills.Reservation("PurityOfFire", 1));
        Assert.Equal((34, 0), skills.Reservation("Clarity", 1));
        Assert.Equal((28, 0), skills.Reservation("Vitality", 1));
        Assert.Equal((22, 0), skills.Reservation("Precision", 1));
        Assert.Equal((0, 5000), skills.Reservation("Haste", 1));
    }

}
