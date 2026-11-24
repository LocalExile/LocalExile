public static partial class Game
{
    // Placement and expiry update the area list. Contacts only inspect owners with a live source.
    static readonly RuleTable<GameArea, Player[]> RejuvenationCasters = new();
    static readonly RuleTable<Player, AuraPotency> RejuvenationPotencies = new();

    sealed record AuraPotency(int Percent);

    static (Player Owner, TotemState.Totem Totem)? RejuvenationTotemAt(Player recipient, (int X, int Y) at,
        bool preferRegeneration = false)
    {
        if (recipient.Frames.Area is not { } area || !RejuvenationCasters.TryGetValue(area, out var casters)) return null;
        (Player Owner, TotemState.Totem Totem)? source = null;
        double strongest = double.NegativeInfinity;
        double distance = double.PositiveInfinity;
        void Consider(Player owner)
        {
            if (owner.Closed || owner.LeavingArea || owner.PlayerLife == 0
                || owner.Frames.Area != area || owner.Totems.All.Count == 0) return;
            double now = AreaTime(owner);
            foreach (var totem in owner.Totems.All)
            {
                if (totem.Aura is not { HitRedirectPercent: > 0 } aura || !totem.Alive(now)) continue;
                double reach = Dist(totem.At, at);
                if (reach > aura.Radius) continue;
                double strength = preferRegeneration ? LifeRegenerationRate(owner, aura) : aura.HitRedirectPercent;
                if (strength < strongest || strength == strongest && reach >= distance) continue;
                source = (owner, totem);
                strongest = strength;
                distance = reach;
            }
        }
        foreach (var owner in casters) Consider(owner);
        return source;
    }

    static void SyncRejuvenationAllyZones(Player owner, double now, bool leaving = false)
    {
        if (owner.Frames.Area is not { } area) return;
        var casters = RejuvenationCasters.TryGetValue(area, out var known) ? known : [];
        bool registered = Array.IndexOf(casters, owner) >= 0;
        if (!registered && (leaving || owner.Totems.All.Count == 0)) return;
        TotemState.Totem[] live = leaving ? [] : owner.Totems.Living(now)
            .Where(t => t.Aura is { HitRedirectPercent: > 0 }).ToArray();
        if (live.Length == 0)
        {
            if (!registered) return;
            if (casters.Length == 1) RejuvenationCasters.Remove(area);
            else RejuvenationCasters.AddOrUpdate(area, casters.Where(p => !ReferenceEquals(p, owner)).ToArray());
            RejuvenationPotencies.Remove(owner);
        }
        else
        {
            if (!registered) RejuvenationCasters.AddOrUpdate(area, [.. casters, owner]);
            int potency = owner.Pools.AuraEffectPct;
            if (!RejuvenationPotencies.TryGetValue(owner, out var previous) || potency != previous.Percent)
            {
                RejuvenationPotencies.AddOrUpdate(owner, new(potency));
                MarkPlayerStats(owner, PlayerStatStep.TotemAura);
                foreach (var totem in live)
                    foreach (var id in ZoneMembers(owner, owner.Id, ZoneKind.RejuvenationTotemAllies, totem.Id))
                        MarkAlly(id);
            }
        }

        SyncZones(owner, owner.Id, ZoneKind.RejuvenationTotemAllies,
            live.Select(t => (t.Id, t.Aura!.Radius, t.At)),
            new(ReachesPlayersBut(owner.Id), Changed: MarkAlly));

        void MarkAlly(EntityId id)
        {
            if (AreaPlayer(owner, id.Value) is not { } ally) return;
            MarkPlayerStats(ally, PlayerStatStep.TotemAura);
            PresentNearby(ally);
        }
    }

    internal static void EndRejuvenationTotemAura(Player owner)
    {
        using var frame = ProjectileMutation(owner, advance: false);
        SyncRejuvenationAllyZones(owner, AreaTime(owner), leaving: true);
    }

    static double TotemAuraStat(Player source, int value) =>
        Math.Round(value * Math.Max(0d, 100d + source.Pools.AuraEffectPct) / 100d, MidpointRounding.AwayFromZero);

    static double LifeRegenerationRate(Player owner, TotemState.Aura aura) =>
        TotemAuraStat(owner, aura.Stats.FirstOrDefault(s => s.Stat == Stat.BaseLifeRegenerationRatePerMinute).Value);

    static (Player Owner, TotemState.Totem Totem)? ReceivedTotemAura(Player player, double now)
    {
        var own = TotemAuraCovering(player, now);
        var nearby = RejuvenationTotemAt(player, player.Walk.Position(now), preferRegeneration: true);
        if (nearby is not { } external)
            return own is null ? null : (player, own);
        return own?.Aura is { } local && LifeRegenerationRate(player, local)
            >= LifeRegenerationRate(external.Owner, external.Totem.Aura!)
            ? (player, own) : external;
    }

    static double RejuvenationMinionRegeneration(Player owner, SpawnedMonster minion, double now)
    {
        return RejuvenationTotemAt(owner, minion.Walk.Position(now), preferRegeneration: true) is { } source
            ? LifeRegenerationRate(source.Owner, source.Totem.Aura!) / 60d : 0;
    }
}
