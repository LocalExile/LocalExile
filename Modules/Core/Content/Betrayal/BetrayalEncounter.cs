using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static GameServer.Modules.Betrayal.BetrayalTables;

namespace GameServer.Modules.Betrayal;

/// <summary>
/// Jun's Betrayal mission in an area instance: Jun and the mission stand from the area's population (the Transportation's
/// carts and guards, or the Intervention's ruleset), the member comes out when a character is close, kneels at the floor of
/// its life by a MakeChoice, and once the choice is made leaves with Jun and the carts. The mission belongs to the instance
/// (<see cref="BetrayalRun"/>): every player in it sees and drives the same one, and it ends with the instance, not with a
/// player leaving.
/// </summary>
public sealed class BetrayalEncounter
{
    /// <summary>The mission starts when a living character is this close to its spot, in cells [ESTIMATE: the official capture
    /// shows the start but not where the character stood when it came].</summary>
    public const int StartDistance = 55;

    /// <summary>The member does not die: a hit that would kill it stops at this much of its life and it kneels instead.</summary>
    public const int KneelLifePercent = 1;

    /// <summary>How close a character walks to the MakeChoice to use it (its Positioned object_size).</summary>
    public const int ChoiceReach = 3;

    /// <summary>The member's states at the kneel [capture 4/4]: defeated 1 (the client stops targeting it), post_emerge 3,
    /// walk_emerge_ended 1, stasis −1; a non-Transportation member is also put in shackles 1.</summary>
    static readonly (string Name, int Value)[] KneelStates =
        [("betrayal_target", 1), ("defeated", 1), ("post_emerge", 3), ("walk_emerge_ended", 1), ("stasis", -1)];

    internal sealed class Live(BetrayalRun run)
    {
        public readonly BetrayalRun Run = run;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public StateMachineData? Kneel;
    }

    readonly ConditionalWeakTable<InstanceState, Live> lives = new();

    /// <summary>Drops the module's state (module unload); the instance's monsters stay as the area's own.</summary>
    public void Clear() => lives.Clear();

    Live? LiveOf(Player player) => player.Instance is { } instance && lives.TryGetValue(instance, out var live) ? live : null;

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static List<Player> Clients(Player player) => [.. player.Instance.PlayersExcept(player.Id).Prepend(player)
        .Where(c => Current(c) && ReferenceEquals(c.Instance, player.Instance)).Distinct()];

    /// <summary>Sends to everyone in the instance; the player whose entry populates it may not count as in the area yet.
    /// Each recipient gets its own copy: the wire encrypts the buffer in place, so sharing one array across clients
    /// would send peer #2+ peer #1's ciphertext re-encrypted with their own keystream (review #1379 point 1).</summary>
    static async Task Broadcast(Player player, byte[] message, string log)
    {
        var clients = Clients(player);
        if (!clients.Contains(player) && !player.Closed) clients.Insert(0, player);
        foreach (var member in clients) await Send(member, message.ToArray(), log);
    }

    static double Distance((int X, int Y) a, (int X, int Y) b) => double.Hypot(a.X - b.X, a.Y - b.Y);

    // ---- the area ------------------------------------------------------------------------------------------------

