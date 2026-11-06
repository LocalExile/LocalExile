public sealed class AscendancyRevisionTests
{
    [Theory]
    [InlineData("3.29.3b", true)]
    [InlineData("3.29.3c", true)]
    [InlineData("future", false)]
    public void Only_reviewed_revisions_are_known(string revision, bool known) =>
        Assert.Equal(known, AscendancyCatalog.IsKnownRevision(revision));

    [Fact]
    public void A_save_from_the_previous_build_is_the_same_choice()
    {
        var saved = new AscendancyIdentity(AscendancyFamily.Phrecia, "Gladiator", "3.29.3b");
        var current = saved with { Revision = "3.29.3c" };
        Assert.True(AscendancyCatalog.SameChoice(saved, current));
        Assert.False(AscendancyCatalog.SameChoice(saved, current with { Id = "Chieftain" }));
        Assert.False(AscendancyCatalog.SameChoice(saved, current with { Family = AscendancyFamily.Normal }));
        Assert.False(AscendancyCatalog.SameChoice(saved, current with { Revision = "future" }));
    }

    [Fact]
    public void The_catalog_targets_3_29_3c() => Assert.Equal("3.29.3c", AscendancyCatalog.Revision);
}
