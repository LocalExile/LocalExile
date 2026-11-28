using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using G = global::Game;

namespace GameServer.Modules.Settlers.Town;

/// <summary>
/// Karui Shores intro, Act 11 Settlers quests (<c>Quest</c> 196 <c>settlers_tutorial</c> and 197 <c>settlers_mining</c>),
/// Johan &amp; Faustus Hideout invitations, and <c>0x001f</c> (<c>0x9b</c>) ruleset attachment + village sync
/// across Kingsmarch, Karui Shores, and Hideouts.
/// </summary>
public static class KaruiShoresIntro
{
    public const string KaruiShoresAreaId = "2_11_endgame_town";
    public const string KingsmarchAreaId = KingsmarchHubWorld.AreaId;

    // NPC metadata Ids
    public const string NpcPrefix = "Metadata/NPC/League/Kalguur/";
    public const string NpcJohanKingsmarch = NpcPrefix + "VillageMayor";
    public const string NpcJohanTown = NpcPrefix + "VillageMayorTown";
    public const string NpcJohanHideout = NpcPrefix + "VillageMayorHideout";
    public const string NpcFaustusKingsmarch = NpcPrefix + "VillageFaustus";
    public const string NpcFaustusTown = NpcPrefix + "VillageFaustusTown";
    public const string NpcFaustusHideout = NpcPrefix + "VillageFaustusHideout";

    // QuestFlags.dat Ids (3.29.3c verified from client.db)
    public const string QfIsKingsmarchArea = "IsKingsmarchArea";          // 3229
    public const string QfIsMapOwner = "IsMapOwner";                      // 2020
    public const string QfJohanIntroTownSeen = "JohanIntroTownSeen";      // 3233
    public const string QfMetJohan = "Met-Johan";                         // 3271
    public const string QfMetFaustusVillage = "Met-FaustusVillage";       // 3274
    public const string QfCanSeeFaustusInHideout = "CanSeeFaustusInHideout"; // 3509

    // LeagueQuestFlags IDs (1-based client IDs verified from NPCTalk & QuestStates 196/197)
    public const int LqfKingsmarchUnlocked = 608;          // VillageKingsmarchUnlocked
    public const int LqfDiscoverStrangeOres = 609;         // VillageDiscoverStrangeOres
    public const int LqfClearedStrangeOre = 610;           // Ore cleared for prospectors
    public const int LqfAssignedMiner = 611;               // Mining worker assigned
    public const int LqfMiningEstablished = 613;           // Mining unlocked
    public const int LqfSmeltingDannig = 619;              // VillageKingsmarchSmeltingDannig
    public const int LqfFarmingTalkNpc = 623;              // VillageKingsmarchFarmingTalkNPC
    public const int LqfRogIntroSeen = 627;                // Rog intro seen
    public const int LqfIslaIntroSeen = 630;               // Isla intro seen
    public const int LqfTujenIntroSeen = 335;              // Tujen intro seen
    public const int LqfTalkInvest = 334;                  // VillageTalkInvest (Manage Town + noticeboards)
    public const int LqfJohanTownIntro = 685;              // Karui Shores Johan intro seen
    public const int LqfFaustusMet = 686;                  // VillageFaustusMet
    public const int LqfJohanKingsmarchIntro = 689;        // Kingsmarch Johan intro seen
    public const int LqfJohanFaustusTalk = 88;
    public const int LqfJohanRaulfTalk = 95;
    public const int LqfRaulfIntro = 108;
    public const int LqfRecruitmentEstablished = 131;
    public const int LqfMiningQuestComplete = 717;
    public const int LqfSmeltingUnlocked = 1364;
    public const int LqfFarmingUnlocked = 1375;
    public const int LqfShippingUnlocked = 1386;
    public const int LqfDisenchantingUnlocked = 1397;
    public const int LqfMappingUnlocked = 1408;
    public const int LqfRecombinatorBaseUnlocked = 1419;
    public const int LqfTownUpgradesBase = 1427;
    public const int LqfRecruitHired = 1434;
    public const int LqfGoldDeposited = 1436;
    public const int LqfTutorialComplete = 1440;
    public const int LqfMappingTalkIsla = 1577;            // VillageKingsmarchMappingTalkIsla
    public const int LqfRecombinatorUnlocked = 1578;       // VillageKingsmarchRecombinatorUnlocked

