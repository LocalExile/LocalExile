using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Bestiary;

/// <summary>
/// Einhar's Bestiary mission in an area instance: the red beast placed away from the packs with its minions walking in once a
/// character is close, the yellow beasts, Einhar arriving when a beast is first brought low, the capture of each beast at its
/// kill, and Einhar leaving after the last. The mission belongs to the instance (<see cref="BestiaryRun"/> and its
/// <see cref="Live"/> part): it is placed once when the area is populated, every player in the instance sees and drives the
/// same one, and it ends with the instance, not with a player leaving. The monsters and Einhar are the instance's own.
/// </summary>
public sealed class BestiaryEncounter
{
    /// <summary>Einhar's monster type.</summary>
    public const string EinharType = "Metadata/Monsters/Masters/Einhar";

    /// <summary>The mission's quest flags (QuestFlags Ids): Received when Einhar arrives, Complete at the last capture. Both
    /// are session flags (SessionQuestFlags), so each player's follow the instance's mission, not their save.</summary>
    public const string ReceivedFlag = "EinharMission-Received";
    public const string CompleteFlag = "EinharMission-Complete";

    /// <summary>Einhar's StateMachine state that detaches his exclamation mark (his type's on_state_mission_1).</summary>
    public const string MissionState = "mission";

    /// <summary>The wave starts when a character is this close to the living red beast.</summary>
    public const int WaveNear = 50;
    /// <summary>How many minions walk in [capture: 20 and 24], one every <see cref="WaveGapFrames"/> frames, within
    /// <see cref="WaveRadius"/> cells of the red beast; each is magic at <see cref="WaveMagicChance"/>.</summary>
    public const int WaveMin = 20, WaveMax = 24, WaveGapFrames = 7, WaveRadius = 85;
    public const double WaveMagicChance = 0.16;
    /// <summary>Einhar arrives this far from the character towards the beast.</summary>
    public const int EinharDistance = 50;
    /// <summary>Einhar teleports once every character is farther than this from him, to within <see cref="FollowNear"/> of the
    /// nearest.</summary>
    public const int FollowDistance = 100, FollowNear = 10;
    /// <summary>Frames after the last capture before Einhar leaves.</summary>
    public const int LeaveFrames = 120;
    /// <summary>Einhar arrives when a mission beast first falls to this much of its life.</summary>
    public const int ArrivalLifePercent = 50;
    /// <summary>One frame at 30 fps.</summary>
    public const double FrameSeconds = 1.0 / 30;

    /// <summary>The instance's mission with what it needs to run: Einhar, the wave and the effects up.</summary>
    internal sealed class Live(BestiaryRun run, Random rng)
    {
        public readonly BestiaryRun Run = run;
        public readonly Random Rng = rng;
        public readonly object Gate = new();
        public BestiaryTables.RedKind? Red;
        public (int X, int Y) RedCell;
        public bool[]? RedReach;
        public uint Einhar;
        public int EinharNpc = -1;
        public bool WaveStarted, Left, Done;
        public int WaveTotal, WaveDone;
        public double Clock, WaveClock;
        public double? CompletedAt;
        public readonly List<(uint Id, double RemoveAt)> Effects = [];
    }

    readonly ConditionalWeakTable<InstanceState, Live> lives = new();

    /// <summary>Drops the module's tables (module unload); the instance's monsters stay as the area's own.</summary>
    public void Clear() => lives.Clear();

    Live? LiveOf(Player player) => player.Instance is { } instance && lives.TryGetValue(instance, out var live) ? live : null;

    /// <summary>Whether the instance has a mission still to advance.</summary>
    public bool Has(Player player) => LiveOf(player) is { Done: false };

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static IEnumerable<Player> Clients(Player player) =>
        InGame.Keys.Where(c => !c.Controlled && ReferenceEquals(c.Instance, player.Instance) && Current(c));

