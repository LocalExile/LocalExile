/// <summary>
/// Server-driven player character voice lines (<c>CharacterAudioEvents</c>).
/// <para>
/// In the 3.29.3c client, server-driven voice lines are triggered by S→C <c>0x2bc</c>
/// <c>PlayerQuestFlagsUpdate</c> transitioning a <c>CharacterAudioEvents.Event</c> <c>QuestFlags</c> row from
/// 0 to 1 (<c>0x14010bd30</c>, baseline snapshotted at <c>0x140108c80</c>). Standard campaign area-entry lines
/// (<c>WorldAreas.StartQuestFlags</c>, 96 events) play when <see cref="Game.RecordVisit"/> runs after
/// <see cref="Game.SendPlayer"/>; level-up lines (<c>LevelUp2..90</c>) play automatically on
/// <c>PlayerExperienceUpdate</c> (<c>0x140d5b2f0</c>); combat/UI feedback lines (<c>NoMana</c>, <c>Cooldown</c>,
/// <c>InvFull</c>, <c>KillRare</c>, <c>KillRogueExile</c>, <c>SkillTown</c>, <c>QuestItem</c>, <c>LowSTR/INT/DEX</c>,
/// <c>Flask*</c>, <c>EventJack*</c>) fire locally in the client (<c>0x14010c960</c>).
/// </para>
/// <para>
/// This class covers the remaining server-driven <c>CharacterAudioEvents</c> whose <c>QuestFlags</c> are not
/// already set by <c>WorldAreas.StartQuestFlags</c> or the campaign quest tables:
/// Act 4 mine-entry shakes, first Atlas map entry, Act 1–5/9 &amp; endgame boss-kill audio flags,
/// first NPC meetings (<c>Met-*</c> / <c>Approach*</c>), and companion quest-event audio flags.
/// Because each flag is persisted in the character's <c>QuestFlags</c>, every event plays once per character
/// and never spams on town portals or repeat kills.
/// </para>
/// </summary>
public static class CharacterVoiceLines
{
    // Non-StartQuestFlags area-entry audio events (each area's own StartQuestFlags has no CharacterAudioEvents row).
    static readonly Dictionary<string, string> SpecialAreaFlags = new(StringComparer.Ordinal)
    {
        ["1_4_3_1"] = "a4-BeastShake1",       // EnterMines1 (delay 8000 ms)
        ["1_4_3_2"] = "a4-BeastShake2",       // EnterMines2 (delay 10000 ms)
        ["1_4_3_3"] = "a4-BeastShake3",       // EnterCrystalVeins (delay 12000 ms)
    };

    const string EnteredMapsFlag = "EnteredMaps";

