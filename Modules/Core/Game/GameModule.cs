using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using Rules = global::Game;

namespace GameServer.Modules.Game;

/// <summary>The game's rules: gives the framework its <see cref="IGameLogic"/>.</summary>
public sealed class GameModule : IModule, IDisposable
{
    /// <summary>What one pick of Johan's "Fund the Treasury" moves out of the account's stash: the client asks for the amount
    /// with a slider in the town map window, which this build cannot show, so a pick is a fixed 1000 gold and
    /// <c>/treasury &lt;gold&gt;</c> takes any other amount.</summary>
    private const int TreasuryPerPick = 1000;
    /// <summary>
    /// Builds, when the module loads, the catalogues every first login and first arrival reads (the stash rules, gems,
    /// monster contacts, life tables, golem scaling, the room index, the hideouts). Each is built on first use otherwise,
    /// which was while the first player after a start waited: about 3 s of server time on the first login (2026-11-17).
    /// A catalogue that cannot be built here is left for its first use, which reports why as it always did.
    /// </summary>
    static void WarmCatalogues()
    {
        try
        {
            Program.Items.Warm();
            Program.Skills.Warm();
            Rules.Monsters.Warm();
            Rules.GolemData.Warm();
            AreaPopulation.Warm();
            _ = HideoutCatalog.Hideouts;
            _ = CharacterAudioCatalog.Default;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] catalogues not built at startup, left for their first use: {e.Message}");
        }
    }

    /// <summary>The end of an unload: the host disposes a module's entry after it stopped the module's lifetime, so the
    /// clean-ups that lifetime owns (the fights' CleanupOnUnload) have read the rule tables by now.</summary>
    public void Dispose()
    {
        Rules.ForgetClaimedCharacters();
        Rules.ResetAchievementCache();
        RuleTables.Empty();
    }

    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        // The catalogs the others read first. The HardMode data set waits for its first player (Rules.LoadHardModeData).
        Rules.LoadSettingsData();
        // The catalogs the rules share (Settings.cs and the systems' own files), each after the ones it reads.
        Rules.Monsters = new();
        Rules.Packs = new();
        Rules.Cosmetics = new();
        Rules.Mtx = new();
        Rules.MtxEffects = new();
        Rules.Art = new();
        Rules.Uniques = new(Program.Items);
        Rules.Essences = new(Program.Items, Program.Rolls);
        Rules.Drops = new(Program.Items, Program.Rolls, maps: Rules.MapDevices.Atlas, uniques: Rules.Uniques);
        Rules.AtlasPassiveNodes = new();
        Rules.TimelessJewels = new();
        Rules.OrdinaryPassives = new();
        Rules.Ascendancies = new();
        Rules.Descendancies = new();
        Rules.AscendancyRules = new(Rules.Ascendancies);
        Rules.Animations = new();
        Rules.Emerges = new();
        Rules.GolemData = new();
        Rules.SummonData = new();
        Rules.LoadSummonsData();
        Rules.LoadOwnedSummonsData();
        Rules.RampageData = new();
        Rules.Supports = new();
        Rules.LoadMinionSupportsData();
        Rules.LoadWornGrantedSkillsData();
        Rules.LoadPhasingData();
        Rules.LoadAlchemistsGeniusData();
        // The Heist rogue allies carry SectorEvents, which the client's create reads nothing for; the other types that
        // carry it (Kirac, the Sanctum NPC, Expedition, pirates, Delve) stay uncreatable.
        MonsterObject.AllowExtraFor("SectorEvents", "Metadata/Monsters/LeagueHeist/NPCAllies/NPCAlliesBase");
        Rules.LoadBuffCancelData();
        Rules.LoadDeathPenaltyData();
        Rules.LoadModuleWorldData();
        Rules.LoadMonsterModsData();
        Rules.LoadPassiveGrantedSkillsRulesData();
        // Combat, skills and projectiles.
        Rules.LoadOwnAurasData();
        global::StationaryCollision.LoadStationaryCollisionData();
        Rules.LoadProjectilesData();
        Rules.LoadPlacedSkillsData();
        Rules.LoadTotemsRulesData();
        // Flasks.
        global::AntiquarianEffects.LoadAntiquarianEffectsData();
        Rules.LoadFlaskGroundsData();
        // Monsters.
        global::BossKits.LoadBossKitsData();
        global::BossMods.LoadBossModsData();
        global::IzaroSkills.LoadIzaroSkillsData();
        Rules.LoadMonsterContagionsData();
        Rules.LoadMonsterMinionsData();
        Rules.LoadHostileGroundsData();
        global::MonsterSkills.LoadMonsterSkillsData();
        // Passives and the player.
        global::ClusterJewelLayout.LoadClusterJewelLayoutData();
        global::ClusterJewelStats.LoadClusterJewelStatsData();
        global::JewelPathEffects.LoadJewelPathEffectsData();
        global::ForbiddenTattoo.LoadForbiddenTattooData();
        global::JourneyMakanuiCatalogue.LoadJourneyMakanuiCatalogueData();
        global::TattooCatalogue.LoadTattooCatalogueData();
        // The world.
        global::MapSulphite.LoadMapSulphiteData();
        Rules.LoadQuestEngineData();
        global::TourRecords.LoadTourRecordsData();
        global::MapEssenceCatalog.LoadGameData();
        global::MapEssences.LoadGameData();
        global::MapScarabBosses.LoadGameData();
        global::MapScarabContent.LoadGameData();
        global::MapScarabPacks.LoadGameData();
        global::StrongboxCatalog.LoadGameData();
        global::StrongboxMods.LoadGameData();
        global::MapStrongboxes.LoadGameData();
        global::CampaignDoors.LoadGameData();
        global::CampaignChests.LoadGameData();
        global::WeaponIdentity.LoadGameData();
        global::GroundArt.LoadGameData();
        global::BeyondCatalog.LoadGameData();
        global::TormentCatalog.LoadGameData();
        global::HarbingerCatalog.LoadGameData();
        Rules.LoadEssenceAurasData();
        Rules.LoadEssenceSummonsData();
        Rules.LoadEssenceProjectiles();
        Rules.LoadRemnantAuraData();
        Rules.LoadSelfBuffSkillsData();
        Rules.LoadMapAreaStats();
        Rules.LoadAchievementsData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        Rules.CombatLifetime = module.Lifetime;
        // The game's systems in each area's own world (GameArea).
        AreaSetup.Load(module);
        global::Game.LoadStats(module);
        module.Lifetime.Own(Rules.CleanupTemporaryAuras());
        module.Lifetime.Own(Rules.CleanupEssenceAuras());
        module.Lifetime.Own(Rules.CleanupMonsterSummons());
        module.Lifetime.Own(Rules.CleanupRemnantAuras());
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.InArea(global::Sirus.Area, global::Sirus.Tick));
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => Rules.CloseInteractionsIfMovingAway(player, null, Rules.AreaTime(player))));
        // A map device's portal labels read "<area> (Completed)" once that run's boss is dead (World/Portals.cs).
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => Rules.TickMapRunBoss(player)));
        GameEvents.MonsterKilled.Add(module.Manifest.Id, kill => Rules.TickMapRunBoss(kill.Player, force: true));
        // The Kingsmarch town: the post-campaign unlock, the town's objects, the farm, the port and the treasury
        // (World/KingsmarchTown.cs, World/KingsmarchPort.cs, World/UnlockAll.cs). The client never draws the town's
        // buildings, and has no working village panel in this build, so the rules are server-side and reach the player
        // through these hooks and the chat commands below.
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, now) => KingsmarchTown.Tick(Program.PlayerOf(player), now)));
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, now) => KingsmarchPort.Tick(Program.PlayerOf(player), now)));
        GameEvents.AreaGenerated.Add(module.Manifest.Id, player =>
        {
            KingsmarchTown.Entered(Program.PlayerOf(player));
            return Task.CompletedTask;
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, (player, use, now) => KingsmarchTown.Use(Program.PlayerOf(player), use.TargetId));
        // The settlement records (0x1f) and their village events (0x1e) go out with the area's own messages: the client
        // reads them once, while it builds the area, so a record that arrives later is never read.
        GameEvents.BeforeEnterMessages.Add(module.Manifest.Id, player => KingsmarchTown.BeforeEnterMessages(Program.PlayerOf(player)));
        GameEvents.PlayerEntered.Add(module.Manifest.Id, player => Rules.UnlockTick(Program.PlayerOf(player)));
        // A dead monster's debuffs come off: each player's step for it.
        GameEvents.MonsterKilled.Add(module.Manifest.Id, kill =>
        {
            // A Misery proximity shield dies with its monster: the counter is a hot-path guard, not a state that
            // outlives the monster (nothing would ever decrement it for a monster that leaves the area).
            MapEssences.ClearProximityShield(kill.Player, kill.Monster.ObjectId);
            if (kill.Player.Area.Monsters.TryGetValue(kill.Monster.ObjectId, out var killedMonster))
                Rules.CleanupDeadMonsterLocked(kill.Player.Area, kill.Monster.ObjectId, killedMonster);
            foreach (var p in kill.Player.Frames.Players()) global::Game.MonsterDebuffsChanged(p, kill.Monster.ObjectId);
            return Task.CompletedTask;
        });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => Rules.ClearPlayerMonsterAurasAsync(player));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => global::Sirus.Leaving(player));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Sirus.PlayerStats);
        InstanceEvents.AreaStats.Add(module.Manifest.Id, Rules.MapAreaStats);
        InstanceEvents.AreaDisplayStats.Add(module.Manifest.Id, Rules.MapAreaDisplayStats);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabContent.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabPacks.Opening);
        GameEvents.Drops.Add(module.Manifest.Id, MapScarabPacks.Drops);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapStrongboxes.Opening);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, MapStrongboxes.Enter);
        GameEvents.ObjectUse.Add(module.Manifest.Id, MapStrongboxes.Use);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => MapStrongboxes.Tick(player)));
        GameEvents.ChestsOpened.Add(module.Manifest.Id, MapStrongboxes.OpenNear);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapStrongboxes.CurseStats);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemOnObjectId).Add(module.Manifest.Id, MapStrongboxes.UseItemOn);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemTypeOnObjectId).Add(module.Manifest.Id, MapStrongboxes.UseItemTypeOn);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { MapStrongboxes.Leave(player); return Task.CompletedTask; });
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapStrongboxes.Describe);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, CampaignDoors.OnAreaGenerated);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, CampaignDoors.OnPlayerEntered);
        GameEvents.ObjectUse.Add(module.Manifest.Id, CampaignDoors.Use);
        GameEvents.SkillCast.Add(module.Manifest.Id, (player, _, _, from, _) => { _ = CampaignDoors.BreakSmashablesNear(player, from); return null; });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { CampaignDoors.Leave(player); return Task.CompletedTask; });
        GameEvents.AreaGenerated.Add(module.Manifest.Id, CampaignChests.OnAreaGenerated);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, CampaignChests.OnPlayerEntered);
        GameEvents.ObjectUse.Add(module.Manifest.Id, CampaignChests.Use);
        GameEvents.ChestsOpened.Add(module.Manifest.Id, CampaignChests.OpenNear);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { CampaignChests.Leave(player); return Task.CompletedTask; });
        GameEvents.MapOpening.Add(module.Manifest.Id, MapEssences.Opening);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabBosses.Opening);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, MapEssences.Enter);
        GameEvents.ObjectUse.Add(module.Manifest.Id, MapEssences.Use);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemOnObjectId).Add(module.Manifest.Id, MapEssences.UseItemOn);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemTypeOnObjectId).Add(module.Manifest.Id, MapEssences.UseItemTypeOn);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(MapEssences.Tick));
        GameEvents.Drops.Add(module.Manifest.Id, MapEssences.Drops);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, Rules.RemnantAuraStats);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapEssences.EssenceDebuffStats);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { MapEssences.Leave(player); return Task.CompletedTask; });
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapEssences.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapTorment.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapTorment.Enter);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(MapTorment.Tick));
        GameEvents.Drops.Add(module.Manifest.Id, MapTorment.Drops);
        GameEvents.Kill.Add(module.Manifest.Id, MapTorment.Kill);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapTorment.Leave(conn); return Task.CompletedTask; });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapTorment.PlayerStats);
        GameEvents.BuffCancel.Add(module.Manifest.Id, MapTorment.CancelBuff);
        GameEvents.BuffCancel.Add(module.Manifest.Id, Rules.CancelRemovableBuff);
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapTorment.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapHarbinger.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapHarbinger.Enter);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(MapHarbinger.Tick));
        GameEvents.Kill.Add(module.Manifest.Id, MapHarbinger.Kill);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapHarbinger.Leave(conn); return Task.CompletedTask; });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapHarbinger.PlayerStats);
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapHarbinger.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapBeyond.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapBeyond.Enter);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(MapBeyond.Tick));
        GameEvents.Kill.Add(module.Manifest.Id, MapBeyond.Kill);
        GameEvents.Drops.Add(module.Manifest.Id, MapBeyond.Drops);
        GameEvents.Experience.Add(module.Manifest.Id, MapBeyond.Experience);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapBeyond.Leave(conn); return Task.CompletedTask; });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapBeyond.PlayerStats);
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapBeyond.Describe);
        module.Lifetime.Own(IncomingResourceDefences.HandOverFinalTaken([MapBeyond.BossLessDamageStat]));
        module.Lifetime.Own(MonsterModEffects.HandOverMore(Rules.AtlasMoreLife, Rules.AtlasMoreDamage));
        module.Lifetime.Own(MonsterModEffects.HandOverMore([MapTorment.LifeStat, MapBeyond.LifeStat], [], [MapTorment.TaperStat]));
        // Secrets of Suffering's "Cannot Ignite, Chill, Freeze or Shock" and Stormblood's Sap and Brittle
        // (MonsterAlternateAilments).
        module.Lifetime.Own(MonsterElementalAilments.HandOverInflictProhibitions(Rules.SecretsOfSufferingProhibitions));
        module.Lifetime.Own(UtilityFlasks.HandOverDuringEffectFlags(Rules.AlternateAilmentFlaskFlags));
        module.Lifetime.Own(global::Sirus.CleanupOnUnload());
        Rules.InitializeAccountHideouts(module.Settings<Rules.HideoutMigrationSettings>().LegacySourceCharacter);
        // Build the optional melee clock tables before combat can admit an action, rather than inside its send gate.
        Program.Skills.WarmMonsterMeleeClocks();
        WarmCatalogues();
        Program.SetGameLogic(new GameLogic(module.Lifetime));
        module.Lifetime.Own(Rules.InstallJoining());
        string id = module.Manifest.Id;
        // Kingsmarch's chat commands (the client's own town panel is never built in this build: World/KingsmarchTown.cs
        // and World/KingsmarchPort.cs carry the rules and these are how the player reaches them).
        GameEvents.CommandsBuilding.Add(id, (commands, player) =>
        {
            // The Kingsmarch rules carry the modding-layer player (World/KingsmarchTown.cs); the game's own
            // connection, layout and walk stay on the game's player.
            IPlayer me = Program.PlayerOf(player);
            commands.Add("village", "/village [hire | assign <worker> <plot 1-6 | mine | smelter | ship 1-3> | crop <plot> <crop>] (the Kingsmarch farm, mine, smeltery and ships)",
                async args =>
                {
                    if (args.Length > 0 && args[0] == "hire")
                    { await me.Tell("Kingsmarch: " + await KingsmarchTown.Hire(me)); return; }
                    if (args.Length >= 3 && args[0] == "crop")
                    {
                        if (!int.TryParse(args[1], out int which)) { await me.Tell("usage: /village crop <plot 1-6> <wheat | corn | pumpkin | globulora | orbisfera>"); return; }
                        await me.Tell("Kingsmarch: " + await KingsmarchTown.Choose(me, which, args[2]));
                        return;
                    }
                    if (args.Length >= 2 && args[0] == "assign")
                    {
                        if (!int.TryParse(args[1], out int worker)) { await me.Tell("usage: /village assign <worker> <plot 1-6 | mine | smelter | ship 1-3>"); return; }
                        if (args.Length >= 3 && args[2] is "ship" or "shipping")
                        {
                            int ship = 1;
                            if (args.Length >= 4 && (!int.TryParse(args[3], out ship) || ship < 1 || ship > 3))
                            { await me.Tell("usage: /village assign <worker> ship <1-3>"); return; }
                            await me.Tell("Kingsmarch: " + KingsmarchTown.AssignJob(me, worker, "ship", ship));
                            return;
                        }
                        if (args.Length >= 3 && args[2] is "mine" or "smelter")
                        { await me.Tell("Kingsmarch: " + KingsmarchTown.AssignJob(me, worker, args[2])); return; }
                        if (args.Length >= 3 && int.TryParse(args[2], out int plot))
                        { await me.Tell("Kingsmarch: " + await KingsmarchTown.Assign(me, worker, plot)); return; }
                        await me.Tell("usage: /village assign <worker> <plot 1-6 | mine | smelter | ship 1-3>"); return;
                    }
                    await me.Tell($"Kingsmarch farm: {KingsmarchTown.StoreText(me)}");
                });
            commands.Add("village_spawn", "/village_spawn (dev: place the Kingsmarch town's objects)", async _ =>
            {
                KingsmarchTown.Arm();
                await me.Tell("Kingsmarch: placing the town's objects.");
            });
            commands.Add("recruit", "/recruit [hire <1-3>] (Kingsmarch's workers)", async args =>
            {
                if (args.Length > 0 && args[0] == "hire")
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out int index)) { await me.Tell("usage: /recruit hire <1-3>"); return; }
                    await me.Tell("Kingsmarch: " + await KingsmarchTown.Recruit(me, index)); return;
                }
                await me.Tell($"Kingsmarch recruiter: {KingsmarchTown.RecruitText(me)}");
            });
            commands.Add("treasury", "/treasury [gold] (Kingsmarch's treasury, which pays the workers)", async args =>
            {
                if (args.Length > 0 && int.TryParse(args[0], out int amount))
                { await me.Tell("Kingsmarch: " + await KingsmarchTown.FundTreasury(me, amount)); return; }
                await me.Tell($"Kingsmarch treasury: {KingsmarchTown.TreasuryText(me)}");
            });
            commands.Add("port", "/port [route <slot> <port> | load <slot> <crop> <count> | sail <slot> [port] | unload <slot> | send <port> <crop> <count> | storm <percent> | crew <count>] (Kingsmarch shipping)",
                async args =>
                {
                    if (args.Length > 0 && args[0] == "storm")
                    {
                        if (args.Length < 2 || !int.TryParse(args[1], out int percent)) { await me.Tell("usage: /port storm <percent> | /port storm off"); return; }
                        await me.Tell("Kingsmarch: " + KingsmarchPort.Storm(percent)); return;
                    }
                    if (args.Length > 0 && args[0] == "crew")
                    {
                        if (args.Length < 2 || !int.TryParse(args[1], out int hands)) { await me.Tell("usage: /port crew <count>"); return; }
                        await me.Tell("Kingsmarch: " + KingsmarchPort.Crew(me, hands)); return;
                    }
                    // The port window's 貨物 box: produce goes into a dock slot, where the card draws it until it sails.
                    if (args.Length >= 4 && args[0] == "load" && int.TryParse(args[1], out int slot) && int.TryParse(args[3], out int units))
                    { await me.Tell("Kingsmarch: " + KingsmarchPort.Load(me, slot, args[2], units)); return; }
                    // The port window's 選擇港口 button: the card is bound for a port before it is loaded.
                    if (args.Length >= 3 && args[0] == "route" && int.TryParse(args[1], out int routeSlot) && int.TryParse(args[2], out int routePort))
                    { await me.Tell("Kingsmarch: " + KingsmarchPort.Route(me, routeSlot, routePort)); return; }
                    // The port window's 啟航 button: the loaded slot becomes a ship at sea.
                    if (args.Length >= 2 && args[0] == "sail" && int.TryParse(args[1], out int sailSlot))
                    { await me.Tell("Kingsmarch: " + KingsmarchPort.Sail(me, sailSlot, args.Length >= 3 && int.TryParse(args[2], out int sailPort) ? sailPort : 0)); return; }
                    if (args.Length >= 2 && args[0] == "unload" && int.TryParse(args[1], out int unloadSlot))
                    { await me.Tell("Kingsmarch: " + KingsmarchPort.Unload(me, unloadSlot)); return; }
                    if (args.Length >= 4 && args[0] == "send" && int.TryParse(args[1], out int port) && int.TryParse(args[3], out int count))
                    { await me.Tell("Kingsmarch: " + KingsmarchPort.Send(me, port, args[2], count)); return; }
                    await me.Tell($"Kingsmarch port: {KingsmarchPort.Report(me)}");
                });
            commands.Add("kmui", "/kmui <kind in hex> [a] [b] [c] [d] (dev: ask the client to open one of its screens)", async args =>
            {
                string hex = args.Length > 0 && args[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? args[0][2..] : args.Length > 0 ? args[0] : "";
                if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int kind))
                { await me.Tell("usage: /kmui <kind in hex> [a] [b] [c] [d]"); return; }
                uint[] slot = new uint[4];
                for (int i = 0; i < 4 && i + 1 < args.Length; i++)
                    uint.TryParse(args[i + 1], out slot[i]);
                await me.Send(InstanceWaypoints.OpenUi((byte)kind, slot[0], slot[1], slot[2], slot[3]),
                    $"0x007b open UI kind 0x{kind:x2} a {slot[0]} b {slot[1]} c {slot[2]} d {slot[3]}");
                await me.Tell($"0x007b open UI kind 0x{kind:x2} a {slot[0]} b {slot[1]} c {slot[2]} d {slot[3]}");
            });
            commands.Add("goto", "/goto <area id> (dev: walk the character into another area)", async args =>
            {
                if (args.Length < 1) { await me.Tell("usage: /goto <area id>"); return; }
                await me.Tell("Travelling to " + args[0] + ".");
                await Program.WorldOf(me).TravelTo(args[0], "dev /goto");
            });
            commands.Add("tp", "/tp <x> <y> (dev: put the character on a cell of this area)", async args =>
            {
                if (args.Length < 2 || !int.TryParse(args[0], out int tx) || !int.TryParse(args[1], out int ty))
                {
                    await me.Tell("usage: /tp <x> <y>");
                    return;
                }
                var game = Program.PlayerOf(me);
                game.Walk.Teleport(tx, ty, game.Frames.Now(game));
                await global::Game.SendWorldRecord(me, game.Id, InstanceProtocol.Teleport(game.Id, tx, ty), $"dev /tp to ({tx}, {ty})");
                await me.Tell($"Teleported to ({tx}, {ty}) in {me.AreaId}.");
            });
            commands.Add("pos", "/pos (where the server thinks you stand)", async _ =>
                await me.Tell(me.Position.ToString()));
            commands.Add("cell", "/cell <x> <y> (dev: does the server think a cell is walkable)", async args =>
            {
                if (args.Length < 2 || !int.TryParse(args[0], out int x) || !int.TryParse(args[1], out int y))
                { await me.Tell("usage: /cell <x> <y>"); return; }
                var layout = player.Layout;
                var cells = layout.Walkable;
                var answer = cells is null ? "no walk map" : (cells(x, y) ? "walkable" : "blocked");
                await me.Tell($"cell ({x}, {y}) in {layout.Id}: {answer}");
            });
            commands.Add("cells", "/cells [halfWidth] [halfHeight] (dev: the walk grid around you)", async args =>
            {
                int halfX = 24, halfY = 8;
                if (args.Length > 0 && int.TryParse(args[0], out halfX)) { if (args.Length > 1 && int.TryParse(args[1], out halfY)) { } }
                await me.Tell(KingsmarchTown.Cells(me, halfX, halfY));
            });
            // /blk, not /block: the game already answers /block with the block-chance report.
            commands.Add("blk", "/blk <x0> <y0> <x1> <y1> (dev: server-side blockers over a rectangle)", async args =>
            {
                if (args.Length < 4 || !int.TryParse(args[0], out int x0) || !int.TryParse(args[1], out int y0)
                    || !int.TryParse(args[2], out int x1) || !int.TryParse(args[3], out int y1))
                { await me.Tell("usage: /blk <x0> <y0> <x1> <y1>"); return; }
                await me.Tell("Kingsmarch: " + KingsmarchTown.Block(me, x0, y0, x1, y1));
            });
            commands.Add("prop", "/prop <type> <model> <dx> <dy> (dev: an object of any type with any model)", async args =>
            {
                if (args.Length < 4) { await me.Tell("usage: /prop <type> <model> <dx> <dy>"); return; }
                if (!int.TryParse(args[2], out int dx) || !int.TryParse(args[3], out int dy))
                { await me.Tell("usage: /prop <type> <model> <dx> <dy>"); return; }
                await me.Tell("Kingsmarch: " + await KingsmarchTown.Prop(me, args[0], args[1], dx, dy));
            });
            return commands;
        });
        // The town's treasury: the "Fund the Treasury" NPCTalk row (the client never builds its button).
        GameEvents.NpcOptionPicked.Add(id, async (player, talk) =>
        {
            // A hook that declines must answer true: false means "this option is refused" and drops it before any module
            // sees it (GameEvents.AllowNpcOption), which is how Johan's "Manage Town" stopped opening the town panel.
            if (talk.Option != "Fund the Treasury") return true;
            IPlayer me = Program.PlayerOf(player);
            await Program.WorldOf(me).NpcClose("0x0084 close the dialogue for Fund the Treasury");
            await me.Tell("Kingsmarch: " + await KingsmarchTown.FundTreasury(me, TreasuryPerPick));
            return true;
        });
        GameEvents.PlayerLeaving.Add(id, (player, _) => { Rules.LeaveArea(player); return Task.CompletedTask; });
        GameEvents.PlayerLeaving.Add(id, (player, _) => { Rules.EndAreaTotems(player); return Task.CompletedTask; });
        GameEvents.PlayerLeaving.Add(id, (player, _) => { Rules.EndRejuvenationTotemAura(player); return Task.CompletedTask; });
        GameEvents.PlayerLeaving.Add(id, (player, _) => { Rules.ClearOwnerAttacks(player); return Task.CompletedTask; });
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::DoedreSewers.Area, global::DoedreSewers.Tick));
        GameEvents.ObjectUse.Add(module.Manifest.Id, global::DoedreSewers.Use);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::DoedreSewers.PlayerStats);
        module.Lifetime.Own(global::DoedreSewers.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::DoedreSewers.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::BrineKing.Area, global::BrineKing.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => global::AtlasBosses.Tick(player)));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::VaalOversoul.Area, global::VaalOversoul.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::DepravedTrinity.Area, global::DepravedTrinity.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::Arakaali.Area, global::Arakaali.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::Arakaali.Area, global::ArakaaliLasers.Tick));
        module.Lifetime.Own(global::ArakaaliLasers.CleanupOnUnload());
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::Yugul.Area, global::Yugul.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::SolarisLunaris.Area, global::SolarisLunaris.Tick));
        module.Lifetime.Own(global::SolarisLunaris.CleanupOnUnload());
        module.Lifetime.Own(global::Yugul.CleanupOnUnload());
        module.Lifetime.Own(global::Arakaali.CleanupOnUnload());
        module.Lifetime.Own(global::DepravedTrinity.CleanupOnUnload());
        GameEvents.ObjectUse.Add(module.Manifest.Id, global::DepravedTrinity.Use);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::DepravedTrinity.PlayerStats);
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::DepravedTrinity.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::KitavaActFive.Area, global::KitavaActFive.Tick));
        module.Lifetime.Own(global::KitavaActFive.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::KitavaActFive.Leaving(player));
        // Act 1-5 signature campaign boss encounter controllers.
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::MalachaiEncounter.Area, global::MalachaiEncounter.Tick));
        module.Lifetime.Own(global::MalachaiEncounter.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::MalachaiEncounter.Leaving(player));
        GameEvents.PlayerEntered.Add(id, global::DominusEncounter.OnPlayerEntered);
        GameEvents.ObjectUse.Add(id, global::DominusEncounter.Use);
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::DominusEncounter.Area, global::DominusEncounter.Tick));
        module.Lifetime.Own(global::DominusEncounter.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::DominusEncounter.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::PietyLunarisEncounter.Area, global::PietyLunarisEncounter.Tick));
        module.Lifetime.Own(global::PietyLunarisEncounter.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::PietyLunarisEncounter.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::InnocenceEncounter.Area, global::InnocenceEncounter.Tick));
        module.Lifetime.Own(global::InnocenceEncounter.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::InnocenceEncounter.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::DaressoKaomEncounters.KaomsStronghold, global::DaressoKaomEncounters.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::DaressoKaomEncounters.DaressosDream, global::DaressoKaomEncounters.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::DaressoKaomEncounters.GrandArena, global::DaressoKaomEncounters.Tick));
        module.Lifetime.Own(global::DaressoKaomEncounters.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::DaressoKaomEncounters.Leaving(player));
        foreach (string earlyArea in global::EarlyActBossPolish.Areas)
            GameEvents.AreaTick.Add(id, GameEvents.InArea(earlyArea, global::EarlyActBossPolish.Tick));
        GameEvents.MonsterKilled.Add(id, global::EarlyActBossPolish.OnMonsterKilled);
        module.Lifetime.Own(global::EarlyActBossPolish.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::EarlyActBossPolish.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::KitavaActTen.Area, global::KitavaActTen.Tick));
        module.Lifetime.Own(global::KitavaActTen.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::KitavaActTen.Leaving(player));
        // Act 6-10 dedicated campaign boss encounter controllers.
        global::Act6To8Encounters.Register(module);
        global::Act9To10Encounters.Register(module);
        module.Lifetime.Own(global::VaalOversoul.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::VaalOversoul.Leaving(player));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::BrineKing.PlayerStats);
        // Curses, Withered, grounds and the other debuffs monsters put on the player (Game.PlayerDebuffs).
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, Rules.PlayerDebuffStats);
        module.Lifetime.Own(global::BrineKing.CleanupOnUnload());
        module.Lifetime.Own(global::AtlasBosses.CleanupOnUnload());
        GameEvents.Kill.Add(module.Manifest.Id, Rules.ReservationKill);
        GameEvents.Kill.Add(module.Manifest.Id, Rules.GrantLinkedMinionRareModifiers);
        GameEvents.Kill.Add(module.Manifest.Id, Rules.ItemMinionsOnKill);
        GameEvents.Kill.Add(module.Manifest.Id, Rules.AkoyaItemOnKill);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, Rules.ItemMinionStatSources);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, Rules.ItemHarbingerStatSources);
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => global::Slayer.Tick(player)));
        GameEvents.Kill.Add(module.Manifest.Id, global::Slayer.Kill);
        GameEvents.Kill.Add(module.Manifest.Id, Rules.SpreadPoisonOnKill);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Slayer.PlayerStats);
        GameEvents.OutgoingDamage.Add(module.Manifest.Id, global::Slayer.OutgoingDamage);
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::Slayer.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => global::Game.VaalImmortalCallTick(player)));
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => global::Game.VaalArcticArmourTick(player)));
        GameEvents.TransientClosing.Add(id, global::Game.EndTimedSkillBuffs);
        GameEvents.PlayerLeaving.Add(id, (player, _) => { global::Game.EndTimedSkillBuffs(player); return Task.CompletedTask; });
        module.Lifetime.Own(new global::Game.TimedSkillBuffCleanup());
        GameEvents.SkillCastAt.Add(id, Rules.CastZoneSpell);
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => Rules.TickZoneSpells(player)));
        GameEvents.PlayerStatSources.Add(id, Rules.ZoneSpellPlayerStats);
        GameEvents.PlayerLeaving.Add(id, (player, _) => { Rules.EndZoneSpells(player); return Task.CompletedTask; });
        GameEvents.TransientClosing.Add(id, Rules.EndZoneSpells);
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::Game.LeavingVaalImmortalCall(player));
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => global::Hierophant.Tick(player)));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Hierophant.PlayerStats);
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::Hierophant.Leaving(player));
        GameEvents.Packet(InstanceEntities.ResetAllPassivesId).Add(module.Manifest.Id, async (player, _) =>
        {
            await Rules.ResetPassivesFree(player);
            return true;
        });
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::BrineKing.Leaving(player));

        // The post-campaign unlock (World/UnlockAll.cs): the NPCTalk rows that only appear once their quest flags are set.
        // "data/unlock-all.txt" makes it automatic, "/unlock [all]" applies it on demand.
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => Rules.UnlockTick(Program.PlayerOf(player))));
        GameEvents.Command(id, "unlock", "/unlock [all] (grant the quest flags that gate post-campaign dialogue)", async (player, args) =>
        {
            var mod = Program.PlayerOf(player);
            bool all = args.Length > 0 && string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase);
            int set = await Rules.Unlock(mod, all);
            await mod.Tell(set == 0
                ? "Nothing to unlock: every post-campaign flag is already set."
                : $"Unlocked {set} post-campaign quest flag(s){(all ? " (all, with the epilogue flag)" : "")}.");
        });
        GameEvents.CompanionTick.Add(id, Rules.TickBrands);
        GameEvents.PlayerStatSources.Add(id, Rules.BrandPlayerStats);
        GameEvents.PlayerLeaving.Add(id, (player, _) => { Rules.ClearBrands(player); return Task.CompletedTask; });
        GameEvents.TransientClosing.Add(id, Rules.ClearBrands);
        GameEvents.GrantedSkillExecutable.Add(id, (_, effectId) => Rules.IsBrandsurgeEffect(effectId));
        GameEvents.Kill.Add(id, Rules.BrandOnMonsterKill);
        module.Lifetime.Own(Rules.CleanupBrandsOnUnload());
        GameEvents.AreaGenerated.Add(id, CharacterVoiceLines.OnAreaGenerated);
        GameEvents.Kill.Add(id, CharacterVoiceLines.OnKill);
        GameEvents.AreaHeader.Add(id, player => new AreaHeader(null, Rules.ActiveAchievementSets(player)));
        GameEvents.EnterMessages.Add(id, Rules.AchievementEnterMessages);
        GameEvents.PlayerEntered.Add(id, Rules.CheckAreaEntryAchievements);
        GameEvents.Kill.Add(id, Rules.OnMonsterKillAchievements);
    }
}

