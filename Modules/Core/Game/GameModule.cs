using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using Rules = global::Game;

namespace GameServer.Modules.Game;

/// <summary>The game's rules: gives the framework its <see cref="IGameLogic"/>.</summary>
public sealed class GameModule : IModule
{
    /// <summary>What one pick of Johan's "Fund the Treasury" moves out of the account's stash: the real client asks for the
    /// amount with a slider in the town map window, which this build cannot show, so a pick is a fixed 1000 gold and
    /// <c>/treasury &lt;gold&gt;</c> takes any other amount.</summary>
    private const int TreasuryPerPick = 1000;

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        Rules.CombatLifetime = module.Lifetime;
        Program.SetGameLogic(new GameLogic(module.Lifetime));
        module.OnTick((player, _) => global::BrineKing.Tick(Program.ConnOf(player)));
        // The map overlay's "X monsters remain" (S->C 0x1a0): the entry's send, then one per change (MonsterRemaining.cs).
        module.OnTick((player, _) => Rules.TickMonstersRemaining(Program.ConnOf(player)));
        // The post-campaign unlock: the NPCTalk rows that only appear once their quest flags are set (World/UnlockAll.cs).
        // "data/unlock-all.txt" makes it automatic, "/unlock [all]" applies it on demand.
        module.OnTick((player, _) => Rules.UnlockTick(player));
        module.OnChatCommand("unlock", "/unlock [all] (grant the quest flags that gate post-campaign dialogue)", async command =>
        {
            bool all = command.Args.Count > 0 && string.Equals(command.Args[0], "all", StringComparison.OrdinalIgnoreCase);
            int set = await Rules.Unlock(command.Player, all);
            await command.Player.Tell(set == 0
                ? "Nothing to unlock: every post-campaign flag is already set."
                : $"Unlocked {set} post-campaign quest flag(s){(all ? " (all, with the epilogue flag)" : "")}.");
        });
        // Kingsmarch's town objects: the buildings the client never draws on its own (World/KingsmarchTown.cs).
        module.OnTick((player, now) => KingsmarchTown.Tick(player, now));
        // An area load starts the town over: its objects and the crops that were growing on them.
        module.OnAreaGenerated(KingsmarchTown.Entered);
        // A click on one of the town's farm plots plants or harvests (World/KingsmarchTown.cs, Use).
        module.OnObjectUsed(use => KingsmarchTown.Use(use.Player, use.ObjectId));
        // The town's farm and jobs: "/village" reports them, "/village hire" hires a worker, "/village assign <worker>
        // <plot | mine | smelter>" puts one on a plot, the mine or the smeltery. The client's own farm and recruitment
        // windows cannot be built in this build, so these actions live here instead of behind those panels.
        module.OnChatCommand("village", "/village [hire | assign <worker> <plot 1-6 | mine | smelter>] (the Kingsmarch farm, mine and smeltery)", async command =>
        {
            string verb = command.Args.Count > 0 ? command.Args[0].ToLowerInvariant() : "";
            if (verb == "hire")
            {
                await command.Player.Tell("Kingsmarch: " + await KingsmarchTown.Hire(command.Player));
                return;
            }
            if (verb == "assign")
            {
                string job = command.Args.Count > 2 ? command.Args[2].ToLowerInvariant() : "";
                if (command.Args.Count < 3 || !int.TryParse(command.Args[1], out int worker))
                {
                    await command.Player.Tell("usage: /village assign <worker> <plot 1-6 | mine | smelter>");
                    return;
                }
                if (job is "mine" or "mining" or "smelter" or "smelting" or "smelt" or "smeltery")
                {
                    await command.Player.Tell("Kingsmarch: " + KingsmarchTown.AssignJob(command.Player, worker, job));
                    return;
                }
                if (!int.TryParse(command.Args[2], out int plot))
                {
                    await command.Player.Tell("usage: /village assign <worker> <plot 1-6 | mine | smelter>");
                    return;
                }
                await command.Player.Tell("Kingsmarch: " + await KingsmarchTown.Assign(command.Player, worker, plot));
                return;
            }
            await command.Player.Tell($"Kingsmarch farm: {KingsmarchTown.StoreText(command.Player)}");
        });
        // Kingsmarch's recruiter: the client's recruitment window cannot be filled in this build (no village state in the
        // client), so the candidates and the hire live on this command instead of behind Raulf's panel.
        module.OnChatCommand("recruit", "/recruit [hire <1-3>] (Kingsmarch's workers)", async command =>
        {
            string verb = command.Args.Count > 0 ? command.Args[0].ToLowerInvariant() : "";
            if (verb == "hire")
            {
                if (command.Args.Count < 2 || !int.TryParse(command.Args[1], out int index))
                {
                    await command.Player.Tell("usage: /recruit hire <1-3>");
                    return;
                }
                await command.Player.Tell("Kingsmarch: " + await KingsmarchTown.Recruit(command.Player, index));
                return;
            }
            await command.Player.Tell($"Kingsmarch recruiter: {KingsmarchTown.RecruitText(command.Player)}");
        });
        // Johan, the King's Hand: "Fund the Treasury" (NPCTalk category 7, script empty) moves gold from the account's
        // stash into Kingsmarch's treasury, which pays the workers' wages. The real client asks for the amount in the town
        // map window; that window cannot be filled in this build, so one pick funds a fixed sum and /treasury takes any
        // other amount. NpcDialogue declines this option so the pick lands here.
        module.OnNpcOption(async talk =>
        {
            if (talk.Option != "Fund the Treasury") return false;
            await module.World(talk.Player).NpcClose("0x0084 close the dialogue for Fund the Treasury");
            await talk.Player.Tell("Kingsmarch: " + await KingsmarchTown.FundTreasury(talk.Player, TreasuryPerPick));
            return true;
        });
        module.OnChatCommand("treasury", "/treasury [gold] (Kingsmarch's treasury, which pays the workers)", async command =>
        {
            if (command.Args.Count > 0 && int.TryParse(command.Args[0], out int amount))
            {
                await command.Player.Tell("Kingsmarch: " + await KingsmarchTown.FundTreasury(command.Player, amount));
                return;
            }
            await command.Player.Tell($"Kingsmarch treasury: {KingsmarchTown.TreasuryText(command.Player)}");
        });
        // Dev: the town's actions as a server-drawn choice dialogue. The client's own Kingsmarch panel is never built in
        // this build, so a dialogue with buttons of our own text (S→C 0x8c, no lines) is the experiment for a real,
        // clickable button: /dlg, then click, and read which C→S the client answers with.
        module.OnChatCommand("dlg", "/dlg (dev: Kingsmarch's treasury as a choice dialogue)", async command =>
        {
            var conn = Program.ConnOf(command.Player);
            uint npc;
            lock (conn.Npcs) npc = conn.Npcs.FirstOrDefault(n => n.Value.Npc.Id.Contains("VillageMayor", StringComparison.Ordinal)).Key;
            InstanceNpcs.Choice[] choices =
            [
                new(1, "Fund the Treasury with 1000 gold"),
                new(2, "Leave"),
            ];
            await command.Player.Send(InstanceNpcs.Talk(0), "0x0084 close the open dialogue window");
            await Task.Delay(250);
            await command.Player.Send(InstanceNpcs.Talk(npc), $"0x0084 talk open for npc 0x{npc:x}");
            await Task.Delay(400);
            // The u16 in 0x8c picks the dialogue kind the client draws: 0 was seen to open the vendor's buy window, so the
            // value is swept from the chat line while looking for the one that draws a panel with our choices in it.
            ushort kind = command.Args.Count > 0 && ushort.TryParse(command.Args[0], out ushort asked) ? asked : (ushort)0;
            await command.Player.Send(InstanceNpcs.Dialogue(npc, kind, false, [], choices),
                $"0x008c choice dialogue for npc 0x{npc:x} kind {kind}: {choices.Length} choice(s)");
        });
        // The same message, sent into an ALREADY open dialogue (no 0x84 at all): this is the probe for whether the client
        // draws server-supplied rows in place of its own: click an NPC, then /dlg2, and look at the option list.
        module.OnChatCommand("dlg2", "/dlg2 [kind] (dev: server rows inside the dialogue that is already open)", async command =>
        {
            var conn = Program.ConnOf(command.Player);
            uint npc;
            lock (conn.Npcs) npc = conn.Npcs.FirstOrDefault(n => n.Value.Npc.Id.Contains("VillageMayor", StringComparison.Ordinal)).Key;
            InstanceNpcs.Choice[] choices =
            [
                new(1, "Fund the Treasury with 1000 gold"),
                new(2, "Hire a worker (40 barter)"),
                new(3, "Leave"),
            ];
            ushort kind = command.Args.Count > 0 && ushort.TryParse(command.Args[0], out ushort asked) ? asked : (ushort)0;
            await command.Player.Send(InstanceNpcs.Dialogue(npc, kind, false, [], choices),
                $"0x008c rows for npc 0x{npc:x} kind {kind}: {choices.Length} choice(s), no 0x84 before it");
        });
        // Kingsmarch's port: a ship leaves with the farm's crops and comes home with barter (World/KingsmarchPort.cs).
        // The client's port window is an empty stub in this build, so the whole shipping flow lives on these commands.
        module.OnTick((player, now) => KingsmarchPort.Tick(player, now));
        module.OnChatCommand("port", "/port [send <port> <crop> <count>] (Kingsmarch shipping)", async command =>
        {
            string verb = command.Args.Count > 0 ? command.Args[0].ToLowerInvariant() : "";
            if (verb == "storm")
            {
                double percent = -1;
                if (command.Args.Count > 1 && !string.Equals(command.Args[1], "off", StringComparison.OrdinalIgnoreCase)
                    && !double.TryParse(command.Args[1], out percent))
                {
                    await command.Player.Tell("usage: /port storm <percent> | /port storm off");
                    return;
                }
                await command.Player.Tell("Kingsmarch: " + KingsmarchPort.Storm(percent));
                return;
            }
            if (verb == "crew")
            {
                if (command.Args.Count < 2 || !int.TryParse(command.Args[1], out int hands))
                {
                    await command.Player.Tell("usage: /port crew <count>");
                    return;
                }
                await command.Player.Tell("Kingsmarch: " + KingsmarchPort.Crew(command.Player, hands));
                return;
            }
            if (verb == "send")
            {
                if (command.Args.Count < 4 || !int.TryParse(command.Args[1], out int port) || !int.TryParse(command.Args[3], out int count))
                {
                    await command.Player.Tell("usage: /port send <port> <crop> <count>");
                    return;
                }
                await command.Player.Tell("Kingsmarch: " + KingsmarchPort.Send(command.Player, port, command.Args[2], count));
                return;
            }
            await command.Player.Tell($"Kingsmarch port: {KingsmarchPort.Report(command.Player)}");
        });
        // Dev: spawn the town's objects in this area load (they are not spawned automatically: see KingsmarchTown.Tick).
        module.OnChatCommand("village_spawn", "/village_spawn (dev: place the Kingsmarch town objects)", command =>
        {
            KingsmarchTown.Arm();
            return command.Player.Tell("Kingsmarch: placing the town's objects.");
        });
        // Dev: open any client screen (S->C 0x7b) by kind, e.g. "/ui 59" for the Kingsmarch farm window.
        module.OnChatCommand("ui", "/ui <kind in hex>", command =>
        {
            if (command.Args.Count == 0) return Task.CompletedTask;
            string hex = command.Args[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? command.Args[0][2..] : command.Args[0];
            if (!byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte kind)) return Task.CompletedTask;
            return command.Player.Send(GameServer.Core.Protocol.Instance.InstanceWaypoints.OpenUi(kind), $"0x007b 0x{kind:x2} open (/ui)");
        });
        // Dev: 0x161 restores the area's baseline walk grids and re-stamps the model hulls of everything currently in it
        // (the hideout editor's rebuild, world handler 0x14218ddf0 -> 0x14218be70 -> 0x141d8bd20). Objects a server creates
        // with 0x2bb never stamp the grid by themselves, so this is the candidate for making them block walkers.
        module.OnChatCommand("collide", "/collide (dev: re-stamp the walk grid and the model hulls)", command =>
            command.Player.Send(GameServer.Core.Protocol.Instance.InstanceHideoutEditing.RebuildCollision(),
                "0x0161 rebuild the walk grid and the model hulls"));
        // Dev: a stash on the player's own cell, to see whether the client blocks a server-made object at all.
        module.OnChatCommand("box", "/box (dev: a stash on your cell)", async command =>
            await command.Player.Tell("Kingsmarch: " + await KingsmarchTown.Box(command.Player)));
        module.OnChatCommand("pos", "/pos (where the server thinks you stand)", command =>
            command.Player.Tell($"You stand at ({command.Player.Position.X}, {command.Player.Position.Y}) in {command.Player.AreaId}."));
        module.OnChatCommand("cell", "/cell <x> <y> (dev: does the server think a cell is walkable)", command =>
        {
            var parts = command.Args;
            if (parts.Count < 2 || !int.TryParse(parts[0], out int cx) || !int.TryParse(parts[1], out int cy))
                return command.Player.Tell("usage: /cell <x> <y>");
            var layout = Program.ConnOf(command.Player).Layout;
            bool? walkable = layout.Walkable?.Invoke(cx, cy);
            return command.Player.Tell($"cell ({cx}, {cy}) in {layout.Id}: {(walkable is null ? "no walk map" : walkable.Value ? "walkable" : "blocked")}");
        });
        // /cells, not /cell: a picture of the grid around the player (the stamp is easier to check than to guess).
        module.OnChatCommand("cells", "/cells [halfWidth] [halfHeight] (dev: the walk grid around you)", command =>
        {
            var parts = command.Args;
            int halfX = parts.Count > 0 && int.TryParse(parts[0], out int hx) ? hx : 24;
            int halfY = parts.Count > 1 && int.TryParse(parts[1], out int hy) ? hy : 8;
            return command.Player.Tell(KingsmarchTown.Cells(command.Player, halfX, halfY));
        });
        // /blk, not /block: the game already answers /block with the block-chance report.
        module.OnChatCommand("blk", "/blk <x0> <y0> <x1> <y1> (dev: server-side blockers over a rectangle)", command =>
        {
            var parts = command.Args;
            if (parts.Count < 4 || !int.TryParse(parts[0], out int x0) || !int.TryParse(parts[1], out int y0)
                || !int.TryParse(parts[2], out int x1) || !int.TryParse(parts[3], out int y1))
                return command.Player.Tell("usage: /blk <x0> <y0> <x1> <y1>");
            return command.Player.Tell("Kingsmarch: " + KingsmarchTown.Block(command.Player, x0, y0, x1, y1));
        });
        // Dev: one object of any registered type with any model at an offset from the player (does it render, does it block?).
        module.OnChatCommand("prop", "/prop <type> <model> <dx> <dy> (dev: an object of any type with any model)", async command =>
        {
            var parts = command.Args;
            if (parts.Count < 4 || !int.TryParse(parts[2], out int dx) || !int.TryParse(parts[3], out int dy))
            {
                await command.Player.Tell("usage: /prop <type> <model> <dx> <dy>");
                return;
            }
            await command.Player.Tell("Kingsmarch: " + await KingsmarchTown.Prop(command.Player, parts[0], parts[1], dx, dy));
        });
        module.OnMonsterKilled(kill => Rules.SendMonstersRemaining(Program.ConnOf(kill.Player)));
        // The map device's portal labels read "(Completed)" only for the run whose boss has died: the Atlas module marks a
        // kill here when it is a map's boss (it owns that decision), and the portal list asks (Portals.cs, MapRunBossKey).
        AppContext.SetData(Rules.MapRunBossKey, (Action<ClientState, string>)Rules.MarkRunBossKilled);
        module.Lifetime.Own(GameEvents.PlayerStatSources.Add("BrineKing", global::BrineKing.PlayerStats));
        module.Lifetime.Own(global::BrineKing.CleanupOnUnload());
        module.Lifetime.Own(GameEvents.Kill.Add("Game", Rules.ReservationKill));
        module.OnPlayerLeaving((player, _) => global::BrineKing.Leaving(Program.ConnOf(player)));
    }
}

