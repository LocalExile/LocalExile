using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;

/// <summary>A fighting minion's target this AI step: the hostile monster and where it stands, whether it is in reach, and
/// the cell the minion walks to so it is.</summary>
public readonly record struct MinionTarget(uint Id, SpawnedMonster Monster, (int X, int Y) At, bool InReach, (int X, int Y) Approach);

public static partial class Game
{
    // A fighting minion reaches a monster's edge: its attack's distance (MaximumAttackDistance, as a monster's reach of the
    // player) plus the target's body.
    static double MinionReach(MonsterAttack a, SpawnedMonster target) => a.Range + Math.Max(0, LivingCollision.Size(target));

    /// <summary>
    /// The monster a fighting minion goes for this AI step: the living hostile one nearest it within its aggro range, of it
    /// and of the player (it does not leave him for a distant pack), not lurking unrisen. Null when there is none or the
    /// player is dead; it then follows him. In the area's frame.
    /// </summary>
    public static MinionTarget? MinionTargetLocked(Player player, uint id, SpawnedMonster m, (int X, int Y) at,
        (int X, int Y) playerAt, double now)
    {
        if (m.Attack is not { } a || player.PlayerLife == 0) return null;
        MinionTarget? best = null;
        var beast = player.Beasts.Active;
        var stats = beast?.Id == id && ReferenceEquals(beast.Monster, m) ? beast.SkillStats : null;
        var (aggressive, defensive) = stats is null ? default : MinionStance(stats, CharacterPlayerStats(player), false, false);
        int range = aggressive ? Math.Max(AggressiveMinionMinimumRange, m.AggroRange) : m.AggroRange;
        int ownerRange = defensive ? Math.Min(DefensiveMinionMaximumRange, range) : range;
        double nearest = double.MaxValue;
        foreach (var (targetId, t) in player.Area.Monsters)
        {
            if (targetId == id || !t.Hostile || t.Dead || t.Life == 0 || double.IsPositiveInfinity(t.BusyUntil)) continue;
            var to = t.Walk.Position(now);
            double d = Dist(to, at);
            bool prey = stats is not null && IsPrey(player, stats, targetId, t, now);
            if (!prey && (d >= nearest || d > range || !m.TemporarilyAllied && Dist(to, playerAt) > ownerRange)) continue;
            nearest = d;
            double reach = MinionReach(a, t), f = d > 0 ? Math.Max(0, reach - 1) / d : 0;
            best = new(targetId, t, to, d <= reach,
                ((int)Math.Round(to.X + (at.X - to.X) * f), (int)Math.Round(to.Y + (at.Y - to.Y) * f)));
            if (prey) break;
        }
        return best;
    }

    internal static byte[] MinionActionHit(uint attacker, uint target, byte code, byte angle) =>
        InstanceProtocol.ActionHit(attacker, 0xffff, target,
            flags: MonsterHitFlags(blocked: false, critical: false), kind: 0x03, code: code, angle: angle);

    /// <summary>
    /// A fighting minion's hit at its swing's contact, in the area's frame: both still there and the target in reach,
    /// the minion's accuracy (DefaultMonsterStats at its level) against the target's evasion, the target's block
    /// (Stalwart), then the attack's damage (the minion's own, with the player's minion modifiers it was summoned with)
    /// mitigated by the target as any hit. It is the minion's hit, not the player's (no hit-recently). Null when it did not
    /// land.
    /// </summary>
    public static (MonsterHit Hit, int Damage, byte HitCode)? MinionHitLocked(Player player, uint id, SpawnedMonster m, MinionTarget target,
        double now, Random random) =>
        MinionHitTargetLocked(player, id, m, target.Id, target.Monster, now, random, checkReach: true);

