using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Abyss;

/// <summary>
/// An instance's Abyss: the pit, the cracks, the arms' monsters, the wave and the reward chest. The plan (the 125-unit
/// lattice, up to three arms of 13-16 pieces turning on the captured chain, a family's monsters, the wave, the chest) is
/// made once when the area is populated and belongs to the instance, not to a player: every client of the instance sees and
/// drives the same encounter, one who enters later is given the area's objects as the others have them, and nothing is torn
/// down when a player leaves. The area's tick (the instance's monster runner) creates each piece as a player comes near and
/// runs it: a kill beside an arm starts it closing from its far end, each closing piece brings its share of the 25 arm
/// monsters, the pit opens once every arm is closed and those monsters are dead, the wave comes out of it, and its death
/// raises the plinth and the chest. Times are the tick's game seconds (the captures count 30 frames to the second).
/// </summary>
public sealed class AbyssEncounter
{
    public enum Stage { Arms, Wave, Reward, Over }

    public sealed class Piece
    {
        public uint Id;
        public byte State = AbyssTables.CrackOpen;
    }

    public sealed class Arm
    {
        public required int Dir;
        public required List<(int X, int Y)> Cells;
        public required List<(string Shape, int Rotation, int Mirror)> Shapes;
        public readonly List<Piece> Pieces = [];
        public double? StartedAt;
        /// <summary>When the pit-side piece starts closing: the pit opening + 1 frame [capture 5/5].</summary>
        public double? AdjacentAt;
    }

    /// <summary>One arm monster: the arm and the closing step that brings it, and its type.</summary>
    public sealed class Slot
    {
        public required int Arm;
        public required int Closing;
        public required string Path;
        public bool Spawned;
    }

    public sealed record Plan((int X, int Y) PitLattice, string PitShape, int PitRotation, string Family, List<Arm> Arms,
        List<Slot> Slots, List<(string Path, bool Elite)> Wave, string? Chest);

    public sealed class State
    {
        public Stage Stage = Stage.Arms;
        public required Plan Plan;
        public required byte Level;
        public required Random Random;
        /// <summary>One tick or one click at a time.</summary>
        public readonly SemaphoreSlim Gate = new(1, 1);
        /// <summary>The area the objects are in: a kept instance generated again has none of them (<see cref="Sync"/>).</summary>
        public GameArea? Area;
        public uint Pit;
        public byte PitState = AbyssTables.PitClosed;
        /// <summary>The pit's walk cell: what its create, the wave, the plinth, the chest and the drop stand on.</summary>
        public (int X, int Y) PitCell;
        /// <summary>The cells the pit's own ground reaches: the arm monsters and the wave stand on them.</summary>
        public bool[]? PitReach;
        public bool PitReachKnown;
        public List<Arm> Arms => Plan.Arms;
        public List<Slot> Slots => Plan.Slots;
        public readonly List<uint> ArmMonsters = [];
        public readonly List<uint> WaveMonsters = [];
        /// <summary>The wave monsters whose spawn was tried (one the server cannot create is skipped).</summary>
        public int WaveSpawned;
        public uint Plinth;
        public uint Chest;
        public double Now;
        public double StageAt;
        public bool PlinthUp;
        public bool ChestReady;
        public bool ChestOpened;
        /// <summary>Kills since the last tick (a kill can come from any client's connection; the tick applies them).</summary>
        public readonly ConcurrentQueue<(int X, int Y)> Kills = new();
    }

    readonly ConditionalWeakTable<InstanceState, State> _states = new();

    /// <summary>The module's unload: the encounters go with it (the objects stay in their areas).</summary>
    public void Clear() => _states.Clear();

    public State? Of(Player player) => player.Instance is { } instance && _states.TryGetValue(instance, out var state) ? state : null;

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    /// <summary>The cells of the area's players (the frame's own list, no scan of every instance), or the stepping player's.</summary>
    static List<(int X, int Y)> Here(Player player, double now)
    {
        var players = player.Frames.Players();
        var cells = new List<(int X, int Y)>(players.Length + 1);
        foreach (var c in players)
            if (!c.Controlled && Current(c)) cells.Add(c.Walk.Position(now));
        if (cells.Count == 0) cells.Add(player.Walk.Position(now));
        return cells;
    }

    static bool Within(IReadOnlyList<(int X, int Y)> clients, (int X, int Y) cell, double distance)
    {
        double squared = distance * distance;
        foreach (var p in clients)
        {
            double dx = p.X - cell.X, dy = p.Y - cell.Y;
            if (dx * dx + dy * dy <= squared) return true;
        }
        return false;
    }

    static bool Within(IReadOnlyList<(int X, int Y)> clients, List<(int X, int Y)> cells, double distance)
    {
        foreach (var cell in cells)
            if (Within(clients, cell, distance)) return true;
        return false;
    }

