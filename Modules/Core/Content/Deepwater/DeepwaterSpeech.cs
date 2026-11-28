using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Deepwater;

/// <summary>Valerie's tutorial and encounter voice lines (<c>S→C 0x01c7</c> <see cref="InstanceNpcs.TextAudio"/>),
/// resolved by <c>NPCTextAudio.Id</c> from <c>client.db</c> at load time so row numbers never drift across client builds.</summary>
internal static class DeepwaterSpeech
{
    sealed record Lines(
        ushort? IntroCallout,
        ushort? TutAfterChartOne,
        ushort? TutAfterChartTwo,
        ushort? TutAfterChartPlaced,
        ushort? TutEnterUnderseaOne,
        ushort? TutLanternPlaced,
        ushort? TutLanternLanded,
        ushort? TutSecondLanternLanded,
        ushort? TutThirdLanternLanded,
        ushort? TutLanternsShrinking,
        ushort? TutPlayerDrowns,
        ushort? EnterDrowningFirst,
        ushort[] ActivateChart,
        ushort[] ReachOceanFloor,
        ushort[] PlaceLantern,
        ushort[] LanternsShrinking);

    static Lines Data = new(null, null, null, null, null, null, null, null, null, null, null, null, [], [], [], []);

    public static void LoadGameData()
    {
        var byId = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var activateChart = new List<ushort>();
        var reachOceanFloor = new List<ushort>();
        var placeLantern = new List<ushort>();
        var shrinking = new List<ushort>();

        foreach (var r in ClientTables.TableOrEmpty("NPCTextAudio"))
        {
            if (r.Index < 0 || r.Index > ushort.MaxValue) continue;
            string? id = r.TextOrNull("Id");
            if (string.IsNullOrEmpty(id) || !id.StartsWith("Val_Wild_", StringComparison.Ordinal)) continue;
            ushort row = (ushort)r.Index;
            byId.TryAdd(id, row);
            if (id.StartsWith("Val_Wild_BathosphereActivateChart_", StringComparison.Ordinal)) activateChart.Add(row);
            else if (id.StartsWith("Val_Wild_ReachOceanFloorChart_", StringComparison.Ordinal)) reachOceanFloor.Add(row);
            else if (id.StartsWith("Val_Wild_PlaceLantern_", StringComparison.Ordinal)) placeLantern.Add(row);
            else if (id.StartsWith("Val_Wild_LanternsShrinking_", StringComparison.Ordinal)) shrinking.Add(row);
        }

        ushort? Get(string id) => byId.TryGetValue(id, out ushort row) ? row : null;
        Data = new(
            IntroCallout: Get("Val_Wild_IntroCallout"),
            TutAfterChartOne: Get("Val_Wild_Tut_AfterChartOne"),
            TutAfterChartTwo: Get("Val_Wild_Tut_AfterChartTwo"),
            TutAfterChartPlaced: Get("Val_Wild_Tut_AfterChartPlaced"),
            TutEnterUnderseaOne: Get("Val_Wild_Tut_EnterUnderseaFirstOne"),
            TutLanternPlaced: Get("Val_Wild_Tut_LanternPlaced"),
            TutLanternLanded: Get("Val_Wild_Tut_LanternLanded"),
            TutSecondLanternLanded: Get("Val_Wild_Tut_SecondLanternLanded"),
            TutThirdLanternLanded: Get("Val_Wild_Tut_ThirdLanternLanded"),
            TutLanternsShrinking: Get("Val_Wild_Tut_LanternsShrinking"),
            TutPlayerDrowns: Get("Val_Wild_Tut_PlayerDrowns"),
            EnterDrowningFirst: Get("Val_Wild_EnterDrowningAreaFirst"),
            ActivateChart: [.. activateChart],
            ReachOceanFloor: [.. reachOceanFloor],
            PlaceLantern: [.. placeLantern],
            LanternsShrinking: [.. shrinking]);
    }

    static bool Tutorial(Player player) =>
        !player.Character.LeagueQuestFlags.Contains(DeepwaterConstants.FirstChartLeagueFlag);

