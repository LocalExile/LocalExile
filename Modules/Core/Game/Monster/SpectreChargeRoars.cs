using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static ChargeType? SpectreChargeRoar(GolemState.Golem minion, int effect) =>
        minion.Definition.Kind != "Spectre" ? null : effect switch
        {
            1598 => ChargeType.Frenzy,
            1607 => ChargeType.Power,
            _ => null
        };

    static void ReleaseSpectreChargeRoarLocked(Player player, (int X, int Y) at, ChargeType type,
        double radius, double now, string effect)
    {
        var grant = new ChargeEvent.Grant(type, 1, effect);
        void Grant(Player recipient, (int X, int Y) playerAt, double recipientTime)
        {
            if (recipient.PlayerLife > 0 && Dist(at, playerAt) <= radius)
                ChargesOf(recipient).Enqueue(grant);
            foreach (var fighter in UtilityFighters(recipient))
                if (Dist(at, fighter.Monster.Walk.Position(now)) <= radius)
                    GrantMinionChargeLocked(recipient, fighter, type, recipientTime);
        }

        Grant(player, player.Walk.Position(now), now);
        foreach (var recipient in player.Instance.PlayersExcept(player.Id))
            if (!recipient.Closed && !recipient.LeavingArea
                && !ReferenceEquals(player, recipient) && ReferenceEquals(player.Instance, recipient.Instance))
                Grant(recipient, recipient.Walk.Position(AreaTime(recipient)), AreaTime(recipient));
    }

    static void GrantMinionChargeLocked(Player owner, GolemState.Golem fighter, ChargeType type, double now)
    {
        ExpireMinionChargesLocked(owner, fighter, now);
        var charges = fighter.CombatCharges ??= new();
        var limits = Charges.LimitsOf(type, name => fighter.Monster.Effects[name], charges.Held(type));
        var (_, count) = charges.Gain(type, 1, now, limits);
        if (count == 0) return;
        PublishMinionChargeLocked(owner, fighter, type, count, charges.ExpiresAt(type), now);
    }

    static void ExpireMinionChargesLocked(Player owner, GolemState.Golem fighter, double now)
    {
        if (fighter.CombatCharges is not { } charges) return;
        bool Due(ChargeType type) => charges.Held(type) > 0 && now >= charges.ExpiresAt(type);
        if (!Due(ChargeType.Frenzy) && !Due(ChargeType.Power)) return;
        foreach (var expired in charges.Expire(now))
            PublishMinionChargeLocked(owner, fighter, expired, 0, 0, now);
        if (charges.Held(ChargeType.Frenzy) == 0 && charges.Held(ChargeType.Power) == 0)
            fighter.CombatCharges = null;
    }

    static void PublishMinionChargeLocked(Player player, GolemState.Golem fighter, ChargeType type,
        int count, double until, double now)
    {
        var shown = fighter.CombatCharges!.Presented;
        if (shown[(int)type].Buff is ushort old and not 0)
            QueueGolemDotPacket(player, fighter, InstanceEntities.RemoveBuff(fighter.Id, old));
        if (count == 0)
        {
            shown[(int)type] = default;
            return;
        }
        ushort buff = player.NextBuff++;
        shown[(int)type] = new(count, until, buff);
        QueueGolemDotPacket(player, fighter, InstanceEntities.AddBuff(fighter.Id, buff, ChargeBuffs.Row(type), fighter.Id,
            u68: Buff.Seconds(Math.Max(0, until - now)), stacks: (ushort)Math.Clamp(count, 0, ushort.MaxValue)));
    }
}
