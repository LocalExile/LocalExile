/// <summary>Opt-in campaign calibration with earned progression and the ordinary gameplay modules.</summary>
public sealed class CampaignCalibrationLeague : League
{
    public override LeagueRules Rules => LeagueRules.Standard with { DevelopmentUnlocks = false };
}
