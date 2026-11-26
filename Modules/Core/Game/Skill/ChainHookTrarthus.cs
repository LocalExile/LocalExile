using GameServer.Core.Projectiles;

public static partial class Game
{
    static readonly RuleTable<SpawnedMonster, TrarthusLink> TrarthusAttachments = new();

    internal sealed record TrarthusStrike(
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem,
        RetainedAreaAttack Attack, SkillCriticalRoll? Critical, int Radius, int Length, int Rage, ushort Buff,
        ushort Sequence, double Ruthless, bool? CryExerted, int? RallyingDamage);

    internal sealed record TrarthusLink(uint TargetId, SpawnedMonster Target, TrarthusStrike Strike);

    static bool IsTrarthusChainHook(string? effectId) => effectId is "ChainHookAltY" or "ChainHookAltMercenary";

    static void AttachTrarthusLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem source,
        (int X, int Y) aim, uint targetId, double now, AttackHand hand, SkillCatalog.Unarmed weapon, int? offCritical,
        SkillCriticalRoll? critical, ushort sequence, double radiusMore, double damageMore,
        double ruthless, bool? cryExerted, int? rallyingDamage)
    {
        var stats = SkillAreaStats(player, source.Stats, gem.Effect);
        int StatValue(uint key) => stats.GetValueOrDefault(key);
        int maximum = StatValue(Stat.ChainHookMaxAttachedTargets);
        int count = Math.Min(maximum, StatValue(Stat.ChainHookAttachesToXTargets));
        int length = StatValue(Stat.ChainHookAttachmentRange);
        int range = Math.Min(length, ChainHookRange(StatValue(Stat.ChainHookRangePlusPct)));
        if (count <= 0 || range <= 0) return;

        var origin = player.Walk.Position(now);
        if (targetId != 0 && player.Area.Monsters.GetValueOrDefault(targetId) is { } aimed && ChainHookTarget(aimed))
            aim = aimed.Walk.Position(now);
        var direction = origin == aim ? (0d, -1d) : GroundSlamWave.Direction(origin, aim);
        // The native attachment collector keeps the nearest eligible targets in its 55-degree sector.
        var targets = player.Area.MonstersNear(origin, range)
            .Where(e => ChainHookTarget(e.Value) && !TrarthusAttachments.TryGetValue(e.Value, out _))
            .Select(e => (e.Key, e.Value, At: e.Value.Walk.Position(now)))
            .Select(e => (e.Key, e.Value, e.At, Distance: Dist(origin, e.At)))
            .Where(e => e.Distance <= range && ChainHookInSector(origin, e.At, direction, e.Distance, ChainHookSearchCosine)
                && ChainHookSight(player, origin, e.At))
            .OrderBy(e => e.Distance).Take(count).ToArray();
        if (targets.Length == 0) return;

        var attack = CaptureAreaAttack(player, gem.Id, source, hand, weapon, offCritical, damageMore, damageMore, "chain snap");
        // ChainStrike variation 2 skips the forward sweep and uses only the scaled inner area at the anchor.
        int radius = (int)Math.Floor(SkillArea.Radius(15,
            key => key == Stat.ActiveSkillBaseRadiusPlus ? 0 : StatValue(key), spell: false) * radiusMore);
        var strike = new TrarthusStrike(gem, attack, critical, radius, length,
            StatValue(Stat.ChainHookAttachmentRageToGainPerHit), BuffRows.Find("chain_strike_attachment_buff"),
            sequence, ruthless, cryExerted, rallyingDamage);
        var session = ProjectilesFor(player);
        foreach (var (id, target, _, _) in targets)
            session.AttachTrarthus(new(id, target, strike), maximum, now);
    }

    public sealed partial class ProjectileSession
    {
        List<TrarthusLink>? trarthusLinks;
        GameArea? trarthusArea;

        internal void AttachTrarthus(TrarthusLink link, int maximum, double now)
        {
            if (!Active || !ReferenceEquals(TrarthusAttachments.GetValue(link.Target, _ => link), link)) return;
            trarthusLinks ??= [];
            trarthusArea = Player.Area;
            while (trarthusLinks.Count >= maximum)
                BreakTrarthus(trarthusLinks[0], now, snap: true);
            trarthusLinks.Add(link);
            if (link.Strike.Buff != 0)
                trarthusArea.Buffs.Set(link.TargetId, BuffRows.Spec(link.Strike.Buff, Player.Id, null)
                    with { SourceSkill = link.Strike.Gem.Id });
        }

        void ReconcileTrarthus()
        {
            if (trarthusLinks is not { Count: > 0 } links) return;
            if (Player.PlayerLife == 0 || !ReferenceEquals(Player.Area, trarthusArea))
            {
                ClearTrarthus();
                return;
            }

            double now = Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            var owner = Player.Walk.Position(now);
            for (int i = links.Count - 1; i >= 0; i--)
            {
                var link = links[i];
                bool present = ReferenceEquals(trarthusArea!.Monsters.GetValueOrDefault(link.TargetId), link.Target);
                bool defeated = link.Target.Life == 0 || link.Target.Dead;
                if (defeated || !present || Dist(owner, link.Target.Walk.Position(now)) > link.Strike.Length
                    || !ChainHookTarget(link.Target))
                    BreakTrarthus(link, now, snap: defeated || present && ChainHookTarget(link.Target));
            }
        }

        void ClearTrarthus()
        {
            if (trarthusLinks is not { } links) return;
            while (links.Count > 0) BreakTrarthus(links[^1], 0, snap: false);
            trarthusLinks = null;
            trarthusArea = null;
        }

        void BreakTrarthus(TrarthusLink link, double now, bool snap)
        {
            trarthusLinks!.Remove(link);
            TrarthusAttachments.Remove(link.Target);
            if (link.Strike.Buff != 0)
                trarthusArea!.Buffs.Clear(link.TargetId, link.Strike.Buff, Player.Id);
            if (!snap || link.Strike.Radius <= 0) return;

            var centre = link.Target.Walk.Position(now);
            var area = trarthusArea;
            ScheduleContact(Runtime.Now, () => Player.PlayerLife > 0 && ReferenceEquals(Player.Area, area), () =>
            {
                var strike = link.Strike;
                var gem = strike.Gem;
                var hit = AreaSkillHitForUse(strike.Ruthless, Player, gem.Id, gem, centre, centre, immediate: true,
                    sequence: strike.Sequence, criticalRoll: strike.Critical, radiusOverride: strike.Radius,
                    triggeredCurrent: () => Active && Player.PlayerLife > 0, scheduledSession: this,
                    retainedAttack: strike.Attack, cryExerted: strike.CryExerted, rallyingDamage: strike.RallyingDamage,
                    nearbyOnly: true, targetFilter: (id, at) =>
                        ChainHookTarget(Player.Area.Monsters.GetValueOrDefault(id)) && ChainHookSight(Player, centre, at));
                if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Chain Hook snap must not yield.");
                hit.GetAwaiter().GetResult();
            });
        }
    }
}
