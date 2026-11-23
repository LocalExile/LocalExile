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
            _ = CharacterAudioCatalog.Default.Value;
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
        RuleTables.Empty();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        Rules.CombatLifetime = module.Lifetime;
        module.Lifetime.Own(Rules.CleanupTemporaryAuras());
        module.Lifetime.Own(Rules.CleanupEssenceAuras());
        module.Lifetime.Own(Rules.CleanupMonsterSummons());
        GameEvents.Tick.Add(module.Manifest.Id, (conn, _) => global::Sirus.Tick(conn));
        // A map device's portal labels read "<area> (Completed)" once that run's boss is dead (World/Portals.cs).
        GameEvents.Tick.Add(module.Manifest.Id, (conn, _) => Rules.TickMapRunBoss(conn));
        GameEvents.MonsterKilled.Add(module.Manifest.Id, kill => Rules.TickMapRunBoss(Program.ConnOf(kill.Player), force: true));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => global::Sirus.Leaving(conn));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Sirus.PlayerStats);
        InstanceEvents.AreaStats.Add(module.Manifest.Id, Rules.MapAreaStats);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabContent.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabPacks.Opening);
        GameEvents.Drops.Add(module.Manifest.Id, MapScarabPacks.Drops);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapStrongboxes.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapStrongboxes.Enter);
        GameEvents.ObjectUse.Add(module.Manifest.Id, MapStrongboxes.Use);
        GameEvents.Tick.Add(module.Manifest.Id, (conn, _) => MapStrongboxes.Tick(conn));
        GameEvents.ChestsOpened.Add(module.Manifest.Id, MapStrongboxes.OpenNear);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapStrongboxes.CurseStats);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemOnObjectId).Add(module.Manifest.Id, MapStrongboxes.UseItemOn);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapStrongboxes.Leave(conn); return Task.CompletedTask; });
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapStrongboxes.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapEssences.Opening);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabBosses.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapEssences.Enter);
        GameEvents.ObjectUse.Add(module.Manifest.Id, MapEssences.Use);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemOnObjectId).Add(module.Manifest.Id, MapEssences.UseItemOn);
        GameEvents.Tick.Add(module.Manifest.Id, MapEssences.Tick);
        GameEvents.Drops.Add(module.Manifest.Id, MapEssences.Drops);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapEssences.Leave(conn); return Task.CompletedTask; });
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapEssences.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapTorment.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapTorment.Enter);
        GameEvents.Tick.Add(module.Manifest.Id, MapTorment.Tick);
        GameEvents.Drops.Add(module.Manifest.Id, MapTorment.Drops);
        GameEvents.Kill.Add(module.Manifest.Id, MapTorment.Kill);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapTorment.Leave(conn); return Task.CompletedTask; });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapTorment.PlayerStats);
        GameEvents.BuffCancel.Add(module.Manifest.Id, MapTorment.CancelBuff);
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapTorment.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapHarbinger.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapHarbinger.Enter);
        GameEvents.Tick.Add(module.Manifest.Id, MapHarbinger.Tick);
        GameEvents.Kill.Add(module.Manifest.Id, MapHarbinger.Kill);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapHarbinger.Leave(conn); return Task.CompletedTask; });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapHarbinger.PlayerStats);
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapHarbinger.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapBeyond.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapBeyond.Enter);
        GameEvents.Tick.Add(module.Manifest.Id, MapBeyond.Tick);
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
        Rules.HardModeSkills.Value.WarmMonsterMeleeClocks();
        WarmCatalogues();
        Program.SetGameLogic(new GameLogic(module.Lifetime));
        module.Lifetime.Own(Rules.InstallMirror());
        string id = module.Manifest.Id;
        GameEvents.Tick.Add(id, (conn, _) => global::DoedreSewers.Tick(conn));
        GameEvents.ObjectUse.Add(module.Manifest.Id, global::DoedreSewers.Use);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::DoedreSewers.PlayerStats);
        module.Lifetime.Own(global::DoedreSewers.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::DoedreSewers.Leaving(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::BrineKing.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::AtlasBosses.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::VaalOversoul.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::DepravedTrinity.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::Arakaali.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::ArakaaliLasers.Tick(conn));
        module.Lifetime.Own(global::ArakaaliLasers.CleanupOnUnload());
        GameEvents.Tick.Add(id, (conn, _) => global::Yugul.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::SolarisLunaris.Tick(conn));
        module.Lifetime.Own(global::SolarisLunaris.CleanupOnUnload());
        module.Lifetime.Own(global::Yugul.CleanupOnUnload());
        module.Lifetime.Own(global::Arakaali.CleanupOnUnload());
        module.Lifetime.Own(global::DepravedTrinity.CleanupOnUnload());
        GameEvents.ObjectUse.Add(module.Manifest.Id, global::DepravedTrinity.Use);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::DepravedTrinity.PlayerStats);
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::DepravedTrinity.Leaving(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::KitavaActFive.Tick(conn));
        module.Lifetime.Own(global::KitavaActFive.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::KitavaActFive.Leaving(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::KitavaActTen.Tick(conn));
        module.Lifetime.Own(global::KitavaActTen.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::KitavaActTen.Leaving(conn));
        module.Lifetime.Own(global::VaalOversoul.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::VaalOversoul.Leaving(conn));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::BrineKing.PlayerStats);
        module.Lifetime.Own(global::BrineKing.CleanupOnUnload());
        module.Lifetime.Own(global::AtlasBosses.CleanupOnUnload());
        GameEvents.Kill.Add(module.Manifest.Id, Rules.ReservationKill);
        GameEvents.Tick.Add(id, (conn, _) => global::Slayer.Tick(conn));
        GameEvents.Kill.Add(module.Manifest.Id, global::Slayer.Kill);
        GameEvents.Kill.Add(module.Manifest.Id, Rules.SpreadPoisonOnKill);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Slayer.PlayerStats);
        GameEvents.OutgoingDamage.Add(module.Manifest.Id, global::Slayer.OutgoingDamage);
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::Slayer.Leaving(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::Game.VaalImmortalCallTick(conn));
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::Game.LeavingVaalImmortalCall(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::Hierophant.Tick(conn));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Hierophant.PlayerStats);
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::Hierophant.Leaving(conn));
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::BrineKing.Leaving(conn));

        // Kingsmarch, the Settlers league's hub (World/KingsmarchTown.cs, World/KingsmarchPort.cs): the town's objects, its
        // farm and its workers. The client draws the room's own doodads only up to the plain props, so the hub's floor,
        // decks, buildings and farm plots are objects the server has to create.
        GameEvents.Tick.Add(id, async (conn, now) => await KingsmarchTown.Tick(Program.PlayerOf(conn), now));
        GameEvents.AreaGenerated.Add(id, conn => { KingsmarchTown.Entered(Program.PlayerOf(conn)); return Task.CompletedTask; });
        GameEvents.ObjectUse.Add(id, async (conn, use, _) => await KingsmarchTown.Use(Program.PlayerOf(conn), use.TargetId));
        // The settlement records go out with the area's own messages: the client reads them once, when it builds the area.
        GameEvents.BeforeEnterMessages.Add(id, conn => KingsmarchTown.BeforeEnterMessages(conn));
        // The harbour: one shipment at a time per port, resolved on a timer (World/KingsmarchPort.cs).
        GameEvents.Tick.Add(id, async (conn, now) => await KingsmarchPort.Tick(Program.PlayerOf(conn), now));
        // Johan's "Fund the Treasury" (NPCTalk 667, category Quests) moves gold from the account's stash into the town: the
        // panel's own button cannot exist (the client's village UI elements are never created), so the option acts here.
        GameEvents.NpcOption.Add(id, async talk =>
        {
            if (!string.Equals(talk.Option, "Fund the Treasury", StringComparison.Ordinal)) return false;
            var player = Program.PlayerOf(talk.Conn);
            await Program.WorldOf(player).NpcClose("0x0084 close the dialogue for Fund the Treasury");
            await player.Tell("Kingsmarch: " + await KingsmarchTown.FundTreasury(player, TreasuryPerPick));
            return true;
        });
        // The post-campaign unlock (World/UnlockAll.cs): the NPCTalk rows that only appear once their quest flags are set.
        // "data/unlock-all.txt" makes it automatic, "/unlock [all]" applies it on demand.
        GameEvents.Tick.Add(id, (conn, _) => Rules.UnlockTick(Program.PlayerOf(conn)));
        GameEvents.CommandsBuilding.Add(id, (commands, remote, stream, ciphers, conn) =>
        {
            var player = Program.PlayerOf(conn);
            return commands
                .Add("unlock", "/unlock [all] (grant the quest flags that gate post-campaign dialogue)", async args =>
                {
                    bool all = args.Length > 0 && string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase);
                    int set = await Rules.Unlock(player, all);
                    await player.Tell(set == 0
                        ? "Nothing to unlock: every post-campaign flag is already set."
                        : $"Unlocked {set} post-campaign quest flag(s){(all ? " (all, with the epilogue flag)" : "")}.");
                })
                .Add("village", "/village [hire | assign <worker> <plot>] (the Kingsmarch farm)", async args =>
                {
                    string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                    if (verb == "hire") { await player.Tell("Kingsmarch: " + await KingsmarchTown.Hire(player)); return; }
                    if (verb == "assign")
                    {
                        if (args.Length < 3 || !int.TryParse(args[1], out int worker) || !int.TryParse(args[2], out int plot))
                        {
                            await player.Tell("usage: /village assign <worker> <plot 1-6>");
                            return;
                        }
                        await player.Tell("Kingsmarch: " + await KingsmarchTown.Assign(player, worker, plot));
                        return;
                    }
                    if (verb == "stock" && args.Length >= 3 && int.TryParse(args[2], out int amount))
                    {
                        int now = KingsmarchTown.Grant(player, args[1], amount);
                        await player.Tell(now == 0
                            ? "usage: /village stock <resource> <count>  (e.g. Wheat, IronOre)"
                            : $"Kingsmarch: put {amount} {args[1]} in the town, {now} there now.");
                        return;
                    }
                    await player.Tell(KingsmarchTown.StoreText(player));
                })
                .Add("recruit", "/recruit [hire <1-3>] (Kingsmarch's workers)", async args =>
                {
                    if (args.Length >= 2 && string.Equals(args[0], "hire", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(args[1], out int pick))
                    {
                        await player.Tell("Kingsmarch: " + await KingsmarchTown.Recruit(player, pick));
                        return;
                    }
                    await player.Tell(KingsmarchTown.RecruitText(player));
                })
                .Add("treasury", "/treasury [gold] (the Kingsmarch treasury)", async args =>
                {
                    if (args.Length > 0 && int.TryParse(args[0], out int gold) && gold > 0)
                    {
                        await player.Tell("Kingsmarch: " + await KingsmarchTown.FundTreasury(player, gold));
                        return;
                    }
                    await player.Tell(KingsmarchTown.TreasuryText(player));
                })
                .Add("port", "/port [send <port> <crop> <count>] (Kingsmarch's harbour)", async args =>
                {
                    if (args.Length >= 4 && string.Equals(args[0], "send", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(args[2 - 1], out int port) && int.TryParse(args[3], out int count))
                    {
                        await player.Tell("Kingsmarch: " + KingsmarchPort.Send(player, port, args[2], count));
                        return;
                    }
                    if (args.Length >= 2 && string.Equals(args[0], "crew", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(args[1], out int more) && more > 0)
                    {
                        await player.Tell("Kingsmarch: " + KingsmarchPort.Crew(player, more));
                        return;
                    }
                    await player.Tell(KingsmarchPort.Report(player));
                });
        });
        GameEvents.CompanionTick.Add(id, Rules.TickBrands);
        GameEvents.PlayerStatSources.Add(id, Rules.BrandPlayerStats);
        GameEvents.PlayerLeaving.Add(id, (conn, _) => { Rules.ClearBrands(conn); return Task.CompletedTask; });
        GameEvents.TransientClosing.Add(id, Rules.ClearBrands);
        GameEvents.GrantedSkillExecutable.Add(id, (_, effectId) => Rules.IsBrandsurgeEffect(effectId));
        GameEvents.Kill.Add(id, Rules.BrandOnMonsterKill);
        module.Lifetime.Own(Rules.CleanupBrandsOnUnload());
        GameEvents.AreaGenerated.Add(id, CharacterVoiceLines.OnAreaGenerated);
        GameEvents.Kill.Add(id, CharacterVoiceLines.OnKill);    }
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
    public void QueueControlledIntent(ClientState player, ControlledPlayerIntent intent) => Rules.QueueControlledIntent(player, intent);
    public void ClearControlledIntent(ClientState player) => Rules.ClearControlledIntent(player);
    public object ObserveControlledPlayer(ClientState player, ClientState leader) => Rules.ObserveControlledPlayer(player, leader);
    public void ResumePlayerUpdates(ClientState conn)
    {
        if (ReferenceEquals(Program.GameLogicOrNull, this) && ReferenceEquals(Lifetime, Rules.CombatLifetime))
            Rules.EnsurePlayerUpdates(conn);
    }
    public Task WorldFrameEnded(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double nextFrameStart) =>
        Rules.LockstepFrameEnded(remote, stream, ciphers, conn, nextFrameStart);
    public Task OnClientMessages(string remote, NetworkStream stream, ConnectionCiphers ciphers, IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, ClientState conn, long readAt) =>
        Rules.OnClientMessages(remote, stream, ciphers, messages, unknown, conn, readAt);

    public void Admitting(ClientState conn) => Rules.RegisterVitalsAdmission(conn);

    /// <summary>GGG's login rule: one connection holds a character at a time. The newer login takes it and the older client
    /// is told and dropped, and this one's character is loaded only after that connection saved what it held, so the two
    /// never write the same character (or its items) from two states.</summary>
    public async Task<bool> AdmittingAsync(ClientState conn)
    {
        foreach (var previous in Rules.ClaimCharacter(conn))
        {
            // An area change: the old connection handed the character over (Travel.Transfer) and the client drops it once
            // this one is up (09 §6). It is not another login, and main's transfer path already orders its saves.
            if (previous.TransferAccepted) continue;
            Console.WriteLine($"[gateway] {Program.CharacterName(conn)}: another login took the character; ending the older connection");
            try
            {
                await Program.EndAsync(previous, "another login took this character",
                    "This character was entered from another client: you have been disconnected.");
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"[gateway] {Program.CharacterName(conn)}: the older connection did not finish ending within "
                    + $"{Program.EndingTimeout.TotalSeconds:0} s; this login is refused");
                return false;
            }
        }
        return true;
    }

    public int PrepareCharacter(string remote, ClientState conn, int level)
    {
        Rules.RefuseInterruptedHardcoreDeath(conn);
        level = Rules.PrepareCharacterForArea(remote, conn, level);
        global::KitavaActTen.ConfigurePortal(conn);
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
        Rules.EndEscapePause(conn);
        conn.PaidChannel = null;
        conn.NonChaosDotBypassCarry = conn.NonChaosDotTakenCarry = conn.ChaosDotBypassCarry = 0;
        conn.IncomingAilments.Clear();
        Rules.ClearPlayerDecay(conn);
        Rules.ClearCorruptedBlood(conn);
        Rules.ClearEssenceAuraExposure(conn);
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
        Rules.ReleaseCharacter(conn);
        Rules.ForgetVitalsAdmission(conn);
        Rules.ForgetSourceValues(conn);
        // The instance keeps its monsters as they are now, for the next connection (#12); the player's minions go with him.
        Rules.KeepInstanceMonsters(conn);
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