public sealed class GameLogic(IModuleLifetime? lifetime = null) : IGameLogic, IControlledPlayerRules, IControlledPlayerCommands, IWorldAdmissionRules
{
    public IModuleLifetime? Lifetime { get; } = lifetime ?? Rules.CombatLifetime;
    public bool WorldAdmissionActive => Lifetime is { IsActive: true } && ReferenceEquals(Lifetime, Rules.CombatLifetime);
    public double MaximumLeaderDistance => Rules.ControlledFollowerLeash;
    public ItemModStats.ModifierValueRule? ItemModifierValues(Item item, ModCatalog mods) =>
        HeistModifierMagnitudes.For(item, mods);
    public void ValidateImportedBuild(GameServer.Core.Accounts.Character character, CharacterSaves.Save save) =>
        Rules.ValidateImportedBuild(character, save);
    public void QueueControlledIntent(Player player, ControlledPlayerIntent intent) => Rules.QueueControlledIntent(player, intent);
    public void ClearControlledIntent(Player player) => Rules.ClearControlledIntent(player);
    public object ObserveControlledPlayer(Player player, Player leader) => Rules.ObserveControlledPlayer(player, leader);
    public void ResumePlayerUpdates(Player player)
    {
        if (ReferenceEquals(Program.GameLogicOrNull, this) && ReferenceEquals(Lifetime, Rules.CombatLifetime))
            Rules.EnsurePlayerUpdates(player);
    }
    public Task WorldFrameEnded(Player player, double nextFrameStart) =>
        Rules.LockstepFrameEnded(player, nextFrameStart);
    public void WritingWorldDelta(Player player, IReadOnlyList<AreaDelta.Record> delta) =>
        Rules.CorpseDeathsWritten(player, delta);
    public Task OnClientMessages(IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, Player player, long readAt) =>
        Rules.OnClientMessages(messages, unknown, player, readAt);