    // ---- the roll ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the area has an Abyss, from CoreLeagues row 9 as the client reads it: the area's WorldAreaLeagueChances chance
    /// (campaign areas have one too, as the two captures' Acts 6 and 9 areas show; the area is not required to be a map),
    /// raised as the map's stats say (<c>map_league_base_chance_+%_final</c> and the row's own BaseLeagueChanceStat
    /// <c>map_abyss_encounter_chance_+%</c> as increases, <c>map_league_content_additional_chance_%</c> and the row's
    /// LeagueChanceStat <c>map_abyss_chance_%</c> as additions: chance × (100 + increased) / 100 + added [ESTIMATE for the
    /// row's two own stats; the formula is the client's for the general ones]). Off where the map disables the row (its
    /// DisableStat <c>map_disable_breach_abyss</c>, its bit of <c>map_disable_leagues</c>, <c>map_disable_abyss_from_chance</c>
    /// for the roll) or has no extra content; <c>map_spawn_abysses</c> forces one, <c>map_extra_content_has_no_base_chance_
    /// to_spawn</c> takes the base chance away. The row allows ten per area; one is made [ESTIMATE: no capture has two].
    /// <c>Game_LEAGUE_CHANCES=Abyss=N</c> is a test's base chance for every populated area.
    /// </summary>
    public static bool Rolled(IReadOnlyDictionary<uint, int> stats, string areaId, Random rng, int? forced = null)
    {
        int S(uint stat) => stats.GetValueOrDefault(stat);
        if (S(Stat.MapDisableBreachAbyss) > 0 || (S(Stat.MapDisableLeagues) & (1 << AbyssTables.LeagueRow)) != 0
            || S(Stat.AtlasKeystoneNoExtraContent) > 0) return false;
        if (S(Stat.MapSpawnAbysses) > 0) return true;
        if (S(Stat.MapDisableAbyssFromChance) > 0) return false;
        long baseChance = S(Stat.MapExtraContentHasNoBaseChanceToSpawn) > 0 ? 0 : forced ?? LeagueChances.Current.ChanceOf(areaId, LeagueChances.Abyss).Chance;
        long increased = S(Stat.MapLeagueBaseChancePlusPctFinal) + S(Stat.MapAbyssEncounterChancePlusPct);
        long chance = Math.Clamp(baseChance * Math.Max(0, 100 + increased) / 100 + S(Stat.MapLeagueContentAdditionalChancePct) + S(Stat.MapAbyssChancePct), 0, 100);
        return chance >= 1 && (chance >= 100 || rng.Next(100) < chance);
    }

