using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

namespace GameServer.Modules.Incursion;

/// <summary>
/// Alva's incursions in an area instance: up to three spots, the one she stands at, her dialogue, and the time-limited run
/// her portal opens, laid out on the past room's own room file around a floor cell by her.
/// <list type="bullet">
/// <item>The incursions belong to the instance (<see cref="IncursionRun"/> and its <see cref="Live"/> part): a new instance
/// whose stats give Alva's master mission (<see cref="Rolled"/>) plans the spots when its monsters are placed
/// (<see cref="Populated"/>); every player in the instance sees and drives the same ones (the world objects replay to a
/// later player with the area's own object records), and they end with the instance, not with a player leaving.</item>
/// <item>The instance's IncursionRules ruleset is added before 0x21 (S→C 0x1f), only where it has an incursion. The portal
/// appears when a character comes near the spot and Alva when one comes close; a click on her opens her dialogue (she is an
/// NPC of each player in the instance, who is given the <see cref="OwnerFlag"/> session flag her "Enter Incursion" option
/// requires); the option opens the portal, and a click on the portal starts the run for that player.</item>
/// <item>The run: the character lands on the room's telepad, the two architects stand on its architect spawns, the cursed
/// treasures on its treasure spots, the temple slot's closed doors at the measured offsets, and 150..187 monsters of the
/// area's two pack families over its pack spots. The timer (0x163) starts 28 frames after the last create, a kill by
/// anyone adds its bracket's seconds, a cursed treasure costs 8; at 0 everyone inside is put back beside Alva, the run's
/// entities are removed and the next spot can show her. Another player who clicks the open portal joins the run in
/// progress; the run's owner (who started it) is whose temple plan it advances, so it is dropped, with the spot kept, when
/// the owner leaves.</item>
/// <item>Which room a run opens and which architects it meets is the owner's temple plan's (<see cref="IncursionTemple"/>),
/// and the run's outcome is applied to it and saved under the owner's character. The temple panel (0x223 state and 0x7b
/// kind 0x18) opens at a run's start and closes at its end.</item>
/// </list>
/// Not built: the past section the client builds from the area header's Alva entries (and with it IncursionPortal2, a
/// type the client knows only where that section was loaded, so the run ends on its timer), the Temple of Atzoatl area
/// itself (and so the IncursionTempleReady flag that would offer it), and what a cursed treasure drops.
/// </summary>
public sealed class IncursionEncounter
{
    /// <summary>The session quest flag Alva's "Enter Incursion" NPCTalk row requires (QuestFlag1), as Delve's
    /// DelveOwner is one: the client lists the option and the Gateway lets it through only with it.</summary>
    public const string OwnerFlag = "IncursionOwner";

    /// <summary>Where the character's temple plan is saved (<c>IPlayerWorld.CharacterData</c>).</summary>
    public const string TempleKey = "Incursion.temple.v1";

    public sealed class Treasure
    {
        public (int X, int Y) Cell;
        public bool Opened;
    }

    /// <summary>The instance's incursions with what they need to run: Alva and her portal, the run's room and who is in it.</summary>
    internal sealed class Live(IncursionRun run)
    {
        public readonly IncursionRun Run = run;
        /// <summary>Held by the flow (a click, a reveal, the end); the tick skips its turn rather than wait on it, since a
        /// create awaits the frame the tick is part of.</summary>
        public readonly SemaphoreSlim Gate = new(1, 1);
        public uint Alva, Portal, Effect;
        public (int X, int Y) AlvaCell, PortalCell, RoomCentre, Landing;
        public Player? Owner;
        public readonly HashSet<Player> Inside = [];
        public IncursionTemple? Temple;
        public IncursionTemple.PickedRoom? Pick;
        public IncursionRoom? Room;
        public string RoomId = "";
        public readonly List<uint> RoomObjects = [];
        /// <summary>The monsters a room being built has created so far, so a build that stops half way can take them back.</summary>
        public readonly List<uint> Spawned = [];
        /// <summary>Read by a click before it takes <see cref="Gate"/>, written under it.</summary>
        public readonly ConcurrentDictionary<uint, Treasure> Treasures = [];
        public double Clock, EnteredAt, EffectAt;
        /// <summary>The start delay is over and the timer is running.</summary>
        public bool Started;
        /// <summary>The room is being created: the start delay runs from the last create.</summary>
        public bool Building;
    }

    readonly ConditionalWeakTable<InstanceState, Live> lives = new();

    /// <summary>Drops the module's tables (module unload); the instance's monsters stay as the area's own.</summary>
    public void Clear() => lives.Clear();

    Live? LiveOf(Player player) => player.Instance is { } instance && lives.TryGetValue(instance, out var live) ? live : null;

    /// <summary>Whether the instance has incursions still to advance.</summary>
    public bool Has(Player player) => LiveOf(player) is { } live && live.Run.Current != IncursionRun.Stage.Over;

    public bool IsActivePause(Player player) => LiveOf(player) is { Run.Current: IncursionRun.Stage.Inside };