    /// <summary>All 3.29 core pre-unlocked Kingsmarch infrastructure &amp; interactable league quest flags.</summary>
    static readonly int[] KingsmarchCoreLeagueFlags =
    [
        LqfKingsmarchUnlocked,
        LqfJohanTownIntro,
        LqfFaustusMet,
        LqfJohanKingsmarchIntro,
        LqfJohanFaustusTalk,
        LqfJohanRaulfTalk,
        LqfRaulfIntro,
        LqfRecruitmentEstablished,
        LqfMiningEstablished,
        LqfSmeltingUnlocked,
        LqfFarmingUnlocked,
        LqfShippingUnlocked,
        LqfDisenchantingUnlocked,
        LqfMappingUnlocked,
        LqfRecombinatorBaseUnlocked,
        LqfTownUpgradesBase,
        LqfTalkInvest,
        LqfSmeltingDannig,
        LqfFarmingTalkNpc,
        LqfTujenIntroSeen,
        LqfRogIntroSeen,
        LqfIslaIntroSeen,
        LqfMappingTalkIsla,
        LqfRecombinatorUnlocked,
    ];

    static readonly string[] KingsmarchAreaFlags = [QfIsKingsmarchArea, QfIsMapOwner];
    static readonly string[] NoAreaFlags = [];

    sealed class TickAccumulator { public double Seconds; }
    static readonly ConditionalWeakTable<Player, TickAccumulator> TickAccumulators = new();

    public static bool NeedsVillageRuleset(Player player) =>
        player.Layout.Id.Equals(KingsmarchAreaId, StringComparison.OrdinalIgnoreCase)
        || player.Layout.Id.Equals(KaruiShoresAreaId, StringComparison.OrdinalIgnoreCase)
        || HideoutCatalog.ByArea(player.Layout.Hash) is not null;

    /// <summary>S→C <c>0x001f</c> attaching <c>VillageRules</c> (<c>0x9b</c>) before <c>0x0021</c> activates the area.</summary>
    public static IEnumerable<(string Log, byte[] Message)> BeforeEnterMessages(Player player)
    {
        if (!NeedsVillageRuleset(player)) yield break;
        yield return ("0x001f attach VillageRules (0x9b)", InstanceVillage.AttachRuleset());
    }

    /// <summary>Area-local quest flags granted in Kingsmarch (<c>IsKingsmarchArea</c> + <c>IsMapOwner</c>).</summary>
    public static IReadOnlyList<string> AreaQuestFlags(Player player) =>
        KingsmarchHubWorld.IsKingsmarch(player) ? KingsmarchAreaFlags : NoAreaFlags;

    /// <summary>All Kalguur / Kingsmarch NPCs evaluate and set their <c>LeagueQuestFlags</c> columns.</summary>
    public static bool UsesNpcLeagueFlags(Player player, NpcCatalog.Npc npc) =>
        npc.Id.StartsWith(NpcPrefix, StringComparison.Ordinal);

    /// <summary>On entering Kingsmarch, Karui Shores, or Hideout: ensure 3.29 flags, run offline catch-up, and sync full village state.</summary>
    public static async Task OnPlayerEnteredAsync(Player player)
    {
        if (!NeedsVillageRuleset(player)) return;
        VillageTownService.TrackPlayer(player);

        if (KingsmarchHubWorld.IsKingsmarch(player))
        {
            await EnsureKingsmarchUnlockedFlagsAsync(player);
            await KingsmarchHubWorld.OnPlayerEnteredAsync(player);
            var state = VillageTownService.GetState(player);
            lock (state.Gate)
            {
                if (!state.TutorialFlags.HasFlag(VillageTutorialFlags.KingsmarchVisited))
                {
                    state.TutorialFlags |= VillageTutorialFlags.KingsmarchVisited;
                    VillageTownService.SaveLocked(state);
                }
            }
        }
        else
        {
            // Keep Johan's Karui Shores "Introduction" (NPCTalk 3632, hidden if 685 or 608 is set) visible on first visit;
            // only sync baseline access flags once the player has heard Johan's intro or visited Kingsmarch.
            var state = VillageTownService.GetState(player);
            bool unlocked;
            lock (state.Gate)
            {
                unlocked = state.TutorialFlags.HasFlag(VillageTutorialFlags.JohanIntroDone)
                    || state.TutorialFlags.HasFlag(VillageTutorialFlags.KingsmarchVisited);
            }
            if (unlocked)
                await EnsureBaselineAccessFlagsAsync(player);
        }

        await VillageTownService.SyncFullAsync(player);
    }

