using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Mercenary;

public sealed partial class MercenaryModule
{
    // ---- the roll -----------------------------------------------------------------------------------------------------------

    /// <summary>The percent chance a map area holds a mercenary. CoreLeagues row 28 is not rolled by <see cref="LeagueChances.Count"/>,
    /// so the area's own WorldAreaLeagueChances column is read (as Delve's sulphite does) and the map's stats are added. The
    /// stat ids are the table's stored values plus one: Mercenaries' disable-from-chance, disable, additional-chance and
    /// chance-increase stats and the forcing <c>map_mercenary_league</c>. 100 for a forced one, 0 for an excluded one.</summary>
    public static long ChanceOf(string areaId, bool hardMode, IReadOnlyDictionary<uint, int> stats)
    {
        int S(uint stat) => stats.GetValueOrDefault(stat);
        if ((S(Stat.MapDisableLeagues) & (1 << LeagueChances.Mercenaries)) != 0 || S(Stat.AtlasKeystoneNoExtraContent) != 0
            || S(Stat.MapDisableMercenary) > 0) return 0;
        if (S(Stat.MapMercenaryLeague) > 0) return 100;
        if (S(Stat.MapDisableMercenaryFromChance) > 0) return 0;
        int baseChance = S(Stat.MapExtraContentHasNoBaseChanceToSpawn) > 0 ? 0
            : LeagueChances.Current.ChanceOf(areaId, LeagueChances.Mercenaries, alternate: hardMode).Chance;
        long chance = baseChance * Math.Max(0L, 100L + S(Stat.MapLeagueBaseChancePlusPctFinal)) / 100
            + S(Stat.MapLeagueContentAdditionalChancePct) + S(Stat.MapMercenaryAdditionalChancePct);
        return Math.Max(0L, chance) * Math.Max(0L, 100L + S(Stat.MapMercenaryChancePlusPct)) / 100;
    }