    /// <summary>Alva's id in the instance, 0 when she is not there.</summary>
    public uint AlvaOf(Player player) => LiveOf(player)?.Alva ?? 0;

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static IEnumerable<Player> Clients(Player player) =>
        InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance) && Current(c));

    static double Distance((int X, int Y) a, (int X, int Y) b) => double.Hypot(a.X - b.X, a.Y - b.Y);

    static bool OnFloor(AreaLayout layout, (int X, int Y) c) =>
        c.X >= 0 && c.Y >= 0 && c.X < layout.GridW && c.Y < layout.GridH && layout.WalkableFor(c.X, c.Y, 0);

    static bool InMap(AreaLayout layout, (int X, int Y) c) => c.X >= 0 && c.Y >= 0 && c.X < layout.GridW && c.Y < layout.GridH;

    // ---- the roll ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the area has an incursion. CoreLeagues row 12 (Incursion) is a master mission the area's league chances do
    /// not roll (+0xa0 set, so <see cref="LeagueChances.Count"/> gives none): its row names the stats instead
    /// [data: DisableStat map_disable_missions, ForcedSpawnStat map_incursion_league, LeagueChanceStat
    /// map_master_mission_alva_%_chance, BaseLeagueChanceStat map_master_mission_alva_chance_+%], which a map's Atlas
    /// passives and mods give (<see cref="AreaLeagueStats"/>; none outside a map). The chance is increased as the client's
    /// league roll increases one, chance × (100 + increased) / 100 [ESTIMATE for this row: no capture shows a roll]. It is
    /// off where the map disables missions or the Incursion league (MapDisableLeagues bit of the row), has no extra
    /// content, nukes everything or is underlevelled; the row has no base chance, so a map with
    /// MapExtraContentHasNoBaseChanceToSpawn loses nothing else. <c>Game_LEAGUE_CHANCES=Incursion=N</c> is a test's chance
    /// for every populated area. An Incursion area itself never has one.
    /// </summary>
    public static bool Rolled(IReadOnlyDictionary<uint, int> stats, bool isMap, bool incursionArea, Random rng, int? forced = null)
    {
        if (incursionArea) return false;
        int S(uint stat) => stats.GetValueOrDefault(stat);
        if (S(Stat.MapDisableMissions) > 0 || IncursionTables.LeagueRow is >= 0 and < 31 && (S(Stat.MapDisableLeagues) & (1 << IncursionTables.LeagueRow)) != 0
            || S(Stat.AtlasKeystoneNoExtraContent) > 0 || S(Stat.MapNukeEverything) > 0 || S(Stat.MapAreaUnderlevelled) > 0) return false;
        int chance;
        if (forced is int test) chance = test;
        else
        {
            if (!isMap) return false;
            if (S(Stat.MapIncursionLeague) > 0) return true;
            chance = S(Stat.MapMasterMissionAlvaPctChance) * (100 + S(Stat.MapMasterMissionAlvaChancePlusPct)) / 100;
        }
        chance = Math.Clamp(chance, 0, 100);
        return chance >= 1 && (chance >= 100 || rng.Next(100) < chance);
    }

    /// <summary>The test chance <c>Game_LEAGUE_CHANCES</c> gives the Incursion row, as <see cref="LeagueChances"/> reads it for
    /// the rows it rolls.</summary>
    public static int? ForcedChance(string? setting)
    {
        foreach (var entry in (setting ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals("Incursion", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    /// <summary>The roll for an instance, pure in its area's stats and its population seed
    /// (<see cref="AreaPopulation.PopulationSeed"/>), so the entry can tell before the monsters are placed; the draws go on
    /// to the spots.</summary>
    static bool RolledFor(Player player, int populationSeed, out Random rng)
    {
        rng = new Random(populationSeed ^ 0x496e6375);
        var layout = player.Layout;
        return Rolled(AreaLeagueStats(player) ?? new Dictionary<uint, int>(), MapDevices.IsMapArea(layout.Hash),
            layout.Id.StartsWith("Incursion_", StringComparison.Ordinal), rng, ForcedChance(Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES")));
    }

    // ---- the area ------------------------------------------------------------------------------------------------

    /// <summary>A new instance's incursion: up to three spots where a league encounter stands
    /// (<see cref="AreaPopulation.EncounterSpot"/>), <see cref="IncursionTables.SpotSeparation"/> apart, each one reachable
    /// from where the character arrives.</summary>
    public Task Populated(AreaPopulated area)
    {
        var player = area.Player;
        var layout = player.Layout;
        if (player.Instance is not { } instance || layout.Walkable is not { } walkable || !RolledFor(player, area.Seed, out var rng)) return Task.CompletedTask;
        var taken = area.Taken.ToList();
        var arrival = AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY));
        var spots = new List<(int X, int Y)>();
        for (int tries = 0; tries < IncursionTables.SpotsPerArea * 2 && spots.Count < IncursionTables.SpotsPerArea; tries++)
        {
            if (AreaPopulation.EncounterSpot(area, taken, rng)
                is not { } at) break;
            taken.Add(at);
            var portal = (X: at.X + IncursionTables.Portal1Offset.X, Y: at.Y + IncursionTables.Portal1Offset.Y);
            if (!InMap(layout, portal) || arrival is not null && !arrival[at.Y * layout.GridW + at.X]
                || spots.Any(s => Distance(s, at) < IncursionTables.SpotSeparation)) continue;
            spots.Add(at);
        }
        if (spots.Count == 0) return Task.CompletedTask;
        lives.AddOrUpdate(instance, new Live(new IncursionRun(spots, area.Level, area.Seed)));
        Console.WriteLine($"[incursion] {layout.Id}: {spots.Count} spot(s), level {area.Level}");
        return Task.CompletedTask;
    }

    /// <summary>The IncursionRules ruleset (S→C 0x1f) for an instance that has an incursion, sent before 0x21 as the official
    /// area header lists it where Alva is placed. The first entry has not placed the monsters yet, so it reads the roll; a
    /// later one reads what was placed.</summary>
    public IEnumerable<(string Log, byte[] Message)> EntryMessages(Player player)
    {
        if (IncursionTables.RulesetRow is not byte rules || player.Layout.Walkable is null) return [];
        bool has = LiveOf(player) is not null
            || player.Instance?.World.Populated != true && RolledFor(player, AreaPopulation.PopulationSeed(player.Layout.Hash, player.Seed), out _);
        return has ? [($"0x001f ruleset IncursionRules (row {rules})", InstanceProtocol.AddRuleset(rules))] : [];
    }

    // ---- the players ---------------------------------------------------------------------------------------------

    /// <summary>A player entered (the first time, again by portal or after death, or as a second party member): Alva is
    /// theirs to talk to when she is out. The objects themselves come with the join, as the area has them. Someone who
    /// left a run by dying is not in it any more.</summary>
    public async Task Entered(Player player)
    {
        // The flag is saved with the character (Quests.cs), so one left by a crash, a reload or a disconnect is cleared on the
        // next entry, wherever it is, not only in an instance that has incursions.
        if (LiveOf(player) is not { } live)
        {
            await SyncFlagOff(player);
            return;
        }
        lock (live.Inside) live.Inside.Remove(player);
        await SyncPlayer(player, live);
    }

    /// <summary>A player leaves: their flag goes with them, whether the instance still has incursions or not; the
    /// incursions stay with the instance.</summary>
    public async Task Leaving(Player player)
    {
        if (LiveOf(player) is { } live) lock (live.Inside) live.Inside.Remove(player);
        await SyncFlagOff(player);
    }

    /// <summary>Alva, as an NPC of this player and the flag her option needs, while she stands out.</summary>
    static async Task SyncPlayer(Player player, Live live)
    {
        bool standing = live.Alva != 0 && live.Run.Current is IncursionRun.Stage.Shown or IncursionRun.Stage.Open or IncursionRun.Stage.Inside;
        if (standing && Npcs.Find(IncursionWire.AlvaType) is { } npc)
            lock (player.Npcs) player.Npcs[live.Alva] = new SpawnedNpc(npc, live.AlvaCell.X, live.AlvaCell.Y);
        if (ClientHasQuestFlag(player, OwnerFlag) != standing) await SetQuestFlags(player, [OwnerFlag], standing);
    }

    // ---- the tick ------------------------------------------------------------------------------------------------

    /// <summary>One tick, by the connection that steps the area's monsters: shows the portal and Alva as a character comes
    /// near, runs the timer and tells the players in the run.</summary>
    public async Task Advance(Player player, double seconds)
    {
        if (LiveOf(player) is not { } live || live.Run.Current == IncursionRun.Stage.Over || !IsMonsterRunner(player) || !Current(player)) return;
        if (!await live.Gate.WaitAsync(0)) return;
        try
        {
            live.Clock += seconds;
            var run = live.Run;
            double now = AreaTime(player);
            if (live.Effect != 0 && live.Clock - live.EffectAt >= IncursionTables.EffectFrames * IncursionTables.FrameSeconds) await RemoveEffect(player, live);
            switch (run.Current)
            {
                case IncursionRun.Stage.Hidden when run.Spot is { } spot:
                    if (Clients(player).Any(c => Distance(c.Walk.Position(now), spot) <= IncursionTables.PortalDistance)) await RevealPortal(player, live, spot);
                    break;
                case IncursionRun.Stage.PortalShown:
                    if (Clients(player).Any(c => Distance(c.Walk.Position(now), live.AlvaCell) <= IncursionTables.AlvaDistance)) await RevealAlva(player, live);
                    break;
                case IncursionRun.Stage.Inside when !live.Building:
                    await Run(player, live, seconds);
                    break;
            }
        }
        finally { live.Gate.Release(); }
    }

    async Task Run(Player player, Live live, double seconds)
    {
        var run = live.Run;
        // The owner's temple plan is the run's: without the owner it is dropped, and the spot stays.
        if (live.Owner is null || !Clients(player).Contains(live.Owner))
        {
            await Abandon(player, live);
            return;
        }
        if (!live.Started)
        {
            if (live.Clock - live.EnteredAt < IncursionTables.StartDelayFrames * IncursionTables.FrameSeconds) return;
            live.Started = true;
            run.Begin();
        }
        else if (run.Tick(seconds))
        {
            await Finish(player, live);
            return;
        }
        await SendProgress(() => InsideClients(player, live), run);
    }

    List<Player> InsideClients(Player player, Live live)
    {
        List<Player> inside;
        lock (live.Inside) inside = [.. live.Inside];
        var here = Clients(player).ToHashSet();
        return [.. inside.Where(here.Contains)];
    }

    // ---- the flow ------------------------------------------------------------------------------------------------

    /// <summary>The portal appears asleep on the ground at the spot, then is woken (0x1a9) [capture: it is created when a
    /// character is 217..325 from it, long before Alva].</summary>
    async Task RevealPortal(Player player, Live live, (int X, int Y) spot)
    {
        live.AlvaCell = spot;
        live.PortalCell = (spot.X + IncursionTables.Portal1Offset.X, spot.Y + IncursionTables.Portal1Offset.Y);
        live.Run.Step(IncursionRun.Stage.Hidden, IncursionRun.Stage.PortalShown);
        live.Portal = player.TakeObjectId();
        uint id = live.Portal;
        await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(IncursionWire.Portal1Type),
                IncursionWire.Portal1Body(live.PortalCell, flag: 0)), $"IncursionPortal1 0x{id:x} at ({live.PortalCell.X}, {live.PortalCell.Y})",
            wake: true, awaitBirth: false);
    }

    /// <summary>Alva and her arrival effect appear beside the portal [capture: Alva is created when a character is 29..56
    /// from her]. She is registered as an NPC of everyone in the instance, so a click on her opens her dialogue through the
    /// Gateway's own NPC path.</summary>
    async Task RevealAlva(Player player, Live live)
    {
        live.Run.Step(IncursionRun.Stage.PortalShown, IncursionRun.Stage.Shown);
        live.Alva = player.TakeObjectId();
        uint alva = live.Alva;
        await SendWorldCreate(player, alva, () => InstanceEntities.CreateObject(alva, InstanceEntities.ObjectTypeHash(IncursionWire.AlvaType),
                IncursionWire.AlvaBody(live.AlvaCell, [1, 0, 0])), $"Alva 0x{alva:x} at ({live.AlvaCell.X}, {live.AlvaCell.Y})", awaitBirth: false);
        live.Effect = player.TakeObjectId();
        live.EffectAt = live.Clock;
        uint effect = live.Effect;
        await SendWorldCreate(player, effect, () => InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(IncursionWire.EffectType),
                IncursionWire.EffectBody(live.AlvaCell, IncursionWire.TeleportAo)), $"Alva's arrival effect 0x{effect:x}", awaitBirth: false);
        foreach (var client in Clients(player)) await SyncPlayer(client, live);
    }

    /// <summary>The arrival effect is removed a moment after it appears, as the official server does [capture: 46 frames], so
    /// that the area's object records do not replay it to a player who comes later.</summary>
    static async Task RemoveEffect(Player player, Live live)
    {
        uint id = live.Effect;
        live.Effect = 0;
        if (id != 0) await SendWorldRemoval(player, id, $"Alva's arrival effect 0x{id:x} ends");
    }

    /// <summary>"View Temple" (NPCTalkCategory <c>ViewIncursionTemple</c>): the character's own temple plan opens in its panel
    /// (0x223 state, then 0x7b kind 0x18) and Alva's options are listed again, as the capture's dialogue goes on after the
    /// panel opens. Only Alva of this instance.</summary>
    public async Task<bool> ViewTemple(Player player)
    {
        if (LiveOf(player) is not { } live || live.Alva == 0 || live.Alva != player.TalkingTo) return false;
        if (await TempleFor(player, live.Run.Level) is not { } temple) return false;
        await SendTempleUi(player, temple, open: true);
        await Send(player, InstanceNpcs.Talk(player.TalkingTo), $"0x0084 npc 0x{player.TalkingTo:x}: options again (the temple panel is open)");
        return true;
    }

    /// <summary>Alva's "Enter Incursion" (NPCTalkCategory <c>OpenIncursionPortal</c>, script
    /// <c>DispatchRulesetEvent(open_incursion_portal)</c>): her state machine goes to (1, 1, 0), open_room, and the
    /// portal's InteractionAction to 1 and Transitionable to 2, so it can be clicked [capture: the three updates in one
    /// frame].</summary>
    public async Task<bool> Talk(Player player)
    {
        if (LiveOf(player) is not { } live || live.Alva == 0) return false;
        await live.Gate.WaitAsync();
        try
        {
            if (!live.Run.Step(IncursionRun.Stage.Shown, IncursionRun.Stage.Open)) return false;
            await Component(player, live.Alva, InstanceEntities.StateMachineUpdate(live.Alva, IncursionWire.AlvaStateMachine,
                new StateMachineData([1, 1, 0], 0)), "Alva open_room 1");
            await Component(player, live.Portal, InstanceEntities.ComponentUpdate(live.Portal, IncursionWire.PortalInteraction, [1]), "IncursionPortal1 interaction 1");
            await Component(player, live.Portal, InstanceEntities.TransitionableUpdate(live.Portal, IncursionWire.PortalTransitionable, 2), "IncursionPortal1 transitionable 2");
            return true;
        }
        finally { live.Gate.Release(); }
    }

    /// <summary>A click on an object of the incursions, as the other walked-up objects take it (MapStrongboxes.Use): the walk
    /// up, the action's start and end, nothing for a dead, closing or leaving player, and the work only if the player is
    /// still in reach once there.</summary>
    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (LiveOf(player) is not { } live) return false;
        if (use.TargetId == live.Portal && live.Portal != 0)
            return await Interact(player, live, use, requestedAt, live.PortalCell, 4, () => Enter(player, live));
        if (live.Treasures.TryGetValue(use.TargetId, out var treasure))
            return await Interact(player, live, use, requestedAt, treasure.Cell, 4, () => OpenTreasure(player, live, use.TargetId));
        return false;
    }

    static async Task<bool> Interact(Player player, Live live, InstanceTransfer.UseSkillOnObject use, double requestedAt,
        (int X, int Y) at, int reach, Func<Task> act)
    {
        // The portal extends AreaTransition: taking the click here keeps it from being read as a way out of the area.
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || InGracePeriod(player)) return true;
        if (!TryBeginObjectWalk(player, requestedAt, at.X, at.Y, reach, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await live.Gate.WaitAsync();
        try
        {
            if (PlayerActionCurrent(player, action) && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea
                && PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), at, reach))
                await act();
        }
        finally
        {
            live.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>The open portal is clicked: the run starts for that player, or, when one is going, they join it. The timer
    /// starts 28 frames after the last create (<see cref="Run"/>): the room's creates take this server longer than the
    /// official one's frame.</summary>
    async Task Enter(Player player, Live live)
    {
        var run = live.Run;
        if (run.Current == IncursionRun.Stage.Inside)
        {
            if (live.Building || !live.Started) return;
            // Someone already inside who clicks the portal by Alva is not teleported again.
            lock (live.Inside) if (!live.Inside.Add(player)) return;
            await Place(player, live.Landing);
            run.ResendAll();
            // The panel the run's start opened for the others.
            if (live.Temple is { } running) await SendTempleUi(player, running, open: true, live.Pick);
            return;
        }
        if (run.Current != IncursionRun.Stage.Open) return;
        live.Owner = player;
        live.Started = false;
        live.Building = true;
        try { await Build(player, live); }
        catch
        {
            // A build that stops before the run starts leaves nothing of itself behind: the portal stays open for the next click.
            if (run.Current == IncursionRun.Stage.Open) await Rollback(player, live);
            throw;
        }
        finally
        {
            live.Building = false;
            live.EnteredAt = live.Clock;
        }
    }

    /// <summary>Takes back what a build created before its run started: its monsters, treasures and doors.</summary>
    static async Task Rollback(Player player, Live live)
    {
        try
        {
            uint[] monsters;
            lock (live.Spawned) { monsters = [.. live.Spawned]; live.Spawned.Clear(); }
            await RemoveRoom(player, live, monsters);
            await SendRemainingMonsters(player);
        }
        catch (Exception e) { Console.WriteLine($"[incursion] the build's rollback failed: {e.Message}"); }
        live.Owner = null;
        live.Pick = null;
        lock (live.Inside) live.Inside.Clear();
    }

    /// <summary>The character's temple plan: the saved one, else one drawn from the character's name and saved at once.</summary>
    static async Task<IncursionTemple?> TempleFor(Player player, int level)
    {
        var saved = IncursionTemple.FromJson(WorldOf(player).CharacterData.Get(TempleKey));
        if (saved is not null) return saved;
        var temple = IncursionTemple.Create(CharacterName(player), level);
        if (temple is not null) await Save(player, temple);
        return temple;
    }

    static async Task Save(Player player, IncursionTemple temple)
    {
        if (!WorldOf(player).CharacterData.Change(TempleKey, _ => temple.ToJson()))
            await PlayerOf(player).Tell("Incursion: the temple plan could not be saved for this character.");
    }

    /// <summary>The run's two architects: the slot's resident and the other, or the two others of a tier-0 slot; any
    /// two the server can write when the plan drew none.</summary>
    static List<IncursionTables.Architect> ArchitectsFor(int level, Random rng, IncursionTemple.PickedRoom? pick)
    {
        var all = IncursionTables.Architects(level);
        if (pick is not null)
        {
            var first = all.FirstOrDefault(a => a.Row == (pick.Resident ?? pick.First));
            var second = all.FirstOrDefault(a => a.Row == (pick.Resident is not null ? pick.First : pick.Second));
            if (first is not null && second is not null) return [first, second];
        }
        return [.. all.OrderBy(_ => rng.Next()).Take(2)];
    }

    async Task Build(Player player, Live live)
    {
        var layout = player.Layout;
        var run = live.Run;
        live.Pick = null;
        live.RoomObjects.Clear();
        live.Treasures.Clear();
        int level = run.Level;
        var near = layout.Near(live.AlvaCell);
        live.RoomCentre = OnFloor(layout, near) ? near : live.AlvaCell;
        var centre = live.RoomCentre;
        var rng = new Random(run.Seed ^ 0x496e6375 ^ (run.Used + 1) * 7919);

        // The room: the temple plan's slot's; a filler room when the plan draws nothing (the plan then does not advance).
        var temple = live.Temple = await TempleFor(player, level);
        var pick = live.Pick = temple?.Pick(level, rng);
        IncursionTables.Room? room;
        List<IncursionTables.Architect> architects;
        if (pick is null)
        {
            var fillers = IncursionTables.FillerRooms;
            room = fillers.Count == 0 ? null : fillers[rng.Next(fillers.Count)];
            architects = ArchitectsFor(level, rng, null);
        }
        else
        {
            room = IncursionTables.AllRooms[pick.Room];
            architects = ArchitectsFor(level, rng, pick);
        }
        live.RoomId = room?.Id ?? "";
        live.Room = room is null ? null : IncursionRoom.Read(room.PastArm);
        // The ground the character lands on: a point of the room that is not on floor connected to it is not placed.
        live.Landing = FitFloor(layout, centre, live.Room?.Telepad) ?? (OnFloor(layout, centre) ? centre : live.AlvaCell);
        var reach = layout.Walkable is { } walkable ? AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, live.Landing) : null;
        bool Reaches((int X, int Y) c) => OnFloor(layout, c) && (reach is null || reach[c.Y * layout.GridW + c.X]);
        (int X, int Y)? Fit((int X, int Y)? offset) =>
            offset is { } o && Reaches(IncursionRoom.At(centre, o)) ? IncursionRoom.At(centre, o) : null;

        var chosen = new List<(MonsterCatalog.Variety Variety, MonsterRarity Rarity, (int X, int Y) Cell, bool Architect, int Row)>();
        var spawns = new[] { Fit(live.Room?.Spawn1) ?? live.Landing, Fit(live.Room?.Spawn2) ?? live.Landing };
        for (int a = 0; a < architects.Count; a++)
            if (Monsters.Find(architects[a].Path) is { Supported: true } variety)
                chosen.Add((variety, MonsterRarity.Unique, spawns[Math.Min(a, spawns.Length - 1)], true, architects[a].Row));
        int total = rng.Next(IncursionTables.MonstersLo, IncursionTables.MonstersHi + 1);
        int cultists = (int)Math.Round(total * IncursionTables.CultistShare);
        var packSpots = live.Room is { PackSpots.Count: > 0 } r ? r.PackSpots.Select(p => IncursionRoom.At(centre, p)).ToList() : [centre];
        int next = 0;
        var families = IncursionTables.Families(new Random(run.Seed ^ 0x46414d));
        foreach (var (family, count) in (ReadOnlySpan<(string, int)>)[(families.Cultist, cultists), (families.Other, total - cultists)])
        {
            var entries = IncursionTables.PackEntries(family);
            if (entries.Count == 0) continue;
            for (int k = 0; k < count; k++)
            {
                if (IncursionTables.Draw(entries, rng) is not { } entry) continue;
                var from = packSpots[next++ % packSpots.Count];
                double angle = rng.NextDouble() * Math.Tau, radius = rng.NextDouble() * IncursionTables.PackSpread;
                var cell = layout.Near(((int)Math.Round(from.X + radius * Math.Cos(angle)), (int)Math.Round(from.Y + radius * Math.Sin(angle))));
                if (!Reaches(cell)) continue;
                double roll = rng.NextDouble();
                var rarity = roll < IncursionTables.RareChance ? MonsterRarity.Rare
                    : roll < IncursionTables.RareChance + IncursionTables.MagicChance ? MonsterRarity.Magic : MonsterRarity.Normal;
                if (Monsters.Find(entry.Path) is { Supported: true } variety) chosen.Add((variety, rarity, cell, false, 0));
            }
        }

        // The captured order, in the frame of the teleport: the character to the telepad, the cursed treasures, the
        // doors, the architects, the monsters. Treasures and doors are created awake (flag 2), so no 0x1a9 follows; nothing
        // follows them in this frame, so their creates do not wait for a birth.
        await Place(player, live.Landing);
        foreach (var offset in live.Room?.Chests ?? [])
        {
            if (Fit(offset) is not { } cell) continue;
            uint id = player.TakeObjectId();
            await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(IncursionWire.TreasureType),
                IncursionWire.TreasureBody(cell, 0f)), $"AlvaCursedTreasure 0x{id:x} at ({cell.X}, {cell.Y})", awaitBirth: false);
            live.RoomObjects.Add(id);
            live.Treasures[id] = new Treasure { Cell = cell };
        }
        // The slot's closed doors, at the room centre plus the measured offsets [capture 107/107].
        if (pick is not null && temple is not null)
            foreach (string direction in temple.DoorsFor(pick.Slot))
            {
                var (offset, rotation) = IncursionTables.DoorSpots[direction];
                var cell = (X: centre.X + offset.X, Y: centre.Y + offset.Y);
                if (!OnFloor(layout, cell)) continue;
                uint id = player.TakeObjectId();
                await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(IncursionWire.DoorType),
                    IncursionWire.DoorBody(cell, rotation)), $"ClosedDoorPast 0x{id:x} at ({cell.X}, {cell.Y})", awaitBirth: false);
                live.RoomObjects.Add(id);
            }
        // The monsters go in as an area's population does (Monsters.PopulateArea): created in bulk, a lockstep client's together
        // so they share frames, and the overlay's count waits for the last of them.
        uint[] made;
        lock (live.Spawned) live.Spawned.Clear();
        using (HoldMonstersRemaining(player))
        {
            async Task<uint> Spawn((MonsterCatalog.Variety Variety, MonsterRarity Rarity, (int X, int Y) Cell, bool Architect, int Row) m)
            {
                uint id = await SpawnMonster(player, m.Variety, (byte)level, m.Rarity, m.Cell.X, m.Cell.Y, log: false, alone: false, bulk: true);
                if (id != 0) lock (live.Spawned) live.Spawned.Add(id);
                return id;
            }
            if (player.Connection.LockstepClient) made = await Task.WhenAll(chosen.Select(Spawn).ToList());
            else
            {
                made = new uint[chosen.Count];
                for (int i = 0; i < chosen.Count; i++) made[i] = await Spawn(chosen[i]);
            }
        }
        await SendRemainingMonsters(player);
        // A monster that could not be created means the connection is going: the room is taken back, the portal stays open.
        if (made.Any(id => id == 0))
        {
            await Rollback(player, live);
            return;
        }
        var ids = new List<uint>();
        var architectRows = new Dictionary<uint, int>();
        for (int i = 0; i < chosen.Count; i++)
        {
            ids.Add(made[i]);
            if (chosen[i].Architect) architectRows[made[i]] = chosen[i].Row;
        }
        lock (live.Inside) { live.Inside.Clear(); live.Inside.Add(player); }
        if (!run.Start(ids, architectRows, IncursionTables.StartSeconds(level)))
        {
            await Rollback(player, live);
            return;
        }
        lock (live.Spawned) live.Spawned.Clear();
        Console.WriteLine($"[incursion] {CharacterName(player)} in {layout.Id}: {live.RoomId} {live.Room?.ToString() ?? "(no room file)"}, "
            + $"{run.Total} monster(s), {architectRows.Count} architect(s), {live.Treasures.Count} cursed treasure(s), level {level}");
        // The temple panel opens at the run's start: the state, then the panel [capture 4/4].
        if (temple is not null) await SendTempleUi(player, temple, open: true, pick);
    }

    static (int X, int Y)? FitFloor(AreaLayout layout, (int X, int Y) centre, (int X, int Y)? offset) =>
        offset is { } o && OnFloor(layout, IncursionRoom.At(centre, o)) ? IncursionRoom.At(centre, o) : null;

    // ---- the kills -----------------------------------------------------------------------------------------------

    /// <summary>A kill (<see cref="GameEvents.Kill"/>, the character's or a minion's): a monster of the run adds its
    /// bracket's seconds and moves the progress bar; an architect's kill adds the unique rarity's (0 in every bracket) and is
    /// counted for the temple, not in the bar's denominator. The messages follow at the next tick.</summary>
    public void Killed(KillContext kill)
    {
        if (LiveOf(kill.Player) is not { } live) return;
        live.Run.Kill(kill.Monster.ObjectId, IncursionTables.KillSeconds(live.Run.Level, kill.Monster.Rarity));
    }

    /// <summary>A click on a cursed treasure: 8 seconds off the clock and the chest's three component updates [capture].</summary>
    async Task OpenTreasure(Player player, Live live, uint target)
    {
        // Not while the start delay runs, and not once the clock is at 0 (IncursionRun.Cost); under the gate, so a build or the
        // end cannot change the room between the check and the cost.
        if (!live.Started || !live.Treasures.TryGetValue(target, out var treasure) || treasure.Opened
            || !live.Run.Cost(IncursionTables.TreasureCostSeconds)) return;
        treasure.Opened = true;
        await Component(player, target, InstanceEntities.TransitionableUpdate(target, IncursionWire.TreasureTransitionable, 2), "cursed treasure transitionable 2");
        await Component(player, target, InstanceEntities.ComponentUpdate(target, IncursionWire.TreasureInteraction, [0]), "cursed treasure interaction 0");
        await Component(player, target, InstanceEntities.ComponentUpdate(target, IncursionWire.TreasureMagicProperties, [0x08, 0x00, 0x00, 0x00]),
            "cursed treasure magic properties");
    }

    // ---- the end -------------------------------------------------------------------------------------------------

    /// <summary>The timer reached 0: the timer and the progress go to 0, everyone inside is put at Alva + (−3, −9)
    /// [capture 1/1], the run's entities are removed, the outcome goes to the owner's temple plan, Alva's state machine goes
    /// to (2, 1, 0) and the portal's Transitionable to 3. The finished pair stays, as in the captures.</summary>
    async Task Finish(Player player, Live live)
    {
        var run = live.Run;
        var inside = InsideClients(player, live);
        await SendProgress(() => inside, run);
        await Return(player, live, inside);
        var temple = live.Temple;
        var killed = run.KilledArchitects;
        var leftovers = run.Finish();
        await RemoveRoom(player, live, leftovers);
        if (temple is not null)
        {
            if (live.Pick is { } pick && live.Owner is { } owner && Clients(player).Contains(owner))
            {
                temple.Apply(pick, killed, run.Level);
                await Save(owner, temple);
            }
            // The panel closes for everyone inside, whether or not the owner is still here to be given the outcome.
            await SendTempleUi(inside, temple, open: false);
        }
        if (live.Effect != 0) await RemoveEffect(player, live);
        await Component(player, live.Alva, InstanceEntities.StateMachineUpdate(live.Alva, IncursionWire.AlvaStateMachine, new StateMachineData([2, 1, 0], 0)), "Alva lookat 2");
        await Component(player, live.Portal, InstanceEntities.TransitionableUpdate(live.Portal, IncursionWire.PortalTransitionable, 3), "IncursionPortal1 transitionable 3");
        foreach (var client in Clients(player)) { lock (client.Npcs) client.Npcs.Remove(live.Alva); await SyncFlagOff(client); }
        live.Alva = live.Portal = live.Effect = 0;
        live.Owner = null;
        lock (live.Inside) live.Inside.Clear();
    }

    /// <summary>The run's owner has gone: its monsters and objects are removed, those still inside are put back, nothing is
    /// counted and the portal stays open for the next time.</summary>
    async Task Abandon(Player player, Live live)
    {
        var inside = InsideClients(player, live);
        await SendTimer(inside, 0);
        await Return(player, live, inside);
        var rest = live.Run.Abandon();
        await RemoveRoom(player, live, rest);
        await SendTempleUi(inside, live.Temple, open: false);
        live.Owner = null;
        lock (live.Inside) live.Inside.Clear();
    }

    static async Task SyncFlagOff(Player player)
    {
        if (ClientHasQuestFlag(player, OwnerFlag)) await SetQuestFlags(player, [OwnerFlag], false);
    }

    async Task Return(Player player, Live live, List<Player> inside)
    {
        var layout = player.Layout;
        var back = (X: live.AlvaCell.X + IncursionTables.ExitOffset.X, Y: live.AlvaCell.Y + IncursionTables.ExitOffset.Y);
        var to = OnFloor(layout, back) ? back : OnFloor(layout, live.AlvaCell) ? live.AlvaCell : ((int X, int Y)?)null;
        if (to is { } cell)
            foreach (var client in inside) await Place(client, cell);
    }

    /// <summary>Removes the run's monsters that are still alive from the instance and its objects from the client; a monster
    /// that has died is the corpse's own business.</summary>
    static async Task RemoveRoom(Player player, Live live, IReadOnlyList<uint> monsters)
    {
        foreach (uint id in monsters)
        {
            bool alive = false;
            if (player.Area.Monsters.Remove(id, out var m)) { alive = !m.Dead; m.Dead = true; m.Life = 0; m.ActionGeneration++; }
            else if (player.Area.DormantMonsters.Remove(id, out _)) alive = true;
            player.Instance.World.MonsterOrigins.TryRemove(id, out _);
            if (alive) await SendWorldRemoval(player, id);
        }
        foreach (uint id in live.RoomObjects) await SendWorldRemoval(player, id);
        live.RoomObjects.Clear();
        live.Treasures.Clear();
    }

    /// <summary>Puts the character on a cell (0x1b7), the walk moved with it.</summary>
    static async Task Place(Player player, (int X, int Y) cell)
    {
        Interlocked.Increment(ref player.ActionCount);
        player.Walk.Teleport(cell.X, cell.Y, AreaTime(player));
        await SendWorldRecord(player, player.Id, InstanceProtocol.Teleport(player.Id, cell.X, cell.Y, 0x7fffffff),
            $"incursion: the character to ({cell.X}, {cell.Y})");
    }

    // ---- the wire ------------------------------------------------------------------------------------------------

    static async Task SendTempleUi(Player player, IncursionTemple temple, bool open, IncursionTemple.PickedRoom? pick = null)
    {
        await Send(player, IncursionWire.TempleState(temple, open, pick), $"0x0223 Incursion temple state ({(open ? "open" : "close")})");
        await Send(player, InstanceWaypoints.OpenUi(IncursionWire.TempleUiKind, open ? 1u : 0u), $"0x007b {(open ? "open" : "close")} the temple panel");
    }

    static async Task SendTempleUi(IEnumerable<Player> players, IncursionTemple? temple, bool open)
    {
        if (temple is null) return;
        foreach (var player in players) await SendTempleUi(player, temple, open);
    }

    /// <summary>0x163 to each client in the run, only when the whole second changed.</summary>
    static async Task SendTimer(IEnumerable<Player> players, int? seconds)
    {
        if (seconds is not { } s) return;
        foreach (var player in players) await Send(player, IncursionWire.Timer(s), $"0x0163 Incursion timer {s}s");
    }

    /// <summary>0x163 for the second that changed and 0x164 when a kill moved the bar: the room's kills over its monsters,
    /// the architects excluded.</summary>
    static async Task SendProgress(Func<List<Player>> players, IncursionRun run)
    {
        // Most ticks change neither: then nobody is looked up.
        int? seconds = run.SecondsToSend();
        float? fraction = run.ProgressToSend();
        if (seconds is null && fraction is null) return;
        var list = players();
        await SendTimer(list, seconds);
        if (fraction is not { } bar) return;
        foreach (var player in list) await Send(player, IncursionWire.Progress(bar), $"0x0164 Incursion progress {run.Killed}/{run.Total}");
    }

    static Task Component(Player player, uint id, byte[] bytes, string what) =>
        id == 0 ? Task.CompletedTask : SendWorldRecord(player, id, bytes, $"0x02bc {what} 0x{id:x}");
}
