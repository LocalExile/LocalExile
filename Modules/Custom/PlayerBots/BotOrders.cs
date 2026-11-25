using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

using static Game;
using static Program;

namespace GameServer.Modules.PlayerBots;

// How a bot plays: the game's own rules, run for a player the server drives.
public static partial class BotPlay
{
    public sealed record ControlledReceipt(string Id, string State, string Detail, double At);
    sealed class ControlledBrain
    {
        public ControlledPlayerIntent? Intent;
        public ControlledReceipt? Receipt;
    }
    static readonly Store<Player, ControlledBrain> ControlledBrains = new();

    public static void ClearControlledIntent(Player player)
    {
        ControlledBrains.Remove(player);
    }

    public static ControlledReceipt? ControlledIntentReceipt(Player player)
    {
        return ControlledBrains.TryGetValue(player, out var brain) ? brain.Receipt : null;
    }

    public static void QueueControlledIntent(Player player, ControlledPlayerIntent intent)
    {
        if (player.HasClient || player.Closed || player.LeavingArea || player.PlayerLife == 0)
            throw new InvalidOperationException("The party bot is not ready for an order.");
        if (intent.Action is not ("auto" or "hold" or "move" or "attack" or "cast" or "flask" or "focus" or "pickup"))
            throw new ArgumentException("Unknown bot action.");
        double now = AreaTime(player);
        if (!double.IsFinite(intent.Until) || intent.Until <= now || intent.Until > now + 5.01)
            throw new ArgumentException("Order lifetime must be between 0 and 5 game seconds.");
        var brain = ControlledBrains.GetOrCreateValue(player);
        brain.Intent = intent.Action == "auto" ? null : intent;
        brain.Receipt = new(intent.Id, intent.Action == "auto" ? "applied" : "queued", intent.Action, now);
    }

    public static ControlledPlayerIntent? CurrentControlledIntent(Player player, double now)
    {
        if (!ControlledBrains.TryGetValue(player, out var brain) || brain.Intent is not { } intent) return null;
        if (now < intent.Until && (intent.WallDeadline == 0 || System.Diagnostics.Stopwatch.GetTimestamp() < intent.WallDeadline)) return intent;
        brain.Intent = null;
        brain.Receipt = new(intent.Id, "expired", "Order expired; normal party AI resumed.", now);
        return null;
    }

    static void FinishControlledIntent(Player player, ControlledPlayerIntent intent, string state, string detail, double now)
    {
        var brain = ControlledBrains.GetOrCreateValue(player);
        // A newer external decision may have arrived while the previous action was being dispatched.
        if (brain.Intent?.Id != intent.Id) return;
        brain.Intent = null;
        brain.Receipt = new(intent.Id, state, detail, now);
    }

    public static bool ControlledMoveFits(Player player, Player leader, int x, int y, double now)
    {
        var at = (X: x, Y: y);
        var origin = leader.Walk.Position(now);
        if (Dist(player.Walk.Position(now), origin) > ControlledFollowerLeash || Dist(at, origin) > ControlledFollowerLeash)
            return false;
        bool Floor(int gx, int gy) => gx >= 0 && gy >= 0 && gx < player.Layout.GridW && gy < player.Layout.GridH
            && player.Layout.WalkableFor(gx, gy, 2) && !player.Barrier.Contains((gx, gy));
        var bodies = player.Blockers;
        if (!Floor(x, y) || ClientPathfinder.ObjectBlocks(bodies, x, y, 3, 6)) return false;
        if (Dist(player.Walk.Position(now), at) < 3) return true;
        var path = ClientPathfinder.FindPath(player.Walk.Position(now), at, Floor,
            player.Layout.GridW, player.Layout.GridH, bodies, 2);
        return path.Count > 1 && path[^1] == at && !path.Zip(path.Skip(1))
            .Any(p => ClientPathfinder.Line(p.First, p.Second).Any(cell => Dist(cell, origin) > ControlledFollowerLeash));
    }

    static bool ControlledExternalMovement(Player player, Player leader, double now) =>
        CurrentControlledIntent(player, now) is { Action: "move" } move
        && Dist((move.X, move.Y), leader.Walk.Position(now)) <= ControlledFollowerLeash;