    /// <summary>The test chance <c>Game_LEAGUE_CHANCES</c> gives the Abyss row, as <see cref="LeagueChances"/> reads it.</summary>
    public static int? ForcedChance(string? setting)
    {
        foreach (var entry in (setting ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals("Abyss", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    // ---- the area ---------------------------------------------------------------------------------------------------------

    /// <summary>A new instance's monsters are placed: the roll, and with it the plan around an encounter spot the area's
    /// arrival reaches. Null when there is none (and why, for the log).</summary>
    public string? Place(AreaPopulated area)
    {
        var player = area.Player;
        var layout = player.Layout;
        if (player.Instance is not { } instance || layout.Walkable is not { } walkable) return null;
        // The encounter's own draws, apart from the packs' (the same instance gets the same encounter).
        var rng = new Random(area.Seed ^ 0x41627973);
        if (!Rolled(AreaLeagueStats(player), layout.Id, rng, ForcedChance(Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES")))) return null;
        // Only lattice points the arrival walks to: an arm behind a wall would never be reached.
        var arrival = AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY));
        bool Reaches((int X, int Y) cell) => arrival is null || cell.X >= 0 && cell.Y >= 0 && cell.X < layout.GridW && cell.Y < layout.GridH
            && arrival[cell.Y * layout.GridW + cell.X];
        var taken = area.Taken.ToList();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (AreaPopulation.EncounterSpot(area, taken, rng) is not { } spot) break;
            if (MakePlan(layout, spot, rng, Reaches) is { } plan)
            {
                Begin(player, plan, area.Level, rng);
                return null;
            }
            taken.Add(spot);
        }
        return $"{layout.Id}: no encounter spot had room for an Abyss arm";
    }

    /// <summary>The instance's encounter for a plan: no object is sent until a player comes near.</summary>
    public State Begin(Player player, Plan plan, byte level, Random rng)
    {
        foreach (var arm in plan.Arms)
            for (int i = 0; i < arm.Cells.Count; i++) arm.Pieces.Add(new Piece());
        var state = new State { Plan = plan, Level = level, Random = rng, Area = player.Area, PitCell = LatCell(plan.PitLattice) };
        _states.AddOrUpdate(player.Instance!, state);
        return state;
    }

    /// <summary>A player entered the instance (the first time, again by portal or after death, or as a second party
    /// member). The area's objects reach them through its object records; a kept instance generated again is the area that
    /// has none, and gets them here.</summary>
    public async Task Entered(Player player)
    {
        if (Of(player) is not { } state || !Current(player)) return;
        await state.Gate.WaitAsync();
        try { await Sync(player, state); }
        finally { state.Gate.Release(); }
    }

    /// <summary>The objects the encounter holds, made again in an area that is not the one they were made in, as they
    /// stand now.</summary>
    static async Task Sync(Player player, State state)
    {
        if (ReferenceEquals(state.Area, player.Area)) return;
        bool again = state.Area is not null;
        state.Area = player.Area;
        if (!again) return;
        // The monsters of the old area are gone with it.
        state.ArmMonsters.Clear();
        state.WaveMonsters.Clear();
        if (state.Pit != 0)
        {
            await CreatePit(player, state);
            if (state.PitState == AbyssTables.PitDoneState) await PitIcon(player, state);
        }
        foreach (var arm in state.Arms)
            for (int k = 0; k < arm.Cells.Count; k++)
                if (arm.Pieces[k].Id != 0) await CreateCrack(player, state, arm, k);
        if (state.Plinth != 0)
        {
            await CreatePlinth(player, state);
            if (state.PlinthUp) await PlinthUp(player, state);
        }
        if (state.Chest != 0)
        {
            await CreateChest(player, state);
            if (state.ChestReady) await ChestReady(player, state);
            if (state.ChestOpened) await ChestOpened(player, state);
        }
    }

    // ---- the tick ---------------------------------------------------------------------------------------------------------

    /// <summary>One tick of <paramref name="seconds"/> game seconds, by the connection that steps the area's monsters.</summary>
    public async Task Advance(Player player, double seconds)
    {
        if (Of(player) is not { } state || state.Stage == Stage.Over || !IsMonsterRunner(player) || !Current(player)) return;
        await state.Gate.WaitAsync();
        try
        {
            if (state.Stage == Stage.Over || !Current(player)) return;
            await Sync(player, state);
            state.Now += Math.Max(0, seconds);
            double now = AreaTime(player);
            var here = Here(player, now);
            await Reveal(player, state, here);
            while (state.Kills.TryDequeue(out var at)) OnKill(state, at);
            switch (state.Stage)
            {
                case Stage.Arms:
                    StartArms(state, here);
                    await CloseArms(player, state);
                    if (state.Arms.All(Closed) && state.Slots.All(s => s.Spawned) && !Alive(player, state.ArmMonsters))
                        await PitOpen(player, state);
                    return;
                case Stage.Wave:
                    await CloseArms(player, state);
                    await SpawnWave(player, state);
                    if (state.WaveSpawned == state.Plan.Wave.Count && !Alive(player, state.WaveMonsters))
                    {
                        await Success(player, state);
                        state.Stage = Stage.Reward;
                        state.StageAt = state.Now;
                    }
                    return;
                case Stage.Reward:
                    await CloseArms(player, state);
                    await Reward(player, state);
                    return;
            }
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>A kill (<see cref="GameEvents.Kill"/>: the character's or a minion's), applied at the next tick.</summary>
    public void Killed(KillContext kill)
    {
        if (Of(kill.Player) is { Stage: Stage.Arms } state) state.Kills.Enqueue(kill.At);
    }

    static void OnKill(State state, (int X, int Y) at)
    {
        if (state.Stage != Stage.Arms) return;
        foreach (var arm in state.Arms)
            if (arm.StartedAt is null && Within([at], arm.Cells, AbyssTables.InfluenceDistance))
                arm.StartedAt = state.Now;
    }

    /// <summary>A click on the reward chest: walk up to it, open it only in reach with the action still current, and
    /// always end the action. The open is the captured 0x1bd click (to the opener), the chest's component 5 and
    /// Transitionable 3, then the drop. False when the click was not this encounter's chest.</summary>
    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Of(player) is not { Chest: not 0 } state || use.TargetId != state.Chest) return false;
        // A dead, leaving or just-arrived player's click is the encounter's too, and does nothing.
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || InGracePeriod(player)) return true;
        if (state.ChestOpened) return true;
        if (!state.ChestReady)
        {
            // Only the action this click would have ended: a later one is not cancelled.
            int current = player.ActionCount;
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, current));
            return true;
        }
        var at = state.PitCell;
        int size = ChestInfo() is { } info ? info.Size : AbyssTables.ChestSizeFallback;
        if (!TryBeginObjectWalk(player, requestedAt, at.X, at.Y, size, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)   // a lockstep client got its start from the frame pass
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id, here.X, here.Y,
                at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player opens Abyss chest 0x{state.Chest:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
                still: () => PlayerActionCurrent(player, action));
        try
        {
            if (arrive > now) await AreaDelay(player, arrive - now);
            // The encounter's gate first (the tick and the other openers use it), then the claim, with the player where the
            // walk really left them.
            await state.Gate.WaitAsync();
            try
            {
                if (state.ChestOpened || !state.ChestReady || !PlayerActionCurrent(player, action) || player.PlayerLife == 0
                    || player.Closed || player.LeavingArea
                    || !PlayerWalk.InReach(player.Walk.Position(AreaTime(player)), at, size, player.Layout.Walkable)) return true;
                state.ChestOpened = true;
                // The click is the opener's own message (first in the frame, before the open), not the chest's record: a
                // late joiner is not given it.
                await Send(player, AbyssWire.ChestClick(state.Chest, player.Id), $"0x1bd Abyss chest 0x{state.Chest:x} click");
                await ChestOpened(player, state);
                await DropReward(player, state);
            }
            finally { state.Gate.Release(); }
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary><see cref="GameEvents.ChestsOpened"/> (a warcry, the caster mastery): the ready chest opens when the radius
    /// around <paramref name="at"/> reaches it.</summary>
    public async Task OpenNear(Player player, (int X, int Y) at, int radius)
    {
        if (Of(player) is not { Chest: not 0, ChestReady: true } state || Distance(at, state.PitCell) > radius) return;
        await state.Gate.WaitAsync();
        try
        {
            if (state.ChestOpened || !state.ChestReady || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return;
            state.ChestOpened = true;
            await ChestOpened(player, state);
            await DropReward(player, state);
        }
        finally { state.Gate.Release(); }
    }

    static QuestObjectTypes.Info? ChestInfo() =>
        QuestObjectTypes.Find(AbyssTables.RewardChest) ?? QuestObjectTypes.Find(AbyssTables.ChestParentType);

    /// <summary>The index of a component in an object type's list (the update messages name it), or the fallback.</summary>
    static byte Component(QuestObjectTypes.Info? info, string name, byte fallback) =>
        info is not null && IndexOf(info.Components, name) is >= 0 and var i ? (byte)i : fallback;

    static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
            if (names[i] == name) return i;
        return -1;
    }

    // ---- the plan ---------------------------------------------------------------------------------------------------------

    /// <summary>The plan around <paramref name="spot"/>: of the lattice points within <see cref="AbyssTables.SiteRadius"/>
    /// steps of it, the one whose arms are the most and the longest (the nearest of equals), its pit shape, the family, the
    /// 25 arm monsters spread over the closing pieces, the wave and the chest. A point must pass <paramref name="reaches"/> (the arrival walks to it). Null when no
    /// point places an arm.</summary>
    public static Plan? MakePlan(AreaLayout layout, (int X, int Y) spot, Random rng, Func<(int X, int Y), bool>? reaches = null)
    {
        var families = AbyssTables.Families;
        if (families.Count == 0) return null;
        var family = families[rng.Next(families.Count)];
        int seed = rng.Next();
        (int X, int Y)? bestPit = null;
        List<Arm>? bestArms = null;
        (int Arms, int Pieces) best = (0, 0);
        foreach (var pit in Sites(layout, spot, reaches))
        {
            // Each point's arms are drawn apart from the others', so the choice does not depend on the order tried.
            var ra = new Random(seed ^ (pit.X * 7919 + pit.Y * 104729));
            var dirs = new List<int> { 0, 2, 4, 6 };
            for (int i = dirs.Count - 1; i > 0; i--) { int j = ra.Next(i + 1); (dirs[i], dirs[j]) = (dirs[j], dirs[i]); }
            var taken = new List<(int X, int Y)> { pit };
            var arms = new List<Arm>();
            foreach (int dir in dirs)
            {
                if (arms.Count >= AbyssTables.MaxArms) break;
                int length = ra.Next(AbyssTables.ArmLenMin, AbyssTables.ArmLenMax + 1);
                var points = ArmPoints(layout, pit, dir, length, taken, ra);
                if (points.Count < AbyssTables.ArmMin) continue;
                arms.Add(new Arm { Dir = dir, Cells = [.. points.Select(LatCell)], Shapes = Shapes(pit, points) });
                taken.AddRange(points);
            }
            var score = (arms.Count, arms.Sum(a => a.Cells.Count));
            if (arms.Count > 0 && score.CompareTo(best) > 0) (best, bestPit, bestArms) = (score, pit, arms);
        }
        if (bestPit is not { } site || bestArms is not { } chosen) return null;
        var (shape, rotation) = PitShape([.. chosen.Select(a => a.Dir)]);
        // The arm monsters, one slot each, spread evenly over the closing pieces (every piece but the pit-side one) in
        // closing order: a piece carries none, one or several.
        var pieces = new List<(int Closing, int Arm)>();
        for (int ai = 0; ai < chosen.Count; ai++)
            for (int k = 0; k < chosen[ai].Cells.Count - 1; k++) pieces.Add((k, ai));
        pieces.Sort();
        int wanted = AbyssTables.ArmMonsters;
        var slots = new List<Slot>();
        for (int m = 0; pieces.Count > 0 && m < wanted; m++)
        {
            int pick = wanted >= pieces.Count ? m * pieces.Count / wanted
                : (int)Math.Round(m * (pieces.Count - 1) / (double)Math.Max(1, wanted - 1));
            slots.Add(new Slot { Arm = pieces[pick].Arm, Closing = pieces[pick].Closing, Path = family.Regular[rng.Next(family.Regular.Length)] });
        }
        var wave = new List<(string Path, bool Elite)>();
        int count = rng.Next(AbyssTables.WaveMin, AbyssTables.WaveMax + 1);
        bool elite = rng.NextDouble() < AbyssTables.EliteChance;
        for (int i = 0; i < count; i++)
            wave.Add(i == 0 && elite ? (family.Elite, true) : (family.Regular[rng.Next(family.Regular.Length)], false));
        string? chest = AbyssTables.HasRewardChest ? AbyssTables.RewardChest : null;
        return new Plan(site, shape, rotation, family.Name, chosen, slots, wave, chest);
    }

    /// <summary>The lattice points around the spot, nearest first, inside the map and with room for a crack.</summary>
    static List<(int X, int Y)> Sites(AreaLayout layout, (int X, int Y) spot, Func<(int X, int Y), bool>? reaches)
    {
        int nx = (int)(layout.GridW * AbyssTables.Cell / AbyssTables.Lattice);
        int ny = (int)(layout.GridH * AbyssTables.Cell / AbyssTables.Lattice);
        var own = Lattice(spot.X, spot.Y);
        var points = new List<(int X, int Y)>();
        for (int dy = -AbyssTables.SiteRadius; dy <= AbyssTables.SiteRadius; dy++)
            for (int dx = -AbyssTables.SiteRadius; dx <= AbyssTables.SiteRadius; dx++)
            {
                var point = (X: own.X + dx, Y: own.Y + dy);
                if (point.X < 1 || point.Y < 1 || point.X >= nx || point.Y >= ny) continue;
                var cell = LatCell(point);
                if (layout.WalkableFor(cell.X, cell.Y, AbyssTables.CrackSize) && (reaches is null || reaches(cell))) points.Add(point);
            }
        return [.. points.OrderBy(p => (p.X - own.X) * (p.X - own.X) + (p.Y - own.Y) * (p.Y - own.Y))];
    }

    /// <summary>One arm's lattice points, pit side first: the first piece two steps out, then one step per piece on the
    /// captured turn chain; it stops early when no turn fits.</summary>
    static List<(int X, int Y)> ArmPoints(AreaLayout layout, (int X, int Y) pit, int dir, int length,
        IReadOnlyList<(int X, int Y)> taken, Random rng)
    {
        var first = (X: pit.X + 2 * Dirs[dir].X, Y: pit.Y + 2 * Dirs[dir].Y);
        var points = new List<(int X, int Y)>();
        if (!Fits(layout, first, pit) || !Free(first, taken, [])) return points;
        points.Add(first);
        int current = dir, last = 99;
        while (points.Count < length)
        {
            bool placed = false;
            var turns = Turns(last);
            // The chain's weights are its repeated entries: the first that fits of a shuffled order.
            for (int i = turns.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (turns[i], turns[j]) = (turns[j], turns[i]); }
            foreach (int turn in turns)
            {
                int next = ((current + turn / 45) % 8 + 8) % 8;
                var previous = points[^1];
                var point = (X: previous.X + Dirs[next].X, Y: previous.Y + Dirs[next].Y);
                if (!Fits(layout, point, previous) || !Free(point, taken, [.. points.TakeLast(2)])) continue;
                points.Add(point);
                current = next;
                last = turn;
                placed = true;
                break;
            }
            if (!placed) break;
        }
        return points;
    }

    static bool Fits(AreaLayout layout, (int X, int Y) point, (int X, int Y) previous)
    {
        var cell = LatCell(point);
        // Inside the map with the crack's clearance to spare: a walk test alone may answer true off the grid.
        if (cell.X < AbyssTables.CrackSize || cell.Y < AbyssTables.CrackSize
            || cell.X >= layout.GridW - AbyssTables.CrackSize || cell.Y >= layout.GridH - AbyssTables.CrackSize) return false;
        if (!layout.WalkableFor(cell.X, cell.Y, AbyssTables.CrackSize)) return false;
        var mid = CellMid(LatCell(previous), cell);
        return layout.WalkableFor(mid.X, mid.Y, 0);
    }

    /// <summary>A piece keeps two lattice steps from every other piece but the two before it [capture].</summary>
    static bool Free((int X, int Y) point, IReadOnlyList<(int X, int Y)> taken, IReadOnlyList<(int X, int Y)> near) =>
        !taken.Any(q => q != point && !near.Contains(q) && Math.Max(Math.Abs(q.X - point.X), Math.Abs(q.Y - point.Y)) < 2);

    /// <summary>The captured turn chain, its weights as repeated entries: after a 45° turn mostly the other way, after a
    /// right angle the half turn back, and a first turn of 0 / 45 / 90 at 1 / 3 / 1 [capture].</summary>
    static int[] Turns(int last) => last switch
    {
        45 => [.. Repeat(-45, 36), .. Repeat(45, 11), .. Repeat(-90, 2), .. Repeat(90, 2)],
        -45 => [.. Repeat(45, 36), .. Repeat(-45, 11), .. Repeat(-90, 2), .. Repeat(90, 2)],
        90 => [-45], -90 => [45], 0 => [-45, 45],
        _ => [0, -45, -45, 45, 45, -90, 90],
    };

    static int[] Repeat(int turn, int times) => Enumerable.Repeat(turn, times).ToArray();

    /// <summary>Each piece's shape, its rotation (the direction to the next piece out) and the mirrored bit [capture 72/72].</summary>
    static List<(string Shape, int Rotation, int Mirror)> Shapes((int X, int Y) pit, List<(int X, int Y)> points)
    {
        var shapes = new List<(string, int, int)>();
        for (int k = 0; k < points.Count; k++)
        {
            var previous = k == 0 ? pit : points[k - 1];
            int din = DirIndex(points[k].X - previous.X, points[k].Y - previous.Y);
            if (k == points.Count - 1)
            {
                shapes.Add(((din % 2 == 0 ? AbyssTables.StartOrthogonal : AbyssTables.StartDiagonal), (din + 4) % 8, 0));
                continue;
            }
            int dout = DirIndex(points[k + 1].X - points[k].X, points[k + 1].Y - points[k].Y);
            int turn = Turn(din, dout);
            bool diagonal = dout % 2 != 0;
            if (turn == 0) shapes.Add(((diagonal ? AbyssTables.StraightDiagonal : AbyssTables.StraightOrthogonal), dout, 0));
            else if (Math.Abs(turn) == 90)
                shapes.Add(((diagonal ? AbyssTables.Corner90Diagonal : AbyssTables.Corner90Orthogonal), dout, turn > 0 ? 1 : 0));
            else if (diagonal) shapes.Add((AbyssTables.Corner135Reverse, dout, turn > 0 ? 0 : 1));
            else shapes.Add((AbyssTables.Corner135, dout, turn > 0 ? 1 : 0));
        }
        return shapes;
    }

    /// <summary>The pit's shape and rotation for the directions its arms leave in.</summary>
    static (string Shape, int Rotation) PitShape(int[] armDirs)
    {
        int[] dirs = [.. armDirs.Select(d => ((d % 8) + 8) % 8).Distinct().Order()];
        foreach (var (shape, arms) in AbyssTables.PitShapes)
        {
            if (arms.Length != dirs.Length) continue;
            foreach (int rotation in new[] { 0, 2, 4, 6 })
                if (arms.Select(a => (a + rotation) % 8).Order().SequenceEqual(dirs)) return (shape, rotation);
        }
        return (AbyssTables.PitShapes[^1].Shape, 0);
    }

    // ---- the flow ---------------------------------------------------------------------------------------------------------

    /// <summary>One create, held until it is written: a record for the object follows (a piece's next state, the plinth
    /// up, the chest ready) and a lockstep client takes none for an object that is not born yet. The blob's flag 0x02 wakes it.</summary>
    static Task Create(Player player, uint id, string type, byte[] blob, string log) =>
        SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), blob), log);

    static Task Transition(Player player, uint id, string type, byte state, string log) =>
        SendWorldRecord(player, id, InstanceEntities.TransitionableUpdate(id, Component(QuestObjectTypes.Find(type), "Transitionable", 3), state), log);

    /// <summary>Creates each piece a player is near, one by one as the captures do (the pit's too).</summary>
    static async Task Reveal(Player player, State state, IReadOnlyList<(int X, int Y)> clients)
    {
        if (state.Pit == 0 && Within(clients, state.PitCell, AbyssTables.RevealDistance)) await Show(player, state);
        foreach (var arm in state.Arms)
            for (int k = 0; k < arm.Cells.Count; k++)
                if (arm.Pieces[k].Id == 0 && Within(clients, arm.Cells[k], AbyssTables.RevealDistance)) await Show(player, state, arm, k);
    }

    static async Task Show(Player player, State state)
    {
        if (state.Pit != 0) return;
        state.Pit = player.TakeObjectId();
        await CreatePit(player, state);
    }

    static async Task Show(Player player, State state, Arm arm, int k)
    {
        var piece = arm.Pieces[k];
        if (piece.Id != 0) return;
        piece.Id = player.TakeObjectId();
        await CreateCrack(player, state, arm, k);
    }

    static Task CreatePit(Player player, State state)
    {
        var plan = state.Plan;
        return Create(player, state.Pit, AbyssTables.PitType,
            AbyssWire.Pit(state.PitCell, LatticeWorld(plan.PitLattice), Rotation(plan.PitRotation), AbyssTables.AnimRow(plan.PitShape), state.PitState),
            $"0x02bb create Abyss pit 0x{state.Pit:x} at {state.PitCell} ({plan.PitShape}, {state.Arms.Count} arm(s), family {plan.Family})");
    }

    static Task CreateCrack(Player player, State state, Arm arm, int k)
    {
        var piece = arm.Pieces[k];
        var (shape, rotation, mirror) = arm.Shapes[k];
        return Create(player, piece.Id, AbyssTables.CrackType,
            AbyssWire.Crack(arm.Cells[k], LatticeWorld(Lattice(arm.Cells[k].X, arm.Cells[k].Y)), Rotation(rotation),
                AbyssTables.AnimRow(shape), mirror, piece.State), $"0x02bb create Abyss crack 0x{piece.Id:x} at {arm.Cells[k]} ({shape})");
    }

    static void StartArms(State state, IReadOnlyList<(int X, int Y)> clients)
    {
        // An arm starts when one is closing and a player is within ArmNearDistance of it: `running` turns true with the
        // first start, so the other arms near a player start in the same tick.
        bool running = state.Arms.Any(a => a.StartedAt is not null);
        foreach (var arm in state.Arms)
        {
            if (arm.StartedAt is not null || !running || !Within(clients, arm.Cells, AbyssTables.ArmNearDistance)) continue;
            arm.StartedAt = state.Now;
        }
    }

    static async Task CloseArms(Player player, State state)
    {
        foreach (var arm in state.Arms)
        {
            if (arm.StartedAt is null) continue;
            int n = arm.Cells.Count;
            for (int k = 0; k < n; k++)
            {
                int index = n - 1 - k; // from the far end in; the pit-side piece waits for the pit to open
                double from = k == n - 1 ? arm.AdjacentAt ?? double.MaxValue : arm.StartedAt.Value + k * AbyssTables.CloseStepSeconds;
                if (state.Now < from) continue;
                if (arm.Pieces[index].State < AbyssTables.CrackClosing1)
                {
                    await SetPiece(player, state, arm, index, AbyssTables.CrackClosing1);
                    if (k != n - 1) await SpawnArmMonsters(player, state, arm, index, k);
                }
                if (state.Now >= from + AbyssTables.CloseStepSeconds) await SetPiece(player, state, arm, index, AbyssTables.CrackClosing2);
                if (state.Now >= from + 2 * AbyssTables.CloseStepSeconds) await SetPiece(player, state, arm, index, AbyssTables.CrackClosing3);
            }
        }
    }

    static async Task SetPiece(Player player, State state, Arm arm, int index, byte to)
    {
        var piece = arm.Pieces[index];
        if (piece.State >= to) return;
        // A piece nobody has come near yet is made first, open, so the client steps it through the states.
        await Show(player, state, arm, index);
        piece.State = to;
        await Transition(player, piece.Id, AbyssTables.CrackType, to, $"0x02bc Abyss crack 0x{piece.Id:x} state {to}");
    }

    static async Task SpawnArmMonsters(Player player, State state, Arm arm, int index, int closing)
    {
        int ai = state.Arms.IndexOf(arm);
        foreach (var slot in state.Slots)
        {
            if (slot.Spawned || slot.Arm != ai || slot.Closing != closing) continue;
            slot.Spawned = true;
            var at = SpotNear(player, state, arm.Cells[index], AbyssTables.ArmMonsterDistanceMin, AbyssTables.ArmMonsterDistanceMax);
            uint id = await Spawn(player, state, slot.Path, MonsterRarity.Normal, at);
            if (id != 0) state.ArmMonsters.Add(id);
        }
    }

    static async Task PitOpen(Player player, State state)
    {
        state.Stage = Stage.Wave;
        state.StageAt = state.Now;
        await Show(player, state);
        state.PitState = AbyssTables.PitOpening;
        await Transition(player, state.Pit, AbyssTables.PitType, AbyssTables.PitOpening, "0x02bc Abyss pit opens");
        foreach (var arm in state.Arms) arm.AdjacentAt = state.Now + AbyssTables.Frame;
    }

    static async Task SpawnWave(Player player, State state)
    {
        double dt = state.Now - state.StageAt;
        var wave = state.Plan.Wave;
        for (int i = state.WaveSpawned; i < wave.Count; i++)
        {
            if (dt < AbyssTables.WaveFirstSeconds + i * AbyssTables.WaveEverySeconds) break;
            var at = SpotNear(player, state, state.PitCell, AbyssTables.WaveDistanceMin, AbyssTables.WaveDistanceMax);
            var (path, elite) = wave[i];
            // One the server cannot create is skipped, so the wave never waits on it.
            state.WaveSpawned = i + 1;
            uint id = await Spawn(player, state, path, elite ? MonsterRarity.Rare : MonsterRarity.Normal, at);
            if (id != 0) state.WaveMonsters.Add(id);
        }
    }

    static async Task Success(Player player, State state)
    {
        state.Plinth = player.TakeObjectId();
        await CreatePlinth(player, state);
        state.PitState = AbyssTables.PitDoneState;
        await Transition(player, state.Pit, AbyssTables.PitType, AbyssTables.PitDoneState, "0x02bc Abyss pit done");
        await PitIcon(player, state);
        if (state.Plan.Chest is null) return;
        state.Chest = player.TakeObjectId();
        await CreateChest(player, state);
    }

    /// <summary>The pit's minimap icon once the chest is there (AbyssChestActive).</summary>
    static async Task PitIcon(Player player, State state)
    {
        int icon = AbyssTables.ChestIconRow;
        if (icon >= 0)
            await SendWorldRecord(player, state.Pit, InstanceEntities.ComponentUpdate(state.Pit,
                Component(QuestObjectTypes.Find(AbyssTables.PitType), "MinimapIcon", 5), [0x01, (byte)icon, (byte)(icon >> 8)]), "0x02bc Abyss pit icon");
    }

    static Task CreatePlinth(Player player, State state) =>
        Create(player, state.Plinth, AbyssTables.PlinthType,
            AbyssWire.Plinth((state.PitCell.X, state.PitCell.Y - 2), Rotation(state.Plan.PitRotation), state.Pit, AbyssTables.PlinthAo),
            $"0x02bb create Abyss plinth 0x{state.Plinth:x} at {(state.PitCell.X, state.PitCell.Y - 2)}");

    static Task CreateChest(Player player, State state) =>
        Create(player, state.Chest, state.Plan.Chest!, AbyssWire.Chest(state.PitCell, Rotation(state.Plan.PitRotation), state.Level),
            $"0x02bb create Abyss chest 0x{state.Chest:x} at {state.PitCell}");

    /// <summary>The plinth's StateMachine: one value, up (1) [capture: 01 00 01 00].</summary>
    static Task PlinthUp(Player player, State state) =>
        SendWorldRecord(player, state.Plinth, InstanceEntities.StateMachineUpdate(state.Plinth,
            Component(QuestObjectTypes.Find(AbyssTables.PlinthType), "StateMachine", 3), new StateMachineData([1], 0)), "0x02bc Abyss plinth up");

    static async Task ChestReady(Player player, State state)
    {
        var info = ChestInfo();
        await SendWorldRecord(player, state.Chest, InstanceEntities.ComponentUpdate(state.Chest, Component(info, "InteractionAction", 6), [0x01]),
            "0x02bc Abyss chest interaction on");
        await SendWorldRecord(player, state.Chest, InstanceEntities.TransitionableUpdate(state.Chest, Component(info, "Transitionable", 8), AbyssTables.ChestReadyState),
            "0x02bc Abyss chest ready");
    }

    static async Task ChestOpened(Player player, State state)
    {
        var info = ChestInfo();
        await SendWorldRecord(player, state.Chest, InstanceEntities.ComponentUpdate(state.Chest, Component(info, "ObjectMagicProperties", 5), [0x08, 0x00, 0x00, 0x00]),
            "0x02bc Abyss chest magic properties");
        await SendWorldRecord(player, state.Chest, InstanceEntities.TransitionableUpdate(state.Chest, Component(info, "Transitionable", 8), AbyssTables.ChestOpenedState),
            "0x02bc Abyss chest opened");
    }

    static async Task Reward(Player player, State state)
    {
        double dt = state.Now - state.StageAt;
        if (!state.PlinthUp && dt >= AbyssTables.Frame && state.Plinth != 0)
        {
            state.PlinthUp = true;
            await PlinthUp(player, state);
        }
        if (!state.ChestReady && dt >= AbyssTables.ChestReadySeconds && state.Chest != 0)
        {
            state.ChestReady = true;
            await ChestReady(player, state);
        }
        if (state.ChestOpened || state.Chest == 0 && state.PlinthUp) state.Stage = Stage.Over;
    }

    /// <summary>The chest's drop: the quantity and the rarity its row's mods give (1 item, 300% more quantity: 4 [data,
    /// capture: 4 dropped]; 25000% increased rarity) of jewellery [ESTIMATE: the capture's chest dropped Abyss jewels and the item class filter is the row's name's
    /// "Jewellery"] at the area's level on the floor around the pit, as just dropped. Normal loot rules (AllowsLoot).</summary>
    static async Task DropReward(Player player, State state)
    {
        if (!GameEvents.AllowsLoot(player)) return;
        int want = Math.Max(1, (int)Math.Round((100 + AbyssTables.ChestQuantityPct) / 100.0));
        var drops = DropsFor(player).Container(state.Level, want, AbyssTables.ChestRarityPct, state.Random, t => JewelleryClasses.Contains(t.Class));
        var layout = player.Layout;
        for (int i = 0; i < drops.Count; i++)
        {
            double angle = i * 2.399963229728653, radius = 7 + Math.Sqrt(i) * 2;
            var aim = ((int)Math.Round(state.PitCell.X + Math.Cos(angle) * radius), (int)Math.Round(state.PitCell.Y + Math.Sin(angle) * radius));
            var at = RewardCell(layout, aim, state.PitCell, player.Walk.Position(AreaTime(player)));
            var ground = new WorldItemObject(at.X, at.Y, drops[i].Item);
            uint id = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, id, ground, $"Abyss chest drops {drops[i].Type.Name} (item level {drops[i].Item.ItemLevel})", justDropped: true);
        }
    }