    internal static (MonsterHit Hit, int Damage, byte HitCode)? MinionHitTargetLocked(Player player, uint id, SpawnedMonster m,
        uint targetId, SpawnedMonster targetMonster, double now, Random random, bool checkReach,
        MonsterAttack? attack = null, int? accuracy = null)
    {
        var t = targetMonster;
        if (!CurrentGameWork || (attack ?? m.Attack) is not { } a || m.Dead || !player.Minions.Fights(id) && !m.FightsForPlayers || !player.Area.Monsters.TryGetValue(id, out var self)
            || !ReferenceEquals(self, m) || t.Dead || t.Life == 0 || !t.Hostile
            || !player.Area.Monsters.TryGetValue(targetId, out var current) || !ReferenceEquals(current, t)) return null;
        var at = m.Walk.Position(now);
        if (checkReach && Dist(at, t.Walk.Position(now)) > MinionReach(a, t) + 2) return null;
        int hitAccuracy = accuracy ?? m.Mods.Effects.Accuracy(Monsters.BaseAccuracy(m.Level) ?? 0);
        int evasion = UnderBlind(player, targetId, t.Mods.Effects.Evasion(Monsters.BaseEvasion(t.Level) ?? 0));
        if (!DamageModel.Hits(DamageModel.AccuracyHitChance(evasion, hitAccuracy), random.Next(100)) || MonsterBlocks(t, random, spell: false))
            return null;
        var supported = BeastSupportedAttack(player, id, m, a, now);
        var offense = supported?.Attack.Damage ?? a.Damage;
        // A minion the player summoned takes his minion modifiers; a shrine's ally is not his to scale (GGG: "You are not
        // considered to be summoning them").
        int Flat(string bound) => player.Minions.Owns(id) && Stats.Key($"non_golem_minion_global_{bound}_added_physical_damage") is uint key
            ? player.Golems.OwnerStats.GetValueOrDefault(key) : 0;
        if (supported is null && Flat("maximum") > 0) offense = offense.Add(GameServer.Core.World.DamageType.Physical,
            new GameServer.Core.World.DamageRange(Flat("minimum"), Flat("maximum")).Scale(Math.Max(0, 1 + m.Mods.Effects[Stat.DamagePlusPct] / 100.0)));
        if (supported is not null && player.Beasts.Active is { } companion)
            offense = offense.Scale(MeatShieldDamage(player, companion.SkillStats, t, now));
        var mitigated = DamageModel.Mitigate(offense.Roll(random), type => MonsterHitResistance(t, type, default, now));
        int damage = MonsterTakesMitigated(t, mitigated, spell: false, now: now);
        if (ApplyMonsterHitLocked(player, t, damage, allyAt: at) is not { } hit) return null;
        if (!t.Dead && damage > 0 && supported is not null && player.Beasts.Active is { } beast)
            MinionExposureOnHitLocked(beast.SkillStats, t, mitigated, now, random);
        return (hit, damage, HitCode(mitigated));
    }