    // Boss kills whose CharacterAudioEvents flag is a dedicated AudioEventKilled* / side-boss / endgame flag not
    // already set by the campaign quest tables (Acts 2 bandits, Act 4 Voll/Kaom/Daresso/Malachai, Acts 5–10 story
    // bosses, and Izaro already have their CharacterAudioEvents quest flag set by Act*.cs / IzaroFights).
    // Empty Area means any area (endgame pinnacle / league bosses).
    static readonly Dictionary<string, (string Area, string Flag)[]> BossKillFlags = new(StringComparer.Ordinal)
    {
        // Act 1
        ["Metadata/Monsters/ZombieDismember/ZombieDrownedStrandWake1"] = [("1_1_1", "AudioEventKilledBiteZombie")],
        ["Metadata/Monsters/ZombieDismember/ZombieDrownedStrandWake1Standalone"] = [("1_1_1", "AudioEventKilledBiteZombie")],
        ["Metadata/Monsters/ZombieDismember/ZombieDrownedStrandWake2"] = [("1_1_1", "AudioEventKilledBiteZombie")],
        ["Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal"] = [("1_1_1", "AudioEventKilledHillock")],
        ["Metadata/Monsters/Cannibal/CannibalFemaleBossWitch"] = [("1_1_2", "AudioEventKilledFireFury")],
        ["Metadata/Monsters/Necromancer/NecromancerBossFetidPool"] = [("1_1_3a", "AudioEventKilledKadavrus")],
        ["Metadata/Monsters/ShieldCrabs/ShieldCrabBossSlowAura"] = [("1_1_4_0", "AudioEventKilledDweller")],
        ["Metadata/Monsters/Totems/TotemBossSpark"] = [("1_1_5", "AudioEventKilledKuduku")],
        ["Metadata/Monsters/Brute/BossBrute"] = [("1_1_7_2", "AudioEventKilledBrutus")],
        ["Metadata/Monsters/Skeletons/SkeletonBossFairgraves"] = [("1_1_9", "AudioEventKilledFairgraves")],
        ["Metadata/Monsters/Seawitch/SeaWitchBossSpark"] = [("1_1_11_1", "AudioEventKilledMerveilDaughter")],
        ["Metadata/Monsters/Seawitch/SeaWitchBossFirestorm"] = [("1_1_11_2", "AudioEventKilledMerveilDaughter")],
        ["Metadata/Monsters/Seawitch/BossMerveil2"] = [("1_1_11_2", "AudioEventKilledMerveil")],

        // Act 2
        ["Metadata/Monsters/Beasts/BeastBossWhite"] = [("1_2_2a", "AudioEventKilledWhiteBeast")],
        ["Metadata/Monsters/Skeletons/SkeletonBossMelee"] = [("1_2_5_2", "AudioEventKilledGeofri")],
        ["Metadata/Monsters/DemonFourLegged/BossFourLeggedDemon"] = [("1_2_6_2", "AudioEventKilledFidelitas")],
        ["Metadata/Monsters/Spiders/SpiderBossWeaver"] = [("1_2_10", "AudioEventKilledWeaver")],
        ["Metadata/Monsters/IncaShadowBoss/BossIncaShadow"] = [("1_2_14_3", "AudioEventKilledOversoul")],

        // Act 3
        ["Metadata/Monsters/Statue/StoneStatueLargeBossMale"] = [("1_3_5", "AudioEventKilledMarceusStatue")],
        ["Metadata/Monsters/Axis/AxisGeneral"] = [("1_3_13", "AudioEventKilledGravicius")],
        ["Metadata/Monsters/Brute/BossBrute2"] = [("1_3_14_1", "AudioEventKilledKole")],
        ["Metadata/Monsters/Axis/PietyBoss"] = [("1_3_14_2", "AudioEventKilledPiety")],
        ["Metadata/Monsters/Dominusdemon/Dominusdemon"] = [("1_3_18_2", "AudioEventKilledDominus")],

        // Act 4
        // Corrupted Piety in Belly 2: a4q1-KilledPiety has no TextAudio rows, whereas a4q1-PietySacrifice
        // (EventPietySacrifice) has lines for all 7 classes + Goddess; sending it here in the post-kill
        // GameEvents.Kill step guarantees a separate 0x2bc update after a4q1-KilledPiety.
        ["Metadata/Monsters/Axis/PietyBeastBoss"] = [("1_4_6_2", "a4q1-PietySacrifice")],
        ["Metadata/Monsters/Maligaro/Maligaro"] = [("1_4_6_3", "AudioEventKilledMaligaro")],
        ["Metadata/Monsters/Shavronne/Shavronne"] = [("1_4_6_3", "AudioEventKilledShavronne")],
        ["Metadata/Monsters/Doedre/Doedre"] = [("1_4_6_3", "AudioEventKilledDoedre")],

        // Act 5 & Act 9 side bosses
        ["Metadata/Monsters/MotherOfFlames/MotherOfFlamesBoss"] = [("1_5_3", "KilledMatriarchOriathSquare")],
        ["Metadata/Monsters/Axis/Cleric/BlackguardClericPureBoss"] = [("1_5_4b", "KilledCatoTorchedCourts")],
        ["Metadata/Monsters/Kira/Kira"] = [("2_9_2", "a9q2-KilledKira")],

        // Endgame / pinnacle bosses (if not already granted by Atlas unlock flags)
        ["Metadata/Monsters/AtlasExiles/AtlasExile5"] = [("", "SirusDefeated")],
        ["Metadata/Monsters/MavenBoss/TheMaven"] = [("", "Met-EnvoyMaven")],
        ["Metadata/Monsters/MavenBoss/TheMavenEnraged_"] = [("", "Met-EnvoyMaven")],
        ["Metadata/Monsters/MavenBoss/MavenBrainBoss"] = [("", "Met-EnvoyMaven")],
        ["Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss"] = [("", "Met-EnvoyExarch")],
        ["Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss"] = [("", "Met-EnvoyEater")],
        ["Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/VoodooKingBoss"] = [("", "AzmeriKillVoodooKingCompleted")],
        ["Metadata/Monsters/LeagueUltimatum/TrialMaster/TrialMasterBoss"] = [("", "TrialmasterDefeatedInCombat")],
        ["Metadata/Monsters/LeagueSanctum/Boss/Lycia2Boss"] = [("", "KilledLycia2Boss")],
        ["Metadata/Monsters/LeagueHellscape/PaleFaction/HellscapePaleBoss"] = [("", "DefeatBeidatArchangelOfDeath")],
    };

