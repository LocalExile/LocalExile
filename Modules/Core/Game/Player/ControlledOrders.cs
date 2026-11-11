using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using ControlledIntent = ControlledPlayerIntent;

public static partial class Game
{
    public sealed record ControlledReceipt(string Id, string State, string Detail, double At);
    sealed class ControlledBrain
    {
        public ControlledIntent? Intent;
        public ControlledReceipt? Receipt;
    }
    static readonly ConditionalWeakTable<ClientState, ControlledBrain> ControlledBrains = new();

    public static void ClearControlledIntent(ClientState conn)
    {
        lock (conn.Monsters) ControlledBrains.Remove(conn);
    }

    public static ControlledReceipt? ControlledIntentReceipt(ClientState conn)
    {
        lock (conn.Monsters) return ControlledBrains.TryGetValue(conn, out var brain) ? brain.Receipt : null;
    }

    public static void QueueControlledIntent(ClientState conn, ControlledIntent intent)
    {
        lock (conn.Monsters)
        {
            if (!conn.Controlled || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0)
                throw new InvalidOperationException("The party bot is not ready for an order.");
            if (intent.Action is not ("auto" or "hold" or "move" or "attack" or "cast" or "flask" or "focus" or "pickup"))
                throw new ArgumentException("Unknown bot action.");
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (!double.IsFinite(intent.Until) || intent.Until <= now || intent.Until > now + 5.01)
                throw new ArgumentException("Order lifetime must be between 0 and 5 game seconds.");
            var brain = ControlledBrains.GetOrCreateValue(conn);
            brain.Intent = intent.Action == "auto" ? null : intent;
            brain.Receipt = new(intent.Id, intent.Action == "auto" ? "applied" : "queued", intent.Action, now);
        }
    }

    public static ControlledIntent? CurrentControlledIntent(ClientState conn, double now)
    {
        lock (conn.Monsters)
        {
            if (!ControlledBrains.TryGetValue(conn, out var brain) || brain.Intent is not { } intent) return null;
            if (now < intent.Until && (intent.WallDeadline == 0 || System.Diagnostics.Stopwatch.GetTimestamp() < intent.WallDeadline)) return intent;
            brain.Intent = null;
            brain.Receipt = new(intent.Id, "expired", "Order expired; normal party AI resumed.", now);
            return null;
        }
    }

    static void FinishControlledIntent(ClientState conn, ControlledIntent intent, string state, string detail, double now)
    {
        lock (conn.Monsters)
        {
            var brain = ControlledBrains.GetOrCreateValue(conn);
            // A newer external decision may have arrived while the previous action was being dispatched.
            if (brain.Intent?.Id != intent.Id) return;
            brain.Intent = null;
            brain.Receipt = new(intent.Id, state, detail, now);
        }
    }

    public static bool ControlledMoveFits(ClientState conn, ClientState leader, int x, int y, double now)
    {
        var at = (X: x, Y: y);
        var origin = leader.Walk.Position(now);
        if (Dist(conn.Walk.Position(now), origin) > ControlledFollowerLeash || Dist(at, origin) > ControlledFollowerLeash)
            return false;
        bool Floor(int gx, int gy) => gx >= 0 && gy >= 0 && gx < conn.Layout.GridW && gy < conn.Layout.GridH
            && conn.Layout.WalkableFor(gx, gy, 2) && !conn.Barrier.Contains((gx, gy));
        var bodies = conn.Blockers;
        if (!Floor(x, y) || ClientPathfinder.ObjectBlocks(bodies, x, y, 3, 6)) return false;
        if (Dist(conn.Walk.Position(now), at) < 3) return true;
        var path = ClientPathfinder.FindPath(conn.Walk.Position(now), at, Floor,
            conn.Layout.GridW, conn.Layout.GridH, bodies, 2);
        return path.Count > 1 && path[^1] == at && !path.Zip(path.Skip(1))
            .Any(p => ClientPathfinder.Line(p.First, p.Second).Any(cell => Dist(cell, origin) > ControlledFollowerLeash));
    }

