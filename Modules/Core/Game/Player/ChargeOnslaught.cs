using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string MaximumEnduranceOnslaught = "gain_onslaught_while_at_maximum_endurance_charges";
    static readonly (string Stat, string Condition)[] SourceOnslaughtPredicates =
    [
        (MaximumEnduranceOnslaught, "_while_at_maximum_endurance_charges"),
        ("gain_onslaught_while_on_low_life", "_while_on_low_life"),
        ("gain_onslaught_while_not_on_low_mana", "_while_not_on_low_mana"),
        ("gain_onslaught_while_you_have_fortify", "_while_you_have_fortify"),
    ];

    static IReadOnlyDictionary<string, bool> RequalifyChargeOnslaught(ClientState conn,
        IReadOnlyDictionary<string, bool> captured, IReadOnlyDictionary<uint, int> sources,
        IReadOnlyDictionary<uint, int> overrides)
    {
        int Get(string name) => Stats.Key(name) is uint key
            ? overrides.TryGetValue(key, out int replacement) ? replacement : sources.GetValueOrDefault(key) : 0;
        bool source = CurrentGameWork && !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0
            && SourceOnslaughtPredicates.Any(row => Get(row.Stat) != 0 && captured.GetValueOrDefault(row.Condition));
        bool onslaught = source || captured.GetValueOrDefault("independent_onslaught");
        return new Dictionary<string, bool>(captured)
        {
            ["source_onslaught"] = source,
            ["_while_have_onslaught"] = onslaught,
            ["_while_onslaught_is_active"] = onslaught,
        };
    }

    public static bool ChargeOnslaughtActive(ClientState conn) => CurrentGameWork && !conn.Closed
        && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0
        && conn.StatSourcesCache?.Key.Flags?.GetValueOrDefault("source_onslaught") == true;

    public static IEnumerable<UtilityFlasks.Source> ChargeOnslaughtSources(ClientState conn, Func<string, int> character) =>
        ChargeOnslaughtActive(conn) && ItemsFor(conn).Onslaught is { } onslaught
            ? UtilityFlasks.OnslaughtSources(onslaught, Math.Max(0, 1 + character("onslaught_effect_+%") / 100d)) : [];

    sealed class ChargeOnslaughtVisual { public ushort Buff; public bool Active; }
    static readonly RuleTable<ClientState, ChargeOnslaughtVisual> ChargeOnslaughtVisuals = new();

    // A permanent source has no countdown. Its ordinary buff is hidden while another Onslaught owner shows one;
    // the shared utility-source combiner still takes the strongest effect once.
    public static byte[]? ChargeOnslaughtPresentation(ClientState conn)
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
                bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                bytes.AddRange(PlayerSkillsIfChanged(conn, PlayerSkills(conn)));
            }
            return bytes.Count > 0 ? [.. bytes] : null;
        }
    }
}
