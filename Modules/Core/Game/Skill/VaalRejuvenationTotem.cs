public static partial class Game
{
    static (Player Owner, TotemState.Totem Totem)? RejuvenationTotemAt(Player recipient, (int X, int Y) at,
        bool preferRegeneration = false)
    {
        (Player Owner, TotemState.Totem Totem)? source = null;
        double strongest = double.NegativeInfinity;
        double distance = double.PositiveInfinity;
        void Consider(Player owner)
        {
            if (owner.Closed || owner.LeavingArea || owner.PlayerLife == 0
                || owner.Area != recipient.Area || owner.Totems.All.Count == 0) return;
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
        Consider(recipient);
        foreach (var ally in recipient.Instance.PlayersExcept(recipient.Id)) Consider(ally);
        return source;
    }

    static void SyncRejuvenationAllyZones(Player owner, double now) =>
        SyncZones(owner, owner.Id, ZoneKind.RejuvenationTotemAllies,
            owner.Totems.Living(now).Where(t => t.Aura is { HitRedirectPercent: > 0 })
                .Select(t => (t.Id, t.Aura!.Radius, t.At)),
            new(ReachesAlliesBut(owner, owner.Id), Changed: id =>
            {
                if (AreaPlayer(owner, id.Value) is not { } ally) return;
                MarkPlayerStats(ally, PlayerStatStep.TotemAura);
                PresentNearby(ally);
            }));

    static double TotemAuraStat(Player source, int value) =>
        Math.Round(value * Math.Max(0d, 100d + source.Pools.AuraEffectPct) / 100d, MidpointRounding.AwayFromZero);

    static double LifeRegenerationRate(Player owner, TotemState.Aura aura) =>
        TotemAuraStat(owner, aura.Stats.FirstOrDefault(s => s.Stat == Stat.BaseLifeRegenerationRatePerMinute).Value);

    static (Player Owner, TotemState.Totem Totem)? ReceivedTotemAura(Player player, double now)
    {
        var own = TotemAuraCovering(player, now);
        var nearby = RejuvenationTotemAt(player, player.Walk.Position(now), preferRegeneration: true);
        if (nearby is not { } external || external.Owner == player)
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
