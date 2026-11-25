using System.Text.Json;

public sealed class AscendancyRevisionTests
{
    [Fact]
    public void A_save_from_3_29_3b_keeps_its_choice_on_3_29_3c()
    {
        Assert.Equal("3.29.3c", AscendancyCatalog.Revision);
        Assert.False(AscendancyCatalog.IsKnownRevision("future"));
        var saved = new AscendancyIdentity(AscendancyFamilies.Named("Phrecia"), "Gladiator", "3.29.3b");
        var current = saved with { Revision = "3.29.3c" };
        Assert.True(AscendancyCatalog.SameChoice(saved, current));
        Assert.False(AscendancyCatalog.SameChoice(saved, current with { Id = "Chieftain" }));
        Assert.False(AscendancyCatalog.SameChoice(saved, current with { Family = AscendancyFamily.Normal }));
        Assert.False(AscendancyCatalog.SameChoice(saved, current with { Revision = "future" }));
    }

    [Fact]
    public void A_family_is_kept_by_name_and_the_ordinary_one_is_still_read_from_its_old_number()
    {
        // The text a save holds is the family's name, whatever number this run gave the family.
        var phrecia = new AscendancyIdentity(AscendancyFamilies.Named("Phrecia"), "Gladiator", "3.29.3c");
        string json = JsonSerializer.Serialize(phrecia);
        Assert.Contains("\"Family\":\"Phrecia\"", json);
        Assert.Equal(phrecia.Family, JsonSerializer.Deserialize<AscendancyIdentity>(json)!.Family);
        Assert.Equal(phrecia.Family, JsonSerializer.Deserialize<AscendancyIdentity>(json.Replace("Phrecia", "phrecia"))!.Family);
        Assert.Equal("Phrecia", phrecia.Family.Name());
        Assert.Contains("\"Family\":\"Normal\"", JsonSerializer.Serialize(new AscendancyIdentity(AscendancyFamily.Normal, "Slayer", "3.29.3c")));
        // A build converter writes the ordinary family as the number it was, 0. Another number names no family.
        Assert.Equal(AscendancyFamily.Normal,
            JsonSerializer.Deserialize<AscendancyIdentity>("{\"Family\":0,\"Id\":\"Juggernaut\",\"Revision\":\"3.29.3c\"}")!.Family);
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AscendancyIdentity>("{\"Family\":1,\"Id\":\"Gladiator\",\"Revision\":\"3.29.3c\"}"));
        Assert.True(AscendancyFamilies.TryNamed("NORMAL", out var normal) && normal == AscendancyFamily.Normal);
        Assert.False(AscendancyFamilies.TryNamed("no such family", out _));
    }
}