    static bool ControlledExternalMovement(ClientState conn, ClientState leader, double now) =>
        CurrentControlledIntent(conn, now) is { Action: "move" } move
        && Dist((move.X, move.Y), leader.Walk.Position(now)) <= ControlledFollowerLeash;

    // Decisions arrive asynchronously; execution stays in the bot's normal simulation and native skill pipeline.
    static async Task<bool> ExecuteControlledIntent(ClientState conn, ClientState leader, double now, bool following)
    {
        var intent = CurrentControlledIntent(conn, now);
        if (intent is null) return false;
        var here = conn.Walk.Position(now);
        double gap = Dist(here, leader.Walk.Position(now));
        if (gap > ControlledFollowerLeash) return true; // The ordinary follower recovers the gap first.
        if (intent.Action == "hold") return true; // Guard still follows; it does not orbit or select an autonomous target.
        if (intent.Action == "move")
        {
            if (ControlledMoveFits(conn, leader, intent.X, intent.Y, now))
            {
                if (Dist(here, (intent.X, intent.Y)) < 3)
                    FinishControlledIntent(conn, intent, "applied", "Walk destination reached.", now);
                else
                {
                    MoveControlledFollower(conn, (intent.X, intent.Y), now);
                    lock (conn.Monsters)
                    {
                        var brain = ControlledBrains.GetOrCreateValue(conn);
                        if (brain.Intent?.Id == intent.Id)
                            brain.Receipt = new(intent.Id, "issued", "Walking through the ordinary pathfinder.", now);
                    }
                }
            }
            else FinishControlledIntent(conn, intent, "rejected", "Destination or route is blocked or outside the leader radius.", now);
            return true;
        }
        if (intent.Action == "flask")
        {
            if (intent.Flask is < 1 or > 5)
                FinishControlledIntent(conn, intent, "rejected", "Flask slot must be 1..5.", now);
            else
            {
                await UseFlask(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, (uint)(intent.Flask - 1));
                FinishControlledIntent(conn, intent, "issued", "Flask use processed; ordinary charge/resource checks apply.", now);
            }
            return true;
        }
        if (intent.Action == "pickup")
        {
            var ground = conn.Instance.Ground.Get(intent.Target);
            if (ground is null || !conn.SeenGround.Contains(intent.Target)
                || Dist((ground.GridX, ground.GridY), leader.Walk.Position(now)) > ControlledFollowerLeash
                || !conn.Walk.InReachOf(here, (ground.GridX, ground.GridY), (int)PickupReach, PickupToleranceOf(conn, ground.Item)))
            {
                FinishControlledIntent(conn, intent, "rejected", "Loot is unseen, gone or outside nearby pickup reach.", now);
                return true;
            }
            var pickup = new LoginWriter().U16(InstanceTransfer.UseSkillOnObjectId).U32(intent.Target)
                .U16(0x0266).U16(0xffff).U16(0x0408).ToArray();
            await OnClientMessages(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers,
                [(InstanceTransfer.UseSkillOnObjectId, pickup)], [], conn);
            FinishControlledIntent(conn, intent, "issued", "Ordinary ground pickup processed; inventory and ownership checks apply.", now);
            return true;
        }
        SpawnedMonster? enemy = null;
        if (intent.Action is "attack" or "focus" || intent.Action == "cast" && intent.Target != 0)
        {
            lock (conn.Monsters)
                if (conn.Monsters.TryGetValue(intent.Target, out var candidate)
                    && candidate is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                    && !conn.AsleepMonsters.Contains(intent.Target)
                    && Dist(candidate.Walk.Position(now), leader.Walk.Position(now)) <= ControlledFollowerLeash)
                    enemy = candidate;
            if ((enemy is null || InTown(conn)) && intent.Action != "cast")
            {
                FinishControlledIntent(conn, intent, "rejected", "Target is not a live visible hostile near the leader.", now);
                return true;
            }
            if (intent.Action == "focus")
            {
                FollowerPlans.GetOrCreateValue(conn).Target = intent.Target;
                FinishControlledIntent(conn, intent, "applied", "Autonomous attacks prioritize this eligible target.", now);
                return false;
            }
        }
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == intent.Skill);
        if (gem.Name is null || GemSkillInstanceDisabled(conn, gem.Effect, gem.Id) is { }
            || !SupportedSkill(conn, gem.Id).RequirementsMet)
        {
            FinishControlledIntent(conn, intent, "rejected", "Skill is absent, disabled or does not meet its requirements.", now);
            return true;
        }
        double seconds = SkillsFor(conn).IsAttack(gem.Effect) ? SupportedAttackSeconds(conn, gem.Id)
            : (CastMs(conn, gem.Effect, gem.Id) ?? 750) / 1000.0;
        double ahead = ControlledAhead(here, leader.Walk.Position(now), leader.Walk.Destination);
        if (leader.Walk.Moving(now) && !ControlledFollowAttackFits(gap, leader.Walk.Speed, seconds, ahead)) return true;
        byte[] bytes;
        ushort pid;
        if (enemy is not null)
        {
            double reach = ControlledInPlaceReach(conn, gem.Id, gem.Effect);
            if (!conn.Walk.InReachOf(here, enemy.Walk.Position(now), (int)reach, enemy.StopRange - 2))
            {
                FinishControlledIntent(conn, intent, "rejected", "Enemy is out of this skill's in-place reach; follow the leader first.", now);
                return true;
            }
            pid = InstanceTransfer.UseSkillOnObjectId;
            bytes = new LoginWriter().U16(pid).U32(intent.Target).U16(gem.Id).U16(0xffff).U16(1).ToArray();
        }
        else if (intent.Target != 0)
        {
            var ally = intent.Target == conn.PeerId ? null : conn.Instance.Player(intent.Target);
            var at = intent.Target == conn.PeerId ? here : ally is { Owner: not null, Life: > 0 } ? (ally.X, ally.Y) : ((int, int)?)null;
            if (at is null || Dist(at.Value, leader.Walk.Position(now)) > ControlledFollowerLeash || Dist(at.Value, here) > 65)
            {
                FinishControlledIntent(conn, intent, "rejected", "Cast target is not a living nearby party actor.", now);
                return true;
            }
            pid = InstanceTransfer.UseSkillOnObjectId;
            bytes = new LoginWriter().U16(pid).U32(intent.Target == conn.PeerId ? PlayerId : intent.Target)
                .U16(gem.Id).U16(0xffff).U16(1).ToArray();
        }
        else
        {
            if (Dist((intent.X, intent.Y), leader.Walk.Position(now)) > ControlledFollowerLeash
                || Dist((intent.X, intent.Y), here) > 65)
            {
                FinishControlledIntent(conn, intent, "rejected", "Cast location is outside the nearby party area.", now);
                return true;
            }
            pid = InstanceProtocol.UseSkillAtLocationId;
            bytes = new LoginWriter().U16(pid).U32((uint)intent.X).U32((uint)intent.Y).U16(gem.Id).U16(0xffff).U16(1).ToArray();
        }
        HoldControlledFollowerAttack(conn, now, seconds, intent.Target);
        await OnClientMessages(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, [(pid, bytes)], [], conn);
        FinishControlledIntent(conn, intent, "issued", "Native skill request processed; normal mana, cooldown and targeting rules apply.", now);
        return true;
    }

    public static object ObserveControlledPlayer(ClientState conn, ClientState leader)
    {
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var here = conn.Walk.Position(now);
            var origin = leader.Walk.Position(now);
            object Cell((int X, int Y) p) => new { x = p.X, y = p.Y };
            return new
            {
                gameTime = now, paused = conn.Paused, area = conn.Layout.Id,
                self = new { id = conn.PeerId, name = CharacterName(conn), level = conn.Pools.Level,
                    position = Cell(here), destination = Cell(conn.Walk.Destination), moving = conn.Walk.Moving(now),
                    life = conn.PlayerLife, maxLife = conn.Pools.Life, mana = conn.PlayerMana, maxMana = conn.Pools.Mana,
                    energyShield = conn.EnergyShield.Current, maxEnergyShield = conn.EnergyShield.Maximum, alive = conn.PlayerLife > 0,
                    strength = conn.Pools.Strength, dexterity = conn.Pools.Dexterity, intelligence = conn.Pools.Intelligence },
                leader = new { id = leader.PeerId, name = CharacterName(leader), position = Cell(origin), destination = Cell(leader.Walk.Destination),
                    moving = leader.Walk.Moving(now), life = leader.PlayerLife, maxLife = leader.Pools.Life,
                    energyShield = leader.EnergyShield.Current, distance = Dist(here, origin) },
                skills = GemSkillEffects(conn).Select(g => new { id = g.Id, name = g.Name, level = g.Level, quality = g.Quality,
                    requirementsMet = GemSkillInstanceDisabled(conn, g.Effect, g.Id) is null && SupportedSkill(conn, g.Id).RequirementsMet,
                    attack = SkillsFor(conn).IsAttack(g.Effect), inPlaceReach = ControlledInPlaceReach(conn, g.Id, g.Effect),
                    cooldownSeconds = Math.Max(0, conn.SkillReadyAt.GetValueOrDefault(g.Effect) - now), activeAura = conn.Auras.ContainsKey(g.Id),
                    supportedManaCost = SupportedStats(conn, g.Id).GetValueOrDefault(Stats.Key("mana_cost") ?? 0),
                    actionSeconds = SkillsFor(conn).IsAttack(g.Effect) ? SupportedAttackSeconds(conn, g.Id) : (CastMs(conn, g.Effect, g.Id) ?? 750) / 1000.0 }).ToArray(),
                enemies = conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Asleep: false }
                        && !conn.AsleepMonsters.Contains(p.Key) && Dist(p.Value.Walk.Position(now), origin) <= ControlledFollowerLeash)
                    .OrderBy(p => Dist(p.Value.Walk.Position(now), origin)).Take(64)
                    .Select(p => new { id = p.Key, name = p.Value.Label, position = Cell(p.Value.Walk.Position(now)),
                        life = p.Value.Life, invulnerable = p.Value.Invulnerable, targetId = p.Value.Target?.PeerId,
                        targetingLeader = ReferenceEquals(p.Value.Target, leader) }).ToArray(),
                deadEnemies = conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: true }
                        && Dist(p.Value.Walk.Position(now), origin) <= ControlledFollowerLeash).Take(32)
                    .Select(p => new { id = p.Key, name = p.Value.Label, position = Cell(p.Value.Walk.Position(now)) }).ToArray(),
                flasks = ItemsOf(conn).Items(PlayerInventories.Flask).Select(p => new
                    { slot = p.X + 1, name = ItemsFor(conn).ByHash(p.Item.TypeHash)?.Name, charges = p.Item.Charges }).ToArray(),
                loot = conn.Instance.Ground.All().Where(p => conn.SeenGround.Contains(p.Id)
                        && Dist((p.Item.GridX, p.Item.GridY), origin) <= ControlledFollowerLeash).Take(32)
                    .Select(p => new { id = p.Id, name = ItemsFor(conn).ByHash(p.Item.Item.TypeHash)?.Name,
                        position = Cell((p.Item.GridX, p.Item.GridY)), quantity = p.Item.Item.StackSize }).ToArray(),
                inventory = ItemsOf(conn).Items(PlayerInventories.MainInventory).Select(p => new
                    { id = p.Id, name = ItemsFor(conn).ByHash(p.Item.TypeHash)?.Name, x = p.X, y = p.Y, quantity = p.Item.StackSize }).ToArray(),
                lastCommand = ControlledIntentReceipt(conn),
            };
        }
    }
}
