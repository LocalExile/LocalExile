using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // The Harbinger uniques ("Grants Summon Harbinger of ... Skill"). In the game the skill summons a Harbinger that
    // cannot be hurt and uses its own skill on a cooldown: a buff on its owner, or for Storms arcs that chill and shock
    // enemies. Here the Harbinger's skill is used for the wearer on that cooldown while the item is worn; the minion
    // itself is not made, and the hand-cast skill does not have to be used. Every number is the minion skill's own
    // (GrantedEffects HarbingerArcaneSurge ... HarbingerDamageBuffUber: duration, cooldown, values).
    sealed record HarbingerKind(string Effect, double Every, double Seconds, string? Buff, int[][] Values)
    {
        public bool ArcaneSurge { get; init; }
        public (string Buff, int[] Values)? Also { get; init; }
        /// <summary>Storms: how many enemies each arc chains to, and the arcs' "more effect" in percent.</summary>
        public int Arcs { get; init; }
        public int ArcEffectPct { get; init; }
    }

    static readonly HarbingerKind[] HarbingerKinds =
    [
        new("SummonHarbingerOfTheArcane", 8, 4, null, []) { ArcaneSurge = true },
        new("SummonHarbingerOfTime", 20, 8, "harbinger_action_speed_aura", [[-10]]),
        new("SummonHarbingerOfFocus", 8, 4, "harbinger_channel_buff", [[1, 1, -20]]),
        // One of five at random each time: a projectile, a chain, a pierce, fork, or all of them.
        new("SummonHarbingerOfDirections", 4, 4, "harbinger_projectile_buff", [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1], [1, 1, 1, 1]]),
        new("SummonHarbingerOfStorms", 4, 0, null, []) { Arcs = 5 },
        new("SummonHarbingerOfBrutality", 20, 8, "harbinger_damage_buff", [[50, -15, 30, 30]]),
        new("SummonHarbingerOfTheArcaneUber", 8, 4, null, []) { ArcaneSurge = true, Also = ("harbinger_lightning_buff_uber", [10, 0]) },
        new("SummonHarbingerOfTimeUber", 10, 8, "harbinger_action_speed_aura_uber", [[-10]]),
        new("SummonHarbingerOfFocusUber", 8, 4, "harbinger_channel_buff_uber", [[1, 1, 1, -20]]),
        new("SummonHarbingerOfDirectionsUber", 4, 4, "harbinger_projectile_buff_uber",
            [[2, 0, 0, 0, 0], [0, 2, 0, 0, 0], [0, 0, 2, 0, 0], [0, 0, 0, 1, 1], [2, 2, 2, 1, 1]]),
        new("SummonHarbingerOfStormsUber", 4, 0, null, []) { Arcs = 10, ArcEffectPct = 40 },
        new("SummonHarbingerOfBrutalityUber", 20, 8, "harbinger_damage_buff_uber", [[50, -15, 30, 30, 10]]),
    ];

    sealed class HarbingerState
    {
        public object? CheckedSources;
        public long CheckedMutations = -1;
        public HarbingerKind? Kind;
        public double NextAt;
        public BuffSpec[]? Specs;
        public BuffSpec? Also;
    }

    static readonly ConditionalWeakTable<Player, HarbingerState> HarbingerStates = new();
    // How far the Storms arcs look for enemies, in world units, and how long their chill and shock last. Neither is
    // stated by the skill's data beyond "100% more duration": a best guess.
    const int HarbingerArcReach = 60;
    const double HarbingerArcSeconds = 4;

    /// <summary>A stat presentation: uses the worn Harbinger's skill when its cooldown is over, and asks to be called
    /// again when the next use is due. One Harbinger at a time (the skill's "one Harbinger allowed").</summary>
    static Task SendHarbingers(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return Task.CompletedTask;
        var state = HarbingerStates.GetOrCreateValue(player);
        var items = ItemsOf(player);
        if (!ReferenceEquals(state.CheckedSources, player.StatSourcesCache) || state.CheckedMutations != items.Mutations)
        {
            state.CheckedSources = player.StatSourcesCache;
            state.CheckedMutations = items.Mutations;
            var grants = AllWornGrantedSkillsOf(player, items.EquipmentSnapshot());
            HarbingerKind? worn = null;
            foreach (var kind in HarbingerKinds)
                if (grants.Any(g => g.EffectId == kind.Effect && !IsInactive(player, g.Host))) { worn = kind; break; }
            if (!ReferenceEquals(worn, state.Kind))
            {
                state.Kind = worn;
                state.Specs = worn?.Buff is { } buff ? [.. worn.Values.Select(v => BuffWithValues(buff, v))] : null;
                state.Also = worn?.Also is var (also, values) ? BuffWithValues(also, values) : null;
                state.NextAt = 0;
            }
        }
        if (state.Kind is not { } harbinger || player.PlayerLife == 0 || InTown(player)) return Task.CompletedTask;
        double now = AreaTime(player);
        if (now >= state.NextAt)
        {
            state.NextAt = now + harbinger.Every;
            if (harbinger.ArcaneSurge) GainArcaneSurgeLocked(player, now, 4000, 20, 58);
            if (state.Also is { } second) GainTimedBuffLocked(player, second, harbinger.Seconds, 1, now);
            if (state.Specs is { Length: > 0 } specs)
                GainTimedBuffLocked(player, specs[specs.Length == 1 ? 0 : AreaRng(player).Next(specs.Length)], harbinger.Seconds, 1, now);
            if (harbinger.Arcs > 0) HarbingerArcsLocked(player, harbinger, now);
        }
        PresentIn(player, state.NextAt - now + 1e-3);
        return Task.CompletedTask;
    }

    // Storms: a chill arc and a shock arc, each to the nearest enemies, with no damage behind them (the ailments' own
    // 10% and 15%, 40% more for the greater Harbinger).
    static void HarbingerArcsLocked(Player player, HarbingerKind harbinger, double now)
    {
        var here = player.Walk.Position(now);
        var near = new List<(long Distance, SpawnedMonster Monster)>();
        foreach (var monster in player.Area.Monsters.Values)
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0) continue;
            var at = monster.Walk.Position(now);
            long dx = at.X - here.X, dy = at.Y - here.Y, distance = dx * dx + dy * dy;
            if (distance <= (long)HarbingerArcReach * HarbingerArcReach) near.Add((distance, monster));
        }
        if (near.Count == 0) return;
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        int chill = 10 * (100 + harbinger.ArcEffectPct) / 100, shock = 15 * (100 + harbinger.ArcEffectPct) / 100;
        for (int i = 0; i < near.Count && i < harbinger.Arcs; i++)
        {
            var monster = near[i].Monster;
            InflictedLocked(player, monster, monster.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill,
                HarbingerArcSeconds, now, _ => 0, n => monster.Mods.Effects[n], stated: chill));
            InflictedLocked(player, monster, monster.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock,
                HarbingerArcSeconds, now, _ => 0, n => monster.Mods.Effects[n], stated: shock));
        }
    }
}
