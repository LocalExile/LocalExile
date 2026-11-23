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

    static IReadOnlyDictionary<string, bool> RequalifyChargeOnslaught(Player player,
        IReadOnlyDictionary<string, bool> captured, IReadOnlyDictionary<uint, int> sources)
    {
        int Get(uint key) => sources.GetValueOrDefault(key);
        bool source = CurrentGameWork && !player.Closed && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0
            && SourceOnslaughtPredicates.Any(row => Get(row.Stat) != 0 && captured.GetValueOrDefault(row.Condition));
        bool onslaught = source || captured.GetValueOrDefault("independent_onslaught");
        return WithFlags(captured, ("source_onslaught", source), ("_while_have_onslaught", onslaught), ("_while_onslaught_is_active", onslaught));
    }

    public static bool ChargeOnslaughtActive(Player player) => CurrentGameWork && !player.Closed
        && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0
        && player.StatSourcesCache?.Key.Flags?.GetValueOrDefault("source_onslaught") == true;

    public static IEnumerable<UtilityFlasks.Source> ChargeOnslaughtSources(Player player, Func<uint, int> character) =>
        ChargeOnslaughtActive(player) && ItemsFor(player).Onslaught is { } onslaught
            ? UtilityFlasks.OnslaughtSources(onslaught, Math.Max(0, 1 + character(Stat.OnslaughtEffectPlusPct) / 100d)) : [];

    sealed class ChargeOnslaughtVisual { public bool Active; }
    static readonly Store<Player, ChargeOnslaughtVisual> ChargeOnslaughtVisuals = new();

    // A permanent source has no countdown. Its ordinary buff is hidden while another Onslaught owner shows one;
    // the shared utility-source combiner still takes the strongest effect once.
    public static byte[]? ChargeOnslaughtPresentation(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired) return null;
        EnsureConditionalItemPools(player);
        bool active = ChargeOnslaughtActive(player);
        if (!active && !ChargeOnslaughtVisuals.TryGetValue(player, out _)) return null;
        var visual = ChargeOnslaughtVisuals.GetValue(player, _ => new());
        bool show = active && !HasIndependentOnslaught(player) && ItemsFor(player).Onslaught is not null;
        bool changed = active != visual.Active;
        var bytes = new List<byte>();
        if (ItemsFor(player).Onslaught is { } onslaught)
        {
            if (show) player.Area.Buffs.Keep(player.Id, new(onslaught.BuffRow, player.Id, Tag: (ushort)BuffTag.ChargeOnslaught));
            else player.Area.Buffs.Clear(player.Id, onslaught.BuffRow, player.Id, tag: (ushort)BuffTag.ChargeOnslaught);
        }
        visual.Active = active;
        if (changed)
        {
            bytes.AddRange(PlayerStatsOffFrames(player));
            bytes.AddRange(PlayerSkillsIfChanged(player, PlayerSkills(player)));
        }
        return bytes.Count > 0 ? [.. bytes] : null;
    }
}
