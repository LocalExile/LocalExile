using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    internal const string SpectreSoulEaterChance = "spectres_gain_soul_eater_for_20_seconds_on_kill_%_chance";
    const ushort SpectreSoulEaterBuffRow = 278;
    const double SpectreSoulEaterSeconds = 20;
    static readonly RuleTable<ClientState, HashSet<GolemState.Golem>> ActiveSpectreSoulEaters = new();

    internal static bool StartSpectreSoulEaterLocked(ClientState conn, GolemState.Golem spectre, double now, Random random)
    {
        if (spectre.Definition.Kind != "Spectre" || spectre.SoulEaterUntil > now || spectre.Monster.Life == 0
            || !GolemCurrent(conn, spectre) || !RandomRolls.Chance(GolemStat(spectre.Player, SpectreSoulEaterChance), 100, random))
            return false;
        // The timed source is not refreshed by further kills. The activation kill has already passed through feeding.
        PublishSpectreSoulEaterLocked(conn, spectre, now);
        spectre.SoulEater = new();
        spectre.SoulEaterUntil = now + SpectreSoulEaterSeconds;
        PublishSpectreSoulEaterLocked(conn, spectre, now);
        return true;
    }

    static void FeedSpectreSoulEaterLocked(ClientState conn, uint id, (int X, int Y) at, double now)
    {
        if (!ActiveSpectreSoulEaters.TryGetValue(conn, out var eaters)) return;
        if (conn.Monsters.TryGetValue(id, out var victim) && victim.Effects["monster_do_not_give_soul_to_soul_eater"] != 0)
            return;
        eaters.RemoveWhere(g => g.SoulEaterUntil <= now || g.Monster.Life == 0 || !GolemCurrent(conn, g));
        if (eaters.Count == 0) { ActiveSpectreSoulEaters.Remove(conn); return; }
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

    static void PublishSpectreSoulEaterLocked(ClientState conn, GolemState.Golem spectre, double now)
    {
        if (spectre.SoulEater is not { } state) return;
        void Queue(byte[] bytes) => conn.Minions.OutputQueue.Add(new(bytes, null, spectre.Id, spectre.Monster, WorldId: spectre.Id));
        if (now >= spectre.SoulEaterUntil)
        {
            if (ActiveSpectreSoulEaters.TryGetValue(conn, out var eaters) && eaters.Remove(spectre) && eaters.Count == 0)
                ActiveSpectreSoulEaters.Remove(conn);
            if (state.AuraBuff != 0) Queue(InstanceEntities.RemoveBuff(spectre.Id, state.AuraBuff));
            if (state.Buff != 0) Queue(InstanceEntities.RemoveBuff(spectre.Id, state.Buff));
            if (state.SentStacks != 0) Queue(InstanceEntities.StatsUpdate(spectre.Id, SoulEaterActorStats(0)));
            spectre.SoulEater = null;
            return;
        }
        var active = ActiveSpectreSoulEaters.GetOrCreateValue(conn);
        active.Add(spectre);
        if (state.AuraBuff == 0)
        {
            state.AuraBuff = conn.NextBuff++;
            Queue(InstanceEntities.AddBuff(spectre.Id, state.AuraBuff, SoulEaterAuraRow, spectre.Id, auraSource: true, displayState: 0));
        }
        int stacks = state.Stacks(now);
        if (stacks == state.SentStacks) return;
        if (stacks == 0)
        {
            if (state.Buff != 0) Queue(InstanceEntities.RemoveBuff(spectre.Id, state.Buff));
            state.Buff = 0;
        }
        else
        {
            if (state.Buff == 0) state.Buff = conn.NextBuff++;
            Queue(InstanceEntities.AddBuff(spectre.Id, state.Buff, SpectreSoulEaterBuffRow, spectre.Id, stacks: (ushort)stacks));
        }
        Queue(InstanceEntities.StatsUpdate(spectre.Id, SoulEaterActorStats(stacks)));
        state.SentStacks = stacks;
    }

    static IReadOnlyList<(uint Key, int Value)> SoulEaterActorStats(int stacks) =>
        [(Stats.Key("capped_actor_scale_+%")!.Value, stacks), (Stats.Key("virtual_soul_eater_maximum_stacks")!.Value, SoulEaterState.MaximumStacks)];
}