    static readonly HashSet<string> JewelleryClasses = ["Ring", "Amulet", "Belt", "Jewel", "AbyssJewel"];

    /// <summary>A reward item's cell: the layout's nearest walkable cell to <paramref name="aim"/>, else the nearest one to
    /// the pit, else the player's own cell; inside the map.</summary>
    static (int X, int Y) RewardCell(AreaLayout layout, (int X, int Y) aim, (int X, int Y) pit, (int X, int Y) player)
    {
        bool Floor((int X, int Y) c) => c.X >= 0 && c.Y >= 0 && c.X < layout.GridW && c.Y < layout.GridH && layout.WalkableFor(c.X, c.Y, 0);
        var at = layout.Near(aim);
        if (!Floor(at)) at = layout.Near(pit);
        if (!Floor(at)) at = player;
        return (Math.Clamp(at.X, 0, layout.GridW - 1), Math.Clamp(at.Y, 0, layout.GridH - 1));
    }

    static bool Closed(Arm arm) => arm.StartedAt is not null && arm.Pieces.Skip(1).All(p => p.State >= AbyssTables.CrackClosing1);

    /// <summary>Whether any of the monsters still stands (a dormant one too).</summary>
    static bool Alive(Player player, IReadOnlyList<uint> ids) =>
        ids.Any(id => player.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0 || player.Area.DormantMonsters.ContainsKey(id));

