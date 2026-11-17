using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static bool IsVaalMinion(string? effect) => effect == "VaalSummonSkeletons";

    // Native supports allow these proxies, but they need their own Vaal-use admission and release budget.
    // The ordinary proxy loop must neither release free armies nor consume the owner's souls repeatedly.
    static string? VaalMinionProxyDisabled(ClientState conn, int effect, ushort? source)
    {
        if (!IsVaalMinion(SkillsFor(conn).EffectId(effect)) || source is not ushort skill) return null;
        var gem = SupportedSkill(conn, skill);
        return gem.Effect == effect && (GolemStat(gem.Stats, "is_totem") != 0 || PlacedKind(gem) is not (false, false))
            ? "Vaal Summon Skeletons through totems, traps or mines is not implemented" : null;
    }

    internal static async Task<bool> ReleaseVaalMinions(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort source, (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(conn, source);
        bool Live() => current() && SameTriggerSource(gem, SupportedSkill(conn, source));
        if (!gem.RequirementsMet || SkillsFor(conn).EffectId(gem.Effect) != "VaalSummonSkeletons"
            || VaalMinionProxyDisabled(conn, gem.Effect, source) is not null || !Live()) return false;
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