    // ---- the roll ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the area has Einhar's mission. CoreLeagues row 11 (Bestiary) is a master mission the area's league chances do
    /// not roll (+0xa0 set, so <see cref="LeagueChances.Count"/> gives none for any area): its row names the stats instead
    /// [data: DisableStat map_disable_missions, LeagueChanceStat map_master_mission_einhar_%_chance, BaseLeagueChanceStat
    /// map_master_mission_einhar_chance_+%, ForcedSpawnStat map_bestiary_league], which a map's Atlas passives and mods give
    /// (<see cref="AreaLeagueStats"/>; none outside a map). The chance is increased as the row says, chance × (100 + increased)
    /// / 100 [ESTIMATE for this row, as Alva's]. The mission is off where the map disables missions or the Bestiary league
    /// (MapDisableLeagues bit 11) or has no extra content (AtlasKeystoneNoExtraContent). <c>Game_LEAGUE_CHANCES=Bestiary=N</c>
    /// is a test's chance for every populated area.
    /// </summary>
    public static bool Rolled(IReadOnlyDictionary<uint, int> stats, bool isMap, Random rng, int? forced = null)
    {
        int S(uint stat) => stats.GetValueOrDefault(stat);
        if (S(Stat.MapDisableMissions) > 0 || (S(Stat.MapDisableLeagues) & (1 << BestiaryTables.LeagueRow)) != 0
            || S(Stat.AtlasKeystoneNoExtraContent) > 0) return false;
        int chance;
        if (forced is int test) chance = test;
        else
        {
            if (!isMap) return false;
            if (S(Stat.MapBestiaryLeague) > 0) return true;
            chance = S(Stat.MapMasterMissionEinharPctChance) * (100 + S(Stat.MapMasterMissionEinharChancePlusPct)) / 100;
        }
        chance = Math.Clamp(chance, 0, 100);
        return chance >= 1 && (chance >= 100 || rng.Next(100) < chance);
    }

