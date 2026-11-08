public sealed class CosmeticMigrationTests
{
    // Records are MtxTypes rows + 1. Expected rows checked against both clients' MtxTypes.Id: 3.29.3b row 5510
    // (MicrotransactionYearBeastHelmet) is 3.29.3c row 5512, row 5614 is 5621, row 8410 (HideoutShipValveRusted) is
    // 8530, and row 5081 (MicrotransactionWildwoodSeerLevelupExtraEffect) is gone.
    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(5081u, 5081u)]
    [InlineData(5082u, null)]
    [InlineData(5511u, 5513u)]
    [InlineData(5615u, 5622u)]
    [InlineData(8411u, 8531u)]
    [InlineData(0u, null)]
    [InlineData(8412u, null)]
    public void A_3_29_3b_record_moves_to_its_3_29_3c_row(uint saved, uint? expected) =>
        Assert.Equal(expected, CosmeticMigration.Record3293c(saved));
}
