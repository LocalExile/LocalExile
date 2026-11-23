using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    internal const uint SpectreSoulEaterChance = Stat.SpectresGainSoulEaterFor20SecondsOnKillPctChance;
    const ushort SpectreSoulEaterBuffRow = 278;
    const double SpectreSoulEaterSeconds = 20;
    static readonly RuleTable<Player, HashSet<GolemState.Golem>> ActiveSpectreSoulEaters = new();

    internal static bool StartSpectreSoulEaterLocked(Player player, GolemState.Golem spectre, double now, Random random)
    {
        if (spectre.Definition.Kind != "Spectre" || spectre.SoulEaterUntil > now || spectre.Monster.Life == 0
            || !GolemCurrent(player, spectre) || !RandomRolls.Chance(GolemStat(spectre.Player, SpectreSoulEaterChance), 100, random))
            return false;
        // The timed source is not refreshed by further kills. The activation kill has already passed through feeding.
        PublishSpectreSoulEaterLocked(player, spectre, now);
        spectre.SoulEater = new();
        spectre.SoulEaterUntil = now + SpectreSoulEaterSeconds;
        PublishSpectreSoulEaterLocked(player, spectre, now);
        return true;
    }

    static void FeedSpectreSoulEaterLocked(Player player, uint id, (int X, int Y) at, double now)
    {
        if (!ActiveSpectreSoulEaters.TryGetValue(player, out var eaters)) return;
        if (player.Area.Monsters.TryGetValue(id, out var victim) && victim.Effects[Stat.MonsterDoNotGiveSoulToSoulEater] != 0)
            return;
        eaters.RemoveWhere(g => g.SoulEaterUntil <= now || g.Monster.Life == 0 || !GolemCurrent(player, g));
        if (eaters.Count == 0) { ActiveSpectreSoulEaters.Remove(player); return; }
        GolemState.Golem? nearest = null;
        double minimum = SoulEaterRadius;
        foreach (var eater in eaters)
        {
            double distance = Dist(at, eater.Monster.Walk.Position(now));
            if (distance > minimum || nearest is not null && distance == minimum && nearest.Id < eater.Id) continue;
            minimum = distance;
            nearest = eater;
        }
        nearest?.SoulEater?.Soul(now);
    }

    internal static int SpectreSoulEaterCount(GolemState.Golem spectre, double now) =>
        now < spectre.SoulEaterUntil ? spectre.SoulEater?.Stacks(now) ?? 0 : 0;

    static void PublishSpectreSoulEaterLocked(Player player, GolemState.Golem spectre, double now)
    {
        if (spectre.SoulEater is not { } state) return;
        if (now >= spectre.SoulEaterUntil)
        {
            if (ActiveSpectreSoulEaters.TryGetValue(player, out var eaters) && eaters.Remove(spectre) && eaters.Count == 0)
                ActiveSpectreSoulEaters.Remove(player);
            player.Area.Buffs.Clear(spectre.Id, SoulEaterAuraRow, spectre.Id, auraSource: true);
            player.Area.Buffs.ClearRow(spectre.Id, SpectreSoulEaterBuffRow);
            SetStats(player, spectre.Id, [(Stat.CappedActorScalePlusPct, 0)]);
            spectre.SoulEater = null;
            return;
        }
        var active = ActiveSpectreSoulEaters.GetOrCreateValue(player);
        if (active.Add(spectre))
            player.Area.Buffs.Set(spectre.Id, new(SoulEaterAuraRow, spectre.Id, AuraSource: true, DisplayState: 0));
        int stacks = state.Stacks(now);
        if (stacks == state.SentStacks) return;
        if (stacks == 0) player.Area.Buffs.ClearRow(spectre.Id, SpectreSoulEaterBuffRow);
        else player.Area.Buffs.Set(spectre.Id, new(SpectreSoulEaterBuffRow, spectre.Id, Stacks: (ushort)stacks));
        SetStats(player, spectre.Id, [(Stat.CappedActorScalePlusPct, stacks),
            (Stat.VirtualSoulEaterMaximumStacks, SoulEaterState.MaximumStacks)]);
        state.SentStacks = stacks;
    }
}