    // Decisions arrive asynchronously; execution stays in the bot's normal simulation and native skill pipeline.
    static async Task<bool> ExecuteControlledIntent(Player player, Player leader, double now, bool following)
    {
        var intent = CurrentControlledIntent(player, now);
        if (intent is null) return false;
        var here = player.Walk.Position(now);
        double gap = Dist(here, leader.Walk.Position(now));
        if (gap > ControlledFollowerLeash) return true; // The ordinary follower recovers the gap first.
        if (intent.Action == "hold") return true; // Guard still follows; it does not orbit or select an autonomous target.
        if (intent.Action == "move")
        {
            if (ControlledMoveFits(player, leader, intent.X, intent.Y, now))
            {
                if (Dist(here, (intent.X, intent.Y)) < 3)
                    FinishControlledIntent(player, intent, "applied", "Walk destination reached.", now);
                else
                {
                    MoveControlledFollower(player, (intent.X, intent.Y), now);
                    var brain = ControlledBrains.GetOrCreateValue(player);
                    if (brain.Intent?.Id == intent.Id)
                        brain.Receipt = new(intent.Id, "issued", "Walking through the ordinary pathfinder.", now);
                }
            }
            else FinishControlledIntent(player, intent, "rejected", "Destination or route is blocked or outside the leader radius.", now);
            return true;
        }
        if (intent.Action == "flask")
        {
            if (intent.Flask is < 1 or > 5)
                FinishControlledIntent(player, intent, "rejected", "Flask slot must be 1..5.", now);
            else
            {
                await UseFlask(player, (uint)(intent.Flask - 1));
                FinishControlledIntent(player, intent, "issued", "Flask use processed; ordinary charge/resource checks apply.", now);
            }
            return true;
        }
        if (intent.Action == "pickup")
        {
            var ground = player.Instance.Ground.Get(intent.Target);
            if (ground is null
                || Dist((ground.GridX, ground.GridY), leader.Walk.Position(now)) > ControlledFollowerLeash
                || !player.Walk.InReachOf(here, (ground.GridX, ground.GridY), (int)PickupReach, PickupToleranceOf(player, ground.Item)))
            {
                FinishControlledIntent(player, intent, "rejected", "Loot is unseen, gone or outside nearby pickup reach.", now);
                return true;
            }
            var pickup = new LoginWriter().U16(InstanceTransfer.UseSkillOnObjectId).U32(intent.Target)
                .U16(0x0266).U16(0xffff).U16(0x0408).ToArray();
            await OnClientMessages([(InstanceTransfer.UseSkillOnObjectId, pickup)], [], player);
            FinishControlledIntent(player, intent, "issued", "Ordinary ground pickup processed; inventory and ownership checks apply.", now);
            return true;
        }
        SpawnedMonster? enemy = null;
        if (intent.Action is "attack" or "focus" || intent.Action == "cast" && intent.Target != 0)
        {
            if (player.Area.Monsters.TryGetValue(intent.Target, out var candidate)
                && candidate is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                && !MonsterAsleep(player, intent.Target)
                && Dist(candidate.Walk.Position(now), leader.Walk.Position(now)) <= ControlledFollowerLeash)
                enemy = candidate;
            if ((enemy is null || InTown(player)) && intent.Action != "cast")
            {
                FinishControlledIntent(player, intent, "rejected", "Target is not a live visible hostile near the leader.", now);
                return true;
            }
            if (intent.Action == "focus")
            {
                FollowerPlans.GetOrCreateValue(player).Target = intent.Target;
                FinishControlledIntent(player, intent, "applied", "Autonomous attacks prioritize this eligible target.", now);
                return false;
            }
        }
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == intent.Skill);
        if (gem.Name is null || GemSkillInstanceDisabled(player, gem.Effect, gem.Id) is { }
            || !SupportedSkill(player, gem.Id).RequirementsMet)
        {
            FinishControlledIntent(player, intent, "rejected", "Skill is absent, disabled or does not meet its requirements.", now);
            return true;
        }
        double seconds = SkillsFor(player).IsAttack(gem.Effect) ? SupportedAttackSeconds(player, gem.Id)
            : (CastMs(player, gem.Effect, gem.Id) ?? 750) / 1000.0;
        double ahead = ControlledAhead(here, leader.Walk.Position(now), leader.Walk.Destination);
        if (leader.Walk.Moving(now) && !ControlledFollowAttackFits(gap, leader.Walk.Speed, seconds, ahead)) return true;
        byte[] bytes;
        ushort pid;
        if (enemy is not null)
        {
            double reach = ControlledInPlaceReach(player, gem.Id, gem.Effect);
            if (!player.Walk.InReachOf(here, enemy.Walk.Position(now), (int)reach, enemy.StopRange - 2))
            {
                FinishControlledIntent(player, intent, "rejected", "Enemy is out of this skill's in-place reach; follow the leader first.", now);
                return true;
            }
            pid = InstanceTransfer.UseSkillOnObjectId;
            bytes = new LoginWriter().U16(pid).U32(intent.Target).U16(gem.Id).U16(0xffff).U16(1).ToArray();
        }
        else if (intent.Target != 0)
        {
            var ally = intent.Target == player.Id ? null : player.Instance.Player(intent.Target);
            var at = intent.Target == player.Id ? here : ally is { PlayerLife: > 0 } ? ally.Walk.Position(now) : ((int, int)?)null;
            if (at is null || Dist(at.Value, leader.Walk.Position(now)) > ControlledFollowerLeash || Dist(at.Value, here) > 65)
            {
                FinishControlledIntent(player, intent, "rejected", "Cast target is not a living nearby party actor.", now);
                return true;
            }
            pid = InstanceTransfer.UseSkillOnObjectId;
            bytes = new LoginWriter().U16(pid).U32(intent.Target == player.Id ? player.Id : intent.Target)
                .U16(gem.Id).U16(0xffff).U16(1).ToArray();
        }
        else
        {
            if (Dist((intent.X, intent.Y), leader.Walk.Position(now)) > ControlledFollowerLeash
                || Dist((intent.X, intent.Y), here) > 65)
            {
                FinishControlledIntent(player, intent, "rejected", "Cast location is outside the nearby party area.", now);
                return true;
            }
            pid = InstanceProtocol.UseSkillAtLocationId;
            bytes = new LoginWriter().U16(pid).U32((uint)intent.X).U32((uint)intent.Y).U16(gem.Id).U16(0xffff).U16(1).ToArray();
        }
        HoldControlledFollowerAttack(player, now, seconds, intent.Target);
        await OnClientMessages([(pid, bytes)], [], player);
        FinishControlledIntent(player, intent, "issued", "Native skill request processed; normal mana, cooldown and targeting rules apply.", now);
        return true;
    }

    public static object ObserveControlledPlayer(Player player, Player leader)
    {
        double now = AreaTime(player);
        var here = player.Walk.Position(now);
        var origin = leader.Walk.Position(now);
        object Cell((int X, int Y) p) => new { x = p.X, y = p.Y };
        return new
        {
            gameTime = now, paused = player.Paused, area = player.Layout.Id,
            self = new { id = player.Id, name = CharacterName(player), level = player.Pools.Level,
                position = Cell(here), destination = Cell(player.Walk.Destination), moving = player.Walk.Moving(now),
                life = player.PlayerLife, maxLife = player.Pools.Life, mana = player.PlayerMana, maxMana = player.Pools.Mana,
                energyShield = player.EnergyShield.Current, maxEnergyShield = player.EnergyShield.Maximum, alive = player.PlayerLife > 0,
                strength = player.Pools.Strength, dexterity = player.Pools.Dexterity, intelligence = player.Pools.Intelligence },
            leader = new { id = leader.Id, name = CharacterName(leader), position = Cell(origin), destination = Cell(leader.Walk.Destination),
                moving = leader.Walk.Moving(now), life = leader.PlayerLife, maxLife = leader.Pools.Life,
                energyShield = leader.EnergyShield.Current, distance = Dist(here, origin) },
            skills = GemSkillEffects(player).Select(g => new { id = g.Id, name = g.Name, level = g.Level, quality = g.Quality,
                requirementsMet = GemSkillInstanceDisabled(player, g.Effect, g.Id) is null && SupportedSkill(player, g.Id).RequirementsMet,
                attack = SkillsFor(player).IsAttack(g.Effect), inPlaceReach = ControlledInPlaceReach(player, g.Id, g.Effect),
                cooldownSeconds = Math.Max(0, player.SkillReadyAt.GetValueOrDefault(g.Effect) - now), activeAura = player.Auras.ContainsKey(g.Id),
                supportedManaCost = SupportedStats(player, g.Id).GetValueOrDefault(Stat.ManaCost),
                actionSeconds = SkillsFor(player).IsAttack(g.Effect) ? SupportedAttackSeconds(player, g.Id) : (CastMs(player, g.Effect, g.Id) ?? 750) / 1000.0 }).ToArray(),
            enemies = player.Area.MonstersNear(origin, ControlledFollowerLeash).Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, Asleep: false }
                    && !MonsterAsleep(player, p.Key) && Dist(p.Value.Walk.Position(now), origin) <= ControlledFollowerLeash)
                .OrderBy(p => Dist(p.Value.Walk.Position(now), origin)).Take(64)
                .Select(p => new { id = p.Key, name = p.Value.Label, position = Cell(p.Value.Walk.Position(now)),
                    life = p.Value.Life, invulnerable = p.Value.Invulnerable, targetId = p.Value.Target?.Id,
                    targetingLeader = ReferenceEquals(p.Value.Target, leader) }).ToArray(),
            deadEnemies = player.Area.MonstersNear(origin, ControlledFollowerLeash).Where(p => p.Value is { Hostile: true, Dead: true }
                    && Dist(p.Value.Walk.Position(now), origin) <= ControlledFollowerLeash).Take(32)
                .Select(p => new { id = p.Key, name = p.Value.Label, position = Cell(p.Value.Walk.Position(now)) }).ToArray(),
            flasks = ItemsOf(player).Items(PlayerInventories.Flask).Select(p => new
                { slot = p.X + 1, name = ItemsFor(player).ByHash(p.Item.TypeHash)?.Name, charges = p.Item.Charges }).ToArray(),
            loot = player.Instance.Ground.All().Where(p => Dist((p.Item.GridX, p.Item.GridY), origin) <= ControlledFollowerLeash).Take(32)
                .Select(p => new { id = p.Id, name = ItemsFor(player).ByHash(p.Item.Item.TypeHash)?.Name,
                    position = Cell((p.Item.GridX, p.Item.GridY)), quantity = p.Item.Item.StackSize }).ToArray(),
            inventory = ItemsOf(player).Items(PlayerInventories.MainInventory).Select(p => new
                { id = p.Id, name = ItemsFor(player).ByHash(p.Item.TypeHash)?.Name, x = p.X, y = p.Y, quantity = p.Item.StackSize }).ToArray(),
            lastCommand = ControlledIntentReceipt(player),
        };
    }
}