    static Task Say(Player player, ushort? row, string label, uint objectId = 0, uint delayMs = 0)
    {
        if (row is not ushort r || player.Closed || player.LeavingArea) return Task.CompletedTask;
        return PlayerOf(player).Send(
            InstanceNpcs.TextAudio(objectId, DeepwaterProgression.ValerieTutorialRow, r, delayMs),
            $"0x01c7 Valerie {label} (NPCTextAudio {r}{(delayMs > 0 ? $", +{delayMs}ms" : "")})");
    }

    static ushort? Pick(Player player, ushort[] pool) =>
        pool.Length == 0 ? null : pool[AreaRng(player).Next(pool.Length)];

    /// <summary>Coast proximity greeting (<c>ValerieTutorial.ot FirstWildCallout</c>).</summary>
    public static Task CoastIntro(Player player, uint valerieId = 0) =>
        Say(player, Data.IntroCallout, "Val_Wild_IntroCallout", valerieId);

    /// <summary>Valerie's <c>NPCTalk#3930</c> <c>CallOnClient( ChartCallout )</c> after handing over the tutorial chart.</summary>
    public static async Task ChartCallout(Player player)
    {
        await Say(player, Data.TutAfterChartOne, "Val_Wild_Tut_AfterChartOne");
        await Say(player, Data.TutAfterChartTwo, "Val_Wild_Tut_AfterChartTwo", delayMs: 4500);
    }

    /// <summary>Valerie's <c>ClimbInCallout</c> when a Chart's Bathysphere activates.</summary>
    public static Task ClimbInCallout(Player player) => Tutorial(player)
        ? Say(player, Data.TutAfterChartPlaced, "Val_Wild_Tut_AfterChartPlaced")
        : Say(player, Pick(player, Data.ActivateChart), "Val_Wild_BathosphereActivateChart");

    /// <summary>Valerie's greeting upon reaching the ocean floor in a Chart.</summary>
    public static Task EnterChart(Player player) => Tutorial(player)
        ? Say(player, Data.TutEnterUnderseaOne, "Val_Wild_Tut_EnterUnderseaFirstOne", delayMs: 500)
        : Say(player, Pick(player, Data.ReachOceanFloor), "Val_Wild_ReachOceanFloorChart", delayMs: 500);

    /// <summary>Valerie's callouts when lanterns are placed during a Chart or Voyage.</summary>
    public static async Task LanternPlaced(Player player, int placed)
    {
        if (Tutorial(player))
        {
            switch (placed)
            {
                case 1:
                    await Say(player, Data.TutLanternPlaced, "Val_Wild_Tut_LanternPlaced");
                    await Say(player, Data.TutLanternLanded, "Val_Wild_Tut_LanternLanded", delayMs: 1800);
                    break;
                case 2:
                    await Say(player, Data.TutSecondLanternLanded, "Val_Wild_Tut_SecondLanternLanded", delayMs: 1200);
                    break;
                case 3:
                    await Say(player, Data.TutThirdLanternLanded, "Val_Wild_Tut_ThirdLanternLanded", delayMs: 1200);
                    break;
            }
            return;
        }
        if (placed == 1 || AreaRng(player).Next(4) == 0)
            await Say(player, Pick(player, Data.PlaceLantern), "Val_Wild_PlaceLantern");
    }

    /// <summary>Valerie's warning when the Chart's lanterns start collapsing.</summary>
    public static Task LanternsShrinking(Player player) => Tutorial(player)
        ? Say(player, Data.TutLanternsShrinking, "Val_Wild_Tut_LanternsShrinking")
        : Say(player, Pick(player, Data.LanternsShrinking), "Val_Wild_LanternsShrinking");

    /// <summary>Valerie's first-time warning when stepping out of lantern light.</summary>
    public static Task FirstDrowning(Player player) =>
        Say(player, Data.EnterDrowningFirst, "Val_Wild_EnterDrowningAreaFirst");

    /// <summary>Valerie's tutorial line when the player drowns.</summary>
    public static Task PlayerDrowned(Player player) => Tutorial(player)
        ? Say(player, Data.TutPlayerDrowns, "Val_Wild_Tut_PlayerDrowns")
        : Task.CompletedTask;
}