    // A fighting minion attacks its target: its skill aimed at it (0x1ab with the target's id, as a monster's swing at the
    // player), and at the contact point (half the attack time) the hit lands (MinionHitTargetLocked): 0x1af from the minion, then
    // the target's hit and death as for the player's hits. Its kills give the player their experience, items and quest
    // progress, but are not his for his on-kill effects.
    static void StartMinionSwing(Player player,
        PlayerMinions.Attack attack)
    {
        var updates = AreaUpdateContext.Value;
        if (!CurrentGameWork || updates is null || !PlayerUpdatesCurrent(player, updates)
            || !updates.MinionAttacks.Add(attack)) return;
        _ = Continue();
        async Task Continue()
        {
            try { await MinionSwing(player, attack); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
            finally { updates.MinionAttacks.Remove(attack); }
        }
    }

    public static async Task MinionSwing(Player player,
        PlayerMinions.Attack attack)
    {
        uint id = attack.Id;
        try
        {
        var rules = CombatLifetime;
        var (_, m, from, targetId, targetMonster, toAt, contactAt) = attack;
        var target = new MinionTarget(targetId, targetMonster, toAt, true, toAt);
        bool Current() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules) && !player.Closed && !player.LeavingArea
            && (player.Minions.Fights(id) || m.FightsForPlayers)
            && TemporaryAllyAttackCurrent(attack, AreaTime(player))
            && player.Area.Monsters.TryGetValue(id, out var held) && ReferenceEquals(held, m) && !m.Dead
            && player.Minions.Attacks.TryGetValue(id, out var pending) && ReferenceEquals(pending, attack);
        if (!Current()) return;
        var a = m.Attack!;
        if (!attack.Started)
            await Send(player, [],
                $"0x01ab minion 0x{id:x} ({m.Label}) attacks monster 0x{target.Id:x}, attack time {a.Seconds:0.00} s", atGate: () =>
                {
                    if (!Current() || attack.Started) return null;
                    attack.Started = true;
                    return a.InPlace
                        ? InstanceProtocol.ActionStarted(id, from.X, from.Y, target.At.X, target.At.Y, a.SkillId, 0xffff, player.MonsterOrderFlags, targetObjectId: target.Id)
                        : InstanceProtocol.ActionStartedNow(id, from.X, from.Y, target.At.X, target.At.Y, a.SkillId, 0xffff, player.MonsterOrderFlags, target.Id);
                });
        while (true)
        {
            int wait;
            if (!Current()) return;
            double left = contactAt - AreaTime(player);
            wait = !player.Paused && left <= 0 ? 0 : (int)Math.Clamp(Math.Ceiling(left * 1000), 1, 1000);
            if (wait == 0) break;
            await AreaWait(PlayerUpdateRole.MonsterAction, wait);
        }
        (MonsterHit Hit, int Damage, byte HitCode)? landed = null;
        (int X, int Y) at, to;
        while (true)
        {
            using (ProjectileMutation(player))
            {
                if (!Current()) return;
                if (!player.Paused)
                {
                    double now = ProjectileWorldTime(player);
                    (at, to) = (m.Walk.Position(now), target.Monster.Walk.Position(now));
                    if (a.Projectile is { } projectile)
                    {
                        ReleaseMinionProjectile(player, attack, projectile, now);
                        return;
                    }
                    player.Minions.Attacks.Remove(id);
                    landed = MinionHitTargetLocked(player, id, m, target.Id, target.Monster, now, AreaRng(player), checkReach: true);
                    if (landed is { Hit.StageStarts: true } staged)
                    {
                        CommitBossStage(player, target.Id, target.Monster, now);
                        landed = staged with { Hit = staged.Hit with { StageStarts = false } };
                    }
                    if (landed is { } accepted)
                    {
                        // Immediate kind 3 reaches the attacker's native result consumer; 0xffff never supplies a consuming action.
                        player.Minions.OutputQueue.Add(new(MinionActionHit(id, target.Id, accepted.HitCode,
                            HitAngle(to.X - at.X, to.Y - at.Y)),
                            $"0x01af minion 0x{id:x} hit on monster 0x{target.Id:x}", target.Id, target.Monster, WorldId: id));
                        bool owned = player.Minions.Owns(id, m);
                        IReadOnlyDictionary<uint, int>? skill = owned ? OwnedFighter(player, id)?.Own : null;
                        if (owned && skill is null && player.Beasts.Active is { } beast && beast.Id == id) skill = beast.SkillStats;
                        CommitMinionHitLocked(player, target.Id, target.Monster, accepted.Hit, accepted.Damage, now,
                            $"minion 0x{id:x} ({m.Label})", source: owned ? m : null, skill: skill);
                    }
                    break;
                }
            }
            await AreaWait(PlayerUpdateRole.MonsterAction, 100);
        }
        if (landed is not { } hit)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} minion 0x{id:x} ({m.Label}) missed monster 0x{target.Id:x}");
            return;
        }
        await PublishMinionOutcomesFor(player, rules);
        }
        finally
        {
            if (player.Minions.Attacks.TryGetValue(id, out var pending) && ReferenceEquals(pending, attack))
                player.Minions.Attacks.Remove(id);
        }
    }
}