    /// <summary>The test chance <c>Game_LEAGUE_CHANCES</c> gives the Bestiary row, as <see cref="LeagueChances"/> reads it for the
    /// rows it rolls.</summary>
    public static int? ForcedChance(string? setting)
    {
        foreach (var entry in (setting ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals("Bestiary", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    // ---- the area ------------------------------------------------------------------------------------------------

    /// <summary>A new instance's monsters are placed: the roll says whether it has a mission, and the level's mission row decides
    /// the red beast and the yellow beasts, each at a spot of its own, reachable from where the character arrives.</summary>
    public async Task Place(AreaPopulated area)
    {
        var player = area.Player;
        var layout = player.Layout;
        if (player.Instance is not { } instance || layout.Walkable is not { } walkable) return;
        var rng = new Random(area.Seed ^ 0x42657374);
        if (!Rolled(AreaLeagueStats(player), MapDevices.IsMapArea(layout.Hash), rng, ForcedChance(Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES")))) return;
        if (BestiaryTables.PlanFor(layout.Id, area.Level, rng) is not { } plan) return;
        var transitions = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        var taken = area.Taken.ToList();
        var arrival = AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY));
        (int X, int Y)? Spot()
        {
            for (int tries = 0; tries < 8; tries++)
            {
                if (AreaPopulation.EncounterSpot(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY), transitions, taken, rng) is not { } at) return null;
                taken.Add(at);
                if (arrival is null || arrival[at.Y * layout.GridW + at.X]) return at;
            }
            return null;
        }
        var run = new BestiaryRun { MissionId = plan.Mission.Id, Level = area.Level };
        var live = new Live(run, rng);
        async Task Spawn(ushort row, MonsterRarity rarity, (int X, int Y) at, bool red)
        {
            if (Monsters.All[row] is not { Supported: true } variety) return;
            uint id = await SpawnMonster(player, variety, area.Level, rarity, at.X, at.Y, bulk: true);
            if (id != 0 && player.Area.Monsters.TryGetValue(id, out var body)) run.Add(id, body, red);
        }
        foreach (var red in plan.Reds)
            if (Spot() is { } at)
            {
                int before = run.Total;
                await Spawn(red.Row, MonsterRarity.Rare, at, red: true);
                if (run.Total > before && live.Red is null)
                {
                    live.Red = red;
                    live.RedCell = at;
                    live.RedReach = AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, at);
                }
            }
        for (int i = 0; i < plan.Yellow; i++)
            if (Spot() is { } at)
                await Spawn(BestiaryTables.Draw(plan.YellowPool, rng), MonsterRarity.Rare, at, red: false);
        if (run.Total == 0) return;
        lives.AddOrUpdate(instance, live);
        Console.WriteLine($"[bestiary] {layout.Id}: mission {plan.Mission.Id}, {run.Total} beast(s) ({plan.Reds.Count} red), level {area.Level}");
    }

    // ---- the players ---------------------------------------------------------------------------------------------

    /// <summary>A player entered (the first time, again by portal or after death, or as a second party member): their mission
    /// flags follow the instance's mission. The monsters, Einhar among them, are the instance's own and come with the join.</summary>
    public Task Entered(Player player) => SyncFlags(player, LiveOf(player)?.Run);

    /// <summary>A player leaves: the session flags go with them, the mission stays with the instance.</summary>
    public Task Leaving(Player player) => SyncFlags(player, null);

    static async Task SyncFlags(Player player, BestiaryRun? run)
    {
        bool received = run?.Received == true, complete = run?.Complete == true;
        foreach (var (flag, on) in new[] { (ReceivedFlag, received), (CompleteFlag, complete) })
            if (ClientHasQuestFlag(player, flag) != on) await SetQuestFlags(player, [flag], on);
    }

    static async Task SetFlagsForAll(Player player, BestiaryRun run)
    {
        foreach (var peer in Clients(player)) await SyncFlags(peer, run);
    }

    // ---- the tick ------------------------------------------------------------------------------------------------

    /// <summary>One tick of the mission, by the connection that steps the area's monsters: takes down spent effects, brings
    /// Einhar when a beast is low, makes him follow, walks the red beast's minions in, and sends Einhar away after the last
    /// capture.</summary>
    public async Task Advance(Player player, double seconds)
    {
        if (LiveOf(player) is not { Done: false } live || !IsMonsterRunner(player) || !Current(player)) return;
        live.Clock += seconds;
        var run = live.Run;
        await ExpireEffects(player, live);
        if (live.Left)
        {
            lock (live.Gate) if (live.Effects.Count == 0) live.Done = true;
            return;
        }
        double now = AreaTime(player);
        // A beast that died without a kill reaching the mission (it is gone either way) counts as taken, and one brought low
        // brings Einhar.
        foreach (var beast in run.Beasts.Where(b => !b.Captured))
            if (beast.Body is SpawnedMonster body)
            {
                if (body.Dead || body.Life == 0) await Taken(player, live, run.Kill(body), body.Walk.Position(now));
                else if (body.MaxLife > 0 && (ulong)body.Life * 100 <= (ulong)body.MaxLife * ArrivalLifePercent && run.TryArrive())
                    await ArriveEinhar(player, live, body.Walk.Position(now));
            }
        if (live.CompletedAt is { } completed && live.Clock - completed >= LeaveFrames * FrameSeconds)
        {
            await Leave(player, live);
            return;
        }
        await FollowEinhar(player, live, now);
        if (live.CompletedAt is not null || live.Red is not { } red) return;
        if (!live.WaveStarted)
        {
            if (!run.Beasts.Any(b => b.Red && !b.Captured) || !Clients(player).Any(c => Distance(c.Walk.Position(now), live.RedCell) <= WaveNear)) return;
            live.WaveTotal = live.Rng.Next(WaveMin, WaveMax + 1);
            live.WaveStarted = true;
            live.WaveClock = live.Clock;
            return;
        }
        if (live.WaveDone >= live.WaveTotal || red.Minions.Count == 0 || live.Clock - live.WaveClock < WaveGapFrames * FrameSeconds) return;
        live.WaveClock = live.Clock;
        double angle = live.Rng.NextDouble() * 2 * Math.PI, dist = live.Rng.NextDouble() * WaveRadius;
        var rarity = live.Rng.NextDouble() < WaveMagicChance ? MonsterRarity.Magic : MonsterRarity.Normal;
        var layout = player.Layout;
        var cell = layout.Near(((int)Math.Round(live.RedCell.X + dist * Math.Cos(angle)), (int)Math.Round(live.RedCell.Y + dist * Math.Sin(angle))));
        // Only a cell the red beast's own ground reaches: a minion behind a wall would never arrive.
        if (!layout.WalkableFor(cell.X, cell.Y, 0) || live.RedReach is { } reach && !reach[cell.Y * layout.GridW + cell.X]) return;
        if (Monsters.All[BestiaryTables.Draw(red.Minions, live.Rng)] is { Supported: true } minion
            && await SpawnMonster(player, minion, run.Level, rarity, cell.X, cell.Y, alone: false) != 0) live.WaveDone++;
    }

    static double Distance((int X, int Y) a, (int X, int Y) b) => double.Hypot(a.X - b.X, a.Y - b.Y);

    /// <summary>Einhar follows: once every character is farther than <see cref="FollowDistance"/> from him he teleports to within
    /// <see cref="FollowNear"/> of the nearest, a disappear effect at each end first. His cell is where his walk puts him now.</summary>
    async Task FollowEinhar(Player player, Live live, double now)
    {
        if (live.Einhar == 0 || !player.Area.Monsters.TryGetValue(live.Einhar, out var einhar) || einhar.Dead) return;
        var from = einhar.Walk.Position(now);
        var clients = Clients(player).ToList();
        if (clients.Count == 0) return;
        var target = clients.MinBy(c => Distance(c.Walk.Position(now), from))!;
        var (x, y) = target.Walk.Position(now);
        if (Distance((x, y), from) <= FollowDistance) return;
        double towards = Math.Atan2(from.Y - y, from.X - x);
        var layout = player.Layout;
        var near = layout.Near(((int)Math.Round(x + FollowNear * Math.Cos(towards)), (int)Math.Round(y + FollowNear * Math.Sin(towards))));
        if (!layout.WalkableFor(near.X, near.Y, 0)) return;
        await SendEffect(player, live, BestiaryEffects.TeleportBody(near, live.Rng.NextDouble() * 2 * Math.PI, live.Einhar),
            BestiaryEffects.EffectLifeFrames, "teleport to");
        await SendEffect(player, live, BestiaryEffects.TeleportBody(from, live.Rng.NextDouble() * 2 * Math.PI, live.Einhar),
            BestiaryEffects.EffectLifeFrames, "teleport from");
        einhar.Walk.Teleport(near.X, near.Y, now);
        await SendWorldRecord(player, live.Einhar, InstanceProtocol.Teleport(live.Einhar, near.X, near.Y),
            $"0x01b7 Einhar teleports to ({near.X}, {near.Y})");
    }

    // ---- the kills -----------------------------------------------------------------------------------------------

    /// <summary>A kill (<see cref="GameEvents.Kill"/>, the character's or a minion's): a mission beast taken is captured, with
    /// the loot, experience and flask charges the kill already gave. The messages go after the kill's commit.</summary>
    public void Killed(KillContext kill)
    {
        if (LiveOf(kill.Player) is not { Done: false } live || live.Run.Kill(kill.Monster) is not { } capture) return;
        kill.Later(() => Taken(kill.Player, live, capture, kill.At));
    }

    /// <summary>One capture, however the beast was taken: Einhar's arrival on the first, the harpoon, and on the last the
    /// Complete flag for everyone in the instance and Einhar's mission-complete dialogue.</summary>
    async Task Taken(Player player, Live live, BestiaryRun.Capture? taken, (int X, int Y) cell)
    {
        if (taken is not { } capture) return;
        if (capture.First) await ArriveEinhar(player, live, cell);
        await SendEffect(player, live, BestiaryEffects.HarpoonBody(cell, live.Rng.NextDouble() * 2 * Math.PI), BestiaryEffects.HarpoonLifeFrames, "harpoon");
        if (!capture.Last) return;
        await SetFlagsForAll(player, live.Run);
        if (live.Einhar != 0 && live.EinharNpc >= 0)
        {
            if (player.Instance.World.MonsterOrigins.TryGetValue(live.Einhar, out var origin))
                player.Instance.World.MonsterOrigins[live.Einhar] = origin with { Object = origin.Object with { HasDialogue = true } };
            await SendWorldRecord(player, live.Einhar, InstanceNpcs.DialogueAvailability(live.Einhar, (byte)live.EinharNpc, true),
                "0x02bc Einhar NPC dialogue available (mission complete)");
        }
        live.CompletedAt = live.Clock;
    }

    // ---- Einhar --------------------------------------------------------------------------------------------------

    /// <summary>Sends a ServerEffect create at an instance object id and remembers it for its removal after its life. A
    /// presentation object nothing later depends on: its producer does not wait for the birth.</summary>
    async Task SendEffect(Player player, Live live, byte[] body, int lifeFrames, string what)
    {
        uint id = player.Instance.World.NewMonsterId();
        byte[] create = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(BestiaryEffects.EffectType), body);
        double removeAt;
        lock (live.Gate)
        {
            removeAt = live.Clock + lifeFrames * FrameSeconds;
            live.Effects.Add((id, removeAt));
        }
        if (!await SendWorldCreate(player, id, () => create, $"0x02bb create {what} (ServerEffect)", awaitBirth: false))
            lock (live.Gate) live.Effects.RemoveAll(e => e.Id == id);
    }

    static async Task ExpireEffects(Player player, Live live)
    {
        List<(uint Id, double RemoveAt)> due;
        lock (live.Gate)
        {
            due = [.. live.Effects.Where(e => live.Clock >= e.RemoveAt)];
            live.Effects.RemoveAll(e => live.Clock >= e.RemoveAt);
        }
        foreach (var (id, _) in due) await SendWorldRemoval(player, id, $"0x02bd effect 0x{id:x} removed");
    }

    /// <summary>Einhar arrives <see cref="EinharDistance"/> cells from the nearest character towards the beast, a disappear
    /// effect at his cell first. Everyone in the instance has the mission received now (the flag goes through the game's own
    /// setter, which keeps the character's other flags). He is the instance's own allied fighter: he fights beside the
    /// characters, and his state says the mission is on, which takes the exclamation mark off him.</summary>
    async Task ArriveEinhar(Player player, Live live, (int X, int Y) towards)
    {
        await SetFlagsForAll(player, live.Run);
        if (Monsters.Find(EinharType) is not { Supported: true } einhar)
        {
            Console.WriteLine($"[bestiary] {EinharType} cannot be written; Einhar does not come");
            return;
        }
        var layout = player.Layout;
        double now = AreaTime(player);
        var (px, py) = Clients(player).Select(c => c.Walk.Position(now)).DefaultIfEmpty(player.Walk.Position(now))
            .MinBy(p => Distance(p, towards));
        double angle = Math.Atan2(towards.Y - py, towards.X - px);
        var cell = layout.Near(((int)Math.Round(px + EinharDistance * Math.Cos(angle)), (int)Math.Round(py + EinharDistance * Math.Sin(angle))));
        if (!layout.WalkableFor(cell.X, cell.Y, 0)) cell = (px, py);
        uint id = await SpawnMonster(player, einhar, live.Run.Level, MonsterRarity.Normal, cell.X, cell.Y, friendly: true, instanceOwned: true,
            owned: (_, m) => { m.AlliedFighter = true; m.Aggro = true; });
        if (id == 0) return;
        live.Einhar = id;
        int npc = einhar.ComponentsOrBase.ToList().IndexOf("NPC");
        live.EinharNpc = npc >= 0 ? npc : BestiaryEffects.NpcComponent;
        int machine = einhar.ComponentsOrBase.ToList().IndexOf("StateMachine");
        if (machine >= 0 && player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin) && origin.Object.StateMachine is { } states)
        {
            var on = states.Set(MissionState, 1);
            player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = on } };
            if (player.Area.Monsters.TryGetValue(id, out var body)) body.States = on;
            await SendWorldRecord(player, id, InstanceEntities.StateMachineUpdate(id, (byte)machine, on), "0x02bc Einhar mission state 1 (no exclamation mark)");
        }
        await SendEffect(player, live, BestiaryEffects.ArriveBody(cell, live.Rng.NextDouble() * 2 * Math.PI), BestiaryEffects.EffectLifeFrames, "einhar arrives");
    }

    /// <summary>Einhar leaves when the mission ends: a disappear effect at his cell, then his removal. The spawned monsters
    /// stay, as part of the area.</summary>
    async Task Leave(Player player, Live live)
    {
        live.Left = true;
        if (live.Einhar == 0) return;
        uint id = live.Einhar;
        live.Einhar = 0;
        var at = player.Area.Monsters.TryGetValue(id, out var body) ? body.Walk.Position(AreaTime(player)) : player.Walk.Position(AreaTime(player));
        await SendEffect(player, live, BestiaryEffects.LeaveBody(at, id), BestiaryEffects.EffectLifeFrames, "einhar leaves");
        if (player.Area.Monsters.Remove(id, out var einhar)) { einhar.Dead = true; einhar.Life = 0; einhar.ActionGeneration++; }
        player.Instance.World.MonsterOrigins.TryRemove(id, out _);
        await SendWorldRemoval(player, id, $"Einhar 0x{id:x} removed");
    }
}