    // First-meeting NPC voice lines (CharacterAudioEvents Met* / Approach* rows).
    static readonly Dictionary<string, string> NpcMeetFlags = new(StringComparer.Ordinal)
    {
        ["Metadata/NPC/Act3/Maramoa"] = "Met-Maramoa",
        ["Metadata/NPC/Act3/Siosa"] = "Met-Siosa",
        ["Metadata/NPC/Act4/PietyBelly"] = "Met-PietyBelly",
        ["Metadata/NPC/Act5/SinInnocence"] = "Met-SinInnocence",
        ["Metadata/NPC/Act5/Lilly"] = "a5ApproachLilly",
        ["Metadata/NPC/Act6/SinTown"] = "MetSinA6",
        ["Metadata/NPC/Act6/SinTukohama"] = "Met-SinTukohama",
        ["Metadata/NPC/Act6/SinAbberath"] = "Met-SinAbberath",
        ["Metadata/NPC/Act6/SinRyslatha"] = "Met-SinRyslatha",
        ["Metadata/NPC/Act6/SinBrineKing"] = "Met-SinBrineKing",
        ["Metadata/NPC/Act7/Eramir"] = "A7ApproachEramir",
        ["Metadata/NPC/Act7/SinRalakesh"] = "Met-SinRalakesh",
        ["Metadata/NPC/Act7/SinGruthkul"] = "Met-SinGruthkul",
        ["Metadata/NPC/Act7/SinArakaali"] = "Met-SinArakaali",
        ["Metadata/NPC/Act8/SinYugul"] = "Met-SinYugul",
        ["Metadata/NPC/Act8/SinSolarisLunaris"] = "Met-SinSolarisLunaris",
        ["Metadata/NPC/Act9/SinGarukhan"] = "Met-SinGarukhan",
        ["Metadata/NPC/Act9/SinShakari"] = "Met-SinShakari",
        ["Metadata/NPC/Epilogue/Sin"] = "Met-EpilogueSinInnocence",
        ["Metadata/NPC/Epilogue/Innocence"] = "Met-EpilogueSinInnocence",
        ["Metadata/NPC/League/Harvest/HarvestNPCGardenTutorial"] = "Met-Oshabi",
        ["Metadata/NPC/League/Expedition/Gambler"] = "Met-Gwennen",
        ["Metadata/NPC/League/Expedition/GamblerHideout"] = "Met-Gwennen",
    };

