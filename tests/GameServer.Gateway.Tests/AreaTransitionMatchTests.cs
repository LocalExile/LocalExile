namespace GameServer.Gateway.Tests;

/// <summary>How generated or captured tile objects are recognised as area transitions and matched to the atlas
/// (AreaTransitionCatalog): quest exits named …Transition, and reskinned models (Act 1's caverns).</summary>
public sealed class AreaTransitionMatchTests
{
    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/AreaTransition", "x/beach_area_transition.ao", true)]
    [InlineData("Metadata/QuestObjects/WaterCave/WaterCaveAreaTransition", "x/to_cave_area_transition.ao", true)]
    [InlineData("Metadata/QuestObjects/SouthBeachTownEntrance", "x/beachtown_south_entrance.ao", true)]
    // The Cavern of Anger's exit into Act 2 and Prisoner's Gate's road to The Western Forest were left out.
    [InlineData("Metadata/QuestObjects/MerveilExitTransition", "Metadata/Terrain/Beach/Transitions/MerveilExit/merveil_exit_clean_v01_01_area_transition.ao", true)]
    [InlineData("Metadata/QuestObjects/Spikes/BlockedPassageTransition", "x/beach_passageblock_v01_01_area_transition.ao", true)]
    [InlineData("Metadata/QuestObjects/BossArenaEntranceTransition", "x/cavedown_boss_exit_v01_01_area_transition.ao", true)]
    // A room's quest object that only animates (Fairgraves' ghost ship, a1q4's waterfall) is no exit.
    [InlineData("Metadata/QuestObjects/FairgravesShipTransition", "x/ghostship.ao", false)]
    [InlineData("Metadata/MiscellaneousObjects/Waypoint", "x/beach_waypoint_v01_01_waypoint.ao", false)]
    [InlineData("Metadata/MiscellaneousObjects/AreaTransitionEffects/GlowyFX", "x/crater_glow.ao", false)]
    public void Area_transitions_are_known_by_type_and_model(string type, string model, bool transition) =>
        Assert.Equal(transition, AreaTransitionCatalog.IsTransition(type, model));

    [Fact]
    public void A_reskinned_model_matches_the_atlas_model_it_copies()
    {
        const string atlas = "Metadata/Terrain/Cave/Features/caveup_exit_v01_01_area_transition.ao";
        Assert.True(AreaTransitionCatalog.SameModel(atlas, atlas));
        // The Cavern of Wrath and Anger's master has Reskin "corrupted": its tiles place corrupted_<record>.
        Assert.True(AreaTransitionCatalog.SameModel(atlas, "Metadata/Terrain/Cave/Features/corrupted_caveup_exit_v01_01_area_transition.ao"));
        Assert.False(AreaTransitionCatalog.SameModel(atlas, "Metadata/Terrain/Cave/Features/corrupted_cavedown_exit_v01_01_area_transition.ao"));
        Assert.False(AreaTransitionCatalog.SameModel(atlas, null));
        Assert.False(AreaTransitionCatalog.SameModel(null, atlas));
    }
}