    /// <summary>Spawns Johan (<c>VillageMayorHideout</c>) in hideouts once invited (or after visiting Kingsmarch).</summary>
    public static async Task OnHideoutReadyAsync(Player player)
    {
        if (HideoutCatalog.ByArea(player.Layout.Hash) is null) return;
        var state = VillageTownService.GetState(player);
        bool johanAllowed;
        lock (state.Gate)
        {
            johanAllowed = state.TutorialFlags.HasFlag(VillageTutorialFlags.JohanInvitedToHideout)
                || state.TutorialFlags.HasFlag(VillageTutorialFlags.KingsmarchVisited);
        }
        if (!johanAllowed) return;

        // Avoid duplicate spawn if already present in player.Npcs.
        lock (player.Npcs)
        {
            if (player.Npcs.Values.Any(n => n.Npc.Id.Equals(NpcJohanHideout, StringComparison.Ordinal)))
                return;
        }

        if (Npcs.Find(NpcJohanHideout) is not { Supported: true } johan) return;

        // Place Johan cleanly beside Faustus (284, 323) and clear of Eagon (291, 316) on walkable floor.
        var (x, y) = (298, 309);
        if (player.Layout.Walkable?.Invoke(x, y) == false)
            (x, y) = player.Layout.Near((x, y));
        await G.SpawnNpc(player, johan, x, y, 4.0f);
    }

    /// <summary>Handles <c>InviteToHideout</c>, Johan/Faustus intro flags, and pre-open village state sync on NPC options.</summary>
    public static async Task<bool> OnNpcOptionAsync(NpcOption option)
    {
        var player = option.Player;
        SpawnedNpc? npc;
        lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(player.TalkingTo);
        if (npc is null || !npc.Npc.Id.StartsWith(NpcPrefix, StringComparison.Ordinal))
            return false;

        // Handle "Invite to Hideout" for Johan and Faustus.
        if (option.Category == "InviteToHideout")
        {
            if (npc.Npc.Id is NpcJohanTown or NpcJohanKingsmarch)
            {
                var state = VillageTownService.GetState(player);
                lock (state.Gate)
                {
                    state.TutorialFlags |= VillageTutorialFlags.JohanInvitedToHideout;
                    VillageTownService.SaveLocked(state);
                }
                await G.SetQuestFlags(player, [QfMetJohan]);
                await Program.Send(player, InstanceChat.ChatLine("Johan, the King's Hand has been invited to your Hideout."),
                    "0x0017 Johan invited to hideout");
                if (option.HasText)
                    await WorldOf(player).NpcSay(option.Npc, option.Option, option.Index, option.Row);
                else
                    await WorldOf(player).NpcClose("0x0084 Johan invite to hideout complete");
                return true;
            }
            if (npc.Npc.Id is NpcFaustusTown or NpcFaustusKingsmarch)
            {
                var state = VillageTownService.GetState(player);
                lock (state.Gate)
                {
                    state.TutorialFlags |= VillageTutorialFlags.FaustusInvitedToHideout;
                    VillageTownService.SaveLocked(state);
                }
                await G.SetQuestFlags(player, [QfMetFaustusVillage, QfCanSeeFaustusInHideout]);
                await G.SetLeagueQuestFlags(player, [LqfFaustusMet]);
                await Program.Send(player, InstanceChat.ChatLine("Faustus has been invited to your Hideout."),
                    "0x0017 Faustus invited to hideout");
                if (option.HasText)
                    await WorldOf(player).NpcSay(option.Npc, option.Option, option.Index, option.Row);
                else
                    await WorldOf(player).NpcClose("0x0084 Faustus invite to hideout complete");
                return true;
            }
        }

        // Karui Shores & Kingsmarch intro quest flags for Johan & Faustus.
        if (npc.Npc.Id == NpcJohanTown && option.Option == "Introduction")
        {
            await G.SetQuestFlags(player, [QfMetJohan, QfJohanIntroTownSeen]);
            await G.SetLeagueQuestFlags(player, [LqfJohanTownIntro, LqfKingsmarchUnlocked, LqfTalkInvest]);
            var state = VillageTownService.GetState(player);
            lock (state.Gate)
            {
                state.TutorialFlags |= VillageTutorialFlags.JohanIntroDone;
                VillageTownService.SaveLocked(state);
            }
            return false; // Let NpcDialogueModule play the audio/text!
        }

        if (npc.Npc.Id is NpcFaustusTown or NpcFaustusKingsmarch && option.Option == "Introduction")
        {
            await G.SetQuestFlags(player, [QfMetFaustusVillage]);
            await G.SetLeagueQuestFlags(player, [LqfFaustusMet]);
            return false;
        }

        // Whenever a Village* management screen option is chosen, refresh village state first and let NpcScripts open it!
        if (option.Category is "VillageManageTown" or "VillageRecruitment" or "VillageShipping" or "VillageDisenchanting")
        {
            await VillageTownService.TickPlayerAsync(player);
            await VillageTownService.SyncStateAsync(player);
            return false; // Let NpcScripts.EndTalkScript send 0x007b for the exact screen!
        }

        return false;
    }

