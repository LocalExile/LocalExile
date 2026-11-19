using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const uint MaximumEnduranceOnslaught = Stat.GainOnslaughtWhileAtMaximumEnduranceCharges;
    static readonly (uint Stat, string Condition)[] SourceOnslaughtPredicates =
    [
        (MaximumEnduranceOnslaught, "_while_at_maximum_endurance_charges"),
        (Stat.GainOnslaughtWhileOnLowLife, "_while_on_low_life"),
        (Stat.GainOnslaughtWhileNotOnLowMana, "_while_not_on_low_mana"),
        (Stat.GainOnslaughtWhileYouHaveFortify, "_while_you_have_fortify"),
    ];

    static IReadOnlyDictionary<string, bool> RequalifyChargeOnslaught(Player conn,
        IReadOnlyDictionary<string, bool> captured, IReadOnlyDictionary<uint, int> sources)
    {
        int Get(uint key) => sources.GetValueOrDefault(key);
        bool source = CurrentGameWork && !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0
            && SourceOnslaughtPredicates.Any(row => Get(row.Stat) != 0 && captured.GetValueOrDefault(row.Condition));
        bool onslaught = source || captured.GetValueOrDefault("independent_onslaught");
        return WithFlags(captured, ("source_onslaught", source), ("_while_have_onslaught", onslaught), ("_while_onslaught_is_active", onslaught));
    }

    public static bool ChargeOnslaughtActive(Player conn) => CurrentGameWork && !conn.Closed
        && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0
        && conn.StatSourcesCache?.Key.Flags?.GetValueOrDefault("source_onslaught") == true;

    public static IEnumerable<UtilityFlasks.Source> ChargeOnslaughtSources(Player conn, Func<uint, int> character) =>
        ChargeOnslaughtActive(conn) && ItemsFor(conn).Onslaught is { } onslaught
            ? UtilityFlasks.OnslaughtSources(onslaught, Math.Max(0, 1 + character(Stat.OnslaughtEffectPlusPct) / 100d)) : [];

    sealed class ChargeOnslaughtVisual { public ushort Buff; public bool Active; }
    static readonly Store<Player, ChargeOnslaughtVisual> ChargeOnslaughtVisuals = new();

    // A permanent source has no countdown. Its ordinary buff is hidden while another Onslaught owner shows one;
    // the shared utility-source combiner still takes the strongest effect once.
    public static byte[]? ChargeOnslaughtPresentation(Player conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
            EnsureConditionalItemPools(conn);
            bool active = ChargeOnslaughtActive(conn);
            if (!active && !ChargeOnslaughtVisuals.TryGetValue(conn, out _)) return null;
            var visual = ChargeOnslaughtVisuals.GetValue(conn, _ => new());
            bool show = active && !HasIndependentOnslaught(conn) && ItemsFor(conn).Onslaught is not null;
            bool changed = active != visual.Active;
            var bytes = new List<byte>();
            if (visual.Buff != 0 && !show)
            {
                bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, visual.Buff));
                visual.Buff = 0;
            }
            if (show && visual.Buff == 0)
                bytes.AddRange(InstanceEntities.AddBuff(PlayerId, visual.Buff = conn.NextBuff++,
                    ItemsFor(conn).Onslaught!.BuffRow, PlayerId));
            visual.Active = active;
            if (changed)
            {
                bytes.AddRange(PlayerStatsOffFrames(conn));
                bytes.AddRange(PlayerSkillsIfChanged(conn, PlayerSkills(conn)));
            }
            return bytes.Count > 0 ? [.. bytes] : null;
        }
    }
}
