using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>
/// The Envoy &amp; Map Device Beacon encounters (<c>kirac_atlas</c>):
/// <list type="bullet">
/// <item>Spawns <c>Metadata/NPC/Epilogue/Envoy</c> (NPC 484) in T3+ maps once <c>LQF 1038</c> is set (meeting sets
/// <c>LQF 1115</c>; after Kirac <c>LQF 984</c>, <c>NPCTalk 2554</c> "A Warning" sets <c>LQF 425</c> and triggers the
/// first Maven witness via <c>set_maven_region_flag</c>).</item>
/// <item>Spawns <c>EnvoyTangle</c> (487) and <c>EnvoyCleansingFire</c> (488) in maps carrying <c>Stat 15661</c>
/// (<c>map_spawn_envoy</c> = 2 or 3) so their dialogue drops the Flesh Compass &amp; Luminous Astrolabe and plays
/// <c>SendMavenDialog</c>.</item>
/// <item>Spawns post-boss arena Envoys (<c>EnvoyTangleBoss1/2</c> 489/490, <c>EnvoyCleansingFireBoss1/2</c> 491/492,
/// and <c>EnvoyMaven</c> 486) with <c>invisible = 0, npc_mode = 1</c> when their arena boss dies.</item>
/// </list>
/// </summary>
public static class EnvoyEncounters
{
    public const string EnvoyId = "Metadata/NPC/Epilogue/Envoy";
    public const string EnvoyMavenId = "Metadata/NPC/Epilogue/EnvoyMaven";
    public const string EnvoyTangleId = "Metadata/NPC/Epilogue/EnvoyTangle";
    public const string EnvoyCleansingFireId = "Metadata/NPC/Epilogue/EnvoyCleansingFire";
    public const string EnvoyTangleBoss1Id = "Metadata/NPC/Epilogue/EnvoyTangleBoss1";
    public const string EnvoyTangleBoss2Id = "Metadata/NPC/Epilogue/EnvoyTangleBoss2";
    public const string EnvoyCleansingFireBoss1Id = "Metadata/NPC/Epilogue/EnvoyCleansingFireBoss1";
    public const string EnvoyCleansingFireBoss2Id = "Metadata/NPC/Epilogue/EnvoyCleansingFireBoss2";

    public const string MavenBeaconItem = "Metadata/Items/QuestItems/Act11/MavenMapDeviceAlteration";
    public const string TangleAlterationItem = "Metadata/Items/QuestItems/Act11/TangleMapDeviceAlteration";
    public const string CleansingFireAlterationItem = "Metadata/Items/QuestItems/Act11/CleansingFireMapDeviceAlteration";

    public const int LqfExploreAtlas = 1038;
    public const int LqfMetEnvoy = 1115;
    public const int LqfKiracOnEnvoy = 984;
    public const int LqfEnvoyMavenWarning = 425;
    public const int LqfMavenBeacon = 225;
    public const int LqfTangleBeacon = 351;
    public const int LqfCleansingFireBeacon = 352;

    const int EnvoyProximityRange = 35;

    sealed class InstanceEnvoyState
    {
        public (int X, int Y)? IntroEnvoyAt;
        public bool MetNotified;
    }

    static readonly ConditionalWeakTable<InstanceState, InstanceEnvoyState> InstanceStates = new();

    /// <summary>Determines which Envoy NPC (if any) should spawn in an Atlas map for <paramref name="player"/>.</summary>
    public static string? DetermineMapEnvoyNpc(Player player, MapDevice.Run? run, IReadOnlyDictionary<uint, int>? areaStats)
    {
        if (run is not { Atlas: true } || run.Mods.MapRarity == ItemRarity.Unique) return null;
        int spawnStat = areaStats is not null && areaStats.TryGetValue(Stat.MapSpawnEnvoy, out int s) ? s : 0;
        if (spawnStat == 0)
        {
            spawnStat = global::Game.ResolveEnvoySpawnStat(run.Area.Tier, run.Mods.MapRarity,
                player.Character.LeagueQuestFlags,
                ClientHasQuestFlag(player, "HaveTangleMapDeviceAlteration"),
                ClientHasQuestFlag(player, "HaveCleansingFireMapDeviceAlteration"));
        }
        if (spawnStat == 2 && !player.Character.LeagueQuestFlags.Contains(LqfTangleBeacon))
            return EnvoyTangleId;
        if (spawnStat == 3 && !player.Character.LeagueQuestFlags.Contains(LqfCleansingFireBeacon))
            return EnvoyCleansingFireId;

        if (run.Area.Tier < 3) return null;
        var lqfs = player.Character.LeagueQuestFlags;
        // First meeting in T3+ maps after Kirac's "Explore the Atlas" (LQF 1038) and before LQF 1115.
        if (lqfs.Contains(LqfExploreAtlas) && !lqfs.Contains(LqfMetEnvoy))
            return EnvoyId;
        // Second meeting after reporting to Kirac (LQF 984) to trigger "A Warning" (NPCTalk 2554 → LQF 425).
        if (lqfs.Contains(LqfKiracOnEnvoy) && !lqfs.Contains(LqfEnvoyMavenWarning)
            && !lqfs.Contains(LqfMavenBeacon) && !ClientHasQuestFlag(player, "HaveMavenMapDeviceAlteration"))
            return EnvoyId;
        return null;
    }