    /// <summary>A new instance's monsters are placed (once per instance): the roll says whether it has a mission; its member
    /// and spot are drawn, its guards spawned, Jun and the carts created asleep.</summary>
    public async Task Place(AreaPopulated area)
    {
        var player = area.Player;
        var layout = player.Layout;
        if (player.Instance is not { } instance || layout.Walkable is not { } walkable || lives.TryGetValue(instance, out _)) return;
        var rng = new Random(area.Seed ^ 0x4a756e21);
        if (!BetrayalPlan.Rolled(layout.Id, AreaLeagueStats(player), MapDevices.IsMapArea(layout.Hash), rng,
            LeagueFor(player).HardMode, BetrayalPlan.ForcedChance(Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES")))) return;
        if (Members.Count == 0) return;
        var member = Members[rng.Next(Members.Count)];
        bool Floor(int x, int y) => x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH && layout.WalkableFor(x, y, 0);
        var transitions = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        var taken = area.Taken.ToList();
        var arrival = AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY));
        BetrayalPlan.Plan? plan = null;
        for (int tries = 0; tries < 8 && plan is null; tries++)
        {
            if (AreaPopulation.EncounterSpot(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY), transitions, taken, rng)
                is not { } spot) break;
            taken.Add(spot);
            if (arrival is not null && !arrival[spot.Y * layout.GridW + spot.X]) continue;
            plan = BetrayalPlan.Place(member, spot, Floor, rng);
        }
        if (plan is null) return;
        var run = new BetrayalRun(plan, area.Level);
        var live = new Live(run);
        lives.AddOrUpdate(instance, live);
        foreach (var (variety, cell) in plan.Guards)
            if (Monsters.Find(variety) is { Supported: true } guard)
                await SpawnMonster(player, guard, area.Level, MonsterRarity.Normal, cell.X, cell.Y, bulk: true);
        run.Jun = instance.World.TakeObjectId();
        byte[] jun = BetrayalWire.Create(run.Jun, BetrayalWire.JunType, BetrayalWire.JunBody(plan.Jun, plan.JunRotation, flag: 0));
        // Asleep, as captured, and unwoken: the spot can be over 171 cells (the wake zone) from the arrival, and
        // waking it there crashed the client before (review #1379 point 5). The carts are woken at Start, when a
        // character is within StartDistance; Jun is removed there, so he needs no wake.
        await SendWorldCreate(player, run.Jun, () => jun, $"0x02bb Jun 0x{run.Jun:x} at ({plan.Jun.X}, {plan.Jun.Y})", wake: false, awaitBirth: false);
        foreach (var cell in plan.Carts)
        {
            uint id = instance.World.TakeObjectId();
            run.Carts.Add(id);
            byte[] cart = BetrayalWire.Create(id, BetrayalWire.CartType, BetrayalWire.CartBody(cell, BetrayalPlan.CartRotation(plan.Angle)));
            await SendWorldCreate(player, id, () => cart, $"0x02bb Betrayal cart 0x{id:x}", wake: false, awaitBirth: false);
        }
        // No ruleset broadcast here: at population time only the entering player is in the instance, and that player
        // already gets the ruleset row from EnterMessages (0x001f) and its data from Entered (0x02c9) on this same
        // entry, so broadcasting either would send it twice (review #1379 point 4).
        Console.WriteLine($"[betrayal] {layout.Id}: {plan.Job} with {member.Id} at ({plan.Spot.X}, {plan.Spot.Y}), level {area.Level}");
    }

    // ---- the players ---------------------------------------------------------------------------------------------