    /// <summary>A new instance of a map area: roll once, and place the mercenary where the packs leave room. Towns, hideouts and
    /// the campaign's areas are not map areas and roll nothing.</summary>
    async Task Place(AreaPopulated area)
    {
        var player = area.Player;
        var layout = player.Layout;
        if (!MapDevices.IsMapArea(layout.Hash) || layout.Walkable is not { } walkable) return;
        var run = State(player);
        lock (run)
        {
            if (run.Rolled) return;
            run.Rolled = true;
        }
        long chance = ChanceOf(layout.Id, LeagueFor(player).HardMode, AreaLeagueStats(player));
        bool placed = chance >= 100 || chance > 0 && new Random(area.Seed ^ RollTag).Next(100) < chance;
        var rng = new Random(area.Seed ^ PlanTag);
        if (placed && MercenaryTables.Pick(rng) is { } pick
            && AreaPopulation.EncounterSpot(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY),
                [.. layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY))], area.Taken, rng) is { } at)
            await Spawn(player, run, pick.Kind, pick.Build, at, area.Level, area.Seed);
    }

    // ---- the mercenary --------------------------------------------------------------------------------------------------------

    static IReadOnlyList<VisibleItem> GearOf(MercenaryTables.Class kind, MercenaryTables.Build build, byte level, int seed) =>
        MercenaryGear.Of(kind, build, level, new Random(unchecked(seed * 397 ^ build.Row)));

    /// <summary>The variety's own StateMachine with these values, all kept as sent (the client takes the type's default for a state
    /// the update does not mark).</summary>
    static StateMachineData? States(MonsterCatalog.Variety variety, IReadOnlyList<int> values) =>
        variety.StateMachine is { } machine && machine.SharedStates.Count == values.Count
            ? machine with { SharedStates = values, Overridden = Enumerable.Range(0, values.Count).ToHashSet() } : null;

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player).Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, player.Instance)).Distinct();

    /// <summary>A record every player of the instance sees, sent once: in the area's frame a world record already goes to every
    /// client of the area, outside it each member's own connection gets it.</summary>
    static async Task Everyone(Player player, Func<Player, Task> send)
    {
        if (player.Area.Owned && player.Connection.WorldOutput.Started) { await send(player); return; }
        foreach (var member in Members(player)) await send(member);
    }

    /// <summary>Sends what only this player is shown: not kept for a later one.</summary>
    static async Task Privately(Player player, Func<Task> send)
    {
        var previous = ViewOf.Value;
        ViewOf.Value = player;
        try { await send(); }
        finally { ViewOf.Value = previous; }
    }

    static bool Alive(Player player) => !player.Closed && !player.LeavingArea && player.PlayerLife > 0;

    /// <summary>Places the friendly, scripted mercenary: unique, of its class and build, wearing its own gear, with the Mercenary
    /// component's build and the create's team, states and clickable flags the official map create carries. It is the instance's
    /// (<c>instanceOwned</c>), so a player who enters later is given it, with the states it has by then.</summary>
    async Task<bool> Spawn(Player player, Run run, MercenaryTables.Class kind, MercenaryTables.Build build, (int X, int Y) at,
        byte level, int seed)
    {
        if (Monsters.Find(kind.Path) is not { Supported: true } variety || States(variety, MercenaryWire.MetStates) is not { } met)
        {
            return false;
        }
        var gear = GearOf(kind, build, level, seed);
        await SendRulesets(player, kind, gear);
        uint id = await SpawnMonster(player, variety, level, MonsterRarity.Unique, at.X, at.Y, friendly: true, instanceOwned: true,
            scripted: true, ownedLooks: gear, shape: m => m with
            {
                Team = MercenaryWire.MercenaryTeam, StateMachine = met, MercenaryBuild = build.Hash16,
                HasDialogue = true, NpcFlags = 1, InteractionFlags = 1,
            });
        if (id == 0)
        {
            return false;
        }
        if (player.Area.Monsters.TryGetValue(id, out var body))
        {
            body.States = met;
            at = body.Walk.Position(AreaTime(player));
        }
        lock (run)
        {
            run.Stage = Stage.Met; run.Class = kind.Row; run.Build = build.Row; run.X = at.X; run.Y = at.Y;
            run.Level = level; run.Seed = seed; run.Body = id; run.Hired = 0; run.Clock = 0; run.Steps = 0;
        }
        Register(player, run);
        Save(player, run);
        context.Log($"{player.Layout.Id}: mercenary {build.Name} ({kind.House}, {kind.Path.Split('/')[^1]}) at ({at.X}, {at.Y}), level {level}, id {id}");
        return true;
    }

    /// <summary>The ruleset rows of the encounter (0x1f), then their bodies: what the mercenary wears and the area rules' two. Once
    /// per player: the first player gets them as the mercenary is placed, a later one as they enter.</summary>
    async Task SendRulesets(Player player, MercenaryTables.Class kind, IReadOnlyList<VisibleItem> gear)
    {
        if (MercenaryTables.EncounterRulesRow is not { } encounter || MercenaryTables.AreaRulesRow is not { } area) return;
        lock (told)
        {
            if (told.TryGetValue(player, out _)) return;
            told.Add(player, new object());
        }
        var items = MercenaryGear.Visuals(gear);
        await Send(player, InstanceProtocol.AddRuleset(encounter), $"0x001f ruleset MercenaryEncounterRules (row {encounter})");
        await Send(player, InstanceProtocol.AddRuleset(area), $"0x001f ruleset MercenaryAreaRules (row {area})");
        // The official order: the mercenary's own items first, then the area rules' two bodies.
        await Send(player, InstanceRulesets.Data(encounter, MercenaryWire.EncounterRulesBody(MercenaryGear.Attributes(kind), items)),
            $"0x02c9 ruleset {encounter} ({items.Count} items the mercenary wears)");
        await Send(player, InstanceRulesets.Data(area, MercenaryWire.AreaRulesBody1), $"0x02c9 ruleset {area} (1/2)");
        await Send(player, InstanceRulesets.Data(area, MercenaryWire.AreaRulesBody2), $"0x02c9 ruleset {area} (2/2)");
    }

    /// <summary>The mercenary is a monster, so a click on it is a talk with the NPC row of its variety (NPCs has one per
    /// mercenary): registered for this player while it waits.</summary>
    void Register(Player player, Run run)
    {
        if (run.Body == 0 || run.Stage is not (Stage.Met or Stage.Talk) || MercenaryTables.ClassOf(run.Class) is not { } kind
            || Npcs.Find(kind.Path) is not { } npc || npc.Id != kind.Path) return;
        lock (player.Npcs) player.Npcs.TryAdd(run.Body, new SpawnedNpc(npc, run.X, run.Y));
    }

    /// <summary>A player enters an instance that has a mercenary: they are given its rulesets and can talk to it. The monster itself
    /// comes with the instance's own. An instance that was kept and restored brings it back as an ordinary monster under another
    /// id; that one goes and the mercenary is placed again.</summary>
    async Task Enter(Player player)
    {
        if (Peek(player) is not { Stage: not Stage.None } run
            || MercenaryTables.ClassOf(run.Class) is not { } kind || MercenaryTables.BuildOf(run.Build) is not { } build) return;
        if (run.Body != 0 && run.Stage is Stage.Met or Stage.Talk or Stage.Countdown)
        {
            var host = player.Instance.World.Runner ?? player;
            if (!host.Area.Monsters.ContainsKey(run.Body) && !host.Area.DormantMonsters.ContainsKey(run.Body))
            {
                lock (run)
                {
                    if (run.Busy) return;
                    run.Busy = true;
                }
                try
                {
                    foreach (var (id, monster) in player.Area.Monsters.ToArray())
                        if (monster.Variety?.Id == kind.Path && !monster.Hostile) await Remove(player, id);
                    if (!await Spawn(player, run, kind, build, (run.X, run.Y), run.Level, run.Seed)) return;
                }
                finally { lock (run) run.Busy = false; }
            }
            Register(player, run);
        }
        await SendRulesets(player, kind, GearOf(kind, build, run.Level, run.Seed));
    }

    async Task Remove(Player player, uint id)
    {
        player.Instance.World.MonsterOrigins.TryRemove(id, out _);
        foreach (var member in Members(player))
        {
            member.Area.Monsters.Remove(id);
            lock (member.Npcs) member.Npcs.Remove(id);
        }
        await Everyone(player, member => SendWorldRemoval(member, id, $"0x02bd remove mercenary 0x{id:x}"));
    }

    static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i] == value) return i;
        return -1;
    }

    /// <summary>The body's StateMachine to these values: for the instance's record (a later player), the server's monster and every
    /// client's component.</summary>
    async Task SetStates(Player player, uint id, int[] values, string what)
    {
        if (!player.Area.Monsters.TryGetValue(id, out var body) || body.Variety is not { } variety
            || States(variety, values) is not { } states) return;
        int component = IndexOf(variety.ComponentsOrBase, "StateMachine");
        if (component < 0) return;
        if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
            player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = states } };
        foreach (var member in Members(player))
            if (member.Area.Monsters.TryGetValue(id, out var shown)) shown.States = states;
        // A client far from it has put its copy to sleep, and a component update for a sleeping object closed the client: the
        // instance's record above gives it the states when it creates the mercenary.
        await Everyone(player, member => !member.Area.Monsters.TryGetValue(id, out var shown) || shown.Asleep ? Task.CompletedTask
            : SendWorldRecord(member, id, InstanceEntities.StateMachineUpdate(id, (byte)component, states),
                $"0x02bc mercenary 0x{id:x} StateMachine {what}"));
    }

    // ---- the press, the choice, the countdown ---------------------------------------------------------------------------------

    /// <summary>The press: the panel (0x7b), the mark (0x285) and the talk states. Which player pressed is the only one shown the panel.</summary>
    async Task<bool> Talk(Player player, uint id, SpawnedNpc npc)
    {
        if (Peek(player) is not { } run || run.Body != id || run.Stage is not (Stage.Met or Stage.Talk)) return false;
        if (!Alive(player)) return true;
        bool first;
        lock (run)
        {
            first = run.Stage == Stage.Met;
            run.Stage = Stage.Talk;
        }
        await Send(player, InstanceWaypoints.OpenUi(MercenaryWire.TalkPanelKind, id), $"0x007b mercenary 0x{id:x} panel");
        await Privately(player, () => SendWorldRecord(player, id,
            MercenaryWire.Mark(id, 0, 0, MercenaryWire.TalkValue), $"0x0285 mercenary 0x{id:x} talk mark"));
        if (first)
        {
            await SetStates(player, id, MercenaryWire.TalkStates, "(1, 0, 1, 1): its panel is open");
            Save(player, run);
        }
        return true;
    }

    /// <summary>C→S 0x2ca: the panel's duel choice starts the countdown. An action of another ruleset, another choice, or one with no
    /// open panel is not this module's.</summary>
    async Task<bool> Choose(Player player, byte[] message)
    {
        byte row;
        byte[] payload;
        try { (row, payload) = InstanceRulesets.ReadAction(message); }
        catch (InvalidDataException) { return false; }
        if (row != MercenaryTables.EncounterRulesRow || !payload.AsSpan().SequenceEqual(MercenaryWire.DuelChoice)
            || Peek(player) is not { Stage: Stage.Talk } run) return false;
        if (!Alive(player)) return true;
        lock (run)
        {
            if (run.Stage != Stage.Talk) return true;
            run.Stage = Stage.Countdown;
            run.Clock = 0;
            run.Steps = 0;
        }
        await SetStates(player, run.Body, MercenaryWire.CountdownStates, "(7, 0, 1, 1): the duel was chosen");
        Save(player, run);
        return true;
    }

    /// <summary>The countdown, on the frames of the connection that steps the instance's monsters: the second mark,
    /// the quiet state, then the switch to the duel, at the frames the official duels show.</summary>
    async Task Tick(Player player, double seconds)
    {
        if (player.Paused || Peek(player) is not { Stage: Stage.Countdown } run || !IsMonsterRunner(player)) return;
        lock (run)
        {
            if (run.Stage != Stage.Countdown || run.Busy) return;
            run.Busy = true;
        }
        try
        {
            run.Clock += seconds;
            while (run.Steps < MercenaryWire.CountdownFrames.Length
                && run.Clock >= MercenaryWire.CountdownFrames[run.Steps] * MercenaryWire.FrameSeconds)
            {
                int step = run.Steps++;
                uint id = run.Body;
                if (step == 0)
                    await Everyone(player, member => SendWorldRecord(member, id,
                        MercenaryWire.Mark(id, 0, 0, MercenaryWire.CountdownValue),
                        $"0x0285 mercenary 0x{id:x} countdown mark"));
                else if (step == 1) await SetStates(player, id, MercenaryWire.QuietStates, "(7, 0, 0, 1): the countdown");
                else if (!await StartDuel(player, run))
                {
                    run.Steps = step;   // tried again on the next frame
                    break;
                }
            }
        }
        finally { lock (run) run.Busy = false; }
    }

    /// <summary>The switch: the friendly mercenary is replaced by a hostile one of its variety (a monster's scripted and hostile
    /// flags are fixed when it is made), in the duel states, with the same gear, at the same cell. It fights with its variety's own
    /// kit.</summary>
    async Task<bool> StartDuel(Player player, Run run)
    {
        if (MercenaryTables.ClassOf(run.Class) is not { } kind || MercenaryTables.BuildOf(run.Build) is not { } build
            || Monsters.Find(kind.Path) is not { Supported: true } variety || States(variety, MercenaryWire.DuelStates) is not { } duel
            || !Alive(player)) return false;
        uint old = run.Body;
        uint id = await SpawnMonster(player, variety, run.Level, MonsterRarity.Unique, run.X, run.Y,
            ownedLooks: GearOf(kind, build, run.Level, run.Seed), shape: m => m with
            {
                StateMachine = duel, MercenaryBuild = build.Hash16, HasDialogue = false, NpcFlags = 0,
            });
        if (id == 0) return false;
        if (player.Area.Monsters.TryGetValue(id, out var body)) body.States = duel;
        await Remove(player, old);
        lock (run)
        {
            run.Stage = Stage.Duel;
            run.Body = id;
        }
        Save(player, run);
        context.Log($"{player.Layout.Id}: mercenary 0x{old:x} duels as 0x{id:x}");
        return true;
    }

    // ---- the end -------------------------------------------------------------------------------------------------------------------

    /// <summary>The duel opponent's death: the <c>*Allied</c> mercenary stands where it fell.</summary>
    void OnKill(KillContext kill)
    {
        if (Peek(kill.Player) is not { Stage: Stage.Duel } run || MercenaryTables.ClassOf(run.Class) is not { } kind
            || kill.VarietyId != kind.Path) return;
        uint bodyId;
        lock (run) bodyId = run.Body;
        if (bodyId != 0 && kill.Player.Area.Monsters.TryGetValue(bodyId, out var body) && !ReferenceEquals(body, kill.Monster)) return;
        lock (run)
        {
            if (run.Stage != Stage.Duel || run.Body != bodyId) return;
            run.Stage = Stage.Hired;
        }
        Save(kill.Player, run);
        kill.Later(() => Hire(kill.Player, run, kill.At));
    }

    /// <summary>The hire, as the official one is created: the <c>*Allied</c> variety of the class in the states (0, 1, 0, 1), team
    /// 0x8001 and the same build. It stands where the duel ended: that it follows and fights for the player, and is bound to
    /// them, is not built.</summary>
    async Task Hire(Player player, Run run, (int X, int Y) at)
    {
        if (MercenaryTables.ClassOf(run.Class) is not { } kind || MercenaryTables.BuildOf(run.Build) is not { } build
            || Monsters.Find(kind.AlliedPath) is not { Supported: true } allied
            || States(allied, MercenaryWire.HiredStates) is not { } hired) return;
        uint id = await SpawnMonster(player, allied, run.Level, MonsterRarity.Unique, at.X, at.Y, friendly: true, instanceOwned: true,
            scripted: true, ownedLooks: GearOf(kind, build, run.Level, run.Seed), shape: m => m with
            {
                Team = MercenaryWire.AlliedTeam, StateMachine = hired, MercenaryBuild = build.Hash16, NpcFlags = 0, InteractionFlags = 1,
            });
        if (id == 0) return;
        if (player.Area.Monsters.TryGetValue(id, out var body)) body.States = hired;
        lock (run) run.Hired = id;
        Save(player, run);
        context.Log($"{player.Layout.Id}: mercenary hired as 0x{id:x} ({allied.Id})");
    }
}