    /// <summary>Returns the post-boss Envoy NPC metadata path for a pinnacle/miniboss kill, or null.</summary>
    public static string? PostBossEnvoyForKill(string areaId, string varietyId) => (areaId, varietyId) switch
    {
        ("MapWorldsPrimordialBoss1", "Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_") => EnvoyTangleBoss1Id,
        ("MapWorldsPrimordialBoss4", "Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss") => EnvoyTangleBoss2Id,
        ("MapWorldsPrimordialBoss2", "Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss") => EnvoyCleansingFireBoss1Id,
        ("MapWorldsPrimordialBoss3", "Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss") => EnvoyCleansingFireBoss2Id,
        ("MavenBoss", "Metadata/Monsters/MavenBoss/MavenBrainBoss") => EnvoyMavenId,
        _ => null,
    };

    /// <summary>Spawns the appropriate Envoy NPC when entering an eligible Atlas map.</summary>
    internal static async Task OnPlayerEntered(Player player)
    {
        var run = global::Game.MapRunOf(player);
        if (run is not { Atlas: true }) return;
        var areaStats = player.Instance.AreaStats(() => global::Game.AreaLeagueStats(player));
        string? npcId = DetermineMapEnvoyNpc(player, run, areaStats);
        if (npcId is null) return;
        lock (player.Npcs)
            if (player.Npcs.Values.Any(n => string.Equals(n.Npc.Id, npcId, StringComparison.Ordinal)))
                return;
        if (global::Game.Npcs.Find(npcId) is not { Supported: true } npc) return;
        var at = FindEnvoySpawnCell(player, npcId);
        await global::Game.SpawnNpc(player, npc, at.X, at.Y, 0f);
        if (npcId == EnvoyId)
        {
            var state = InstanceStates.GetOrCreateValue(player.Instance);
            state.IntroEnvoyAt = at;
        }
    }

    /// <summary>AreaTick binding: approaching the intro Envoy in a T3+ map marks LQF 1115 (MetEnvoy).</summary>
    internal static Func<Player, double, Task>? TickBinding(Player player)
    {
        if (global::Game.MapRunOf(player) is not { Atlas: true, Area.Tier: >= 3 }) return null;
        if (player.Character.LeagueQuestFlags.Contains(LqfMetEnvoy)) return null;
        return async (p, _) =>
        {
            if (p.Closed || p.LeavingArea || p.Character.LeagueQuestFlags.Contains(LqfMetEnvoy)) return;
            if (!InstanceStates.TryGetValue(p.Instance, out var state) || state.MetNotified || state.IntroEnvoyAt is not { } at)
                return;
            var here = p.Walk.Position(global::Game.AreaTime(p));
            int dx = here.X - at.X, dy = here.Y - at.Y;
            if (dx * dx + dy * dy > EnvoyProximityRange * EnvoyProximityRange) return;
            state.MetNotified = true;
            await RecordEnvoyMet(p);
        };
    }

    /// <summary>Talking to an Envoy NPC sets its meeting flags while allowing normal dialogue to continue.</summary>
    internal static async Task<bool> OnNpcTalk(Player player, uint npcId, SpawnedNpc npc)
    {
        switch (npc.Npc.Id)
        {
            case EnvoyId:
                await RecordEnvoyMet(player);
                break;
            case EnvoyTangleId:
                if (!ClientHasQuestFlag(player, "Met-EnvoyEater"))
                    await SetQuestFlags(player, ["Met-EnvoyEater"]);
                break;
            case EnvoyCleansingFireId:
                if (!ClientHasQuestFlag(player, "Met-EnvoyExarch"))
                    await SetQuestFlags(player, ["Met-EnvoyExarch"]);
                break;
        }
        return false;
    }

