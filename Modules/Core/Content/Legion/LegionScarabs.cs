using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

public static class LegionScarabs
{
    public static void Opening(MapOpening request)
    {
        if (!request.Campaign && request.Opening is { } opening && MapDevices.IsMapArea(opening.Area.Hash16)
            && LegionData.Current.Factions.Count >= 5 && QuestObjectTypes.Find(LegionMonolith.InitiatorType) is not null)
            request.HandledContent.Add("Legion");
    }

    // The client exposes the switches, but not the difficulty curve. These increases are server calibration.
    public static int Difficulty(int breakouts, bool spread) => checked(Math.Max(0, breakouts) * 50 + (spread ? 100 : 0));
    public static double Rewards(int breakouts, int increase) => 1 + Math.Max(0, breakouts) * Math.Max(0, increase) / 100d;
    public static List<(uint Key, int Value)> Stats(int difficulty, int[]? rewards) =>
        [ (Stat.MaximumLifePlusPct, difficulty), (Stat.DamagePlusPct, difficulty),
          .. LegionStasis.RewardIcon(rewards) is int icon ? new[] { (Stat.MonsterMinimapIcon, icon) } : [] ];

    internal static IReadOnlyDictionary<uint, int> FrozenStats(int taken, LegionStasis.Member member)
    {
        var stats = LegionStasis.FrozenStats(taken).ToDictionary();
        foreach (var (key, value) in Stats(Difficulty(member.Breakouts, member.Spread), member.Rewards)) stats[key] = value;
        return stats;
    }
}