sealed class GameLogic(IModuleLifetime? lifetime = null) : IGameLogic, IControlledPlayerRules, IControlledPlayerCommands
{
    internal IModuleLifetime? Lifetime { get; } = lifetime ?? Rules.CombatLifetime;
    public double MaximumLeaderDistance => Rules.ControlledFollowerLeash;
    public void QueueControlledIntent(ClientState player, ControlledPlayerIntent intent) => Rules.QueueControlledIntent(player, intent);
    public void ClearControlledIntent(ClientState player) => Rules.ClearControlledIntent(player);
    public object ObserveControlledPlayer(ClientState player, ClientState leader) => Rules.ObserveControlledPlayer(player, leader);
    public void ResumePlayerUpdates(ClientState conn)
    {
        if (ReferenceEquals(Program.GameLogicOrNull, this) && ReferenceEquals(Lifetime, Rules.CombatLifetime))
            Rules.EnsurePlayerUpdates(conn);
    }
    public Task OnClientMessages(string remote, NetworkStream stream, ConnectionCiphers ciphers, IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, ClientState conn, long readAt) =>
        Rules.OnClientMessages(remote, stream, ciphers, messages, unknown, conn, readAt);

    public void Admitting(ClientState conn) => Rules.RegisterVitalsAdmission(conn);

