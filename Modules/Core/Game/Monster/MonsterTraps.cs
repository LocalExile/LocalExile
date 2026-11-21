using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// A monster's traps, for any monster whose skill is a trap (ActiveSkillTypes Trapped): the same object, throw, landing,
/// arming and lifetime as the player's (PlacedSkills: PlacedObjectPath, PlacedFlightSeconds, PlacedArmSeconds,
/// PlacedDurationSeconds, TrapTriggerRadius), on the monsters' team. Set off by the player or a minion of theirs within
/// its trigger radius, it plays its skill from where it lies (MonsterAttack.Trap), with its owner's stats, whatever its
/// owner does meanwhile: its projectiles from the trap, its area around the trap, or, for a trap with neither (Bear Trap),
/// a hit on whoever set it off. A trap whose own action draws the ground it leaves (Fire Trap's burning ground:
/// ActionClasses.GroundAt.Trap) stays, spent, for as long as that ground lasts.
/// </summary>
public static partial class Game
{
    /// <summary>Metadata/Monsters/Monster.ot: base_number_of_traps_allowed 3, every monster's.</summary>
    public const int MonsterTrapsAllowed = 3;

    // The trap's skill: its own stats with the trap flags cleared, as the player's proxy is (PlaceSkill), so the object
    // plays the skill rather than another throw.
    static GrantedSkill? MonsterTrapSkill(Player player, uint owner, MonsterAttack a, int effect, byte level,
        IReadOnlyDictionary<uint, int> stats)
    {
        var payload = new Dictionary<uint, int>(stats);
        foreach (uint flag in new[] { Stat.IsTrap, Stat.IsRemoteMine, Stat.BaseSkillIsTrapped, Stat.BaseSkillIsMined })
            payload[flag] = 0;
        var catalog = SkillsFor(player);
        return catalog.FindByRow(0x4000, effect, catalog.MaxLevelAt(effect, level), [.. payload.Select(p => (p.Key, p.Value))]) is { } skill
            ? skill with { Parent = (owner, a.SkillId) } : null;
    }