    public void Admitting(Player player) => Rules.RegisterVitalsAdmission(player);

    /// <summary>GGG's login rule: one connection holds a character at a time. The newer login takes it and the older client
    /// is told and dropped, and this one's character is loaded only after that connection saved what it held, so the two
    /// never write the same character (or its items) from two states.</summary>
    public async Task<bool> AdmittingAsync(Player player)
    {
        // A Ruthless league's data set is built for its first character, before that character is loaded.
        if (Program.LeagueFor(player) is { HardMode: true } league) await Task.Run(() => Rules.LoadHardModeData(league));
        foreach (var previous in Rules.ClaimCharacter(player))
        {
            // An area change: the old connection handed the character over (Travel.Transfer) and the client drops it once
            // this one is up (09 搂6). It is not another login, and main's transfer path already orders its saves.
            if (previous.TransferAccepted) continue;
            Console.WriteLine($"[gateway] {Program.CharacterName(player)}: another login took the character; ending the older connection");
            try
            {
                await Program.EndAsync(previous, "another login took this character",
                    "This character was entered from another client: you have been disconnected.");
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"[gateway] {Program.CharacterName(player)}: the older connection did not finish ending within "
                    + $"{Program.EndingTimeout.TotalSeconds:0} s; this login is refused");
                return false;
            }
        }
        return true;
    }

    public int PrepareCharacter(Player player, int level)
    {
        Rules.RefuseInterruptedHardcoreDeath(player);
        level = Rules.PrepareCharacterForArea(player, level);
        global::KitavaActTen.ConfigurePortal(player);
        // Restore mode vitals before any admitted-area send can fail and enter save cleanup.
        Rules.InitializeCharacterVitals(player);
        return level;
    }

    public IReadOnlyDictionary<uint, int>? AreaLeagueStats(Player player) => Rules.LeagueStatsFor(player);

    public async Task Entered(Player player, int level, int ticketLevel)
    {
        // The client generates the area from here (S鈫扖 0x1d) until its 0x7d: the packs are planned meanwhile.
        Rules.PlanPopulationAhead(player);
        if (level != ticketLevel)
        {
            // The saved experience is the character's level: Auth's (the ticket's) was not updated at the last level-up.
            var fixedLevel = await Rules.SynchronizeAccountLevel(player);
            Console.WriteLine($"[gateway] {player.Connection.Remote} {Program.CharacterName(player)}: level {level} from the saved experience (the account had {ticketLevel})" +
                (fixedLevel.Ok ? "" : $"; not saved to the account: {fixedLevel.Error}"));
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} {Program.CharacterName(player)}: {Rules.SkillsFor(player).ClassFor(player.Character.ClassRow).ObjectType} (Characters row {player.Character.ClassRow}), level {player.Pools.Level} (experience {player.Character.Experience}, {player.Character.Passives.Count} passive(s), life {player.Pools.Life}, mana {player.Pools.Mana}, attack speed +{player.Pools.AttackSpeedPct}%), league {player.Character.League ?? "?"}");
    }

    public void Closing(Player player)
    {
        foreach (var close in GameEvents.TransientClosing.For(player)) close(player);
        Rules.EndEscapePause(player);
        player.PaidChannel = null;
        player.NonChaosDotBypassCarry = player.NonChaosDotTakenCarry = player.ChaosDotBypassCarry = player.ChaosDotManaCarry = 0;
        player.IncomingAilments.Clear();
        Rules.ClearPlayerDecay(player);
        Rules.ClearCorruptedBlood(player);
        Rules.ClearEssenceAuraExposure(player);
        player.RetirePlayerUpdates();
        Rules.FortificationOf(player).Clear();
        Rules.LeechOf(player).Clear(Rules.AreaTime(player));
        player.Rage.Clear();
        player.Rampage.Clear();
        player.RetireOwnedCombat();
        player.RetireResourceEffects();
        player.VaalPreventionUntil = 0;
    }

    public void Save(Player player, bool prepared)
    {
        // A refused admission must not load/start inventories or flush a previous connection's cached copy.
        if (prepared)
        {
            CharacterSaves.SaveExperience(Program.CharacterName(player));
            Rules.ItemsOf(player).SaveUnsaved();
            Rules.PersistVitals(player);
            Rules.SaveGolems(player);
        }
        Rules.CommitHardcoreDeath(player);
    }

    public void Left(Player player)
    {
        Rules.ReleaseCharacter(player);
        Rules.ForgetVitalsAdmission(player);
        Rules.ClearPyroclastAuras(player);
        Rules.ForgetSourceValues(player);
        // The instance keeps its monsters as they are now, for the next connection (#12); the player's minions go with him.
        Rules.KeepInstanceMonsters(player);
    }

    public IDisposable ProjectileMutation(Player player) => Rules.ProjectileMutation(player, advance: false);
    public IGamePlayer PlayerFor(Player player) => new Rules.ModPlayer(player);
    public IPlayerWorld WorldFor(Player player) => new Rules.ModuleWorld(player);
    public IEnumerable<(string Value, string Detail)>? CompletionValues(string kind) => Rules.CompletionValues(kind);
    public double PlayerActionFactor(Player player) => Rules.PlayerActionFactor(player);
    public double MovementSpeedMultiplier(Player player) => Rules.MovementSpeedMultiplier(player);
    public CharacterSaves.Save BuildControlled(GameServer.Core.Accounts.CharacterClass characterClass, int level) => Rules.BuildControlledPlayer(characterClass, level);
    public Task InitializeControlled(Player player, Player leader) => Rules.InitializeControlledPlayer(player, leader);
    public Task StepControlled(Player player, Player leader) => Rules.StepControlledPlayer(player, leader);
    public Task ControlledCompletion(Player player) => player.PlayerUpdatesState is PlayerUpdatesState updates ? updates.Completion : Task.CompletedTask;
}