    public int PrepareCharacter(string remote, ClientState conn, int level)
    {
        Rules.RefuseInterruptedHardcoreDeath(conn);
        level = Rules.PrepareCharacterForArea(remote, conn, level);
        // Restore mode vitals before any admitted-area send can fail and enter save cleanup.
        Rules.InitializeCharacterVitals(conn);
        return level;
    }

    public IReadOnlyDictionary<uint, int>? AreaLeagueStats(ClientState conn) => Rules.LeagueStatsFor(conn);

    public async Task Entered(string remote, ClientState conn, int level, int ticketLevel)
    {
        if (level != ticketLevel)
        {
            // The saved experience is the character's level: Auth's (the ticket's) was not updated at the last level-up.
            var fixedLevel = await Rules.SynchronizeAccountLevel(conn);
            Console.WriteLine($"[gateway] {remote} {Program.CharacterName(conn)}: level {level} from the saved experience (the account had {ticketLevel})" +
                (fixedLevel.Ok ? "" : $"; not saved to the account: {fixedLevel.Error}"));
        }
        Console.WriteLine($"[gateway] {remote} {Program.CharacterName(conn)}: {Rules.SkillsFor(conn).ClassFor(conn.ClassRow).ObjectType} (Characters row {conn.ClassRow}), level {conn.Pools.Level} (experience {conn.Experience}, {conn.Passives.Count} passive(s), life {conn.Pools.Life}, mana {conn.Pools.Mana}, attack speed +{conn.Pools.AttackSpeedPct}%), league {conn.League ?? "?"}");
    }

