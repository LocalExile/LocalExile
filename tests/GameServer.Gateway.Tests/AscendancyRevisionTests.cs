public sealed class AscendancyRevisionTests
{
    [Fact]
    public void A_save_from_3_29_3b_keeps_its_choice_on_3_29_3c()
    {
        Assert.Equal("3.29.3c", AscendancyCatalog.Revision);
        Assert.False(AscendancyCatalog.IsKnownRevision("future"));
        var saved = new AscendancyIdentity(AscendancyFamily.Phrecia, "Gladiator", "3.29.3b");
        var current = saved with { Revision = "3.29.3c" };
        Assert.True(AscendancyCatalog.SameChoice(saved, current));
        Assert.False(AscendancyCatalog.SameChoice(saved, current with { Id = "Chieftain" }));
        Assert.False(AscendancyCatalog.SameChoice(saved, current with { Family = AscendancyFamily.Normal }));
        Assert.False(AscendancyCatalog.SameChoice(saved, current with { Revision = "future" }));
    }
}