    /// <summary>Handles <see cref="GameEvents.NpcTalkScript"/> to record flags when <c>GoToKingsmarch()</c> runs.</summary>
    public static async Task<bool> OnNpcTalkScriptAsync(Player player, uint npcId, SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (talk.GoToKingsmarch)
        {
            await G.SetQuestFlags(player, [QfMetJohan, QfJohanIntroTownSeen]);
            await G.SetLeagueQuestFlags(player, [LqfJohanTownIntro, LqfKingsmarchUnlocked, LqfTalkInvest]);
            // Return false so NpcScripts executes the built-in ChangeArea to Kingsmarch!
            return false;
        }
        return false;
    }

    public static Func<Player, double, Task>? TickFor(Player player)
    {
        if (!NeedsVillageRuleset(player)) return null;
        return async (p, dt) =>
        {
            var acc = TickAccumulators.GetOrCreateValue(p);
            acc.Seconds += dt;
            if (acc.Seconds < 1.0) return;
            acc.Seconds = 0.0;
            await VillageTownService.TickPlayerAsync(p);
        };
    }

    public static Task OnPlayerLeavingAsync(Player player, string why)
    {
        VillageTownService.UntrackPlayer(player);
        KingsmarchHubWorld.OnPlayerLeaving(player);
        if (NeedsVillageRuleset(player))
        {
            var state = VillageTownService.GetState(player);
            lock (state.Gate)
            {
                if (VillageTownService.SimulateLocked(state, VillageTownService.ClockUtcSeconds()) || state.Dirty)
                    VillageTownService.SaveLocked(state);
            }
        }
        return Task.CompletedTask;
    }

    public static Task OnCharacterAtRestAsync(Player player)
    {
        VillageTownService.UntrackPlayer(player);
        var state = VillageTownService.GetState(player);
        lock (state.Gate)
        {
            if (VillageTownService.SimulateLocked(state, VillageTownService.ClockUtcSeconds()) || state.Dirty)
                VillageTownService.SaveLocked(state);
        }
        return Task.CompletedTask;
    }

    // ---- Quest progression notifications from economy / map ores ----------------------------------------------------

    public static Task OnFirstRecruitHiredAsync(Player player) =>
        SetMissingLeagueFlagsAsync(player, [LqfRecruitHired]);

    public static Task OnFirstGoldDepositedAsync(Player player) =>
        SetMissingLeagueFlagsAsync(player, [LqfGoldDeposited, LqfTutorialComplete]);

    public static Task OnFirstWorkerAssignedAsync(Player player, byte jobRow)
    {
        if (jobRow is >= VillageData.JobMiningFirst and <= VillageData.JobMiningLast)
            return SetMissingLeagueFlagsAsync(player, [LqfAssignedMiner, LqfMiningQuestComplete]);
        return Task.CompletedTask;
    }