    /// <summary>Marks session quest flags when EnvoyTangle or EnvoyCleansingFire drops a Map Device alteration.</summary>
    internal static async Task OnNpcOptionPicked(Player player, NpcCatalog.Talk talk)
    {
        if (talk.Row is 2802 or 2803)
        {
            var flags = new List<string>();
            if (!ClientHasQuestFlag(player, "DroppedMapQuestItem")) flags.Add("DroppedMapQuestItem");
            if (!ClientHasQuestFlag(player, "PickUpTangleMapDeviceAlteration")) flags.Add("PickUpTangleMapDeviceAlteration");
            if (flags.Count > 0) await SetQuestFlags(player, flags);
        }
        else if (talk.Row is 2811 or 2812)
        {
            var flags = new List<string>();
            if (!ClientHasQuestFlag(player, "DroppedMapQuestItem")) flags.Add("DroppedMapQuestItem");
            if (!ClientHasQuestFlag(player, "PickUpCleansingFireMapDeviceAlteration")) flags.Add("PickUpCleansingFireMapDeviceAlteration");
            if (flags.Count > 0) await SetQuestFlags(player, flags);
        }
    }

    /// <summary>Spawns the matching post-boss arena Envoy when Infinite Hunger, Eater of Worlds, Black Star,
    /// Searing Exarch, or Nucleus of the Maven dies.</summary>
    internal static async Task OnBossKilled(Player player, MonsterInfo monster)
    {
        string? envoyId = PostBossEnvoyForKill(player.Layout.Id, monster.VarietyId);
        if (envoyId is null) return;
        await SpawnPostBossEnvoy(player, envoyId, (monster.X, monster.Y));
    }

    /// <summary>Spawns a post-boss Envoy NPC (<c>EnvoyPostBoss</c> / <c>EnvoyMaven</c>) with shared states
    /// <c>invisible = 0, npc_mode = 1</c> for every player in the arena instance.</summary>
    public static async Task SpawnPostBossEnvoy(Player player, string envoyNpcId, (int X, int Y) bossAt)
    {
        if (global::Game.Npcs.Find(envoyNpcId) is not { Supported: true } baseNpc) return;
        var visibleNpc = baseNpc.StateMachine is { } sm
            ? baseNpc with { StateMachine = sm.Set("invisible", 0).Set("npc_mode", 1) }
            : baseNpc;
        var at = player.Layout.Near((bossAt.X + 4, bossAt.Y));
        foreach (var peer in InstancePeers(player))
        {
            bool exists;
            lock (peer.Npcs)
                exists = peer.Npcs.Values.Any(n => string.Equals(n.Npc.Id, envoyNpcId, StringComparison.Ordinal));
            if (exists) continue;
            try { await global::Game.SpawnNpc(peer, visibleNpc, at.X, at.Y, 0f); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    internal static async Task RecordEnvoyMet(Player player)
    {
        if (!player.Character.LeagueQuestFlags.Contains(LqfMetEnvoy))
        {
            await SetLeagueQuestFlags(player, [LqfMetEnvoy]);
            await MavenWitnessing.PlayMavenDialog(player, "EnvoyGreeting");
        }
        if (!ClientHasQuestFlag(player, "Met-EnvoyMaven"))
            await SetQuestFlags(player, ["Met-EnvoyMaven"]);
    }

    static (int X, int Y) FindEnvoySpawnCell(Player player, string npcId)
    {
        // Prefer room objects placed by ExtraTerrainFeatures (TangleQuest / CleansingFireQuest) when present.
        if (player.Layout.Level?.RoomObjects is { Count: > 0 } rooms)
        {
            string? needle = npcId switch
            {
                EnvoyTangleId => "EnvoyTangle",
                EnvoyCleansingFireId => "EnvoyCleansingFire",
                _ => "Envoy",
            };
            var match = rooms.FirstOrDefault(o => o.Type.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return player.Layout.Near((match.X, match.Y));
        }
        // For Maven intro Envoy, place near the map boss when present; otherwise near arrival so the player always finds him.
        if (npcId == EnvoyId)
        {
            var boss = player.Area.Monsters.Values.FirstOrDefault(m => !m.Dead && m.Rarity == MonsterRarity.Unique);
            if (boss is not null)
                return player.Layout.Near((boss.Walk.Position(global::Game.AreaTime(player)).X + 10,
                    boss.Walk.Position(global::Game.AreaTime(player)).Y));
        }
        return player.Layout.Near((player.Layout.SpawnX + 12, player.Layout.SpawnY));
    }

    static IEnumerable<Player> InstancePeers(Player player)
    {
        var peers = InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance)
            && c.Connection.PlayerSent && !c.LeavingArea && !c.Closed).ToList();
        return peers.Contains(player) ? peers : [player, .. peers];
    }
}
