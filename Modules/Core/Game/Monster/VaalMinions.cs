using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static bool IsVaalMinion(string? effect) => effect == "VaalSummonSkeletons";

    internal static async Task<bool> ReleaseVaalMinions(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort source, (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(conn, source);
        bool Live() => current() && SameTriggerSource(gem, SupportedSkill(conn, source));
        if (!gem.RequirementsMet || SkillsFor(conn).EffectId(gem.Effect) != "VaalSummonSkeletons" || !Live()) return false;
        double duration = SkillDurationModifiers.Seconds(GolemStat(gem.Stats, "base_skill_effect_duration"),
            n => GolemStat(CharacterPlayerStats(conn), n), n => GolemStat(gem.Stats, n));
        var definition = SummonData.Find("VaalSummonSkeletons")!;
        foreach (var (stat, body, variants) in new[] { ("melee", "Melee", 2), ("archer", "Ranged", 2), ("mage", "Spellcaster", 3), ("leader", "General", 4) })
            for (int i = 0; i < Math.Clamp(GolemStat(gem.Stats, "number_of_" + stat + "_skeletons_to_summon"), 0, 200) && Live(); i++)
            {
                string path = "Metadata/Monsters/RaisedSkeletons/RaisedSkeleton" + body + Random.Shared.Next(1, variants + 1) + "Army";
                if (await SummonFighter(remote, stream, ciphers, conn, source, definition, aim, restoredVariety: path,
                    current: Live, remaining: duration, triggered: true) == 0) return false;
            }
        return Live();
    }
}