    static async Task<uint> Spawn(Player player, State state, string path, MonsterRarity rarity, (int X, int Y) cell) =>
        Monsters.Find(path) is { Supported: true } variety && variety.Id == path
            ? await SpawnMonster(player, variety, state.Level, rarity, cell.X, cell.Y, log: rarity != MonsterRarity.Normal, alone: rarity != MonsterRarity.Normal)
            : 0;

    /// <summary>A walkable cell between <paramref name="low"/> and <paramref name="high"/> cells from the centre that the
    /// pit's own ground reaches, so nothing stands behind a wall; the centre itself when none is found.</summary>
    static (int X, int Y) SpotNear(Player player, State state, (int X, int Y) center, double low, double high)
    {
        var layout = player.Layout;
        if (!state.PitReachKnown)
        {
            state.PitReachKnown = true;
            if (layout.Walkable is { } walkable)
                state.PitReach = AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, layout.Near(state.PitCell));
        }
        for (int attempt = 0; attempt < 24; attempt++)
        {
            double angle = state.Random.NextDouble() * Math.Tau, radius = low + state.Random.NextDouble() * (high - low);
            var at = ((int)Math.Round(center.X + radius * Math.Cos(angle)), (int)Math.Round(center.Y + radius * Math.Sin(angle)));
            // Never a cell outside the map (the client crashes on an object there).
            if (at.Item1 < 0 || at.Item2 < 0 || at.Item1 >= layout.GridW || at.Item2 >= layout.GridH) continue;
            if (!layout.WalkableFor(at.Item1, at.Item2, 0)) continue;
            if (state.PitReach is { } reach && !reach[at.Item2 * layout.GridW + at.Item1]) continue;
            return at;
        }
        return center;
    }

    // ---- the lattice ------------------------------------------------------------------------------------------------------

    static readonly (int X, int Y)[] Dirs = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];

    /// <summary>The walk cell of a lattice point.</summary>
    public static (int X, int Y) LatCell((int X, int Y) lattice) =>
        ((int)Math.Floor(lattice.X * AbyssTables.Lattice / AbyssTables.Cell), (int)Math.Floor(lattice.Y * AbyssTables.Lattice / AbyssTables.Cell));

    /// <summary>The lattice point nearest a cell.</summary>
    static (int X, int Y) Lattice(int cellX, int cellY) =>
        ((int)Math.Round(cellX * AbyssTables.Cell / AbyssTables.Lattice), (int)Math.Round(cellY * AbyssTables.Cell / AbyssTables.Lattice));

    /// <summary>The world position a crack's or the pit's Positioned carries: the lattice point in world units [capture].</summary>
    static (float X, float Y) LatticeWorld((int X, int Y) lattice) => (lattice.X * (float)AbyssTables.Lattice, lattice.Y * (float)AbyssTables.Lattice);

    static (int X, int Y) CellMid((int X, int Y) a, (int X, int Y) b) => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    static int DirIndex(int dx, int dy) => Array.IndexOf(Dirs, (Math.Sign(dx), Math.Sign(dy)));

    static int Turn(int from, int to) { int t = ((to - from) % 8 + 8) % 8 * 45; return t > 180 ? t - 360 : t; }

    static float Rotation(int index) => (index % 8) * MathF.PI / 4;

    static double Distance((int X, int Y) a, (int X, int Y) b) => double.Hypot(a.X - b.X, a.Y - b.Y);

}
