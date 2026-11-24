using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static ChargeType? SpectreChargeRoar(GolemState.Golem minion, int effect) =>
        minion.Definition.Kind != "Spectre" ? null : effect switch
        {
            273 => ChargeType.Endurance,
            1598 => ChargeType.Frenzy,
            1607 => ChargeType.Power,
            _ => null
        };

    static void ReleaseSpectreChargeRoarLocked(Player player, (int X, int Y) at, ChargeType type,
        GolemState.Ability ability, double now)
    {
        int amount = type == ChargeType.Endurance
            ? GolemStat(ability.Stats, Stat.EnduringCryGrantsXAdditionalEnduranceCharges) : 1;
        if (amount <= 0) return;
        GrantNearbyChargesLocked(player, at, now, ability.Radius, new(type, amount, ability.Id), includeOwner: true);
    }

    static void GrantNearbyChargesLocked(Player player, (int X, int Y) at, double now, double radius,
        ChargeEvent.Grant grant, bool includeOwner, uint exceptFighter = 0)
    {
        void Grant(Player recipient, (int X, int Y) playerAt, double recipientTime)
        {
            if ((includeOwner || !ReferenceEquals(player, recipient)) && recipient.PlayerLife > 0 && Dist(at, playerAt) <= radius)
                ChargesOf(recipient).Enqueue(grant);
            foreach (var fighter in UtilityFighters(recipient))
                if (fighter.Id != exceptFighter && GolemCurrent(recipient, fighter)
                    && Dist(at, fighter.Monster.Walk.Position(recipientTime)) <= radius)
                    GrantMinionChargeLocked(recipient, fighter, grant.Type, grant.Count, recipientTime);
        }

        Grant(player, player.Walk.Position(now), now);
        foreach (var recipient in player.Instance.PlayersExcept(player.Id))
            if (!recipient.Closed && !recipient.LeavingArea
                && !ReferenceEquals(player, recipient) && ReferenceEquals(player.Instance, recipient.Instance))
                Grant(recipient, recipient.Walk.Position(AreaTime(recipient)), AreaTime(recipient));
    }

    // The native on-hit/on-kill modifier rows describe allies within four metres.
    const int NearbyChargeRadius = 40;

    static void PlayerGrantsNearbyChargesLocked(Player player, bool kill, double now)
    {
        var stats = StatSourceValues(player);
        uint first = kill ? Stat.ChanceToGrantPowerChargeToNearbyAlliesOnKillPct : Stat.ChanceToGrantFrenzyChargeToNearbyAlliesOnHitPct;
        uint second = kill ? Stat.ChanceToGrantFrenzyChargeToNearbyAlliesOnKillPct : Stat.ChanceToGrantEnduranceChargeToNearbyAlliesOnHitPct;
        void TryGrant(uint key, ChargeType type)
        {
            if (GameServer.Core.World.RandomRolls.Chance(GolemStat(stats, key), 100, AreaRng(player)))
                GrantNearbyChargesLocked(player, player.Walk.Position(now), now, NearbyChargeRadius,
                    new(type, 1, "nearby allies"), includeOwner: false);
        }
        TryGrant(first, kill ? ChargeType.Power : ChargeType.Frenzy);
        TryGrant(second, kill ? ChargeType.Frenzy : ChargeType.Endurance);
    }

    static void MinionGrantsNearbyChargesOnHitLocked(Player player, GolemState.Golem fighter,
        (int X, int Y) at, double now, Random random)
    {
        int chance = fighter.Monster.Effects[Stat.ChanceToGrantFrenzyChargeToNearbyAlliesOnHitPct];
        if (GameServer.Core.World.RandomRolls.Chance(chance, 100, random))
            GrantNearbyChargesLocked(player, at, now, NearbyChargeRadius, new(ChargeType.Frenzy, 1, "minion hit"),
                includeOwner: true, exceptFighter: fighter.Id);
    }

    static void GrantMinionChargeLocked(Player owner, GolemState.Golem fighter, ChargeType type, int amount, double now)
    {
        ExpireMinionChargesLocked(owner, fighter, now);
        var charges = fighter.CombatCharges ??= new();
        var limits = Charges.LimitsOf(type, name => fighter.Monster.Effects[name], charges.Held(type));
        var (_, count) = charges.Gain(type, amount, now, limits);
        if (count == 0) return;
        PublishMinionChargeLocked(owner, fighter, type, count, charges.ExpiresAt(type), now);
    }

    static void ExpireMinionChargesLocked(Player owner, GolemState.Golem fighter, double now)
    {
        if (fighter.CombatCharges is not { } charges) return;
        bool Due(ChargeType type) => charges.Held(type) > 0 && now >= charges.ExpiresAt(type);
        if (!Due(ChargeType.Endurance) && !Due(ChargeType.Frenzy) && !Due(ChargeType.Power)) return;
        foreach (var expired in charges.Expire(now))
            PublishMinionChargeLocked(owner, fighter, expired, 0, 0, now);
        if (charges.Held(ChargeType.Endurance) == 0 && charges.Held(ChargeType.Frenzy) == 0 && charges.Held(ChargeType.Power) == 0)
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