    // Quest flags whose CharacterAudioEvents row is a companion AudioEvent* flag.
    static readonly Dictionary<string, string> CompanionQuestFlags = new(StringComparer.Ordinal)
    {
        ["a1q9-SeenBlockage"] = "AudioEventSeenSpikes",
    };

    /// <summary>The voice-line quest flags for killing <paramref name="varietyId"/> in <paramref name="areaId"/>.</summary>
    public static IReadOnlyList<string> FlagsForKill(string areaId, string varietyId)
    {
        if (!BossKillFlags.TryGetValue(varietyId, out var rules)) return [];
        List<string>? matched = null;
        foreach (var (area, flag) in rules)
            if (area.Length == 0 || area == areaId)
                (matched ??= []).Add(flag);
        return matched ?? (IReadOnlyList<string>)[];
    }

    /// <summary>The voice-line quest flag for entering <paramref name="areaId"/> (or an Atlas map), if not driven by StartQuestFlags.</summary>
    public static string? FlagForAreaEntry(string areaId, bool isMapArea)
    {
        if (SpecialAreaFlags.TryGetValue(areaId, out var flag)) return flag;
        return isMapArea ? EnteredMapsFlag : null;
    }

    /// <summary>The voice-line quest flag for first talking to <paramref name="npcId"/>.</summary>
    public static string? FlagForNpcMeet(string npcId) =>
        NpcMeetFlags.TryGetValue(npcId, out var flag) ? flag : null;

    /// <summary>GameEvents.AreaGenerated: grants special area-entry voice-line flags (Act 4 mines, first Atlas map).</summary>
    public static async Task OnAreaGenerated(Player player)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        string areaId = player.Layout.Id;
        bool isMap = Game.MapDevices.IsMapArea(player.Layout.Hash);
        if (FlagForAreaEntry(areaId, isMap) is { } flag)
            await GrantAudioFlag(player, flag);
    }

    /// <summary>GameEvents.Kill: schedules boss-kill voice-line quest flags after the kill commits.</summary>
    public static void OnKill(KillContext kill)
    {
        var player = kill.Player;
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        string areaId = player.Layout.Id;
        var flags = FlagsForKill(areaId, kill.VarietyId);
        if (flags.Count == 0) return;
        kill.Later(() => GrantKillFlags(player, areaId, kill.VarietyId));
    }

    /// <summary>Grants boss-kill voice-line quest flags (used by solo kills and party shared kills).</summary>
    public static async Task GrantKillFlags(Player player, string areaId, string varietyId)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        foreach (string flag in FlagsForKill(areaId, varietyId))
            await GrantAudioFlag(player, flag);
    }

    /// <summary>Grants first-meeting NPC voice-line quest flags when the player opens dialogue with an NPC.</summary>
    public static async Task OnNpcTalked(Player player, string npcId)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        if (FlagForNpcMeet(npcId) is { } flag)
            await GrantAudioFlag(player, flag);
    }

    /// <summary>Grants companion voice-line quest flags when a trigger quest flag is published (caller holds <c>QuestGate</c>).</summary>
    public static async Task OnQuestFlagsPublishedHeld(Player player, IReadOnlyList<string> effectiveFlags)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent || effectiveFlags.Count == 0) return;
        foreach (string flag in effectiveFlags)
            if (CompanionQuestFlags.TryGetValue(flag, out var companion))
                await GrantAudioFlag(player, companion, gateHeld: true);
    }

    static async Task GrantAudioFlag(Player player, string flag, bool gateHeld = false)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        if (player.Character.HasQuestFlag(flag)) return;
        // Guard against missing/unknown tables in minimal test environments while still allowing valid flags.
        var catalog = CharacterAudioCatalog.Default;
        if (catalog.All.Count > 0 && catalog.ByQuestFlag(flag) is null) return;
        if (gateHeld)
            await Game.SetQuestFlagsHeld(player, [flag]);
        else
            await Game.SetQuestFlags(player, [flag]);
    }
}