    /// <summary>Called when a player discovers/clears a Strange Ore encounter in maps (Quest 197 <c>settlers_mining</c>).</summary>
    public static Task OnStrangeOreDiscoveredAsync(Player player, bool cleared) =>
        SetMissingLeagueFlagsAsync(player, cleared ? [LqfDiscoverStrangeOres, LqfClearedStrangeOre] : [LqfDiscoverStrangeOres]);

    static async Task EnsureKingsmarchUnlockedFlagsAsync(Player player)
    {
        var desired = new List<int>(KingsmarchCoreLeagueFlags);
        var state = VillageTownService.GetState(player);
        lock (state.Gate)
        {
            if (state.TutorialFlags.HasFlag(VillageTutorialFlags.FirstRecruitHired))
                desired.Add(LqfRecruitHired);
            if (state.TutorialFlags.HasFlag(VillageTutorialFlags.FirstGoldDeposited))
            {
                desired.Add(LqfGoldDeposited);
                desired.Add(LqfTutorialComplete);
            }
            if (state.TutorialFlags.HasFlag(VillageTutorialFlags.FirstWorkerAssigned))
            {
                desired.Add(LqfAssignedMiner);
                desired.Add(LqfMiningQuestComplete);
            }
        }
        await SetMissingLeagueFlagsAsync(player, desired);
        if (!G.ClientHasQuestFlag(player, QfMetJohan))
            await G.SetQuestFlags(player, [QfMetJohan, QfJohanIntroTownSeen]);
    }

    static async Task EnsureBaselineAccessFlagsAsync(Player player)
    {
        await SetMissingLeagueFlagsAsync(player, [LqfKingsmarchUnlocked, LqfTalkInvest]);
    }

    static async Task SetMissingLeagueFlagsAsync(Player player, IEnumerable<int> desired)
    {
        var missing = desired.Where(f => !player.Character.LeagueQuestFlags.Contains(f)).Distinct().ToArray();
        if (missing.Length > 0)
            await G.SetLeagueQuestFlags(player, missing);
    }
}

/// <summary>
/// Single registration entry point for the Kingsmarch Hub World, Karui Shores Intro, and Core Idle Town Engine.
/// Wired from <c>SettlersModule</c> with a 2-line call (<see cref="LoadGameData"/> + <see cref="Register"/>).
/// </summary>
public static class TownSubsystem
{
    public static void LoadGameData()
    {
        VillageData.LoadGameData();
        KingsmarchHubWorld.LoadGameData();
    }

    public static void Register(IModuleContext context)
    {
        string id = context.Manifest.Id;
        context.Lifetime.Own(new Cleanup());

        VillageTownService.OnStateSynced += KingsmarchHubWorld.SyncWorldVisualsAsync;

        GameEvents.BeforeEnterMessages.Add(id, KaruiShoresIntro.BeforeEnterMessages);
        GameEvents.AreaQuestFlags.Add(id, KaruiShoresIntro.AreaQuestFlags);
        GameEvents.NpcUsesLeagueFlags.Add(id, KaruiShoresIntro.UsesNpcLeagueFlags);
        GameEvents.AreaGenerated.Add(id, KingsmarchHubWorld.OnAreaGeneratedAsync);
        GameEvents.PlayerEntered.Add(id, KaruiShoresIntro.OnPlayerEnteredAsync);
        GameEvents.HideoutReady.Add(id, KaruiShoresIntro.OnHideoutReadyAsync);
        GameEvents.ObjectUse.Add(id, KingsmarchHubWorld.OnObjectUseAsync);
        GameEvents.NpcOption.Add(id, KaruiShoresIntro.OnNpcOptionAsync);
        GameEvents.NpcTalkScript.Add(id, KaruiShoresIntro.OnNpcTalkScriptAsync);
        GameEvents.AreaTick.Add(id, KaruiShoresIntro.TickFor);
        GameEvents.PlayerLeaving.Add(id, KaruiShoresIntro.OnPlayerLeavingAsync);
        GameEvents.CharacterAtRest.Add(id, KaruiShoresIntro.OnCharacterAtRestAsync);
        GameEvents.RulesetAction(InstanceVillage.RulesetRow).Add(id, VillageWorkersAndEconomy.HandleCommandAsync);
    }

    sealed class Cleanup : IDisposable
    {
        public void Dispose() => VillageTownService.Reset();
    }
}