    public void Closing(ClientState conn)
    {
        foreach (var close in GameEvents.TransientClosing.For(conn)) close(conn);
        conn.PaidChannel = null;
        conn.NonChaosDotBypassCarry = conn.NonChaosDotTakenCarry = 0;
        conn.RetirePlayerUpdates();
        Rules.FortificationOf(conn).Clear();
        Rules.LeechOf(conn).Clear(conn.Clock.Elapsed.TotalSeconds);
        conn.Rage.Clear();
        conn.Rampage.Clear();
        conn.RetireOwnedCombat();
        conn.RetireResourceEffects();
        conn.VaalPreventionUntil = 0;
    }

    public void Save(ClientState conn, bool prepared)
    {
        // A refused admission must not load/start inventories or flush a previous connection's cached copy.
        if (prepared)
        {
            CharacterSaves.SaveExperience(Program.CharacterName(conn));
            Rules.ItemsOf(conn).SaveUnsaved();
            Rules.PersistVitals(conn);
            Rules.SaveGolems(conn);
        }
        Rules.CommitHardcoreDeath(conn);
    }

    public void Left(ClientState conn)
    {
        Rules.ForgetVitalsAdmission(conn);
        // The instance keeps its monsters as they are now, for the next connection (#12); the player's minions go with him.
        if (conn.MonstersPlaced) Rules.KeepInstanceMonsters(conn);
    }

    public IDisposable ProjectileMutation(ClientState conn) => Rules.ProjectileMutation(conn, advance: false);
    public IConnectionPlayer PlayerFor(ClientState conn) => new Rules.ModPlayer(conn);
    public IPlayerWorld WorldFor(ClientState conn) => new Rules.ModuleWorld(conn);
    public IEnumerable<(string Value, string Detail)>? CompletionValues(string kind) => Rules.CompletionValues(kind);
    public double PlayerActionFactor(ClientState conn) => Rules.PlayerActionFactor(conn);
    public CharacterSaves.Save BuildControlled(GameServer.Core.Accounts.CharacterClass characterClass, int level) => Rules.BuildControlledPlayer(characterClass, level);
    public Task InitializeControlled(ClientState player, ClientState leader) => Rules.InitializeControlledPlayer(player, leader);
    public Task StepControlled(ClientState player, ClientState leader) => Rules.StepControlledPlayer(player, leader);
    public Task ControlledCompletion(ClientState player) => player.PlayerUpdatesState is PlayerUpdatesState updates ? updates.Completion : Task.CompletedTask;
}