    /// <summary>The throw at the action's contact: 1 + number_of_additional_traps_to_throw traps toward the aim, the oldest of
    /// the owner's beyond Monster.ot's three (plus number_of_additional_traps_allowed) taken away.</summary>
    static async Task ThrowMonsterTraps(Player player,
        uint id, SpawnedMonster m, MonsterAttack a, MonsterAttack payload, (int X, int Y) aim)
    {
        if (a.Effect is not int effect) return;
        var stats = GolemData.Skill(SkillsFor(player), effect, m.Level).Stats;
        int Get(uint key) => stats.GetValueOrDefault(key);
        int count = Math.Clamp(1 + Get(Stat.NumberOfAdditionalTrapsToThrow), 1, 20);
        int limit = Math.Max(1, MonsterTrapsAllowed + Get(Stat.NumberOfAdditionalTrapsAllowed));
        double flight = PlacedFlightSeconds(false, Get);
        string path = PlacedObjectPath(false, Get);
        if (MonsterTrapSkill(player, id, a, effect, m.Level, stats) is not { } skill) return;
        for (int i = 0; i < count; i++)
        {
            uint born = 0;
            (int X, int Y) from = default;
            double landsAt = 0;
            await SendWorldBatch(player, [], admission =>
            {
                if (!MonsterPresent(player, id, m) || !player.Layout.WalkableFor(aim.X, aim.Y, 0)) return null;
                double now = AreaTime(player);
                from = m.Walk.Position(now);
                var bytes = new WorldRecordBuilder(player, admission);
                var owned = player.MonsterPlaced.Traps.Values.Where(t => t.Owner == id).OrderBy(t => t.Armed).ToList();
                for (int k = 0; k + limit <= owned.Count; k++)
                {
                    player.MonsterPlaced.Traps.Remove(owned[k].Id);
                    bytes.Remove(owned[k].Id);
                }
                uint trap = player.TakeObjectId();
                landsAt = now + flight;
                player.MonsterPlaced.Traps[trap] = new(trap, id, m, payload, aim, landsAt, landsAt + PlacedArmSeconds,
                    landsAt + PlacedDurationSeconds(false, Get), TrapTriggerRadius(Get)) { Landed = false };
                bytes.Create(trap, InstanceEntities.CreateObject(trap, InstanceEntities.ObjectTypeHash(path),
                    new TrapMineObject(from.X, from.Y, false, [skill], PlacedObjectStats(false, flight), Team: 1).Blob()));
                bytes.Presentation(trap, InstanceProtocol.EnterWakeZone(trap));
                if (!player.Connection.LockstepClient)
                    foreach (var record in PlacedThrowRecords(player, trap, from, aim, flight)) bytes.Presentation(trap, record);
                born = trap;
                return bytes.Build();
            });
            if (born == 0) return;
            if (player.Connection.LockstepClient)
                await SendWorldRecords(player, born, () =>
                {
                    return player.MonsterPlaced.Traps.ContainsKey(born) && !player.Closed && !player.LeavingArea
                        ? [.. PlacedThrowRecords(player, born, from, aim, landsAt - AreaTime(player))] : null;
                }, "throw a monster's trap after birth");
            Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} ({m.Label}) throws trap 0x{born:x} {path} to ({aim.X}, {aim.Y})");
        }
    }

    /// <summary>Each tick: traps land and arm, leave when their time is up or their owner is gone, and go off when the
    /// player or one of their minions stands within the trigger radius.</summary>
    static async Task MonsterTrapsTick(Player player)
    {
        if (player.MonsterPlaced.Traps.Count == 0) return;
        List<(MonsterPlacedObjects.Trap Trap, (int X, int Y) Aim)> fired = [];
        await SendWorldBatch(player, [], admission =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return null;
            double now = AreaTime(player);
            var bytes = new WorldRecordBuilder(player, admission);
            foreach (var trap in player.MonsterPlaced.Traps.Values.ToArray())
            {
                if (player.Connection.LockstepClient && !player.Connection.WorldOutput.PublishedObject(trap.Id).IsReady) continue;
                bool owner = ReferenceEquals(player.Area.Monsters.GetValueOrDefault(trap.Owner), trap.Monster);
                // A spent trap draws its ground, which outlives its owner (Game.HostileGrounds).
                if (!owner && !trap.Spent || now >= trap.Until)
                {
                    player.MonsterPlaced.Traps.Remove(trap.Id);
                    bytes.Remove(trap.Id);
                    continue;
                }
                if (trap.Spent) continue;
                if (!trap.Landed && now >= trap.LandAt)
                {
                    trap.Landed = true;
                    PlacedLandRecords(player, trap.Id, trap.At, bytes);
                }
                if (now < trap.Armed || player.PlayerLife == 0 || InGracePeriod(player)) continue;
                (int X, int Y)? enemy = Dist(player.Walk.Position(now), trap.At) <= trap.TriggerRadius ? player.Walk.Position(now)
                    : player.Golems.All.Values.Concat(player.Reservations.Fighters.Values).Concat(player.Summons.Fighters.Values)
                        .Where(g => !g.Monster.Dead && g.Monster.Life > 0)
                        .Select(g => g.Monster.Walk.Position(now))
                        .Where(p => Dist(p, trap.At) <= trap.TriggerRadius)
                        .Select(p => ((int X, int Y)?)p).FirstOrDefault();
                if (enemy is not { } aim) continue;
                bytes.World(trap.Id, InstanceProtocol.ActionStarted(trap.Id, trap.At.X, trap.At.Y, aim.X, aim.Y, 0x4000, 0xffff, 0x0408));
                if (trap.Payload.Ground is { ClientDrawn: true } drawn)
                    player.MonsterPlaced.Traps[trap.Id] = trap with { Until = now + drawn.Seconds, Spent = true, Landed = true };
                else player.MonsterPlaced.Traps.Remove(trap.Id);
                fired.Add((trap, player.Walk.Position(now)));
            }
            return bytes.Count > 0 ? bytes.Build() : null;
        });
        foreach (var (trap, aim) in fired)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} trap 0x{trap.Id:x} of monster 0x{trap.Owner:x} ({trap.Monster.Label}) is set off");
            bool Owner() { return ReferenceEquals(player.Area.Monsters.GetValueOrDefault(trap.Owner), trap.Monster); }
            ushort sequence = player.NextActionSequence();
            if (trap.Payload.Projectile is { } projectile)
                LaunchDetachedMonsterProjectile(player, trap.Owner, trap.Monster, trap.Payload, projectile,
                    trap.At, Owner, sequence);
            else
                await MonsterDetachedAreaHit(player, trap.Owner, trap.Monster, trap.Payload, trap.At,
                    trap.Payload.AreaRadius > 0 ? trap.Payload.AreaRadius : trap.TriggerRadius, sequence, Owner);
            if (trap.Payload.Ground is not { ClientDrawn: true })
                await SendWorldRemoval(player, trap.Id, $"0x0013 trap 0x{trap.Id:x} set off");
        }
    }
}