    /// <summary>The ruleset row for an entering client before the area's objects (GameEvents.EnterMessages).</summary>
    public IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player) =>
        LiveOf(player)?.Run.RulesetPayload is not null && AssassinationRules is { } row
            ? [($"0x001f ruleset JobAssassinationRules (row {row})", InstanceProtocol.AddRuleset(row))] : [];

    /// <summary>A player entered (a party member, a re-entry through a portal or after a death): the ruleset's current data.
    /// Jun, the carts, the member and its MakeChoice come with the area's objects and monsters.</summary>
    public async Task Entered(Player player)
    {
        if (LiveOf(player)?.Run.RulesetPayload is { } payload && AssassinationRules is { } row)
            await Send(player, InstanceRulesets.Data(row, [payload]), $"0x02c9 JobAssassinationRules {payload}");
    }

    // ---- the tick ------------------------------------------------------------------------------------------------

    /// <summary>Whether the instance has a mission still to advance.</summary>
    public bool Has(Player player) => LiveOf(player) is { } live && live.Run.Current != BetrayalRun.Stage.Done;

    /// <summary>One tick of the mission, by the connection that steps the area's monsters: what is due, the start when a
    /// character comes close, and the kneel when the member is brought down.</summary>
    public async Task Advance(Player player, double seconds)
    {
        if (LiveOf(player) is not { } live || !IsMonsterRunner(player) || !Current(player)) return;
        if (!await live.Gate.WaitAsync(0)) return;
        try
        {
            var run = live.Run;
            foreach (var due in run.Step(seconds)) await Run(player, live, due);
            switch (run.Current)
            {
                case BetrayalRun.Stage.Waiting:
                    double now = AreaTime(player);
                    if (Clients(player).Any(c => c.PlayerLife > 0 && Distance(c.Walk.Position(now), run.Plan.Spot) <= StartDistance)
                        && run.Advance(BetrayalRun.Stage.Waiting))
                        await Start(player, live);
                    break;
                case BetrayalRun.Stage.Fight:
                    if (run.MemberBody is SpawnedMonster body
                        && (body.Dead || body.Life == 0 || body.LifePhases is { } phases && phases.Begin(body.Life, body.MaxLife))
                        && run.Advance(BetrayalRun.Stage.Fight))
                        await Defeat(player, live, body);
                    break;
            }
        }
        finally { live.Gate.Release(); }
    }

    /// <summary>The member comes out at the first cart (or the spot): Jun leaves, the Intervention's ruleset goes active, the
    /// entrance portal opens under the member and the assassination portals around it.</summary>
    async Task Start(Player player, Live live)
    {
        var run = live.Run;
        var plan = run.Plan;
        var instance = player.Instance;
        await SendWorldRemoval(player, run.Jun, $"0x02bd Jun 0x{run.Jun:x} leaves");
        run.Jun = 0;
        // The carts were created asleep at Place; a character is within StartDistance now, inside the wake zone, so
        // wake them before anything updates them (Defeat's cart_finished 0x02bc would otherwise reach a client whose
        // copy never woke). Jun is gone, so he gets no wake.
        foreach (uint cart in run.Carts)
            await SendWorldRecord(player, cart, InstanceProtocol.EnterWakeZone(cart), $"0x01a9 cart 0x{cart:x} enters wake zone");
        if (run.RulesetPayload is { } payload && AssassinationRules is { } row)
            await Broadcast(player, InstanceRulesets.Data(row, [payload]), $"0x02c9 JobAssassinationRules {payload}");
        var cell = plan.MemberCell;
        if (Monsters.All.ElementAtOrDefault(plan.Member.Variety) is { Supported: true } variety)
        {
            uint id = await SpawnMonster(player, variety, run.Level, MonsterRarity.Unique, cell.X, cell.Y, rotation: (float)plan.MemberRotation,
                owned: (_, m) => m.LifePhases = new LifePhaseGate([KneelLifePercent]));
            if (id != 0 && player.Area.Monsters.TryGetValue(id, out var body))
            {
                run.Member = id;
                run.MemberBody = body;
            }
        }
        if (run.Member == 0)
        {
            Console.WriteLine($"[betrayal] {plan.Member.Id} (variety {plan.Member.Variety}) cannot be written; the mission ends");
            run.Advance(BetrayalRun.Stage.Fight);
            run.Advance(BetrayalRun.Stage.Defeated);
            // No Advance(Chosen) here: Leave performs the Chosen -> Done transition, and advancing past it first
            // would make Leave return at once with the carts stranded (review #1379 point 2).
            await Leave(player, live);
            return;
        }
        // A server object, not a monster: a monster id would make AreaObjectRecords skip it as a shared monster.
        run.Portal = instance.World.TakeObjectId();
        byte[] portal = BetrayalWire.Create(run.Portal, BetrayalWire.EffectType, BetrayalWire.PortalBody(cell, plan.MemberRotation, run.Member));
        if (await SendWorldCreate(player, run.Portal, () => portal, $"0x02bb entrance portal 0x{run.Portal:x}", awaitBirth: false))
            run.After(BetrayalRun.PortalFrames, BetrayalRun.Due.RemovePortal);
        foreach (var (tier, at) in plan.Spawners)
        {
            uint id = instance.World.TakeObjectId();
            run.Spawners.Add(id);
            byte[] spawner = BetrayalWire.Create(id, tier == 1 ? BetrayalWire.SpawnerTier1 : BetrayalWire.SpawnerTier2, BetrayalWire.SpawnerBody(at));
            await SendWorldCreate(player, id, () => spawner, $"0x02bb assassination portal 0x{id:x}", awaitBirth: false);
        }
    }

    /// <summary>The member is down: it kneels where it fell (its states and its NPC component as the official server's), stops
    /// fighting, and the MakeChoice stands there. The carts finish, the assassination portals close and go, and the
    /// Intervention's ruleset goes idle.</summary>
    async Task Defeat(Player player, Live live, SpawnedMonster body)
    {
        var run = live.Run;
        var plan = run.Plan;
        var instance = player.Instance;
        var cell = body.Walk.Position(AreaTime(player));
        var variety = Monsters.All.ElementAtOrDefault(plan.Member.Variety);
        if (!body.Dead && body.Life > 0 && variety is not null)
        {
            var components = variety.ComponentsOrBase.ToList();
            int machine = components.IndexOf("StateMachine"), npc = components.IndexOf("NPC");
            if (machine >= 0 && (body.States ?? variety.StateMachine) is { } states)
            {
                foreach (var (name, value) in KneelStates) states = states.Set(name, value);
                if (plan.Job != Job.Transportation)
                {
                    states = states.Set("shackles", 1);
                    run.After(BetrayalRun.ShacklesFrames, BetrayalRun.Due.Shackles);
                }
                await SetStates(player, live, body, (byte)machine, states, "kneels");
            }
            // The NPC component's two fields (InstanceNpcs.DialogueAvailability's): 01 01 at the kneel [capture 4/4].
            if (npc >= 0)
            {
                await SendWorldRecord(player, run.Member, InstanceEntities.ComponentUpdate(run.Member, (byte)npc, [1, 1]), "0x02bc member NPC 01 01");
                // And the instance's copy, so a late joiner or re-entrant reconstructs the member with dialogue
                // available instead of NPC 00 ff (review #1379 point 4). The create blob writes the second byte
                // as 0xff by design (MonsterObject), so the origin carries [01 ff], not the live update's [01 01].
                if (player.Instance.World.MonsterOrigins.TryGetValue(run.Member, out var origin))
                    player.Instance.World.MonsterOrigins[run.Member] = origin with { Object = origin.Object with { HasDialogue = true } };
            }
            body.Aggro = false;
            body.BusyUntil = double.PositiveInfinity;
            // Stop it where it kneels: an in-flight swing would otherwise still complete and hit while it kneels
            // (MonsterActionCurrent checks the generation), and a walk left running would drift the MakeChoice cell.
            body.Walk.StopAt(AreaTime(player));
            body.ActionGeneration++;
            body.SwingGeneration = null;
            body.LastTarget = null;
            body.Target = null;
            // Non-hostile while it kneels, so MonsterTargetsLocked never wakes it back up onto another player in
            // multiplayer. Hostile is init-only, so TemporaryAllyUntil is the runtime lever; it is not a conversion
            // (nothing drives it: the AI pass already skips it while the kneel gate holds, and no minion attack is
            // ever queued for a non-minion). Invulnerable already holds through the kneel gate (LifePhases.Active).
            body.TemporaryAllyUntil = double.PositiveInfinity;
        }
        run.Choice = instance.World.TakeObjectId();
        // The cell the MakeChoice actually stands on: Use walks to this, not to where the member's walk has drifted
        // to by the time the choice is used (review #1379 point 5).
        run.ChoiceCell = cell;
        byte[] choice = BetrayalWire.Create(run.Choice, BetrayalWire.ChoiceType, BetrayalWire.ChoiceBody(cell, plan.MemberRotation, run.Member, plan.Member.Row));
        await SendWorldCreate(player, run.Choice, () => choice, $"0x02bb MakeChoice 0x{run.Choice:x} at ({cell.X}, {cell.Y})", awaitBirth: false);
        var finished = BetrayalWire.CartStates.Set("cart_finished", 1);
        foreach (uint cart in run.Carts)
            await SendWorldRecord(player, cart, InstanceEntities.StateMachineUpdate(cart, BetrayalWire.CartStateMachine, finished), "0x02bc cart finished");
        foreach (uint spawner in run.Spawners)
            await SendWorldRecord(player, spawner, InstanceEntities.TransitionableUpdate(spawner, BetrayalWire.SpawnerTransitionable, BetrayalWire.SpawnerClosed),
                "0x02bc assassination portal closes");
        if (run.Spawners.Count > 0) run.After(BetrayalRun.SpawnersFrames, BetrayalRun.Due.RemoveSpawners);
        if (run.RulesetPayload is { } payload && AssassinationRules is { } row)
            await Broadcast(player, InstanceRulesets.Data(row, [payload]), $"0x02c9 JobAssassinationRules {payload}");
    }

    /// <summary>The member's states change: the update, and the instance's copy for whoever enters later.</summary>
    static async Task SetStates(Player player, Live live, SpawnedMonster body, byte component, StateMachineData states, string what)
    {
        uint id = live.Run.Member;
        live.Kneel = states;
        body.States = states;
        if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
            player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = states } };
        await SendWorldRecord(player, id, InstanceEntities.StateMachineUpdate(id, component, states), $"0x02bc member {what}");
    }

    async Task Run(Player player, Live live, BetrayalRun.Due due)
    {
        var run = live.Run;
        switch (due)
        {
            case BetrayalRun.Due.RemovePortal:
                await SendWorldRemoval(player, run.Portal, $"0x02bd entrance portal 0x{run.Portal:x}");
                break;
            case BetrayalRun.Due.RemoveSpawners:
                foreach (uint id in run.Spawners) await SendWorldRemoval(player, id, $"0x02bd assassination portal 0x{id:x}");
                run.Spawners.Clear();
                break;
            case BetrayalRun.Due.Shackles or BetrayalRun.Due.Interrogate:
                if (run.MemberBody is SpawnedMonster body && live.Kneel is { } kneel
                    && Monsters.All.ElementAtOrDefault(run.Plan.Member.Variety) is { } variety
                    && variety.ComponentsOrBase.ToList().IndexOf("StateMachine") is int machine and >= 0)
                    await SetStates(player, live, body, (byte)machine,
                        due == BetrayalRun.Due.Shackles ? kneel.Set("shackles", 2) : kneel.Set("interrogate", 1),
                        due == BetrayalRun.Due.Shackles ? "shackles 2" : "interrogate");
                break;
            case BetrayalRun.Due.Leave:
                await Leave(player, live);
                break;
        }
    }

    // ---- the choice ----------------------------------------------------------------------------------------------

    /// <summary>A character uses the MakeChoice (GameEvents.ObjectUse): walks up to it, and the choice is made. The choice
    /// window and the Syndicate board are not built, so the choice is the first one, Interrogate: the member's life goes out,
    /// the MakeChoice goes, Jun comes back, and the member leaves after its interrogation.</summary>
    public async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (LiveOf(player) is not { } live || live.Run.Choice == 0 || use.TargetId != live.Run.Choice) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || InGracePeriod(player)) return true;
        var run = live.Run;
        // The MakeChoice's own cell, stored when it was created: the member's walk was stopped at the kneel, but
        // its last position still drifts between ticks, and the choice is where it was put, not where the member is.
        var at = run.ChoiceCell ?? run.Plan.MemberCell;
        if (run.Current != BetrayalRun.Stage.Defeated || !TryBeginObjectWalk(player, requestedAt, at.X, at.Y, ChoiceReach, out var walk))
            return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: run.Choice), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await live.Gate.WaitAsync();
        try
        {
            // Re-read under the gate: the choice may have been used or removed since the check above.
            if (run.Choice == 0 || use.TargetId != run.Choice) return true;
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0
                || !PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), at, ChoiceReach)
                || !run.Advance(BetrayalRun.Stage.Defeated)) return true;
            await Choose(player, live);
        }
        finally
        {
            live.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>The choice is made: the member's Life component goes 00 [capture 4/4: the whole update is that one byte], the
    /// MakeChoice is removed and Jun comes back awake; the interrogation and the leaving follow on the frames.</summary>
    async Task Choose(Player player, Live live)
    {
        var run = live.Run;
        var plan = run.Plan;
        if (Monsters.All.ElementAtOrDefault(plan.Member.Variety) is { } variety
            && variety.ComponentsOrBase.ToList().IndexOf("Life") is int life and >= 0)
        {
            await SendWorldRecord(player, run.Member, InstanceEntities.ComponentUpdate(run.Member, (byte)life, [0]), "0x02bc member Life 00");
            // And the instance's copy, so a late joiner or re-entrant reconstructs the member with Life 00 instead
            // of its kneel-floor life (review #1379 point 4).
            if (player.Instance.World.MonsterOrigins.TryGetValue(run.Member, out var origin))
                player.Instance.World.MonsterOrigins[run.Member] = origin with { Object = origin.Object with { Life = 0 } };
        }
        await SendWorldRemoval(player, run.Choice, $"0x02bd MakeChoice 0x{run.Choice:x}");
        run.Choice = 0;
        run.Jun = player.Instance.World.TakeObjectId();
        byte[] jun = BetrayalWire.Create(run.Jun, BetrayalWire.JunType, BetrayalWire.JunBody(plan.Jun, plan.JunRotation, flag: 2));
        await SendWorldCreate(player, run.Jun, () => jun, $"0x02bb Jun 0x{run.Jun:x} comes back", awaitBirth: false);
        run.After(BetrayalRun.InterrogateFrames, BetrayalRun.Due.Interrogate);
        run.After(BetrayalRun.LeaveFrames, BetrayalRun.Due.Leave);
    }

    /// <summary>The mission ends: the member leaves the fight without dying (it is taken out of the area's monsters), and it,
    /// Jun and the carts are removed together [capture 1/1]. The guards stay, as part of the area.</summary>
    async Task Leave(Player player, Live live)
    {
        var run = live.Run;
        if (!run.Advance(BetrayalRun.Stage.Chosen)) return;
        if (run.Member != 0)
        {
            if (player.Area.Monsters.Remove(run.Member, out var member)) { member.Dead = true; member.Life = 0; member.ActionGeneration++; }
            player.Instance.World.MonsterOrigins.TryRemove(run.Member, out _);
            await SendWorldRemoval(player, run.Member, $"0x02bd member 0x{run.Member:x} leaves");
        }
        if (run.Jun != 0)
        {
            await SendWorldRemoval(player, run.Jun, $"0x02bd Jun 0x{run.Jun:x} leaves");
            // Reset at once: without this a second Leave (or a Start that already removed him) would issue a
            // duplicate SendWorldRemoval for the same id (review #1379 point 2).
            run.Jun = 0;
        }
        foreach (uint cart in run.Carts) await SendWorldRemoval(player, cart, $"0x02bd cart 0x{cart:x}");
        foreach (uint spawner in run.Spawners) await SendWorldRemoval(player, spawner, $"0x02bd assassination portal 0x{spawner:x}");
        run.Spawners.Clear();
        run.Carts.Clear();
    }

    // ---- the kills -----------------------------------------------------------------------------------------------

    /// <summary>A kill (GameEvents.Kill, the character's or a minion's) of the member while it fights: the floor of its life
    /// keeps it from dying, so this is only a kill that went around the floor; the mission goes on to its MakeChoice where it
    /// died. The messages go after the kill's commit.</summary>
    public void Killed(KillContext kill)
    {
        if (LiveOf(kill.Player) is not { } live || !ReferenceEquals(live.Run.MemberBody, kill.Monster)
            || live.Run.MemberBody is not SpawnedMonster body) return;
        kill.Later(async () =>
        {
            await live.Gate.WaitAsync();
            try { if (live.Run.Advance(BetrayalRun.Stage.Fight)) await Defeat(kill.Player, live, body); }
            finally { live.Gate.Release(); }
        });
    }
}
